using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/geofence-recovery")]
[Authorize]
public sealed class GeofenceRecoveryController(TmsDbContext db, ILogger<GeofenceRecoveryController> logger) : ControllerBase
{
    [HttpPost("ensure"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Ensure(CancellationToken ct)
    {
        try
        {
            await GeofenceRuntimeRepair.EnsureAsync(db, ct);
            await GeofenceRunProgression.EnsureSchemaAsync(db, ct);

            var activeBefore = await db.SiteGeofences.AsNoTracking().CountAsync(x => x.Active, ct);
            var seeded = 0;
            var matched = 0;

            if (activeBefore == 0)
            {
                using var document = JsonDocument.Parse(GeofenceSeedPayload.Json);
                foreach (var record in document.RootElement.EnumerateArray())
                {
                    var payload = JsonSerializer.SerializeToElement(new
                    {
                        format = "falcon.geofence",
                        version = 1,
                        category = NullableText(record, "category"),
                        category_max_wait_time = NullableInt(record, "category_max_wait_time"),
                        geofences = new[]
                        {
                            new
                            {
                                name = NullableText(record, "name"),
                                max_wait_time = NullableInt(record, "max_wait_time"),
                                pending_entry_minutes = NullableInt(record, "pending_entry_minutes") ?? 0,
                                pending_exit_minutes = NullableInt(record, "pending_exit_minutes") ?? 0,
                                site_no = NullableText(record, "site_no"),
                                points = record.GetProperty("points")
                            }
                        }
                    });
                    var result = await GeofenceRunProgression.ImportFalconAsync(db, payload, ct);
                    seeded += result.Inserted;
                    matched += result.SiteMatched;
                }
            }

            var activeAfter = await db.SiteGeofences.AsNoTracking().CountAsync(x => x.Active, ct);
            var visitsBefore = await db.GeofenceVisits.AsNoTracking().CountAsync(ct);
            var replayed = 0;

            if (activeAfter > 0 && visitsBefore == 0)
            {
                var since = DateTimeOffset.UtcNow.AddHours(-24);
                var events = await db.VehicleTrackingEvents.AsNoTracking()
                    .Where(x => x.EventTimeUtc >= since)
                    .OrderBy(x => x.EventTimeUtc)
                    .Take(5000)
                    .ToListAsync(ct);

                if (events.Count > 0)
                {
                    var records = events.Select(x => new DotTelemetryRecord(
                        x.ProviderEventId,
                        x.VehicleIdentifier,
                        x.EventTimeUtc,
                        x.Latitude,
                        x.Longitude,
                        x.SpeedKph,
                        x.IgnitionOn,
                        x.IsMoving,
                        x.MatchStatus,
                        x.RawPayload)).ToList();

                    await GeofenceRunProgression.ProcessTelemetryAsync(db, records, ct);
                    replayed = events.Count;
                    await GeofenceVisitRepair.RepairRecentAsync(db, ct);
                }
            }

            var visitsAfter = await db.GeofenceVisits.AsNoTracking().CountAsync(ct);
            var linked = await db.SiteGeofences.AsNoTracking().CountAsync(x => x.Active && x.SiteId != null, ct);

            logger.LogInformation(
                "Geofence recovery complete. Active {ActiveBefore}->{ActiveAfter}, seeded {Seeded}, linked {Linked}, visits {VisitsBefore}->{VisitsAfter}, replayed {Replayed} tracking events.",
                activeBefore, activeAfter, seeded, linked, visitsBefore, visitsAfter, replayed);

            return Ok(new
            {
                ready = activeAfter > 0,
                activeBefore,
                activeAfter,
                seeded,
                siteMatched = matched,
                linked,
                visitsBefore,
                visitsAfter,
                replayedTrackingEvents = replayed,
                recoveredAtUtc = DateTimeOffset.UtcNow
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Automatic geofence recovery failed.");
            db.ChangeTracker.Clear();
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                ready = false,
                code = "geofence_recovery_failed",
                message = exception.GetBaseException().Message,
                traceId = HttpContext.TraceIdentifier
            });
        }
    }

    private static string? NullableText(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined ? value.ToString() : null;

    private static int? NullableInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt32(out var number) ? number : null;
}
