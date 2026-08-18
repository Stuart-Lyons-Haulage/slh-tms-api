using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public sealed class AssistantSafeFixService(
    TmsDbContext db,
    AzureMapsRouteClient maps,
    ILogger<AssistantSafeFixService> logger)
{
    public async Task<SafeFixResult> Apply(CancellationToken ct)
    {
        var changes = new List<string>();
        var skipped = new List<string>();

        await NormaliseVehicleRegistrations(changes, skipped, ct);
        await RepairSites(changes, skipped, ct);
        await NormaliseCustomerEmails(changes, ct);

        if (changes.Count > 0)
        {
            db.StagedImports.Add(new StagedImport
            {
                EntityType = "assistantfix",
                IdempotencyKey = $"assistantfix:{Guid.NewGuid():N}",
                PayloadJson = JsonSerializer.Serialize(new { changes, skipped, appliedAtUtc = DateTimeOffset.UtcNow }),
                Source = "SLH Assistant safe validation fixes",
                Status = StagingStatus.Promoted,
                ReviewedAtUtc = DateTimeOffset.UtcNow,
                ReviewNote = $"Applied {changes.Count} deterministic low-risk validation fixes from the portal."
            });
        }

        await db.SaveChangesAsync(ct);
        return new SafeFixResult(changes.Count, skipped.Count, changes.Take(100).ToList(), skipped.Take(100).ToList());
    }

    private async Task NormaliseVehicleRegistrations(List<string> changes, List<string> skipped, CancellationToken ct)
    {
        var vehicles = await db.Vehicles.ToListAsync(ct);
        var counts = vehicles.GroupBy(x => TmsAssistantService.NormaliseRegistration(x.Registration)).ToDictionary(x => x.Key, x => x.Count());
        foreach (var vehicle in vehicles.Where(x => x.Active))
        {
            var normalised = TmsAssistantService.NormaliseRegistration(vehicle.Registration);
            if (vehicle.Registration == normalised) continue;
            if (counts[normalised] > 1)
            {
                skipped.Add($"{vehicle.Registration}: normalised registration would conflict with another vehicle.");
                continue;
            }
            changes.Add($"Vehicle {vehicle.Registration} normalised to {normalised}.");
            vehicle.Registration = normalised;
        }
    }

    private async Task RepairSites(List<string> changes, List<string> skipped, CancellationToken ct)
    {
        var sites = await db.Sites.Where(x => x.Active).ToListAsync(ct);
        await MasterDetailStore.EnrichSitesAsync(db, sites, ct);

        foreach (var site in sites.Where(x => !string.IsNullOrWhiteSpace(x.CollectionAddress) && string.IsNullOrWhiteSpace(x.MapLink)))
        {
            site.MapLink = $"https://www.google.com/maps/search/?api=1&query={Uri.EscapeDataString(site.CollectionAddress!.Trim())}";
            await MasterDetailStore.SaveAsync(db, "site", site.ExternalCode, JsonSerializer.Serialize(site), "SLH Assistant map-link repair", null, ct);
            changes.Add($"Created a driver map link for {site.Name}.");
        }

        var geocoded = 0;
        foreach (var site in sites.Where(x => !string.IsNullOrWhiteSpace(x.CollectionAddress) && (x.Latitude is null || x.Longitude is null)))
        {
            if (geocoded >= 20)
            {
                skipped.Add($"{site.Name}: map point left for the next Assistant run (20-site safety limit).");
                continue;
            }
            try
            {
                var coordinate = await maps.SearchCoordinate(site.CollectionAddress!, ct);
                if (coordinate is null)
                {
                    skipped.Add($"{site.Name}: Azure Maps could not confidently locate this address.");
                    continue;
                }
                site.Latitude = coordinate.Value.Latitude;
                site.Longitude = coordinate.Value.Longitude;
                await MasterDetailStore.SaveAsync(db, "site", site.ExternalCode, JsonSerializer.Serialize(site), "SLH Assistant Azure Maps validation", null, ct);
                changes.Add($"Added map point {site.Latitude:F6}, {site.Longitude:F6} for {site.Name}.");
                geocoded++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Assistant could not geocode site {SiteCode}.", site.ExternalCode);
                skipped.Add($"{site.Name}: map point lookup failed and no coordinate was changed.");
            }
        }

        // Only auto-consolidate duplicates where both the normalised site name and
        // the normalised full collection address are identical. Ambiguous matches
        // remain review-only. Records are deactivated, never deleted.
        var exactGroups = sites
            .Where(x => !string.IsNullOrWhiteSpace(x.Name) && !string.IsNullOrWhiteSpace(x.CollectionAddress))
            .GroupBy(x => $"{Normalise(x.Name)}|{Normalise(x.CollectionAddress)}", StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var group in exactGroups)
        {
            var ordered = group.OrderByDescending(Completeness).ThenBy(x => x.ExternalCode).ToList();
            var canonical = ordered[0];
            var duplicates = ordered.Skip(1).ToList();

            canonical.MapLink ??= duplicates.Select(x => x.MapLink).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
            canonical.CollectionInstructions ??= duplicates.Select(x => x.CollectionInstructions).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
            canonical.DriverTextName ??= duplicates.Select(x => x.DriverTextName).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
            canonical.Latitude ??= duplicates.Select(x => x.Latitude).FirstOrDefault(x => x is not null);
            canonical.Longitude ??= duplicates.Select(x => x.Longitude).FirstOrDefault(x => x is not null);
            canonical.Aliases = MergeAliases(canonical, duplicates);

            foreach (var duplicate in duplicates)
            {
                duplicate.Active = false;
                await MasterDetailStore.SaveAsync(db, "site", duplicate.ExternalCode, JsonSerializer.Serialize(duplicate), "SLH Assistant exact duplicate consolidation", null, ct);
            }
            await MasterDetailStore.SaveAsync(db, "site", canonical.ExternalCode, JsonSerializer.Serialize(canonical), "SLH Assistant exact duplicate consolidation", null, ct);
            changes.Add($"Consolidated {duplicates.Count} exact duplicate site record{(duplicates.Count == 1 ? "" : "s")} into {canonical.Name} ({canonical.ExternalCode}); originals were retained inactive.");
        }
    }

    private async Task NormaliseCustomerEmails(List<string> changes, CancellationToken ct)
    {
        var contacts = await db.CustomerContacts.Where(x => x.Active && x.Email != null).ToListAsync(ct);
        foreach (var contact in contacts)
        {
            var normalised = contact.Email!.Trim().ToLowerInvariant();
            if (normalised == contact.Email) continue;
            contact.Email = normalised;
            changes.Add($"Normalised the ETA email for {contact.CustomerCode} / {contact.Name}.");
        }
    }

    private static int Completeness(Site site) => new object?[]
    {
        site.MapLink, site.CollectionInstructions, site.DriverTextName, site.Latitude, site.Longitude, site.Aliases
    }.Count(x => x is not null && !string.IsNullOrWhiteSpace(x.ToString()));

    private static string MergeAliases(Site canonical, IReadOnlyCollection<Site> duplicates)
    {
        return string.Join(", ", new[] { canonical.Name, canonical.DriverTextName, canonical.Aliases }
            .Concat(duplicates.SelectMany(x => new[] { x.Name, x.DriverTextName, x.Aliases, x.ExternalCode }))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .SelectMany(x => x!.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string Normalise(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
