using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Selects the customer-facing final delivery destination for a run. A route may contain
/// operational work after the last customer delivery (for example a return/depot stop),
/// so ETA displays must not blindly use the highest stop sequence as the customer target.
/// </summary>
public static class RunFinalDestination
{
    public static LoadStop? Select(IEnumerable<LoadStop>? stops)
    {
        var ordered = (stops ?? []).OrderBy(stop => stop.Sequence).ToList();
        if (ordered.Count == 0) return null;

        return ordered.LastOrDefault(IsDeliveryDestination) ?? ordered[^1];
    }

    public static bool IsDeliveryDestination(LoadStop stop) =>
        stop.OrderId is not null ||
        stop.Name.StartsWith("Deliver", StringComparison.OrdinalIgnoreCase);
}
