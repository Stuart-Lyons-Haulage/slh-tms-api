using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
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
            var repair = await RepairLinksInternal(ct);
            return Ok(new
            {
                result.Inserted,
                result.Updated,
                result.SiteMatched,
                relinked = repair.Relinked,
                remainingUnlinked = repair.Unlinked,
                invalidPolygons = repair.Invalid,
                importedAtUtc = DateTimeOffset.UtcNow
            });
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
        var repair = await RepairLinksInternal(ct);
        return Ok(new
        {
            supplied = document.RootElement.GetArrayLength(), inserted, updated, siteMatched = matched,
            relinked = repair.Relinked, remainingUnlinked = repair.Unlinked, invalidPolygons = repair.Invalid,
            importedAtUtc = DateTimeOffset.UtcNow
        });
    }

    [HttpPost("repair-links")]
    [Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> RepairLinks(CancellationToken ct)
    {
        var repair = await RepairLinksInternal(ct);
        return Ok(new
        {
            total = repair.Total,
            linked = repair.Linked,
            relinked = repair.Relinked,
            unlinked = repair.Unlinked,
            validPolygons = repair.Valid,
            invalidPolygons = repair.Invalid,
            repairedAtUtc = DateTimeOffset.UtcNow
        });
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        await GeofenceRunProgression.EnsureSchemaAsync(db, ct);
        // Relink opportunistically on reads so historical imports recover without requiring a re-upload.
        await RepairLinksInternal(ct);
        var rows = await db.SiteGeofences.AsNoTracking().OrderBy(x => x.Category).ThenBy(x => x.Name).ToListAsync(ct);
        var result = rows.Select(x => new
        {
            x.Id,
            x.Name,
            x.Category,
            x.MaxWaitMinutes,
            x.CategoryMaxWaitMinutes,
            x.SiteNumber,
            x.SiteId,
            x.Active,
            polygonValid = PolygonIsValid(x.PolygonJson),
            geofenceAvailable = x.Active && PolygonIsValid(x.PolygonJson),
            siteLinked = x.SiteId != null,
            validationStatus = !PolygonIsValid(x.PolygonJson) ? "Invalid" : x.SiteId == null ? "Unlinked" : "Valid"
        }).ToList();
        return Ok(new { count = result.Count, records = result });
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

    private async Task<RepairResult> RepairLinksInternal(CancellationToken ct)
    {
        await GeofenceRunProgression.EnsureSchemaAsync(db, ct);
        var sites = await db.Sites.AsNoTracking().Where(x => x.Active).ToListAsync(ct);
        var fences = await db.SiteGeofences.Where(x => x.Active).ToListAsync(ct);
        var siteIds = sites.Select(x => x.Id).ToHashSet();
        var relinked = 0;
        var linked = 0;
        var valid = 0;
        var invalid = 0;

        foreach (var fence in fences)
        {
            if (PolygonIsValid(fence.PolygonJson)) valid++; else invalid++;

            if (fence.SiteId is Guid existingId && siteIds.Contains(existingId))
            {
                linked++;
                continue;
            }

            var match = MatchSite(fence, sites);
            if (match is null)
            {
                if (fence.SiteId is not null) fence.SiteId = null;
                continue;
            }

            if (fence.SiteId != match.Id)
            {
                fence.SiteId = match.Id;
                fence.UpdatedAtUtc = DateTimeOffset.UtcNow;
                relinked++;
            }
            linked++;
        }

        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);
        return new RepairResult(fences.Count, linked, relinked, fences.Count - linked, valid, invalid);
    }

    private static Site? MatchSite(SiteGeofence fence, IReadOnlyCollection<Site> sites)
    {
        var siteNumber = Normalize(fence.SiteNumber);
        var fenceName = Normalize(fence.Name);

        if (siteNumber.Length > 0)
        {
            var byCode = sites.FirstOrDefault(site => Normalize(site.ExternalCode) == siteNumber);
            if (byCode is not null) return byCode;

            var byNameOrDriver = sites.FirstOrDefault(site =>
                Normalize(site.Name) == siteNumber || Normalize(site.DriverTextName) == siteNumber);
            if (byNameOrDriver is not null) return byNameOrDriver;
        }

        var exact = sites.FirstOrDefault(site => Normalize(site.Name) == fenceName || Normalize(site.DriverTextName) == fenceName);
        if (exact is not null) return exact;

        return sites.FirstOrDefault(site =>
        {
            var name = Normalize(site.Name);
            var driver = Normalize(site.DriverTextName);
            return name.Length >= 5 && (fenceName.Contains(name, StringComparison.Ordinal) || name.Contains(fenceName, StringComparison.Ordinal))
                || driver.Length >= 5 && (fenceName.Contains(driver, StringComparison.Ordinal) || driver.Contains(fenceName, StringComparison.Ordinal));
        });
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

        if (element.GetArrayLength() >= 2 && element[0].ValueKind == JsonValueKind.Number && element[1].ValueKind == JsonValueKind.Number)
            return 1;

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

    private static string Normalize(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static string? NullableText(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined ? value.ToString() : null;

    private static int? NullableInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt32(out var number) ? number : null;

    private sealed record RepairResult(int Total, int Linked, int Relinked, int Unlinked, int Valid, int Invalid);
}
