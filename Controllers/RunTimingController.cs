using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Operational wallboard timing derived from geofence execution.
/// Dwell starts at first observation inside a geofence. Once a stop is departed,
/// the ETA for the next stop is anchored to that geofence departure rather than
/// continuously rebasing from the vehicle's current GPS position.
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
            var nextStop = completed ? null : orderedStops.FirstOrDefault(stop => !completedStopIds.Contains(stop.Id));
            var lastDeparture = visits
                .Where(visit => visit.ExitedAtUtc is not null && visit.LoadStopId is not null && completedStopIds.Contains(visit.LoadStopId.Value))
                .OrderByDescending(visit => visit.ExitedAtUtc)
                .FirstOrDefault();

            DateTimeOffset? nextEtaUtc = null;
            string etaSource = "Unavailable";
            if (!completed && currentVisit is null && lastDeparture?.ExitedAtUtc is DateTimeOffset departedAt && nextStop is not null &&
                nextStop.Longitude is not null && nextStop.Latitude is not null)
            {
                var previousStop = lastDeparture.LoadStopId is Guid previousStopId
                    ? orderedStops.FirstOrDefault(stop => stop.Id == previousStopId)
                    : null;
                var origin = Origin(previousStop, lastDeparture.Fence);
                if (origin is not null)
                {
                    try
                    {
                        var route = await maps.TravelTimeEstimate(origin.Value, (nextStop.Longitude.Value, nextStop.Latitude.Value), ct);
                        nextEtaUtc = departedAt + route.TravelTime;
                        etaSource = route.IsApproximate ? "GeofenceEstimated" : "Geofence";
                    }
                    catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or Azure.Identity.AuthenticationFailedException)
                    {
                        logger.LogDebug(exception, "Could not calculate geofence-anchored ETA for run {Run} stop {Stop}.", load.Reference, nextStop.Name);
                    }
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
                lastDeparture?.ExitedAtUtc,
                currentVisit?.EnteredAtUtc,
                currentVisit?.Fence.Name));
        }

        return Ok(new RunTimingResponse(planningDate, DateTimeOffset.UtcNow, true, records));
    }

    private static (decimal Longitude, decimal Latitude)? Origin(LoadStop? stop, EmbeddedFence fence)
    {
        if (stop?.Longitude is not null && stop.Latitude is not null)
            return (stop.Longitude.Value, stop.Latitude.Value);
        if (fence.Points.Count == 0) return null;
        return ((decimal)fence.Points.Average(point => point.Longitude), (decimal)fence.Points.Average(point => point.Latitude));
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
    DateTimeOffset? PreviousGeofenceDepartureUtc,
    DateTimeOffset? DwellStartedAtUtc,
    string? CurrentGeofenceName);
