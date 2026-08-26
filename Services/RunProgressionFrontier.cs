using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Separates execution progression from evidence completeness. A missed earlier geofence
/// remains an evidence gap, but once a later sequenced stop has credible completion evidence
/// the operational route must never point the vehicle backwards to that earlier stop.
/// </summary>
public static class RunProgressionFrontier
{
    public static int Sequence(
        IReadOnlyList<LoadStop> orderedStops,
        IReadOnlySet<Guid> completedStopIds,
        Guid? activeStopId = null)
    {
        var frontier = 0;
        foreach (var stop in orderedStops)
        {
            if (completedStopIds.Contains(stop.Id) || activeStopId == stop.Id)
                frontier = Math.Max(frontier, stop.Sequence);
        }
        return frontier;
    }

    public static LoadStop? NextOperationalStop(
        IReadOnlyList<LoadStop> orderedStops,
        IReadOnlySet<Guid> completedStopIds,
        Guid? activeStopId = null)
    {
        var frontier = Sequence(orderedStops, completedStopIds, activeStopId);
        return orderedStops.FirstOrDefault(stop => stop.Sequence > frontier && !completedStopIds.Contains(stop.Id));
    }

    public static IReadOnlyList<LoadStop> RemainingOperationalStops(
        IReadOnlyList<LoadStop> orderedStops,
        IReadOnlySet<Guid> completedStopIds,
        Guid? activeStopId = null)
    {
        var frontier = Sequence(orderedStops, completedStopIds, activeStopId);
        return orderedStops
            .Where(stop => stop.Sequence > frontier && !completedStopIds.Contains(stop.Id))
            .ToList();
    }

    public static IReadOnlyList<LoadStop> EvidenceGapsBeforeFrontier(
        IReadOnlyList<LoadStop> orderedStops,
        IReadOnlySet<Guid> completedStopIds,
        Guid? activeStopId = null)
    {
        var frontier = Sequence(orderedStops, completedStopIds, activeStopId);
        return orderedStops
            .Where(stop => stop.Sequence < frontier && !completedStopIds.Contains(stop.Id))
            .ToList();
    }

    public static bool FinalStopCompleted(IReadOnlyList<LoadStop> orderedStops, IReadOnlySet<Guid> completedStopIds)
    {
        var final = orderedStops.LastOrDefault();
        return final is not null && completedStopIds.Contains(final.Id);
    }
}
