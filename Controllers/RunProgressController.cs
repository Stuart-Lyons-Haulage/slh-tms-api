using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/run-progress")]
[Authorize]
public sealed class RunProgressController(TmsDbContext db, ILogger<RunProgressController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] DateOnly? date, CancellationToken ct)
    {
        var planningDate = date ?? UkOperatingDate(DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow;

        try
        {
            // Production planning is register-backed. Do not probe the absent legacy
            // Loads table here: doing so only generates false operational warnings.
            var loads = (await PlanningRegisterStore.ReadLoadsAsync(db, planningDate, ct))
                .Where(x => x.Status != LoadStatus.Cancelled)
                .OrderBy(x => x.Reference)
                .ToList();

            // The planner intentionally uses concise operational labels such as
            // NWF-Merston, while Falcon calls the same fence Merston (Natures Way).
            // Match on a cloned view so the planner-facing labels remain unchanged.
            var geofenceLoads = GeofencePlanningMatch.PrepareLoads(loads);
            var snapshot = await EmbeddedGeofenceEngine.BuildAsync(db, planningDate, geofenceLoads, ct);
            var records = loads.Select(load => BuildRecord(load, snapshot, now)).ToList();

            return Ok(new
            {
                planningDate,
                calculatedAtUtc = now,
                count = records.Count,
                source = "PlanningRegister+EmbeddedSLHGeofences",
                geofenceAvailable = snapshot.Fences.Count > 0,
                geofenceCount = snapshot.Fences.Count,
                geofenceVisitCount = snapshot.Visits.Count,
                geofenceLinkedRuns = snapshot.Visits.Where(x => x.LoadId != null).Select(x => x.LoadId!.Value).Distinct().Count(),
                trackingEventCount = snapshot.TrackingEventCount,
                latestTrackingUtc = snapshot.LatestTrackingUtc,
                warning = (string?)null,
                records
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Embedded geofence run progression failed for {PlanningDate}.", planningDate);
            db.ChangeTracker.Clear();

            List<Load> loads;
            try { loads = await PlanningRegisterStore.ReadLoadsAsync(db, planningDate, ct); }
            catch { loads = []; db.ChangeTracker.Clear(); }

            var records = loads.OrderBy(x => x.Reference).Select(load =>
            {
                var stops = (load.Stops ?? []).OrderBy(x => x.Sequence).ToList();
                var next = stops.FirstOrDefault();
                return new
                {
                    loadId = load.Id,
                    loadReference = load.Reference,
                    loadStatus = load.Status.ToString(),
                    runState = load.Status.ToString(),
                    totalStops = stops.Count,
                    completedStops = 0,
                    progressPercent = 0m,
                    nextStop = next is null ? null : new { next.Id, next.Sequence, next.Name, next.Address, next.PlannedArrivalUtc },
                    currentVisit = (object?)null,
                    lastDeparture = (object?)null,
                    calculatedAtUtc = now
                };
            }).ToList();

            var geofenceCount = 0;
            try { geofenceCount = EmbeddedGeofenceEngine.ApprovedFences.Count; }
            catch (Exception geofenceException) when (geofenceException is not OperationCanceledException)
            {
                logger.LogError(geofenceException, "Approved geofence payload could not be initialised while building safe run-progress fallback.");
            }

            return Ok(new
            {
                planningDate,
                calculatedAtUtc = now,
                count = records.Count,
                source = "PlanningRegisterSafeFallback",
                geofenceAvailable = geofenceCount > 0,
                geofenceCount,
                geofenceVisitCount = 0,
                geofenceLinkedRuns = 0,
                warning = geofenceCount > 0
                    ? "Approved SLH geofences are loaded, but live progression could not be calculated from tracking on this refresh."
                    : "Live run progression could not be calculated and the approved geofence payload was unavailable on this refresh.",
                records
            });
        }
    }

    private static object BuildRecord(Load load, EmbeddedGeofenceSnapshot snapshot, DateTimeOffset now)
    {
        var orderedStops = (load.Stops ?? []).OrderBy(x => x.Sequence).ToList();
        var visits = snapshot.Visits.Where(x => x.LoadId == load.Id).OrderBy(x => x.EnteredAtUtc).ToList();
        var completedStopIds = GeofencePlanningMatch.CompletedStopIds(load, visits);

        var activeVisit = snapshot.ActiveVisits
            .Where(x => x.LoadId == load.Id)
            .OrderByDescending(x => x.EnteredAtUtc)
            .FirstOrDefault();
        var nextStop = orderedStops.FirstOrDefault(x => !completedStopIds.Contains(x.Id));
        var lastDeparture = visits
            .Where(x => x.ExitedAtUtc != null && x.ConfirmedAtUtc != null)
            .OrderByDescending(x => x.ExitedAtUtc)
            .FirstOrDefault();

        var totalStops = orderedStops.Count;
        var completedStops = completedStopIds.Count;
        var progressPercent = totalStops == 0 ? 0m : Math.Round((decimal)completedStops / totalStops * 100m, 1);
        var waitLimit = activeVisit?.Fence.MaxWaitMinutes ?? activeVisit?.Fence.CategoryMaxWaitMinutes;
        var dwell = activeVisit?.DwellMinutes;
        if (activeVisit is not null && now - activeVisit.LastInsideAtUtc <= TimeSpan.FromMinutes(5))
            dwell = Math.Max(activeVisit.DwellMinutes, Math.Max(0, (int)Math.Floor((now - activeVisit.EnteredAtUtc).TotalMinutes)));
        var delayed = activeVisit is not null && waitLimit is int limit && dwell.GetValueOrDefault() > limit;
        var activeStatus = activeVisit is null ? null : delayed ? "SiteDelay" : activeVisit.ConfirmedAtUtc is not null ? "OnSiteConfirmed" : "Arrived";
        var runState = totalStops > 0 && completedStops >= totalStops
            ? "Completed"
            : activeStatus ?? (completedStops > 0 ? "BetweenStops" : load.Status.ToString());

        return new
        {
            loadId = load.Id,
            loadReference = load.Reference,
            loadStatus = load.Status.ToString(),
            runState,
            totalStops,
            completedStops,
            progressPercent,
            nextStop = nextStop is null ? null : new { nextStop.Id, nextStop.Sequence, nextStop.Name, nextStop.Address, nextStop.PlannedArrivalUtc },
            currentVisit = activeVisit is null ? null : new
            {
                activeVisit.Id,
                geofenceId = activeVisit.Fence.Id,
                geofenceName = activeVisit.Fence.Name,
                category = activeVisit.Fence.Category,
                activeVisit.LoadStopId,
                activeVisit.EnteredAtUtc,
                activeVisit.ConfirmedAtUtc,
                dwellMinutes = dwell,
                waitLimitMinutes = waitLimit,
                isDelayed = delayed,
                status = activeStatus,
                statusReason = delayed
                    ? $"Dwell is {dwell.GetValueOrDefault()} minutes; site threshold is {waitLimit} minutes."
                    : activeVisit.ConfirmedAtUtc is not null
                        ? $"Confirmed after {dwell.GetValueOrDefault()} minutes in {activeVisit.Fence.Name}."
                        : $"Inside {activeVisit.Fence.Name}; awaiting 10-minute confirmation."
            },
            lastDeparture = lastDeparture is null ? null : new { lastDeparture.LoadStopId, lastDeparture.ExitedAtUtc, lastDeparture.DwellMinutes },
            calculatedAtUtc = now
        };
    }

    private static DateOnly UkOperatingDate(DateTimeOffset value)
    {
        try
        {
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, TimeZoneInfo.FindSystemTimeZoneById("Europe/London")).DateTime);
        }
        catch (TimeZoneNotFoundException)
        {
            return DateOnly.FromDateTime(value.UtcDateTime);
        }
    }
}
