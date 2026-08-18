using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
            return Ok(await BuildProgressAsync(planningDate, now, ct));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Live run progress failed safely for {PlanningDate}.", planningDate);
            db.ChangeTracker.Clear();

            var warning = $"Live run progress failed safely: {exception.GetBaseException().Message}";
            var fallback = await TryLoadRegisterRunsAsync(planningDate, warning, ct);
            if (fallback is not null) return Ok(fallback);

            return Ok(new
            {
                planningDate,
                calculatedAtUtc = now,
                count = 0,
                source = "Unavailable",
                geofenceAvailable = false,
                warning,
                records = Array.Empty<object>()
            });
        }
    }

    private async Task<object> BuildProgressAsync(DateOnly planningDate, DateTimeOffset now, CancellationToken ct)
    {
        var geofenceAvailable = true;
        string? geofenceWarning = null;

        try
        {
            await GeofenceRunProgression.EnsureSchemaAsync(db, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            geofenceAvailable = false;
            geofenceWarning = $"Geofence progress schema is not available yet: {exception.GetBaseException().Message}";
            logger.LogWarning(exception, "Live run progress is continuing without geofence visit data.");
        }

        var (loads, loadSource, loadWarning) = await LoadRunsAsync(planningDate, ct);
        var loadIds = loads.Select(x => x.Id).ToList();

        List<GeofenceVisit> visits = [];
        Dictionary<Guid, SiteGeofence> geofences = [];
        if (geofenceAvailable && loadIds.Count > 0)
        {
            try
            {
                visits = await db.GeofenceVisits.AsNoTracking()
                    .Where(x => x.LoadId != null && loadIds.Contains(x.LoadId.Value))
                    .OrderBy(x => x.EnteredAtUtc)
                    .ToListAsync(ct);

                var geofenceIds = visits.Select(x => x.GeofenceId).Distinct().ToList();
                geofences = geofenceIds.Count == 0
                    ? []
                    : await db.SiteGeofences.AsNoTracking()
                        .Where(x => geofenceIds.Contains(x.Id))
                        .ToDictionaryAsync(x => x.Id, ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                db.ChangeTracker.Clear();
                geofenceAvailable = false;
                geofenceWarning = $"Geofence visit data is not available yet: {exception.GetBaseException().Message}";
                logger.LogWarning(exception, "Live run progress loaded runs but skipped geofence visit data.");
                visits = [];
                geofences = [];
            }
        }

        var records = loads.Select(load =>
        {
            var orderedStops = (load.Stops ?? []).OrderBy(x => x.Sequence).ToList();
            var loadVisits = geofenceAvailable
                ? visits.Where(x => x.LoadId == load.Id).ToList()
                : [];
            var completedStopIds = loadVisits
                .Where(x => x.LoadStopId != null && x.ConfirmedAtUtc != null && x.ExitedAtUtc != null && x.Status == "Departed")
                .Select(x => x.LoadStopId!.Value)
                .ToHashSet();

            var activeVisit = loadVisits.Where(x => x.ExitedAtUtc == null).OrderByDescending(x => x.EnteredAtUtc).FirstOrDefault();
            var nextStop = orderedStops.FirstOrDefault(x => !completedStopIds.Contains(x.Id));
            var lastDeparture = loadVisits.Where(x => x.ExitedAtUtc != null && x.ConfirmedAtUtc != null)
                .OrderByDescending(x => x.ExitedAtUtc).FirstOrDefault();

            SiteGeofence? activeFence = null;
            if (activeVisit is not null) geofences.TryGetValue(activeVisit.GeofenceId, out activeFence);
            var currentDwellMinutes = activeVisit is null
                ? (int?)null
                : Math.Max(activeVisit.DwellMinutes, Math.Max(0, (int)Math.Floor((now - activeVisit.EnteredAtUtc).TotalMinutes)));
            var waitLimit = activeFence?.MaxWaitMinutes ?? activeFence?.CategoryMaxWaitMinutes;
            var totalStops = orderedStops.Count;
            var completedStops = completedStopIds.Count;
            var progressPercent = totalStops == 0 ? 0m : Math.Round((decimal)completedStops / totalStops * 100m, 1);
            var runState = totalStops > 0 && completedStops >= totalStops
                ? "Completed"
                : activeVisit?.Status ?? (completedStops > 0 ? "BetweenStops" : load.Status.ToString());
            var delayed = activeVisit is not null && (activeVisit.Status == "SiteDelay" || waitLimit is int limit && currentDwellMinutes.GetValueOrDefault() > limit);

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
                    geofenceId = activeVisit.GeofenceId,
                    geofenceName = activeFence?.Name,
                    category = activeFence?.Category,
                    activeVisit.LoadStopId,
                    activeVisit.EnteredAtUtc,
                    activeVisit.ConfirmedAtUtc,
                    dwellMinutes = currentDwellMinutes,
                    waitLimitMinutes = waitLimit,
                    isDelayed = delayed,
                    activeVisit.Status,
                    activeVisit.StatusReason
                },
                lastDeparture = lastDeparture is null ? null : new { lastDeparture.LoadStopId, lastDeparture.ExitedAtUtc, lastDeparture.DwellMinutes },
                calculatedAtUtc = now
            };
        }).ToList();

        return new
        {
            planningDate,
            calculatedAtUtc = now,
            count = records.Count,
            source = loadSource,
            geofenceAvailable,
            warning = loadWarning ?? geofenceWarning,
            records
        };
    }

    private async Task<(List<Load> Loads, string Source, string? Warning)> LoadRunsAsync(DateOnly planningDate, CancellationToken ct)
    {
        try
        {
            var loads = await db.Loads.AsNoTracking().Include(x => x.Stops)
                .Where(x => x.PlanningDate == planningDate && x.Status != LoadStatus.Cancelled)
                .OrderBy(x => x.Reference)
                .Take(500)
                .ToListAsync(ct);

            if (loads.Count > 0) return (loads, "Loads", null);

            var registerLoads = await SafeReadRegisterLoadsAsync(planningDate, ct);
            if (registerLoads.Count > 0)
                return (registerLoads, "PlanningRegister", "No dedicated planning loads were returned, so the live progress panel is using the audited planning register fallback.");

            return (loads, "Loads", null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            var registerLoads = await SafeReadRegisterLoadsAsync(planningDate, ct);
            return (registerLoads, "PlanningRegister", $"Dedicated planning load tables are not available yet: {exception.GetBaseException().Message}");
        }
    }

    private async Task<List<Load>> SafeReadRegisterLoadsAsync(DateOnly planningDate, CancellationToken ct)
    {
        try
        {
            return await PlanningRegisterStore.ReadLoadsAsync(db, planningDate, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            logger.LogWarning(exception, "Planning register fallback could not be read for {PlanningDate}.", planningDate);
            return [];
        }
    }

    private async Task<object?> TryLoadRegisterRunsAsync(DateOnly planningDate, string warning, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var loads = await SafeReadRegisterLoadsAsync(planningDate, ct);
        if (loads.Count == 0) return null;

        var records = loads.OrderBy(load => load.Reference).Select(load =>
        {
            var stops = (load.Stops ?? []).OrderBy(stop => stop.Sequence).ToList();
            var nextStop = stops.FirstOrDefault();
            return new
            {
                loadId = load.Id,
                loadReference = load.Reference,
                loadStatus = load.Status.ToString(),
                runState = load.Status.ToString(),
                totalStops = stops.Count,
                completedStops = 0,
                progressPercent = 0m,
                nextStop = nextStop is null ? null : new { nextStop.Id, nextStop.Sequence, nextStop.Name, nextStop.Address, nextStop.PlannedArrivalUtc },
                currentVisit = (object?)null,
                lastDeparture = (object?)null,
                calculatedAtUtc = now
            };
        }).ToList();

        return new
        {
            planningDate,
            calculatedAtUtc = now,
            count = records.Count,
            source = "PlanningRegisterSafeFallback",
            geofenceAvailable = false,
            warning,
            records
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
