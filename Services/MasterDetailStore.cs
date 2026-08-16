using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using System.Text.RegularExpressions;

namespace Slh.Tms.Api.Services;

public static class MasterDetailStore
{
    private const string DriverType = "masterdetail:driver";

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
                driver.TachoMasterDriverId = Text(payload, "tachoMasterDriverId") ?? Text(payload, "tachomasterDriverId");
                driver.DrivingLicenceNumber = Text(payload, "drivingLicenceNumber") ?? Text(payload, "licenceNumber");
                driver.LicenceExpiry = DateOnly.TryParse(Text(payload, "licenceExpiry"), out var expiry) ? expiry : null;
                driver.LicenceStatus = Text(payload, "licenceStatus");
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
}
