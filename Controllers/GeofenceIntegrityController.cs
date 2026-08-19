using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/geofence-integrity")]
[Authorize]
public sealed class GeofenceIntegrityController(TmsDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        try
        {
            await GeofenceRuntimeRepair.EnsureAsync(db, ct);
            await GeofenceRunProgression.EnsureSchemaAsync(db, ct);

            var now = DateTimeOffset.UtcNow;
            var geofences = await db.SiteGeofences.AsNoTracking().OrderBy(x => x.Category).ThenBy(x => x.Name).ToListAsync(ct);
            var active = geofences.Where(x => x.Active).ToList();
            var valid = active.Where(x => PolygonIsValid(x.PolygonJson)).ToList();
            var linked = valid.Count(x => x.SiteId != null);

            var latestTracking = await db.VehicleTrackingEvents.AsNoTracking()
                .OrderByDescending(x => x.EventTimeUtc)
                .Select(x => new
                {
                    x.VehicleIdentifier,
                    x.EventTimeUtc,
                    x.Latitude,
                    x.Longitude,
                    x.ProviderName
                })
                .FirstOrDefaultAsync(ct);

            var latestVisit = await db.GeofenceVisits.AsNoTracking()
                .OrderByDescending(x => x.EnteredAtUtc)
                .Select(x => new
                {
                    x.GeofenceId,
                    x.VehicleIdentifier,
                    x.EnteredAtUtc,
                    x.ConfirmedAtUtc,
                    x.ExitedAtUtc,
                    x.LoadId,
                    x.LoadStopId,
                    x.DwellMinutes,
                    x.Status,
                    x.StatusReason
                })
                .FirstOrDefaultAsync(ct);

            var latestConfirmed = await db.GeofenceVisits.AsNoTracking()
                .Where(x => x.ConfirmedAtUtc != null)
                .OrderByDescending(x => x.ConfirmedAtUtc)
                .Select(x => new
                {
                    x.GeofenceId,
                    x.VehicleIdentifier,
                    x.EnteredAtUtc,
                    x.ConfirmedAtUtc,
                    x.ExitedAtUtc,
                    x.LoadId,
                    x.LoadStopId,
                    x.DwellMinutes,
                    x.Status
                })
                .FirstOrDefaultAsync(ct);

            var since = now.AddHours(-24);
            var recentVisits = await db.GeofenceVisits.AsNoTracking()
                .Where(x => x.EnteredAtUtc >= since)
                .OrderByDescending(x => x.EnteredAtUtc)
                .Take(20)
                .Select(x => new
                {
                    x.GeofenceId,
                    x.VehicleIdentifier,
                    x.EnteredAtUtc,
                    x.ConfirmedAtUtc,
                    x.ExitedAtUtc,
                    x.LoadId,
                    x.LoadStopId,
                    x.DwellMinutes,
                    x.Status
                })
                .ToListAsync(ct);

            var nameById = geofences.ToDictionary(x => x.Id, x => x.Name);
            var trackingAgeMinutes = latestTracking is null ? (double?)null : Math.Max(0, (now - latestTracking.EventTimeUtc).TotalMinutes);
            var trackingFresh = trackingAgeMinutes is not null && trackingAgeMinutes <= 15;
            var engineReady = valid.Count > 0;
            var planningLinkReady = linked > 0;

            return Ok(new
            {
                checkedAtUtc = now,
                engineReady,
                planningLinkReady,
                liveRunProgressionReady = engineReady && planningLinkReady && trackingFresh,
                trackingFresh,
                trackingAgeMinutes,
                geofences = new
                {
                    total = geofences.Count,
                    active = active.Count,
                    valid = valid.Count,
                    linked,
                    unlinked = valid.Count - linked,
                    invalid = active.Count - valid.Count
                },
                records = geofences.Select(x => new
                {
                    x.Id,
                    x.Name,
                    x.Category,
                    x.CategoryMaxWaitMinutes,
                    x.MaxWaitMinutes,
                    x.PendingEntryMinutes,
                    x.PendingExitMinutes,
                    x.SiteNumber,
                    x.SiteId,
                    x.PolygonJson,
                    x.Active,
                    polygonValid = PolygonIsValid(x.PolygonJson),
                    geofenceAvailable = x.Active && PolygonIsValid(x.PolygonJson),
                    siteLinked = x.SiteId != null,
                    validationStatus = !PolygonIsValid(x.PolygonJson) ? "Invalid" : x.SiteId == null ? "Unlinked" : "Valid"
                }),
                latestTracking,
                latestGeofenceHit = latestVisit is null ? null : new
                {
                    geofenceName = nameById.GetValueOrDefault(latestVisit.GeofenceId, "Unknown / legacy geofence"),
                    latestVisit.VehicleIdentifier,
                    latestVisit.EnteredAtUtc,
                    latestVisit.ConfirmedAtUtc,
                    latestVisit.ExitedAtUtc,
                    latestVisit.LoadId,
                    latestVisit.LoadStopId,
                    latestVisit.DwellMinutes,
                    latestVisit.Status,
                    latestVisit.StatusReason
                },
                latestConfirmedHit = latestConfirmed is null ? null : new
                {
                    geofenceName = nameById.GetValueOrDefault(latestConfirmed.GeofenceId, "Unknown / legacy geofence"),
                    latestConfirmed.VehicleIdentifier,
                    latestConfirmed.EnteredAtUtc,
                    latestConfirmed.ConfirmedAtUtc,
                    latestConfirmed.ExitedAtUtc,
                    latestConfirmed.LoadId,
                    latestConfirmed.LoadStopId,
                    latestConfirmed.DwellMinutes,
                    latestConfirmed.Status
                },
                recentHits = recentVisits.Select(x => new
                {
                    geofenceName = nameById.GetValueOrDefault(x.GeofenceId, "Unknown / legacy geofence"),
                    x.VehicleIdentifier,
                    x.EnteredAtUtc,
                    x.ConfirmedAtUtc,
                    x.ExitedAtUtc,
                    x.LoadId,
                    x.LoadStopId,
                    x.DwellMinutes,
                    x.Status
                })
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                checkedAtUtc = DateTimeOffset.UtcNow,
                engineReady = false,
                liveRunProgressionReady = false,
                code = "geofence_integrity_unavailable",
                message = exception.GetBaseException().Message
            });
        }
    }

    private static bool PolygonIsValid(string? polygonJson)
    {
        if (string.IsNullOrWhiteSpace(polygonJson)) return false;
        try
        {
            using var document = JsonDocument.Parse(polygonJson);
            return CountCoordinatePoints(document.RootElement) >= 3;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static int CountCoordinatePoints(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (HasCoordinatePair(element)) return 1;
            var total = 0;
            foreach (var property in element.EnumerateObject()) total += CountCoordinatePoints(property.Value);
            return total;
        }
        if (element.ValueKind != JsonValueKind.Array) return 0;
        if (element.GetArrayLength() >= 2 && element[0].ValueKind == JsonValueKind.Number && element[1].ValueKind == JsonValueKind.Number) return 1;
        var count = 0;
        foreach (var child in element.EnumerateArray()) count += CountCoordinatePoints(child);
        return count;
    }

    private static bool HasCoordinatePair(JsonElement element)
    {
        static bool HasNumber(JsonElement e, string name) => e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number;
        var hasLat = HasNumber(element, "latitude") || HasNumber(element, "lat") || HasNumber(element, "y");
        var hasLon = HasNumber(element, "longitude") || HasNumber(element, "lng") || HasNumber(element, "lon") || HasNumber(element, "x");
        return hasLat && hasLon;
    }
}
