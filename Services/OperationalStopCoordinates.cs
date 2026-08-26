using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Resolves a routable operational coordinate for a planned stop. Site Master coordinates
/// remain authoritative when present; otherwise a uniquely matching approved DOT/Falcon
/// geofence centre is used so an already-running journey is not left without an ETA simply
/// because the geofence-only delivery location has not yet been promoted into Site Master.
/// </summary>
public static class OperationalStopCoordinates
{
    public static (decimal Longitude, decimal Latitude)? Resolve(LoadStop stop)
    {
        if (stop.Longitude is not null && stop.Latitude is not null)
            return (stop.Longitude.Value, stop.Latitude.Value);

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
