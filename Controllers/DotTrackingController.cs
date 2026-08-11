using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Secure, read-only RoadTech Falcon telemetry preview.
/// It does not create planning records or alter vehicle master data.
/// </summary>
[ApiController]
[Route("api/v1/tracking/dot")]
[Authorize(Policy = "TmsWrite")]
public sealed class DotTrackingController(
    DotTrackingClient trackingClient,
    TmsDbContext db,
    DotTrackingTelemetryStore telemetryStore,
    ILogger<DotTrackingController> logger) : ControllerBase
{
    [HttpGet("telemetry")]
    [ProducesResponseType(typeof(DotTelemetryResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<DotTelemetryResponse>> GetCurrentTelemetry(CancellationToken cancellationToken)
    {
        try
        {
            var telemetry = await trackingClient.GetLatestVehicleEventsAsync(cancellationToken);
            var records = telemetry.Select(DotTelemetryRecord.FromProvider).ToList();
            await telemetryStore.PersistAsync(records, cancellationToken);

            return Ok(new DotTelemetryResponse(
                "RoadTech Falcon",
                DateTimeOffset.UtcNow,
                records.Count,
                records));
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "DOT Tracking configuration is not ready.");
            return Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "DOT Tracking is not configured",
                detail: "The provider settings are incomplete or the integration is disabled.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "RoadTech Falcon telemetry request failed.");
            return Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "DOT Tracking is unavailable",
                detail: "The provider could not be reached or rejected the request.");
        }
    }

    [HttpGet("history")]
    public async Task<IActionResult> GetHistory(
        [FromQuery] DateOnly? date,
        [FromQuery] string? vehicle,
        [FromQuery] int take = 1000,
        CancellationToken cancellationToken = default)
    {
        var selectedDate = date ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var from = new DateTimeOffset(selectedDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        var to = from.AddDays(1);
        var query = db.VehicleTrackingEvents.AsNoTracking()
            .Where(item => item.EventTimeUtc >= from && item.EventTimeUtc < to);
        if (!string.IsNullOrWhiteSpace(vehicle))
            query = query.Where(item => item.VehicleIdentifier == vehicle.Trim());

        var records = await query.OrderBy(item => item.EventTimeUtc).Take(Math.Clamp(take, 1, 5000)).Select(item => new
        {
            item.VehicleIdentifier, item.EventTimeUtc, item.Latitude, item.Longitude, item.SpeedKph, item.IsMoving, status = item.MatchStatus
        }).ToListAsync(cancellationToken);
        return Ok(new { provider = "RoadTech Falcon", date = selectedDate, recordCount = records.Count, records });
    }

    [HttpGet("fleet-status")]
    public async Task<ActionResult<FleetStatusResponse>> GetFleetStatus(CancellationToken cancellationToken)
    {
        var vehicles = await db.Vehicles.AsNoTracking().Where(vehicle => vehicle.Active).OrderBy(vehicle => vehicle.Registration).ToListAsync(cancellationToken);
        var liveStatuses = await db.VehicleLiveStatuses.AsNoTracking().ToListAsync(cancellationToken);
        var latestByIdentifier = liveStatuses.GroupBy(status => NormaliseIdentifier(status.VehicleIdentifier)).ToDictionary(group => group.Key, group => group.OrderByDescending(status => status.LastEventTimeUtc).First());
        var now = DateTimeOffset.UtcNow;
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var assignments = await db.Loads.AsNoTracking().Where(load => load.PlanningDate == today && load.VehicleId != null && load.Status != LoadStatus.Cancelled && load.Status != LoadStatus.Completed).ToListAsync(cancellationToken);
        var driverIds = assignments.Where(load => load.DriverId != null).Select(load => load.DriverId!.Value).Distinct().ToList();
        var drivers = await db.Drivers.AsNoTracking().Where(driver => driverIds.Contains(driver.Id)).ToDictionaryAsync(driver => driver.Id, cancellationToken);
        var records = vehicles.Select(vehicle =>
        {
            var keys = new[] { vehicle.Registration, vehicle.FleetNumber, vehicle.Abbreviation }.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => NormaliseIdentifier(value!));
            var live = keys.Select(key => latestByIdentifier.GetValueOrDefault(key)).Where(status => status is not null).OrderByDescending(status => status!.LastEventTimeUtc).FirstOrDefault();
            var age = live is null ? (TimeSpan?)null : now - live.LastEventTimeUtc;
            var condition = DetermineCondition(live, now);
            var assignment = assignments.Where(load => load.VehicleId == vehicle.Id).OrderByDescending(load => LoadPriority(load.Status)).FirstOrDefault();
            var driverName = assignment?.DriverId is Guid driverId && drivers.TryGetValue(driverId, out var driver) ? driver.DisplayName : null;
            return new FleetVehicleStatus(vehicle.Id, vehicle.Registration, vehicle.FleetNumber, live?.VehicleIdentifier, condition, live?.LastEventTimeUtc, live?.IgnitionOn, live?.IsMoving, live?.SpeedKph, live?.Latitude, live?.Longitude, age is null ? null : (int)Math.Max(0, age.Value.TotalMinutes), assignment?.Reference, assignment?.Status.ToString(), driverName);
        }).ToList();
        return Ok(new FleetStatusResponse("RoadTech Falcon", now, records.Count, records.Count(record => record.Condition is "Moving" or "Started"), records.Count(record => record.Condition is "NotSignedOn" or "Stale"), records));
    }

    private static string NormaliseIdentifier(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static int LoadPriority(LoadStatus status) => status switch { LoadStatus.InProgress => 4, LoadStatus.Dispatched => 3, LoadStatus.Planned => 2, LoadStatus.Draft => 1, _ => 0 };
    private static string DetermineCondition(VehicleLiveStatus? live, DateTimeOffset now)
    {
        if (live is null || live.LastEventTimeUtc.UtcDateTime.Date < now.UtcDateTime.Date) return "NotSignedOn";
        if (now - live.LastEventTimeUtc > TimeSpan.FromMinutes(30)) return "Stale";
        if (live.IsMoving == true || live.SpeedKph.GetValueOrDefault() > 3) return "Moving";
        if (live.IgnitionOn == true) return "Started";
        if (live.IgnitionOn == false) return "Stationary";
        return "SignedOn";
    }
}

public sealed record DotTelemetryResponse(
    string Provider,
    DateTimeOffset RetrievedAtUtc,
    int RecordCount,
    IReadOnlyList<DotTelemetryRecord> Records);

public sealed record FleetStatusResponse(string Provider, DateTimeOffset RetrievedAtUtc, int VehicleCount, int ReadyCount, int AttentionCount, IReadOnlyList<FleetVehicleStatus> Vehicles);
public sealed record FleetVehicleStatus(Guid VehicleId, string Registration, string? FleetNumber, string? TrackingIdentifier, string Condition, DateTimeOffset? LastEventTimeUtc, bool? IgnitionOn, bool? IsMoving, decimal? SpeedKph, decimal? Latitude, decimal? Longitude, int? AgeMinutes, string? LoadReference, string? LoadStatus, string? DriverName);
