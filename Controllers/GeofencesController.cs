using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/geofences")]
[Authorize]
public sealed class GeofencesController(TmsDbContext db) : ControllerBase
{
    [HttpPost("import-falcon")]
    [Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> ImportFalcon([FromBody] JsonElement payload, CancellationToken ct)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return UnprocessableEntity(new { error = "Expected a Falcon geofence JSON object." });
        try
        {
            var result = await GeofenceRunProgression.ImportFalconAsync(db, payload, ct);
            return Ok(new { result.Inserted, result.Updated, result.SiteMatched, importedAtUtc = DateTimeOffset.UtcNow });
        }
        catch (ArgumentException exception)
        {
            return UnprocessableEntity(new { error = exception.Message });
        }
    }

    [HttpPost("import-slh-seed")]
    [Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> ImportSlhSeed(CancellationToken ct)
    {
        using var document = JsonDocument.Parse(GeofenceSeedPayload.Json);
        var inserted = 0; var updated = 0; var matched = 0;
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
            inserted += result.Inserted; updated += result.Updated; matched += result.SiteMatched;
        }
        return Ok(new { supplied = document.RootElement.GetArrayLength(), inserted, updated, siteMatched = matched, importedAtUtc = DateTimeOffset.UtcNow });
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        await GeofenceRunProgression.EnsureSchemaAsync(db, ct);
        var rows = await db.SiteGeofences.AsNoTracking().OrderBy(x => x.Category).ThenBy(x => x.Name)
            .Select(x => new { x.Id, x.Name, x.Category, x.MaxWaitMinutes, x.CategoryMaxWaitMinutes, x.SiteId, x.Active })
            .ToListAsync(ct);
        return Ok(new { count = rows.Count, records = rows });
    }

    [HttpGet("visits")]
    public async Task<IActionResult> Visits([FromQuery] DateOnly? date, CancellationToken ct)
    {
        await GeofenceRunProgression.EnsureSchemaAsync(db, ct);
        var day = date ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var start = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var end = start.AddDays(1);
        var rows = await db.GeofenceVisits.AsNoTracking()
            .Where(x => x.EnteredAtUtc >= start && x.EnteredAtUtc < end)
            .OrderByDescending(x => x.EnteredAtUtc).Take(1000)
            .Select(x => new { x.Id, x.GeofenceId, x.LoadId, x.LoadStopId, x.VehicleId, x.VehicleIdentifier, x.EnteredAtUtc, x.ConfirmedAtUtc, x.ExitedAtUtc, x.DwellMinutes, x.Status, x.StatusReason })
            .ToListAsync(ct);
        return Ok(new { date = day, count = rows.Count, records = rows });
    }

    private static string? NullableText(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined ? value.ToString() : null;

    private static int? NullableInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt32(out var number) ? number : null;
}
