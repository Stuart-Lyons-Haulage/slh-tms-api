using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Builds the execution journey used by ETA/geofence wallboards without changing the
/// underlying order lines. Consecutive rows for the same action at the same physical
/// site represent one vehicle visit, even when several orders are handled there.
/// </summary>
public static class WallboardPhysicalStops
{
    public static IReadOnlyList<LoadStop> Collapse(IEnumerable<LoadStop>? stops)
    {
        var result = new List<LoadStop>();
        string? previousKey = null;

        foreach (var source in (stops ?? []).OrderBy(stop => stop.Sequence))
        {
            var key = PhysicalVisitKey(source.Name);
            if (result.Count > 0 && key == previousKey) continue;

            result.Add(new LoadStop
            {
                Id = source.Id,
                LoadId = source.LoadId,
                OrderId = source.OrderId,
                Sequence = result.Count + 1,
                Name = source.Name,
                Address = source.Address,
                Latitude = source.Latitude,
                Longitude = source.Longitude,
                PlannedArrivalUtc = source.PlannedArrivalUtc,
                PlannerNote = source.PlannerNote
            });
            previousKey = key;
        }

        return result;
    }

    public static void Apply(IEnumerable<Load> loads)
    {
        foreach (var load in loads) load.Stops = Collapse(load.Stops).ToList();
    }

    private static string PhysicalVisitKey(string? name)
    {
        var value = name?.Trim() ?? string.Empty;
        var action = value.StartsWith("Deliver", StringComparison.OrdinalIgnoreCase) ? "D"
            : value.StartsWith("Collect", StringComparison.OrdinalIgnoreCase) ? "C" : "S";
        var site = GeofencePlanningMatch.MatchText(value);
        var normalized = new string(site.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        return $"{action}:{normalized}";
    }
}
