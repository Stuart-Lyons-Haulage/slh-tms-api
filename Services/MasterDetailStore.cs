using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using System.Text.RegularExpressions;

namespace Slh.Tms.Api.Services;

public static class MasterDetailStore
{
    private const string DriverType = "masterdetail:driver";
    private const string SiteType = "masterdetail:site";

    public static async Task SaveAsync(TmsDbContext db, string entityType, string key, string payloadJson, string? source, string? user, CancellationToken ct)
    {
        var type = $"masterdetail:{entityType.ToLowerInvariant()}";
        var idempotencyKey = $"{type}:{NormaliseKey(key)}";
        var row = await db.StagedImports.SingleOrDefaultAsync(item => item.IdempotencyKey == idempotencyKey, ct);
        if (row is null)
        {
            row = new StagedImport { EntityType = type, IdempotencyKey = idempotencyKey, PayloadJson = "{}", Source = source ?? "SLH master detail" };
            db.StagedImports.Add(row);
        }
        row.PayloadJson = payloadJson;
        row.Status = StagingStatus.Promoted;
        row.ReviewedAtUtc = DateTimeOffset.UtcNow;
        row.ReviewedBy = user;
        row.ReviewNote = "Full workbook detail retained in the audited register for legacy production columns.";
        await db.SaveChangesAsync(ct);
    }

    public static async Task EnrichDriversAsync(TmsDbContext db, IReadOnlyCollection<Driver> drivers, CancellationToken ct)
    {
        if (drivers.Count == 0) return;
        var byEmployee = drivers.ToDictionary(driver => NormaliseKey(driver.EmployeeNumber), StringComparer.OrdinalIgnoreCase);
        var rows = await db.StagedImports.AsNoTracking().Where(item => item.EntityType == DriverType && item.Status == StagingStatus.Promoted)
            .OrderByDescending(item => item.ReviewedAtUtc ?? item.ReceivedAtUtc).Take(5000).ToListAsync(ct);
        var applied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            try
            {
                using var document = JsonDocument.Parse(row.PayloadJson);
                var payload = document.RootElement;
                var employeeNumber = Text(payload, "employeeNumber") ?? Text(payload, "driverId") ?? Text(payload, "payrollNumber");
                if (string.IsNullOrWhiteSpace(employeeNumber)) continue;
                var normalised = NormaliseKey(employeeNumber);
                if (!applied.Add(normalised) || !byEmployee.TryGetValue(normalised, out var driver)) continue;
                driver.Coding = Text(payload, "coding");
                driver.AgencyName = Text(payload, "agencyName");
                driver.NorthEligible = Bool(payload, "northEligible");
                driver.PreloadEligible = Bool(payload, "preloadEligible");
                driver.Notes = Text(payload, "notes");
                var tachoMasterDriverId = Text(payload, "tachoMasterDriverId") ?? Text(payload, "tachomasterDriverId");
                if (!string.IsNullOrWhiteSpace(tachoMasterDriverId))
                    driver.TachoMasterDriverId = tachoMasterDriverId;

                var tachoCardNumber = Text(payload, "tachoCardNumber") ?? Text(payload, "cardNumber");
                if (!string.IsNullOrWhiteSpace(tachoCardNumber))
                    driver.TachoCardNumber = tachoCardNumber;

                driver.TachoDriveAvailableTodayMinutes = Int(payload, "tachoDriveAvailableTodayMinutes");
                driver.TachoDriveAvailableWeekMinutes = Int(payload, "tachoDriveAvailableWeekMinutes");
                driver.TachoWorkAvailableWeekMinutes = Int(payload, "tachoWorkAvailableWeekMinutes");
                driver.DrivingLicenceNumber = Text(payload, "drivingLicenceNumber") ?? Text(payload, "licenceNumber");
                driver.LicenceExpiry = DateOnly.TryParse(Text(payload, "licenceExpiry"), out var expiry) ? expiry : null;
                driver.LicenceStatus = Text(payload, "licenceStatus");
                driver.LastTachoSyncUtc = DateTimeOffset.TryParse(Text(payload, "lastTachoSyncUtc"), out var sync) ? sync : null;
            }
            catch (JsonException) { }
        }
    }

    public static async Task EnrichSitesAsync(TmsDbContext db, IReadOnlyCollection<Site> sites, CancellationToken ct)
    {
        if (sites.Count == 0) return;
        var byCode = sites
            .Where(site => !string.IsNullOrWhiteSpace(site.ExternalCode))
            .GroupBy(site => NormaliseKey(site.ExternalCode), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        var rows = await db.StagedImports.AsNoTracking().Where(item => item.EntityType == SiteType && item.Status == StagingStatus.Promoted)
            .OrderByDescending(item => item.ReviewedAtUtc ?? item.ReceivedAtUtc).Take(5000).ToListAsync(ct);
        var applied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            try
            {
                using var document = JsonDocument.Parse(row.PayloadJson);
                var payload = document.RootElement;
                var code = Text(payload, "externalCode") ?? Text(payload, "siteCode");
                if (string.IsNullOrWhiteSpace(code)) continue;
                var normalised = NormaliseKey(code);
                if (!applied.Add(normalised) || !byCode.TryGetValue(normalised, out var matchingSites)) continue;
                foreach (var site in matchingSites)
                {
                    site.Aliases = Text(payload, "aliases");
                    site.CustomField1 = Text(payload, "customField1");
                    site.CustomField2 = Text(payload, "customField2");
                    site.CustomField3 = Text(payload, "customField3");
                    site.Latitude = Decimal(payload, "latitude");
                    site.Longitude = Decimal(payload, "longitude");
                    site.OperationalRegion = Text(payload, "operationalRegion") ?? Text(payload, "region") ?? site.OperationalRegion;
                }
            }
            catch (JsonException) { }
        }
    }

    public static async Task<int> QuarantineFleetioPlaceholdersAsync(TmsDbContext db, CancellationToken ct)
    {
        var candidates = await db.Vehicles.Where(vehicle => vehicle.Active && vehicle.Registration.StartsWith("C")).ToListAsync(ct);
        var placeholders = candidates.Where(vehicle => Regex.IsMatch(vehicle.Registration, "^C\\d{5,}$", RegexOptions.IgnoreCase)).ToList();
        foreach (var vehicle in placeholders) vehicle.Active = false;
        if (placeholders.Count > 0) await db.SaveChangesAsync(ct);
        return placeholders.Count;
    }

    public static async Task<TrailerAliasMergeResult> MergeSlhTrailerAliasesAsync(TmsDbContext db, CancellationToken ct)
    {
        var trailers = await db.Trailers.ToListAsync(ct);
        var renamed = 0;
        var merged = 0;
        var reassignedLoads = 0;
        var reassignedMappings = 0;
        var reassignedAuditEntries = 0;

        for (var number = 1; number <= 88; number++)
        {
            var numericName = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var canonicalName = $"SLH{number}";
            var numeric = trailers.FirstOrDefault(item => string.Equals(item.TrailerNumber.Trim(), numericName, StringComparison.OrdinalIgnoreCase));
            var canonical = trailers.FirstOrDefault(item => string.Equals(item.TrailerNumber.Trim(), canonicalName, StringComparison.OrdinalIgnoreCase));

            if (numeric is null) continue;

            if (canonical is null)
            {
                numeric.TrailerNumber = canonicalName;
                db.MasterDataAudits.Add(new MasterDataAudit
                {
                    EntityType = "Trailer",
                    EntityId = numeric.Id,
                    Action = "Canonicalised",
                    ChangedBy = "startup-repair",
                    ChangesJson = JsonSerializer.Serialize(new { from = numericName, to = canonicalName })
                });
                renamed++;
                continue;
            }

            canonical.Type ??= numeric.Type;
            canonical.StandardCapacity ??= numeric.StandardCapacity;
            canonical.EuroCapacity ??= numeric.EuroCapacity;
            canonical.Active = canonical.Active || numeric.Active;

            var loads = await db.Loads.Where(load => load.TrailerId == numeric.Id).ToListAsync(ct);
            foreach (var load in loads) load.TrailerId = canonical.Id;
            reassignedLoads += loads.Count;

            var mappings = await db.IntegrationMappings
                .Where(mapping => mapping.TmsEntityType == "Trailer" && mapping.TmsEntityId == numeric.Id)
                .ToListAsync(ct);
            foreach (var mapping in mappings) mapping.TmsEntityId = canonical.Id;
            reassignedMappings += mappings.Count;

            var auditEntries = await db.MasterDataAudits
                .Where(audit => audit.EntityType == "Trailer" && audit.EntityId == numeric.Id)
                .ToListAsync(ct);
            foreach (var audit in auditEntries) audit.EntityId = canonical.Id;
            reassignedAuditEntries += auditEntries.Count;

            db.MasterDataAudits.Add(new MasterDataAudit
            {
                EntityType = "Trailer",
                EntityId = canonical.Id,
                Action = "MergedAlias",
                ChangedBy = "startup-repair",
                ChangesJson = JsonSerializer.Serialize(new
                {
                    canonical = canonicalName,
                    mergedAlias = numericName,
                    mergedTrailerId = numeric.Id,
                    loadsReassigned = loads.Count,
                    mappingsReassigned = mappings.Count,
                    auditEntriesReassigned = auditEntries.Count
                })
            });

            db.Trailers.Remove(numeric);
            trailers.Remove(numeric);
            merged++;
        }

        if (renamed > 0 || merged > 0 || reassignedLoads > 0 || reassignedMappings > 0 || reassignedAuditEntries > 0)
            await db.SaveChangesAsync(ct);

        return new TrailerAliasMergeResult(renamed, merged, reassignedLoads, reassignedMappings, reassignedAuditEntries);
    }

    private static string NormaliseKey(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private static string? Text(JsonElement payload, string name)
    {
        foreach (var property in payload.EnumerateObject())
            if (NormaliseKey(property.Name) == NormaliseKey(name))
                return property.Value.ValueKind switch
                {
                    JsonValueKind.String => string.IsNullOrWhiteSpace(property.Value.GetString()) ? null : property.Value.GetString()!.Trim(),
                    JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => property.Value.ToString(),
                    _ => null
                };
        return null;
    }
    private static bool? Bool(JsonElement payload, string name) => bool.TryParse(Text(payload, name), out var value) ? value : null;
    private static int? Int(JsonElement payload, string name) => int.TryParse(Text(payload, name), out var value) ? value : null;
    private static decimal? Decimal(JsonElement payload, string name) => decimal.TryParse(Text(payload, name), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;
}

public sealed record TrailerAliasMergeResult(int Renamed, int Merged, int LoadsReassigned, int MappingsReassigned, int AuditEntriesReassigned);
