using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Operational wallboard timing derived from geofence execution.
/// Dwell starts at first observation inside a geofence. The first-leg ETA starts when
/// the vehicle leaves Lake Lane; subsequent ETAs are anchored to the previous customer
/// geofence departure rather than continuously rebasing from current GPS.
/// </summary>
[ApiController, Route("api/v1/run-timing")]
[Authorize]
public sealed class RunTimingController(
    TmsDbContext db,
    AzureMapsRouteClient maps,
    IConfiguration configuration,
    ILogger<RunTimingController> logger) : ControllerBase
{
    [HttpGet, AllowAnonymous]
    public async Task<IActionResult> Get([FromQuery] DateOnly? date, CancellationToken ct)
    {
        if (!TvWallboardAccess.IsAllowed(HttpContext, configuration)) return Unauthorized();

        var planningDate = date ?? UkOperatingDate(DateTimeOffset.UtcNow);
        var loads = (await PlanningRegisterStore.ReadLoadsAsync(db, planningDate, ct))
            .Where(load => load.Status != LoadStatus.Cancelled)
            .OrderBy(load => load.Reference)
            .Take(500)
            .ToList();

        EmbeddedGeofenceSnapshot snapshot;
        try
        {
            snapshot = await EmbeddedGeofenceEngine.BuildAsync(db, planningDate, GeofencePlanningMatch.PrepareLoads(loads), ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Geofence timing was unavailable for wallboard timing.");
            return Ok(new RunTimingResponse(planningDate, DateTimeOffset.UtcNow, false, []));
        }

        PlannerSourceMasterDataResolver? masterData = null;
        try
        {
            masterData = await PlannerSourceMasterDataResolver.CreateAsync(db, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Site Master resolution was unavailable while calculating final ETAs; embedded geofence coordinate fallback will remain active.");
            db.ChangeTracker.Clear();
        }

        var now = DateTimeOffset.UtcNow;
        var defaultIntermediateDwellMinutes = Math.Clamp(
            configuration.GetValue<int?>("Operations:DefaultIntermediateDwellMinutes") ?? 20,
            0,
            120);
        var records = new List<RunTimingRecord>();

        foreach (var load in loads)
        {
            var orderedStops = (load.Stops ?? []).OrderBy(stop => stop.Sequence).ToList();
            var visits = snapshot.Visits
                .Where(visit => visit.LoadId == load.Id)
                .OrderBy(visit => visit.EnteredAtUtc)
                .ToList();
            var completedStopIds = GeofencePlanningMatch.CompletedStopIds(load, visits);
            var completed = orderedStops.Count > 0 && orderedStops.All(stop => completedStopIds.Contains(stop.Id));
            var currentVisit = visits.LastOrDefault(visit => visit.ExitedAtUtc is null);
            var remainingStops = completed
                ? []
                : orderedStops.Where(stop => !completedStopIds.Contains(stop.Id)).ToList();
            var nextStop = remainingStops.FirstOrDefault();
            var lastDeparture = visits
                .Where(visit => visit.ExitedAtUtc is not null && visit.LoadStopId is not null && completedStopIds.Contains(visit.LoadStopId.Value))
                .OrderByDescending(visit => visit.ExitedAtUtc)
                .FirstOrDefault();

            // Before stop 1, Lake Lane is the authoritative run origin. Once a customer
            // stop has completed, the customer geofence departure becomes authoritative.
            var lakeLaneDeparture = completedStopIds.Count == 0
                ? OperationalRunOrigin.LakeLaneDepartureFor(snapshot, load)
                : null;
            var timingDeparture = lastDeparture ?? lakeLaneDeparture;

            DateTimeOffset? nextEtaUtc = null;
            string etaSource = "Unavailable";
            DateTimeOffset? finalEtaUtc = null;
            string finalEtaSource = "Unavailable";
            string? etaUnavailableStopName = null;
            string? etaUnavailableReason = null;

            if (!completed)
            {
                (decimal Longitude, decimal Latitude)? routeOrigin = null;
                DateTimeOffset? routeAnchorUtc = null;
                var routeStops = remainingStops;
                var finalContainsEstimate = false;

                if (currentVisit is not null)
                {
                    var activeStop = currentVisit.LoadStopId is Guid activeStopId
                        ? orderedStops.FirstOrDefault(stop => stop.Id == activeStopId)
                        : remainingStops.FirstOrDefault(stop => GeofencePlanningMatch.SamePhysicalSite(stop, currentVisit.Fence));

                    if (activeStop is not null)
                    {
                        routeOrigin = OperationalRunOrigin.FenceCentre(currentVisit.Fence);
                        routeStops = remainingStops.Where(stop => stop.Sequence > activeStop.Sequence).ToList();
                        var predictedDwell = PredictedDwellMinutes(snapshot, activeStop, defaultIntermediateDwellMinutes);
                        var elapsedMinutes = Math.Max(0, (now - currentVisit.EnteredAtUtc).TotalMinutes);
                        var remainingDwell = Math.Max(0, predictedDwell - elapsedMinutes);
                        routeAnchorUtc = now + TimeSpan.FromMinutes(remainingDwell);
                        finalContainsEstimate = remainingDwell > 0;
                    }
                    else
                    {
                        etaUnavailableStopName = nextStop?.Name;
                        etaUnavailableReason = $"Current geofence '{currentVisit.Fence.Name}' is not linked to a planned stop for this run.";
                    }
                }
                else if (timingDeparture?.ExitedAtUtc is DateTimeOffset departedAt)
                {
                    routeAnchorUtc = departedAt;
                    if (lastDeparture is not null)
                    {
                        var previousStop = lastDeparture.LoadStopId is Guid previousStopId
                            ? orderedStops.FirstOrDefault(stop => stop.Id == previousStopId)
                            : null;
                        routeOrigin = Origin(previousStop, lastDeparture.Fence, masterData);
                    }
                    else
                    {
                        routeOrigin = OperationalRunOrigin.FenceCentre(timingDeparture.Fence);
                    }
                }
                else if (remainingStops.Count > 0)
                {
                    etaUnavailableStopName = nextStop?.Name;
                    etaUnavailableReason = "No authoritative Lake Lane/customer geofence departure is available to anchor the remaining route.";
                }

                if (routeOrigin is not null && routeAnchorUtc is not null && routeStops.Count > 0)
                {
                    var cursor = routeOrigin.Value;
                    var cursorTime = routeAnchorUtc.Value;
                    var routeFailed = false;

                    for (var index = 0; index < routeStops.Count; index++)
                    {
                        var stop = routeStops[index];
                        var destination = OperationalStopCoordinates.Resolve(stop, masterData);
                        if (destination is null)
                        {
                            etaUnavailableStopName = stop.Name;
                            etaUnavailableReason = "No routable coordinate could be resolved from the plan, Site Master aliases/linked geofence, or approved DOT geofence.";
                            routeFailed = true;
                            break;
                        }

                        try
                        {
                            var route = await maps.TravelTimeEstimate(cursor, destination.Value, ct);
                            cursorTime += route.TravelTime;
                            finalContainsEstimate |= route.IsApproximate;

                            if (index == 0 && currentVisit is null)
                            {
                                nextEtaUtc = cursorTime;
                                etaSource = route.IsApproximate ? "GeofenceEstimated" : "Geofence";
                            }

                            finalEtaUtc = cursorTime;
                            cursor = destination.Value;

                            if (index < routeStops.Count - 1)
                            {
                                var dwellMinutes = PredictedDwellMinutes(snapshot, stop, defaultIntermediateDwellMinutes);
                                if (dwellMinutes > 0)
                                {
                                    cursorTime += TimeSpan.FromMinutes(dwellMinutes);
                                    finalContainsEstimate = true;
                                }
                            }
                        }
                        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or Azure.Identity.AuthenticationFailedException)
                        {
                            logger.LogDebug(exception, "Could not calculate geofence-anchored ETA for run {Run} stop {Stop}.", load.Reference, stop.Name);
                            etaUnavailableStopName = stop.Name;
                            etaUnavailableReason = "The route provider could not calculate this remaining leg.";
                            routeFailed = true;
                            break;
                        }
                    }

                    if (routeFailed)
                    {
                        finalEtaUtc = null;
                        finalEtaSource = "Unavailable";
                    }
                    else if (finalEtaUtc is not null)
                    {
                        finalEtaSource = finalContainsEstimate ? "GeofenceEstimated" : "Geofence";
                    }
                }
                else if (routeStops.Count > 0 && etaUnavailableReason is null)
                {
                    etaUnavailableStopName = nextStop?.Name;
                    etaUnavailableReason = routeOrigin is null
                        ? "The authoritative departure exists, but its route origin could not be resolved."
                        : "The remaining route does not yet have an authoritative timing anchor.";
                }
            }

            records.Add(new RunTimingRecord(
                load.Id,
                RunDisplayLabel.For(load),
                completed,
                nextStop?.Id,
                nextStop?.Sequence,
                nextStop?.Name,
                nextEtaUtc,
                etaSource,
                finalEtaUtc,
                finalEtaSource,
                timingDeparture?.ExitedAtUtc,
                currentVisit?.EnteredAtUtc,
                currentVisit?.Fence.Name,
                etaUnavailableStopName,
                etaUnavailableReason));
        }

        return Ok(new RunTimingResponse(planningDate, now, true, records));
    }

    private static (decimal Longitude, decimal Latitude)? Origin(
        LoadStop? stop,
        EmbeddedFence fence,
        PlannerSourceMasterDataResolver? masterData) =>
        stop is null
            ? OperationalRunOrigin.FenceCentre(fence)
            : OperationalStopCoordinates.Resolve(stop, masterData) ?? OperationalRunOrigin.FenceCentre(fence);

    private static int PredictedDwellMinutes(EmbeddedGeofenceSnapshot snapshot, LoadStop stop, int fallbackMinutes)
    {
        var samples = snapshot.Visits
            .Where(visit => visit.ExitedAtUtc is not null && GeofencePlanningMatch.SamePhysicalSite(stop, visit.Fence))
            .Select(visit => (int)Math.Round((visit.ExitedAtUtc!.Value - visit.EnteredAtUtc).TotalMinutes))
            .Where(minutes => minutes >= 2 && minutes <= 180)
            .OrderBy(minutes => minutes)
            .ToList();
        if (samples.Count == 0) return fallbackMinutes;
        var middle = samples.Count / 2;
        return samples.Count % 2 == 1
            ? samples[middle]
            : (int)Math.Round((samples[middle - 1] + samples[middle]) / 2d);
    }

    private static DateOnly UkOperatingDate(DateTimeOffset value)
    {
        try { return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, TimeZoneInfo.FindSystemTimeZoneById("Europe/London")).DateTime); }
        catch (TimeZoneNotFoundException) { return DateOnly.FromDateTime(value.UtcDateTime); }
    }
}

public sealed record RunTimingResponse(DateOnly PlanningDate, DateTimeOffset CalculatedAtUtc, bool GeofenceAvailable, IReadOnlyList<RunTimingRecord> Records);
public sealed record RunTimingRecord(
    Guid LoadId,
    string LoadReference,
    bool Completed,
    Guid? NextStopId,
    int? NextStopSequence,
    string? NextStopName,
    DateTimeOffset? NextEtaUtc,
    string EtaSource,
    DateTimeOffset? FinalEtaUtc,
    string FinalEtaSource,
    DateTimeOffset? PreviousGeofenceDepartureUtc,
    DateTimeOffset? DwellStartedAtUtc,
    string? CurrentGeofenceName,
    string? EtaUnavailableStopName,
    string? EtaUnavailableReason);
