using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
            var newEvents = records.Where(record => record.Latitude is not null && record.Longitude is not null).ToList();
            foreach (var record in newEvents)
            {
                var exists = await db.VehicleTrackingEvents.AnyAsync(item => item.ProviderName == "RoadTech Falcon" && item.ProviderEventId == record.ProviderEventId, cancellationToken);
                if (!exists) db.VehicleTrackingEvents.Add(new VehicleTrackingEvent
                {
                    ProviderName = "RoadTech Falcon", ProviderEventId = record.ProviderEventId, VehicleIdentifier = record.VehicleIdentifier,
                    EventTimeUtc = record.EventTimeUtc, Latitude = record.Latitude!.Value, Longitude = record.Longitude!.Value,
                    SpeedKph = record.SpeedKph, IsMoving = record.IsMoving, RawPayload = record.RawPayload, MatchStatus = "Received"
                });
            }
            if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(cancellationToken);

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
}

public sealed record DotTelemetryResponse(
    string Provider,
    DateTimeOffset RetrievedAtUtc,
    int RecordCount,
    IReadOnlyList<DotTelemetryRecord> Records);

public sealed record DotTelemetryRecord(string ProviderEventId, string VehicleIdentifier, DateTimeOffset EventTimeUtc, decimal? Latitude, decimal? Longitude, decimal? SpeedKph, bool? IsMoving, string? Status, string RawPayload)
{
    public static DotTelemetryRecord FromProvider(RoadTechTelemetryItem item)
    {
        var rawPayload = JsonSerializer.Serialize(item);
        var gps = item.DataGps;
        var latitude = ReadDecimal(gps, "latitude", "lat");
        var longitude = ReadDecimal(gps, "longitude", "lon", "lng");
        var speed = ReadDecimal(gps, "speedKph", "speed", "speedkmh");
        var timestamp = ReadString(gps, "eventTimeUtc", "timestamp", "time", "datetime");
        var eventTime = DateTimeOffset.TryParse(timestamp, out var parsed) ? parsed.ToUniversalTime() : DateTimeOffset.UtcNow;
        var moving = ReadBoolean(gps, "isMoving", "moving");
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawPayload)))[..24];
        return new DotTelemetryRecord(fingerprint, string.IsNullOrWhiteSpace(item.VehCode) ? item.VehRtid.ToString() : item.VehCode, eventTime, latitude, longitude, speed, moving, latitude is null || longitude is null ? "GPS coordinates unavailable" : "Received", rawPayload);
    }

    private static string? ReadString(JsonElement? source, params string[] names)
    {
        if (source is not { ValueKind: JsonValueKind.Object } objectValue) return null;
        foreach (var property in objectValue.EnumerateObject())
            if (names.Contains(property.Name, StringComparer.OrdinalIgnoreCase)) return property.Value.ToString();
        return null;
    }

    private static decimal? ReadDecimal(JsonElement? source, params string[] names) => decimal.TryParse(ReadString(source, names), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;
    private static bool? ReadBoolean(JsonElement? source, params string[] names) => bool.TryParse(ReadString(source, names), out var value) ? value : null;
}
