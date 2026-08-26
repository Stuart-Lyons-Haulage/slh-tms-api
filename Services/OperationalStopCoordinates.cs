using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Resolves a routable operational coordinate for a planned stop. Coordinates already
/// carried by the plan remain authoritative. When they are absent, canonical Site Master
/// identity (including aliases and manually linked geofences) is preferred before the
/// embedded DOT/Falcon fence-name fallback. This keeps journey/ETA routing on the same
/// master-data identity used by planning rather than inventing a second matcher.
/// </summary>
public static class OperationalStopCoordinates
{
    public static (decimal Longitude, decimal Latitude)? Resolve(
        LoadStop stop,
        PlannerSourceMasterDataResolver? masterData = null)
    {
        if (stop.Longitude is not null && stop.Latitude is not null)
            return (stop.Longitude.Value, stop.Latitude.Value);

        if (masterData is not null)
        {
            var resolved = masterData.Resolve(stop.Name);
            if (resolved.Longitude is not null && resolved.Latitude is not null)
                return (resolved.Longitude.Value, resolved.Latitude.Value);

            if (!string.IsNullOrWhiteSpace(stop.Address))
            {
                resolved = masterData.Resolve(stop.Address);
                if (resolved.Longitude is not null && resolved.Latitude is not null)
                    return (resolved.Longitude.Value, resolved.Latitude.Value);
            }
        }

        var canonical = GeofencePlanningMatch.MatchText(stop.Name);
        var exact = EmbeddedGeofenceEngine.ApprovedFences
            .Where(fence => Normalize(fence.Name) == Normalize(canonical))
            .ToList();
        if (exact.Count == 1)
            return OperationalRunOrigin.FenceCentre(exact[0]);

        var physical = EmbeddedGeofenceEngine.ApprovedFences
            .Where(fence => GeofencePlanningMatch.SamePhysicalSite(stop, fence))
            .ToList();
        return physical.Count == 1 ? OperationalRunOrigin.FenceCentre(physical[0]) : null;
    }

    private static string Normalize(string? value) =>
        new((value ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
}
