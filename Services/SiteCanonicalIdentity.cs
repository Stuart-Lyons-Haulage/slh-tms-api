using System.Text.RegularExpressions;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Shared, conservative identity normalisation for Site Master cleanup and duplicate review.
/// It deliberately produces candidate keys rather than silently choosing a Site when more
/// than one active record can represent the same physical location.
/// </summary>
public static class SiteCanonicalIdentity
{
    private static readonly HashSet<string> PlannerPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "BAR", "BARFOOTS", "LAN", "LANGMEADS", "SB", "GHS", "SLH", "NWF", "WAITROSE", "MORRISONS", "ALDI"
    };

    public static IReadOnlySet<string> Keys(Site site)
    {
        var values = new List<string?> { site.ExternalCode, site.Name, site.DriverTextName };
        values.AddRange(Aliases(site.Aliases));
        return values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .SelectMany(value => Variants(value!))
            .Select(Normalize)
            .Where(key => key.Length >= 4)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlySet<string> Keys(string? value) => string.IsNullOrWhiteSpace(value)
        ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        : Variants(value)
            .Select(Normalize)
            .Where(key => key.Length >= 4)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static bool LooksEquivalent(Site left, Site right) => SharedKeys(left, right).Count > 0;

    public static IReadOnlyList<string> SharedKeys(Site left, Site right)
    {
        var rightKeys = Keys(right);
        return Keys(left)
            .Where(rightKeys.Contains)
            .OrderByDescending(key => key.Length)
            .ThenBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IEnumerable<string> Aliases(string? aliases) => string.IsNullOrWhiteSpace(aliases)
        ? []
        : aliases.Split(new[] { ',', ';', '|', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static IEnumerable<string> Variants(string value)
    {
        var initial = value.Trim();
        if (initial.Length == 0) return [];

        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            initial,
            GeofencePlanningMatch.MatchText(initial)
        };

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
                if (PlannerPrefixes.Contains(prefix))
                {
                    var suffix = candidate[(separator + 1)..].Trim();
                    if (!string.IsNullOrWhiteSpace(suffix)) values.Add(suffix);
                }
            }
        }

        foreach (var candidate in values.ToList())
        {
            var stripped = Regex.Replace(candidate, @"\s+(CHILL|FRV)$", string.Empty, RegexOptions.IgnoreCase).Trim();
            if (!string.IsNullOrWhiteSpace(stripped)) values.Add(stripped);
        }

        return values;
    }

    public static string Normalize(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}