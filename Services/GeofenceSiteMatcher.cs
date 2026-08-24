using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

internal static class GeofenceSiteMatcher
{
    private static readonly HashSet<string> IgnoredTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "THE", "LTD", "LIMITED", "PLC", "RDC", "SITE", "DEPOT", "DELIVERY", "COLLECTION", "CUSTOMER",
        "UNIT", "MANUFACTURING", "WAREHOUSE"
    };

    public static Site? Match(string fenceName, string? siteNumber, IReadOnlyCollection<Site> sites)
    {
        var number = Normalize(siteNumber);
        if (number.Length > 0)
        {
            var byCode = sites.Where(site => Normalize(site.ExternalCode) == number).ToList();
            if (byCode.Count == 1) return byCode[0];
        }

        var fenceKey = Normalize(fenceName);
        var exact = sites.Where(site =>
            Normalize(site.Name) == fenceKey || Normalize(site.DriverTextName) == fenceKey).ToList();
        if (exact.Count == 1) return exact[0];

        var scored = sites
            .Select(site => new { Site = site, Score = Score(fenceName, site) })
            .Where(item => item.Score >= 20)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Site.ExternalCode)
            .ToList();

        if (scored.Count == 0) return null;
        if (scored.Count > 1 && scored[0].Score == scored[1].Score) return null;
        return scored[0].Site;
    }

    private static int Score(string fenceName, Site site)
    {
        var fenceKey = Normalize(fenceName);
        var nameKey = Normalize(site.Name);
        var driverKey = Normalize(site.DriverTextName);
        var score = 0;

        if (fenceKey.Length >= 6 && nameKey.Length >= 6 &&
            (fenceKey.Contains(nameKey, StringComparison.Ordinal) || nameKey.Contains(fenceKey, StringComparison.Ordinal)))
            score += 60;
        if (fenceKey.Length >= 6 && driverKey.Length >= 6 &&
            (fenceKey.Contains(driverKey, StringComparison.Ordinal) || driverKey.Contains(fenceKey, StringComparison.Ordinal)))
            score += 60;

        var fenceTokens = Tokens(fenceName);
        var identityTokens = Tokens($"{site.Name} {site.DriverTextName}");
        var addressTokens = Tokens(site.CollectionAddress);

        score += fenceTokens.Intersect(identityTokens, StringComparer.OrdinalIgnoreCase).Count() * 10;
        score += fenceTokens.Intersect(addressTokens, StringComparer.OrdinalIgnoreCase).Count() * 15;
        return score;
    }

    private static HashSet<string> Tokens(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var spaced = new string(value.Select(character => char.IsLetterOrDigit(character) ? char.ToUpperInvariant(character) : ' ').ToArray());
        return spaced.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length >= 2 && !IgnoredTokens.Contains(token))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string Normalize(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
