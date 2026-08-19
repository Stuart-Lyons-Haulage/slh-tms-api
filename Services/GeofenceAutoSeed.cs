using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;

namespace Slh.Tms.Api.Services;

public static class GeofenceAutoSeed
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<GeofenceAutoSeedResult> EnsureAsync(TmsDbContext db, CancellationToken ct)
    {
        await GeofenceRuntimeRepair.EnsureAsync(db, ct);
        await GeofenceRunProgression.EnsureSchemaAsync(db, ct);

        var active = await db.SiteGeofences.AsNoTracking().CountAsync(x => x.Active, ct);
        if (active > 0) return new GeofenceAutoSeedResult(false, active, 0, 0);

        await Gate.WaitAsync(ct);
        try
        {
            active = await db.SiteGeofences.AsNoTracking().CountAsync(x => x.Active, ct);
            if (active > 0) return new GeofenceAutoSeedResult(false, active, 0, 0);

            using var document = JsonDocument.Parse(GeofenceSeedPayload.Json);
            var inserted = 0;
            var updated = 0;
            var matched = 0;

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
                inserted += result.Inserted;
                updated += result.Updated;
                matched += result.SiteMatched;
            }

            active = await db.SiteGeofences.AsNoTracking().CountAsync(x => x.Active, ct);
            return new GeofenceAutoSeedResult(true, active, inserted + updated, matched);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static string? NullableText(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined ? value.ToString() : null;

    private static int? NullableInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt32(out var number) ? number : null;
}

public sealed record GeofenceAutoSeedResult(bool Seeded, int Active, int Imported, int SiteMatched);
