using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Bridges planner-facing stop labels to the Falcon geofence naming convention
/// without mutating the operational plan returned to the UI.
/// </summary>
public static class GeofencePlanningMatch
{
    private const int CredibleCompletedVisitMinutes = 2;

    private static readonly HashSet<string> NoiseTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "NWF", "NATURES", "WAY", "COLLECT", "COLLECTION", "DELIVER", "DELIVERY", "CUSTOMER", "RDC", "SITE", "DEPOT"
    };

    public static IReadOnlyList<Load> PrepareLoads(IEnumerable<Load> loads) => loads.Select(PrepareLoad).ToList();

    public static Load PrepareLoad(Load load)
    {
        return new Load
        {
            Id = load.Id,
            Reference = load.Reference,
            PlanningDate = load.PlanningDate,
            Status = load.Status,
            VehicleId = load.VehicleId,
            DriverId = load.DriverId,
            TrailerId = load.TrailerId,
            CreatedAtUtc = load.CreatedAtUtc,
            Stops = (load.Stops ?? []).Select(stop => new LoadStop
            {
                Id = stop.Id,
                LoadId = stop.LoadId,
                OrderId = stop.OrderId,
                Sequence = stop.Sequence,
                Name = MatchStop(stop),
                Address = MatchText(stop.Address),
                Latitude = stop.Latitude,
                Longitude = stop.Longitude,
                PlannedArrivalUtc = stop.PlannedArrivalUtc
            }).ToList()
        };
    }

    /// <summary>
    /// The planner deliberately shows concise labels such as "NWF-Runcton".
    /// Resolve the locality against the actual uploaded Falcon NWF Collection fence
    /// rather than assuming a supplier suffix; the category exports are authoritative.
    /// </summary>
    public static string MatchText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value ?? string.Empty;
        var words = Words(value);
        if (words.Count < 2 || !words[0].Equals("NWF", StringComparison.OrdinalIgnoreCase)) return value;

        var locality = words.Skip(1).ToList();
        var categoryMatches = EmbeddedGeofenceEngine.ApprovedFences
            .Where(fence => string.Equals(fence.Category, "NWF Collection", StringComparison.OrdinalIgnoreCase))
            .Where(fence => ContainsAllWords(fence.Name, locality))
            .ToList();
        if (categoryMatches.Count == 1) return categoryMatches[0].Name;

        var estateMatches = EmbeddedGeofenceEngine.ApprovedFences
            .Where(fence => ContainsAllWords(fence.Name, locality))
            .ToList();
        if (estateMatches.Count == 1) return estateMatches[0].Name;

        // A locality-only value is still safer than carrying the planner's NWF prefix:
        // the engine permits one sufficiently specific token but rejects generic brands.
        return string.Join(' ', locality);
    }

    public static HashSet<Guid> CompletedStopIds(Load load, IEnumerable<DerivedVisit> visits)
    {
        var ordered = (load.Stops ?? []).OrderBy(stop => stop.Sequence).ToList();
        var completed = new HashSet<Guid>();

        foreach (var visit in visits.Where(IsCompletedVisit))
        {
            var primaryId = visit.LoadStopId!.Value;
            completed.Add(primaryId);
            var index = ordered.FindIndex(stop => stop.Id == primaryId);
            if (index < 0) continue;

            // Consecutive planner lines can represent multiple jobs completed during
            // one physical site visit (Run 1 AM currently has Runcton twice). Expand
            // only across adjacent stops that resolve to the same Falcon site.
            for (var i = index - 1; i >= 0 && SamePhysicalSite(ordered[i], visit.Fence); i--)
                completed.Add(ordered[i].Id);
            for (var i = index + 1; i < ordered.Count && SamePhysicalSite(ordered[i], visit.Fence); i++)
                completed.Add(ordered[i].Id);
        }

        return completed;
    }

    public static bool SamePhysicalSite(LoadStop stop, EmbeddedFence fence)
    {
        if (StopInsideFence(stop, fence)) return true;

        var left = MeaningfulTokens(MatchText(stop.Name));
        var right = MeaningfulTokens(fence.Name);
        if (left.Count == 0 || right.Count == 0) return false;
        if (left.SetEquals(right)) return true;
        var common = left.Intersect(right, StringComparer.OrdinalIgnoreCase).ToList();
        if (common.Count >= 2) return true;
        return common.Count == 1 && common[0].Length >= 5 && (left.Count == 1 || right.Count == 1);
    }

    private static bool IsCompletedVisit(DerivedVisit visit)
    {
        if (visit.LoadStopId is null || visit.ExitedAtUtc is null) return false;
        if (visit.ConfirmedAtUtc is not null) return true;

        // RoadTech can retain the same provider event while a vehicle is stationary.
        // Once the next observed point proves that the vehicle has exited the fence,
        // entry-to-exit duration is the authoritative dwell evidence even when the
        // synthetic live observations used while stationary are no longer in history.
        return visit.ExitedAtUtc.Value - visit.EnteredAtUtc >= TimeSpan.FromMinutes(CredibleCompletedVisitMinutes);
    }

    private static string MatchStop(LoadStop stop)
    {
        var coordinateFence = FenceContaining(stop.Latitude, stop.Longitude);
        return coordinateFence?.Name ?? MatchText(stop.Name);
    }

    private static EmbeddedFence? FenceContaining(decimal? latitude, decimal? longitude)
    {
        if (latitude is null || longitude is null) return null;
        return EmbeddedGeofenceEngine.ApprovedFences.FirstOrDefault(fence => Contains(fence.Points, longitude.Value, latitude.Value));
    }

    private static bool StopInsideFence(LoadStop stop, EmbeddedFence fence) =>
        stop.Latitude is not null && stop.Longitude is not null && Contains(fence.Points, stop.Longitude.Value, stop.Latitude.Value);

    private static bool Contains(IReadOnlyList<GeoPoint> points, decimal longitude, decimal latitude)
    {
        var x = (double)longitude;
        var y = (double)latitude;
        var inside = false;
        for (int i = 0, j = points.Count - 1; i < points.Count; j = i++)
        {
            var pi = points[i];
            var pj = points[j];
            if (((pi.Latitude > y) != (pj.Latitude > y)) &&
                x < (pj.Longitude - pi.Longitude) * (y - pi.Latitude) / (pj.Latitude - pi.Latitude) + pi.Longitude)
            {
                inside = !inside;
            }
        }
        return inside;
    }

    private static bool ContainsAllWords(string value, IReadOnlyCollection<string> required)
    {
        var words = Words(value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return required.Count > 0 && required.All(words.Contains);
    }

    private static HashSet<string> MeaningfulTokens(string? value)
    {
        return Words(value)
            .Where(token => token.Length >= 2 && !NoiseTokens.Contains(token))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static List<string> Words(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        var spaced = new string(value.Select(character => char.IsLetterOrDigit(character) ? char.ToUpperInvariant(character) : ' ').ToArray());
        return spaced.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }
}
