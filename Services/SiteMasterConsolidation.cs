using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public sealed record SiteMasterConsolidationResult(
    int PromotedCustomers,
    int ArchivedDuplicates,
    int LinkedGeofences,
    int CanonicalizedGeofences,
    int NeedsReview);

/// <summary>
/// Conservatively aligns the legacy customer/location register, Site Master and geofences.
/// Site is the canonical physical-location identity. Ambiguous matches are never guessed:
/// they are written to the staging register for human review.
/// </summary>
public static class SiteMasterConsolidation
{
    private const string LocationOnly = "LOCATION_ONLY";
    private const string SiteReviewType = "masterdata:site-review";
    private const string GeofenceReviewType = "masterdata:geofence-review";

    public static async Task<SiteMasterConsolidationResult> ReconcileAsync(
        TmsDbContext db,
        string source,
        CancellationToken ct)
    {
        var promotedCustomers = 0;
        var archivedDuplicates = 0;
        var linkedGeofences = 0;
        var canonicalizedGeofences = 0;
        var reviewKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var staleReviews = await db.StagedImports
            .Where(x => (x.EntityType == SiteReviewType || x.EntityType == GeofenceReviewType) &&
                        x.Status == StagingStatus.PendingReview)
            .ToListAsync(ct);
        foreach (var review in staleReviews)
        {
            review.Status = StagingStatus.Archived;
            review.ReviewedAtUtc = DateTimeOffset.UtcNow;
            review.ReviewNote = "Superseded by a fresh Site Master reconciliation pass.";
        }
        if (staleReviews.Count > 0)
            await db.SaveChangesAsync(ct);

        // 1. Remove only high-confidence duplicate Sites. A duplicate group is auto-merged
        // when exactly one record owns active geofences; otherwise it is left untouched.
        var activeSites = await db.Sites.Where(x => x.Active).ToListAsync(ct);
        var activeFences = await db.SiteGeofences.Where(x => x.Active).ToListAsync(ct);
        foreach (var group in activeSites
                     .Where(x => Normalize(x.Name).Length > 0)
                     .GroupBy(x => Normalize(x.Name), StringComparer.OrdinalIgnoreCase)
                     .Where(x => x.Count() > 1))
        {
            var rows = group.ToList();
            var fenceBacked = rows
                .Where(site => activeFences.Any(fence => fence.SiteId == site.Id))
                .ToList();

            if (fenceBacked.Count == 1)
            {
                var canonical = fenceBacked[0];
                foreach (var duplicate in rows.Where(x => x.Id != canonical.Id))
                {
                    await AddSiteAliasesAsync(db, canonical,
                        new[] { duplicate.ExternalCode, duplicate.Name, duplicate.DriverTextName }, source, ct);
                    duplicate.Active = false;
                    archivedDuplicates++;
                }
            }
            else
            {
                await FlagReviewAsync(db, SiteReviewType, $"duplicate:{group.Key}", new
                {
                    reason = "duplicate_site_identity_ambiguous",
                    normalizedName = group.Key,
                    candidates = rows.Select(x => new { x.Id, x.ExternalCode, x.Name, x.DriverTextName }).ToArray(),
                    geofenceBackedSiteIds = fenceBacked.Select(x => x.Id).ToArray()
                }, source, reviewKeys, ct);
            }
        }
        await db.SaveChangesAsync(ct);

        // 2. Fold the legacy Customer/location register into Site Master without deleting
        // Customer rows, because historical/order commercial references still use CustomerCode.
        // All Sites are considered for code ownership so an archived Site can never be duplicated.
        var allSites = await db.Sites.ToListAsync(ct);
        activeSites = allSites.Where(x => x.Active).ToList();
        var customers = await db.Customers.Where(x => x.Active).ToListAsync(ct);
        foreach (var customer in customers)
        {
            var codeKey = Normalize(customer.Code);
            var nameKey = Normalize(customer.Name);
            if (codeKey.Length == 0 || nameKey.Length == 0)
                continue;

            var codeMatches = allSites.Where(x => Normalize(x.ExternalCode) == codeKey).ToList();
            if (codeMatches.Count == 1)
            {
                var codeMatch = codeMatches[0];
                if (!codeMatch.Active)
                {
                    if (Normalize(codeMatch.Name) != nameKey && Normalize(codeMatch.DriverTextName) != nameKey)
                    {
                        await FlagReviewAsync(db, SiteReviewType, $"archived-code:{codeKey}", new
                        {
                            reason = "customer_code_owned_by_different_archived_site",
                            customer = new { customer.Id, customer.Code, customer.Name },
                            archivedSite = new { codeMatch.Id, codeMatch.ExternalCode, codeMatch.Name, codeMatch.DriverTextName },
                            requestedAction = "Confirm whether the archived Site should be restored or the Customer/location needs a different Site code."
                        }, source, reviewKeys, ct);
                        continue;
                    }

                    codeMatch.Active = true;
                    activeSites.Add(codeMatch);
                }

                await AddSiteAliasesAsync(db, codeMatch, new[] { customer.Code, customer.Name }, source, ct);
                continue;
            }
            if (codeMatches.Count > 1)
            {
                await FlagReviewAsync(db, SiteReviewType, $"customer-code:{codeKey}", new
                {
                    reason = "customer_code_matches_multiple_sites",
                    customer = new { customer.Id, customer.Code, customer.Name },
                    candidates = codeMatches.Select(x => new { x.Id, x.ExternalCode, x.Name, x.Active }).ToArray()
                }, source, reviewKeys, ct);
                continue;
            }

            var nameMatches = activeSites.Where(x => Normalize(x.Name) == nameKey || Normalize(x.DriverTextName) == nameKey).ToList();
            if (nameMatches.Count == 1)
            {
                await AddSiteAliasesAsync(db, nameMatches[0], new[] { customer.Code, customer.Name }, source, ct);
                continue;
            }
            if (nameMatches.Count > 1)
            {
                await FlagReviewAsync(db, SiteReviewType, $"customer-name:{nameKey}", new
                {
                    reason = "customer_location_matches_multiple_sites",
                    customer = new { customer.Id, customer.Code, customer.Name },
                    candidates = nameMatches.Select(x => new { x.Id, x.ExternalCode, x.Name }).ToArray()
                }, source, reviewKeys, ct);
                continue;
            }

            var site = new Site
            {
                ExternalCode = customer.Code.Trim(),
                Name = customer.Name.Trim(),
                DriverTextName = customer.Name.Trim(),
                Active = true
            };
            db.Sites.Add(site);
            allSites.Add(site);
            activeSites.Add(site);
            promotedCustomers++;
            await AddSiteAliasesAsync(db, site, new[] { customer.Code, customer.Name }, source, ct);
        }
        await db.SaveChangesAsync(ct);

        // 3. Sync every active geofence to the canonical Site identity. SiteId is authoritative;
        // SiteNumber is corrected to the canonical Site code. Unlinked fences use unique code/name/
        // alias matches only. Anything else is flagged for review.
        activeSites = await db.Sites.Where(x => x.Active).ToListAsync(ct);
        try
        {
            await MasterDetailStore.EnrichSitesAsync(db, activeSites, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            activeSites = await db.Sites.Where(x => x.Active).ToListAsync(ct);
        }

        var fences = await db.SiteGeofences.Where(x => x.Active).ToListAsync(ct);
        var sitesById = activeSites.ToDictionary(x => x.Id);
        foreach (var fence in fences)
        {
            if (string.Equals(fence.SiteNumber?.Trim(), LocationOnly, StringComparison.OrdinalIgnoreCase))
                continue;

            if (fence.SiteId is Guid linkedId && sitesById.TryGetValue(linkedId, out var linkedSite))
            {
                if (!string.Equals(fence.SiteNumber?.Trim(), linkedSite.ExternalCode.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    fence.SiteNumber = linkedSite.ExternalCode;
                    fence.UpdatedAtUtc = DateTimeOffset.UtcNow;
                    canonicalizedGeofences++;
                }
                continue;
            }

            var candidates = new List<Site>();
            var siteNumberKey = Normalize(fence.SiteNumber);
            if (siteNumberKey.Length > 0 && siteNumberKey != "1")
                candidates.AddRange(activeSites.Where(x => Normalize(x.ExternalCode) == siteNumberKey));

            if (candidates.Select(x => x.Id).Distinct().Count() != 1)
            {
                candidates.Clear();
                var fenceNameKey = Normalize(fence.Name);
                candidates.AddRange(activeSites.Where(site => SiteNames(site).Any(name => Normalize(name) == fenceNameKey)));
            }

            candidates = candidates.GroupBy(x => x.Id).Select(x => x.First()).ToList();
            if (candidates.Count == 1)
            {
                fence.SiteId = candidates[0].Id;
                fence.SiteNumber = candidates[0].ExternalCode;
                fence.UpdatedAtUtc = DateTimeOffset.UtcNow;
                linkedGeofences++;
                continue;
            }

            await FlagReviewAsync(db, GeofenceReviewType, $"geofence:{fence.Id:N}", new
            {
                reason = candidates.Count == 0 ? "geofence_has_no_site_match" : "geofence_site_match_ambiguous",
                geofence = new { fence.Id, fence.Name, fence.SiteNumber },
                candidates = candidates.Select(x => new { x.Id, x.ExternalCode, x.Name }).ToArray(),
                requestedAction = "Enter the canonical Site code or geofence reference."
            }, source, reviewKeys, ct);
        }

        await db.SaveChangesAsync(ct);
        return new SiteMasterConsolidationResult(
            promotedCustomers,
            archivedDuplicates,
            linkedGeofences,
            canonicalizedGeofences,
            reviewKeys.Count);
    }

    private static IEnumerable<string?> SiteNames(Site site)
    {
        yield return site.Name;
        yield return site.DriverTextName;
        if (!string.IsNullOrWhiteSpace(site.Aliases))
        {
            foreach (var alias in site.Aliases.Split(new[] { ',', ';', '|', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                yield return alias;
        }
    }

    private static async Task AddSiteAliasesAsync(
        TmsDbContext db,
        Site site,
        IEnumerable<string?> aliases,
        string source,
        CancellationToken ct)
    {
        var key = $"masterdetail:site:{Normalize(site.ExternalCode).ToLowerInvariant()}";
        var row = await db.StagedImports.FirstOrDefaultAsync(x => x.IdempotencyKey == key, ct);
        JsonObject payload;
        try
        {
            payload = row is null ? new JsonObject() : JsonNode.Parse(row.PayloadJson)?.AsObject() ?? new JsonObject();
        }
        catch (JsonException)
        {
            payload = new JsonObject();
        }

        var merged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (payload["aliases"] is JsonValue aliasNode && aliasNode.TryGetValue<string>(out var existing) && !string.IsNullOrWhiteSpace(existing))
        {
            foreach (var alias in existing.Split(new[] { ',', ';', '|', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                merged.Add(alias);
        }
        foreach (var alias in aliases.Where(x => !string.IsNullOrWhiteSpace(x)))
            merged.Add(alias!.Trim());

        payload["externalCode"] = site.ExternalCode;
        payload["aliases"] = string.Join("; ", merged.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

        if (row is null)
        {
            db.StagedImports.Add(new StagedImport
            {
                EntityType = "masterdetail:site",
                IdempotencyKey = key,
                PayloadJson = payload.ToJsonString(),
                Status = StagingStatus.Promoted,
                Source = source,
                ReviewedAtUtc = DateTimeOffset.UtcNow,
                ReviewedBy = source,
                ReviewNote = "Canonical Site aliases consolidated from legacy location masters."
            });
        }
        else
        {
            row.EntityType = "masterdetail:site";
            row.PayloadJson = payload.ToJsonString();
            row.Status = StagingStatus.Promoted;
            row.Source ??= source;
            row.ReviewedAtUtc = DateTimeOffset.UtcNow;
            row.ReviewedBy = source;
        }
    }

    private static async Task FlagReviewAsync(
        TmsDbContext db,
        string entityType,
        string suffix,
        object payload,
        string source,
        ISet<string> reviewKeys,
        CancellationToken ct)
    {
        var fullKey = $"{entityType}:{suffix}";
        var key = fullKey.Length <= 200 ? fullKey : fullKey[..200];
        reviewKeys.Add(key);
        var row = await db.StagedImports.FirstOrDefaultAsync(x => x.IdempotencyKey == key, ct);
        var json = JsonSerializer.Serialize(payload);
        if (row is null)
        {
            db.StagedImports.Add(new StagedImport
            {
                EntityType = entityType,
                IdempotencyKey = key,
                PayloadJson = json,
                Status = StagingStatus.PendingReview,
                Source = source,
                ReviewNote = "Site/geofence identity requires manual confirmation."
            });
        }
        else
        {
            row.EntityType = entityType;
            row.PayloadJson = json;
            row.Status = StagingStatus.PendingReview;
            row.Source ??= source;
            row.ReviewedAtUtc = null;
            row.ReviewedBy = null;
            row.ReviewNote = "Site/geofence identity requires manual confirmation.";
        }
    }

    private static string Normalize(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
