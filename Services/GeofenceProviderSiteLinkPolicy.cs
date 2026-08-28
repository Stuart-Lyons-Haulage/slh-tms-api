using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// DOT/Falcon's reviewed 736-geofence export uses site_no=1 on hundreds of unrelated
/// fences. That value is a provider/default placeholder, not SLH Site Reference 1.
/// Canonical operational site identity must come from SLH Site Master (code/name/alias)
/// or an explicit manual link, never from this provider placeholder.
/// </summary>
public static class GeofenceProviderSiteLinkPolicy
{
    public const string ProviderUnassignedSiteNumber = "1";

    public static bool IsProviderPlaceholder(string? value) =>
        string.Equals(value?.Trim(), ProviderUnassignedSiteNumber, StringComparison.OrdinalIgnoreCase);

    public static string? SanitizeProviderSiteNumber(string? value)
    {
        var clean = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return IsProviderPlaceholder(clean) ? null : clean;
    }

    public static Site? ExactCanonicalSite(string geofenceName, string? providerSiteNumber, IReadOnlyCollection<Site> sites)
    {
        var cleanCode = SanitizeProviderSiteNumber(providerSiteNumber);
        if (!string.IsNullOrWhiteSpace(cleanCode))
        {
            var byCode = DistinctSites(sites.Where(site => CodesEquivalent(site.ExternalCode, cleanCode)));
            if (byCode.Count == 1) return byCode[0];
            if (byCode.Count > 1) return null;
        }

        var fenceName = Normalize(geofenceName);
        var byName = DistinctSites(sites.Where(site =>
            Normalize(site.Name) == fenceName || Normalize(site.DriverTextName) == fenceName));
        return byName.Count == 1 ? byName[0] : null;
    }

    private static List<Site> DistinctSites(IEnumerable<Site> values) =>
        values.GroupBy(site => site.Id).Select(group => group.First()).ToList();

    private static bool CodesEquivalent(string? left, string? right)
    {
        var a = Normalize(left);
        var b = Normalize(right);
        if (a.Length == 0 || b.Length == 0) return false;
        if (a == b) return true;
        return long.TryParse(a, out var an) && long.TryParse(b, out var bn) && an == bn;
    }

    private static string Normalize(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}

/// <summary>
/// Idempotently repairs historical SiteGeofence rows created when provider site_no=1
/// was incorrectly interpreted as SLH Site 1. Geometry, geofence IDs and visit history
/// are never deleted or rewritten. Exact Site Master name/alias matches are retained;
/// otherwise the false canonical link is cleared for explicit reconciliation.
/// </summary>
public static class GeofenceProviderPlaceholderRepair
{
    public static async Task<GeofenceProviderPlaceholderRepairResult> EnsureAsync(TmsDbContext db, CancellationToken ct)
    {
        List<SiteGeofence> suspects;
        try
        {
            suspects = await db.SiteGeofences
                .Where(fence => fence.Active && fence.SiteNumber != null)
                .ToListAsync(ct);
            suspects = suspects
                .Where(fence => GeofenceProviderSiteLinkPolicy.IsProviderPlaceholder(fence.SiteNumber))
                .ToList();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            return new GeofenceProviderPlaceholderRepairResult(0, 0, 0);
        }

        if (suspects.Count == 0) return new GeofenceProviderPlaceholderRepairResult(0, 0, 0);

        List<Site> sites;
        try
        {
            sites = await GeofenceSiteResolver.LoadActiveSitesAsync(db, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            return new GeofenceProviderPlaceholderRepairResult(suspects.Count, 0, 0);
        }

        var relinked = 0;
        var cleared = 0;
        var now = DateTimeOffset.UtcNow;

        foreach (var fence in suspects)
        {
            var target = GeofenceProviderSiteLinkPolicy.ExactCanonicalSite(fence.Name, null, sites);
            if (target is not null)
            {
                if (fence.SiteId == target.Id && string.Equals(fence.SiteNumber, target.ExternalCode, StringComparison.OrdinalIgnoreCase))
                    continue;

                var beforeSiteId = fence.SiteId;
                fence.SiteId = target.Id;
                fence.SiteNumber = target.ExternalCode;
                fence.UpdatedAtUtc = now;
                relinked++;
                Audit(db, fence, "RepairedProviderPlaceholderSiteLink", new
                {
                    providerSiteNumber = GeofenceProviderSiteLinkPolicy.ProviderUnassignedSiteNumber,
                    previousSiteId = beforeSiteId,
                    canonicalSiteId = target.Id,
                    canonicalSiteCode = target.ExternalCode,
                    canonicalSiteName = target.Name
                });
                continue;
            }

            var previousSiteId = fence.SiteId;
            fence.SiteId = null;
            fence.SiteNumber = null;
            fence.UpdatedAtUtc = now;
            cleared++;
            Audit(db, fence, "ClearedProviderPlaceholderSiteLink", new
            {
                providerSiteNumber = GeofenceProviderSiteLinkPolicy.ProviderUnassignedSiteNumber,
                previousSiteId,
                reason = "DOT/Falcon site_no=1 is a provider placeholder and no unique exact Site Master name/alias matched this geofence."
            });
        }

        if (relinked > 0 || cleared > 0)
            await db.SaveChangesAsync(ct);

        return new GeofenceProviderPlaceholderRepairResult(suspects.Count, relinked, cleared);
    }

    private static void Audit(TmsDbContext db, SiteGeofence fence, string action, object changes)
    {
        db.MasterDataAudits.Add(new MasterDataAudit
        {
            EntityType = "Geofence",
            EntityId = fence.Id,
            Action = action,
            ChangedBy = "GeofenceProviderPlaceholderRepair",
            ChangesJson = JsonSerializer.Serialize(changes)
        });
    }
}

/// <summary>
/// Repairs active Falcon geofences that have no canonical Site link by matching the
/// geofence name against one unique Site Master code/name/driver-text/alias. This makes
/// the Site Master alias field operational rather than display-only and repairs aliases
/// that were saved before the geofence was imported or linked.
/// </summary>
public static class GeofenceSiteAliasRepair
{
    public static async Task<int> EnsureAsync(TmsDbContext db, CancellationToken ct)
    {
        List<SiteGeofence> unlinked;
        try
        {
            unlinked = await db.SiteGeofences
                .Where(fence => fence.Active && fence.SiteId == null)
                .ToListAsync(ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            return 0;
        }

        if (unlinked.Count == 0) return 0;

        List<Site> sites;
        try
        {
            // GeofenceSiteResolver expands every unique Site Master alias into a synthetic
            // driver-text candidate while retaining the canonical Site Id/code.
            sites = await GeofenceSiteResolver.LoadActiveSitesAsync(db, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            return 0;
        }

        var repaired = 0;
        var now = DateTimeOffset.UtcNow;
        foreach (var fence in unlinked)
        {
            var target = GeofenceProviderSiteLinkPolicy.ExactCanonicalSite(fence.Name, fence.SiteNumber, sites);
            if (target is null) continue;

            fence.SiteId = target.Id;
            fence.SiteNumber = target.ExternalCode;
            fence.UpdatedAtUtc = now;
            repaired++;
            db.MasterDataAudits.Add(new MasterDataAudit
            {
                EntityType = "Geofence",
                EntityId = fence.Id,
                Action = "LinkedFromSiteAlias",
                ChangedBy = "GeofenceSiteAliasRepair",
                ChangesJson = JsonSerializer.Serialize(new
                {
                    geofenceName = fence.Name,
                    canonicalSiteId = target.Id,
                    canonicalSiteCode = target.ExternalCode,
                    canonicalSiteName = target.Name,
                    reason = "Unique exact Site Master name/driver-text/alias match."
                })
            });
        }

        if (repaired > 0) await db.SaveChangesAsync(ct);
        return repaired;
    }
}

public sealed record GeofenceProviderPlaceholderRepairResult(int Found, int Relinked, int Cleared);
