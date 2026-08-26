using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public sealed record SiteGeofenceSyncResult(
    int SitesCoded,
    int GeofencesLinked,
    int GeofencesUnlinked,
    int GeofencesCanonicalized,
    int SitesMissingGeofence,
    IReadOnlyList<SiteGeofenceStatus> Sites,
    IReadOnlyList<string>? Warnings = null);

public sealed record SiteGeofenceStatus(
    Guid SiteId,
    string SiteCode,
    string SiteName,
    IReadOnlyList<string> LinkedGeofences,
    bool GeofenceLinked,
    bool NeedsReview);

public static partial class SiteGeofenceMasterSync
{
    private const string LocationOnly = "LOCATION_ONLY";

    private static readonly HashSet<string> IgnoredTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "SITE", "DEPOT", "WAREHOUSE", "DISTRIBUTION", "CENTRE", "CENTER", "STORE", "MARKET",
        "ROAD", "STREET", "LANE", "UNIT", "SERVICE", "SERVICES", "LIMITED", "LTD", "THE", "AND"
    };

    public static async Task<SiteGeofenceSyncResult> SyncAsync(TmsDbContext db, CancellationToken ct)
    {
        var sites = await db.Sites.Where(x => x.Active).OrderBy(x => x.Name).ThenBy(x => x.Id).ToListAsync(ct);
        try
        {
            await MasterDetailStore.EnrichSitesAsync(db, sites, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            sites = await db.Sites.Where(x => x.Active).OrderBy(x => x.Name).ThenBy(x => x.Id).ToListAsync(ct);
        }

        var fences = await db.SiteGeofences.Where(x => x.Active).OrderBy(x => x.Name).ToListAsync(ct);
        var warnings = new List<string>();
        var sitesCoded = 0;
        try
        {
            sitesCoded = await CanonicalizeSiteCodesAsync(db, sites, fences, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            warnings.Add($"Site code canonicalisation was skipped: {ex.GetBaseException().Message}");
            db.ChangeTracker.Clear();
            sites = await db.Sites.Where(x => x.Active).OrderBy(x => x.Name).ThenBy(x => x.Id).ToListAsync(ct);
            fences = await db.SiteGeofences.Where(x => x.Active).OrderBy(x => x.Name).ToListAsync(ct);
        }

        var sitesById = sites.ToDictionary(x => x.Id);
        var linked = 0;
        var unlinked = 0;
        var canonicalized = 0;

        foreach (var fence in fences)
        {
            if (string.Equals(fence.SiteNumber?.Trim(), LocationOnly, StringComparison.OrdinalIgnoreCase))
                continue;

            // An explicit persisted SiteId is authoritative. Operators use the Geofence
            // dropdown to confirm the physical Site relationship, so do not reject or
            // later undo that choice merely because provider/geofence naming differs.
            if (fence.SiteId is Guid linkedSiteId &&
                sitesById.TryGetValue(linkedSiteId, out var linkedSite))
            {
                if (!string.Equals(fence.SiteNumber, linkedSite.ExternalCode, StringComparison.OrdinalIgnoreCase))
                {
                    fence.SiteNumber = linkedSite.ExternalCode;
                    canonicalized++;
                }
                fence.UpdatedAtUtc = DateTimeOffset.UtcNow;
                continue;
            }

            // Only genuinely unlinked geofences are candidates for automatic matching.
            // Automatic matching remains conservative and still requires one unique best match.
            var candidates = MatchingSites(fence.Name, sites);
            if (candidates.Count == 1)
            {
                var site = candidates[0];
                if (fence.SiteId != site.Id)
                {
                    fence.SiteId = site.Id;
                    linked++;
                }
                if (!string.Equals(fence.SiteNumber, site.ExternalCode, StringComparison.OrdinalIgnoreCase))
                {
                    fence.SiteNumber = site.ExternalCode;
                    canonicalized++;
                }
                fence.UpdatedAtUtc = DateTimeOffset.UtcNow;
                continue;
            }

            if (fence.SiteId is not null || !string.IsNullOrWhiteSpace(fence.SiteNumber))
            {
                fence.SiteId = null;
                fence.SiteNumber = null;
                fence.UpdatedAtUtc = DateTimeOffset.UtcNow;
                unlinked++;
            }
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            warnings.Add($"Automatic geofence link updates were skipped: {ex.GetBaseException().Message}");
            db.ChangeTracker.Clear();
            sites = await db.Sites.AsNoTracking().Where(x => x.Active).OrderBy(x => x.Name).ThenBy(x => x.Id).ToListAsync(ct);
            fences = await db.SiteGeofences.AsNoTracking().Where(x => x.Active).OrderBy(x => x.Name).ToListAsync(ct);
            linked = 0;
            unlinked = 0;
            canonicalized = 0;
        }

        var status = BuildStatus(sites, fences);
        return new SiteGeofenceSyncResult(
            sitesCoded,
            linked,
            unlinked,
            canonicalized,
            status.Count(x => x.NeedsReview),
            status,
            warnings);
    }

    public static async Task<IReadOnlyList<SiteGeofenceStatus>> GetStatusAsync(TmsDbContext db, CancellationToken ct)
    {
        var sites = await db.Sites.AsNoTracking().Where(x => x.Active).OrderBy(x => x.ExternalCode).ThenBy(x => x.Name).ToListAsync(ct);
        try
        {
            await MasterDetailStore.EnrichSitesAsync(db, sites, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sites = await db.Sites.AsNoTracking().Where(x => x.Active).OrderBy(x => x.ExternalCode).ThenBy(x => x.Name).ToListAsync(ct);
        }
        var fences = await db.SiteGeofences.AsNoTracking().Where(x => x.Active).OrderBy(x => x.Name).ToListAsync(ct);
        return BuildStatus(sites, fences);
    }

    public static async Task<SiteGeofenceStatus> LinkGeofenceAsync(TmsDbContext db, Guid geofenceId, string siteCode, CancellationToken ct)
    {
        var sites = await db.Sites.Where(x => x.Active).ToListAsync(ct);
        try { await MasterDetailStore.EnrichSitesAsync(db, sites, ct); } catch (Exception ex) when (ex is not OperationCanceledException) { }

        var requested = sites.FirstOrDefault(x => string.Equals(x.ExternalCode, siteCode.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException("Site code not found.");
        var fence = await ResolveLinkableGeofenceAsync(db, geofenceId, ct);

        // This method is called by the authenticated operator dropdown. The selected
        // canonical Site is therefore an explicit manual decision and is authoritative.
        // Name matching is reserved for automatic linking only.
        fence.SiteId = requested.Id;
        fence.SiteNumber = requested.ExternalCode;
        fence.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return BuildStatus(sites, new[] { fence }).First(x => x.SiteId == requested.Id);
    }

    private static async Task<SiteGeofence> ResolveLinkableGeofenceAsync(TmsDbContext db, Guid geofenceId, CancellationToken ct)
    {
        var fence = await db.SiteGeofences.FirstOrDefaultAsync(x => x.Id == geofenceId && x.Active, ct);
        if (fence is not null) return fence;

        var embedded = EmbeddedGeofenceEngine.ApprovedFences.FirstOrDefault(x => x.Id == geofenceId)
            ?? throw new KeyNotFoundException("Geofence not found.");
        var normalizedName = NormalizeName(embedded.Name);
        fence = await db.SiteGeofences.FirstOrDefaultAsync(x => x.NormalizedName == normalizedName, ct);
        if (fence is null)
        {
            fence = new SiteGeofence
            {
                Id = embedded.Id,
                Name = embedded.Name,
                NormalizedName = normalizedName,
                Category = embedded.Category,
                CategoryMaxWaitMinutes = embedded.CategoryMaxWaitMinutes,
                MaxWaitMinutes = embedded.MaxWaitMinutes,
                PendingEntryMinutes = embedded.PendingEntryMinutes,
                PendingExitMinutes = embedded.PendingExitMinutes,
                SiteNumber = embedded.SiteNumber,
                PolygonJson = PolygonJson(embedded),
                Active = true
            };
            db.SiteGeofences.Add(fence);
            return fence;
        }

        fence.Active = true;
        fence.Name = embedded.Name;
        fence.NormalizedName = normalizedName;
        fence.Category ??= embedded.Category;
        fence.CategoryMaxWaitMinutes ??= embedded.CategoryMaxWaitMinutes;
        fence.MaxWaitMinutes ??= embedded.MaxWaitMinutes;
        fence.PendingEntryMinutes = embedded.PendingEntryMinutes;
        fence.PendingExitMinutes = embedded.PendingExitMinutes;
        if (string.IsNullOrWhiteSpace(fence.PolygonJson)) fence.PolygonJson = PolygonJson(embedded);
        return fence;
    }

    internal static bool NameConfirms(string geofenceName, Site site)
        => MatchScore(geofenceName, site) > 0;

    private static List<Site> MatchingSites(string geofenceName, IReadOnlyList<Site> sites)
    {
        var scored = sites
            .Select(site => new { Site = site, Score = MatchScore(geofenceName, site) })
            .Where(x => x.Score > 0)
            .ToList();
        if (scored.Count == 0) return [];

        var bestScore = scored.Max(x => x.Score);
        return scored.Where(x => x.Score == bestScore).Select(x => x.Site).ToList();
    }

    private static int MatchScore(string geofenceName, Site site)
    {
        var fenceTokens = Tokens(geofenceName);
        if (fenceTokens.Count == 0) return 0;
        return SiteNames(site)
            .Select(Tokens)
            .Select(tokens => tokens.Count(fenceTokens.Contains))
            .DefaultIfEmpty(0)
            .Max();
    }

    private static string NormalizeName(string value) => string.Join(' ', value.Trim().ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string PolygonJson(EmbeddedFence fence) =>
        JsonSerializer.Serialize(fence.Points.Select(point => new[] { point.Longitude, point.Latitude }));

    private static async Task<int> CanonicalizeSiteCodesAsync(TmsDbContext db, IReadOnlyList<Site> sites, IReadOnlyList<SiteGeofence> fences, CancellationToken ct)
    {
        var used = new HashSet<int>();
        var retained = new Dictionary<Guid, int>();
        foreach (var site in sites)
        {
            var match = SiteCodeRegex().Match(site.ExternalCode?.Trim() ?? string.Empty);
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var number) || number <= 0 || !used.Add(number))
                continue;
            retained[site.Id] = number;
        }

        var next = 1;
        var desiredBySiteId = new Dictionary<Guid, string>();
        foreach (var site in sites)
        {
            if (!retained.TryGetValue(site.Id, out var number))
            {
                while (used.Contains(next)) next++;
                number = next;
                used.Add(number);
                next++;
            }

            var desired = $"SITE{number:D3}";
            desiredBySiteId[site.Id] = desired;
        }

        var changedSites = sites
            .Where(site => desiredBySiteId.TryGetValue(site.Id, out var desired) &&
                !string.Equals(site.ExternalCode, desired, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (changedSites.Count == 0) return 0;

        if (db.Database.IsRelational())
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await ApplySiteCodeRenumberingAsync(db, changedSites, fences, desiredBySiteId, ct);
            await transaction.CommitAsync(ct);
        }
        else
        {
            await ApplySiteCodeRenumberingAsync(db, changedSites, fences, desiredBySiteId, ct);
        }

        return changedSites.Count;
    }

    private static async Task ApplySiteCodeRenumberingAsync(
        TmsDbContext db,
        IReadOnlyList<Site> changedSites,
        IReadOnlyList<SiteGeofence> fences,
        IReadOnlyDictionary<Guid, string> desiredBySiteId,
        CancellationToken ct)
    {
        foreach (var site in changedSites)
            site.ExternalCode = TemporarySiteCode(site.Id);

        await db.SaveChangesAsync(ct);

        foreach (var site in changedSites)
        {
            var desired = desiredBySiteId[site.Id];
            site.ExternalCode = desired;
            foreach (var fence in fences.Where(x => x.SiteId == site.Id))
                fence.SiteNumber = desired;
        }

        await db.SaveChangesAsync(ct);
    }

    private static string TemporarySiteCode(Guid siteId) => $"TMP{siteId:N}"[..35];

    private static IReadOnlyList<SiteGeofenceStatus> BuildStatus(IReadOnlyList<Site> sites, IEnumerable<SiteGeofence> fences)
    {
        var fenceList = fences.ToList();
        return sites
            .OrderBy(x => ParseSiteNumber(x.ExternalCode))
            .ThenBy(x => x.Name)
            .Select(site =>
            {
                var linked = fenceList
                    .Where(fence => fence.SiteId == site.Id)
                    .Select(fence => fence.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x)
                    .ToList();
                return new SiteGeofenceStatus(site.Id, site.ExternalCode, site.Name, linked, linked.Count > 0, linked.Count == 0);
            })
            .ToList();
    }

    private static int ParseSiteNumber(string? code)
    {
        var match = SiteCodeRegex().Match(code?.Trim() ?? string.Empty);
        return match.Success && int.TryParse(match.Groups[1].Value, out var number) ? number : int.MaxValue;
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

    private static HashSet<string> Tokens(string? value)
        => Regex.Matches(value ?? string.Empty, "[A-Za-z0-9]+")
            .Cast<Match>()
            .Select(match => match.Value.ToUpperInvariant())
            .Where(token => token.Length >= 4 && !IgnoredTokens.Contains(token) && !token.All(char.IsDigit))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex("^SITE0*([1-9][0-9]*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SiteCodeRegex();
}
