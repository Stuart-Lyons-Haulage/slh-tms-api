using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Bridges planner-facing stop labels to the Falcon geofence naming convention
/// without mutating the operational plan returned to the UI.
/// </summary>
public static class GeofencePlanningMatch
{
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
                Name = MatchText(stop.Name),
                Address = MatchText(stop.Address),
                Latitude = stop.Latitude,
                Longitude = stop.Longitude,
                PlannedArrivalUtc = stop.PlannedArrivalUtc
            }).ToList()
        };
    }

    /// <summary>
    /// Falcon NWF collection fences use names such as "Merston (Natures Way)"
    /// while the planner deliberately shows "NWF-Merston". Present both concepts
    /// to the engine so the physical site can be linked safely.
    /// </summary>
    public static string MatchText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value ?? string.Empty;
        var words = Words(value);
        if (words.Count >= 2 && words[0].Equals("NWF", StringComparison.OrdinalIgnoreCase))
            return $"{string.Join(' ', words.Skip(1))} Natures Way";
        return value;
    }

    public static HashSet<Guid> CompletedStopIds(Load load, IEnumerable<DerivedVisit> visits)
    {
        var ordered = (load.Stops ?? []).OrderBy(stop => stop.Sequence).ToList();
        var completed = new HashSet<Guid>();

        foreach (var visit in visits.Where(visit => visit.LoadStopId is not null && visit.ConfirmedAtUtc is not null && visit.ExitedAtUtc is not null))
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
        var left = MeaningfulTokens(MatchText(stop.Name));
        var right = MeaningfulTokens(fence.Name);
        if (left.Count == 0 || right.Count == 0) return false;
        if (left.SetEquals(right)) return true;
        var common = left.Intersect(right, StringComparer.OrdinalIgnoreCase).ToList();
        if (common.Count >= 2) return true;
        return common.Count == 1 && common[0].Length >= 5 && (left.Count == 1 || right.Count == 1);
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
