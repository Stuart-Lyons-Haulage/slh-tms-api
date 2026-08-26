using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Resolves the planner's human-readable source-line site labels to stable Site Master
/// identity and the manually linked DOT/Falcon geofence without replacing the source
/// wording used by Planner and driver instructions.
/// </summary>
public sealed class PlannerSourceMasterDataResolver
{
    private static readonly HashSet<string> PlannerPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "BAR", "BARFOOTS", "LAN", "LANGMEADS", "SB", "GHS", "SLH", "WAITROSE", "MORRISONS", "ALDI"
    };

    private readonly IReadOnlyList<Site> _sites;
    private readonly IReadOnlyList<SiteGeofence> _geofences;

    private PlannerSourceMasterDataResolver(IReadOnlyList<Site> sites, IReadOnlyList<SiteGeofence> geofences)
    {
        _sites = sites;
        _geofences = geofences;
    }

    public static async Task<PlannerSourceMasterDataResolver> CreateAsync(TmsDbContext db, CancellationToken ct)
    {
        var sites = await db.Sites.AsNoTracking().Where(site => site.Active).ToListAsync(ct);
        await MasterDetailStore.EnrichSitesAsync(db, sites, ct);

        List<SiteGeofence> geofences;
        try
        {
            geofences = await db.SiteGeofences.AsNoTracking().Where(fence => fence.Active).ToListAsync(ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            geofences = [];
        }

        return new PlannerSourceMasterDataResolver(sites, geofences);
    }

    public PlannerSourceSiteResolution Resolve(string? sourceLabel)
    {
        if (string.IsNullOrWhiteSpace(sourceLabel)) return PlannerSourceSiteResolution.Unresolved(sourceLabel);

        var embeddedFence = ResolveEmbeddedFence(sourceLabel);
        var linkedByFence = embeddedFence is null ? null : LinkedGeofence(embeddedFence.Name);

        var site = MatchSite(sourceLabel)
            ?? SiteFromLink(linkedByFence);

        var linkedGeofence = site is null
            ? linkedByFence
            : _geofences
                .Where(item => item.SiteId == site.Id ||
                    (!string.IsNullOrWhiteSpace(item.SiteNumber) && Normalize(item.SiteNumber) == Normalize(site.ExternalCode)))
                .OrderByDescending(item => item.UpdatedAtUtc)
                .FirstOrDefault()
                ?? linkedByFence;

        embeddedFence ??= ResolveEmbeddedFence(linkedGeofence?.Name)
            ?? ResolveEmbeddedFence(site?.DriverTextName)
            ?? ResolveEmbeddedFence(site?.Name);

        var latitude = site?.Latitude;
        var longitude = site?.Longitude;
        if ((latitude is null || longitude is null) && embeddedFence is not null)
        {
            longitude = (decimal)embeddedFence.Points.Average(point => point.Longitude);
            latitude = (decimal)embeddedFence.Points.Average(point => point.Latitude);
        }

        return new PlannerSourceSiteResolution(
            sourceLabel.Trim(),
            site?.Id,
            site?.ExternalCode,
            site is null ? null : DisplayName(site),
            site?.CollectionAddress,
            latitude,
            longitude,
            linkedGeofence?.Id,
            linkedGeofence?.Name ?? embeddedFence?.Name,
            site is not null,
            linkedGeofence is not null);
    }

    /// <summary>
    /// Returns a canonical Site Master decision for a planned stop versus a physical
    /// embedded geofence. True/false is authoritative when the physical fence has an
    /// explicit Site Master link. Null means canonical evidence is unavailable and the
    /// caller may use the legacy operational/fuzzy matching rules.
    /// </summary>
    public bool? CanonicalGeofenceMatch(string? sourceLabel, EmbeddedFence fence)
    {
        if (string.IsNullOrWhiteSpace(sourceLabel)) return null;

        var linked = LinkedGeofence(fence.Name);
        var linkedSite = SiteFromLink(linked);
        if (linked is null || linkedSite is null) return null;

        var plannedSite = MatchSite(sourceLabel);
        return plannedSite is null ? false : plannedSite.Id == linkedSite.Id;
    }

    private SiteGeofence? LinkedGeofence(string? fenceName)
    {
        if (string.IsNullOrWhiteSpace(fenceName)) return null;
        var key = Normalize(fenceName);
        return _geofences
            .Where(item => Normalize(item.Name) == key || Normalize(item.NormalizedName) == key)
            .OrderByDescending(item => item.UpdatedAtUtc)
            .FirstOrDefault();
    }

    private Site? MatchSite(string value)
    {
        var keys = PlannerSiteVariants(value)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(Normalize)
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var exact = _sites.Where(site => SiteCandidates(site).Any(candidate => keys.Contains(Normalize(candidate), StringComparer.OrdinalIgnoreCase))).ToList();
        if (exact.Select(site => site.Id).Distinct().Count() == 1) return exact[0];

        var fuzzy = _sites.Where(site => SiteCandidates(site).Any(candidate =>
        {
            var candidateKey = Normalize(candidate);
            return candidateKey.Length >= 5 && keys.Any(key => key.Length >= 5 && (key.Contains(candidateKey, StringComparison.Ordinal) || candidateKey.Contains(key, StringComparison.Ordinal)));
        })).ToList();
        return fuzzy.Select(site => site.Id).Distinct().Count() == 1 ? fuzzy[0] : null;
    }

    private static IEnumerable<string> PlannerSiteVariants(string value)
    {
        var initial = value.Trim();
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { initial, GeofencePlanningMatch.MatchText(initial) };

        foreach (var candidate in values.ToList())
        {
            var withoutTemperature = Regex.Replace(candidate, @"\(\s*[+-]?\d+(?:\.\d+)?\s*°?\s*C\s*\)", string.Empty, RegexOptions.IgnoreCase).Trim();
            if (!string.IsNullOrWhiteSpace(withoutTemperature)) values.Add(withoutTemperature);

            var openParen = candidate.LastIndexOf('(');
            if (openParen > 0 && candidate.EndsWith(')'))
            {
                var before = candidate[..openParen].Trim();
                var inside = candidate[(openParen + 1)..^1].Trim();
                if (!string.IsNullOrWhiteSpace(before)) values.Add(before);
                if (!string.IsNullOrWhiteSpace(inside) && !inside.Contains('°')) values.Add(inside);
            }
        }

        foreach (var candidate in values.ToList())
        {
            var separator = candidate.IndexOf('-');
            if (separator > 0)
            {
                var prefix = candidate[..separator].Trim();
                if (PlannerPrefixes.Contains(prefix)) values.Add(candidate[(separator + 1)..].Trim());
            }
        }

        foreach (var candidate in values.ToList())
        {
            var stripped = Regex.Replace(candidate, @"\s+(CHILL|FRV)$", string.Empty, RegexOptions.IgnoreCase).Trim();
            if (!string.IsNullOrWhiteSpace(stripped)) values.Add(stripped);
        }

        return values;
    }

    private Site? SiteFromLink(SiteGeofence? linked)
    {
        if (linked is null) return null;
        if (linked.SiteId is Guid siteId)
        {
            var byId = _sites.FirstOrDefault(site => site.Id == siteId);
            if (byId is not null) return byId;
        }
        if (!string.IsNullOrWhiteSpace(linked.SiteNumber))
        {
            var key = Normalize(linked.SiteNumber);
            return _sites.FirstOrDefault(site => Normalize(site.ExternalCode) == key);
        }
        return null;
    }

    private static EmbeddedFence? ResolveEmbeddedFence(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        var canonical = GeofencePlanningMatch.MatchText(label);
        var matches = EmbeddedGeofenceEngine.ApprovedFences
            .Where(fence => Normalize(fence.Name) == Normalize(canonical) || Normalize(fence.Name) == Normalize(label))
            .ToList();
        if (matches.Count == 1) return matches[0];

        var probe = new LoadStop { Name = label.Trim(), Sequence = 1, LoadId = Guid.Empty };
        matches = EmbeddedGeofenceEngine.ApprovedFences.Where(fence => GeofencePlanningMatch.SamePhysicalSite(probe, fence)).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static IEnumerable<string?> SiteCandidates(Site site)
    {
        yield return site.ExternalCode;
        yield return site.Name;
        yield return site.DriverTextName;
        foreach (var alias in (site.Aliases ?? string.Empty).Split(new[] { ',', ';', '|', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return alias;
    }

    private static string DisplayName(Site site) => !string.IsNullOrWhiteSpace(site.DriverTextName) ? site.DriverTextName.Trim() : site.Name.Trim();

    private static string Normalize(string? value) => new((value ?? string.Empty)
        .Where(char.IsLetterOrDigit)
        .Select(char.ToUpperInvariant)
        .ToArray());
}

public sealed record PlannerSourceSiteResolution(
    string? SourceLabel,
    Guid? SiteId,
    string? SiteNumber,
    string? SiteName,
    string? Address,
    decimal? Latitude,
    decimal? Longitude,
    Guid? GeofenceId,
    string? GeofenceName,
    bool SiteMatched,
    bool GeofenceLinked)
{
    public static PlannerSourceSiteResolution Unresolved(string? sourceLabel) =>
        new(sourceLabel, null, null, null, null, null, null, null, null, false, false);

    public string EvidenceNote => string.Join(" · ", new[]
    {
        SiteMatched ? $"Site ref: {SiteNumber}" : "Site ref: unresolved",
        SiteMatched ? $"Master site: {SiteName}" : null,
        GeofenceLinked ? $"Geofence: {GeofenceName}" : "Geofence: unlinked"
    }.Where(value => !string.IsNullOrWhiteSpace(value)));
}
