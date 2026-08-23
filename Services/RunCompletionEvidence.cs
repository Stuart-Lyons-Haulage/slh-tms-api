using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

internal static class RunCompletionEvidence
{
    public static bool CanAutoComplete(Load load, IReadOnlySet<Guid> confirmedDepartedStopIds)
    {
        if (load.Status != LoadStatus.InProgress) return false;
        var plannedStops = (load.Stops ?? []).OrderBy(stop => stop.Sequence).ToList();
        if (plannedStops.Count == 0) return false;
        if (plannedStops.Any(stop => stop.Id == Guid.Empty)) return false;
        return plannedStops.All(stop => confirmedDepartedStopIds.Contains(stop.Id));
    }
}
