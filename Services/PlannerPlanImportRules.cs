using Slh.Tms.Api.Contracts;

namespace Slh.Tms.Api.Services;

public static class PlannerPlanImportRules
{
    public static string TmsReference(DateOnly planningDate, string runRef)
    {
        if (string.IsNullOrWhiteSpace(runRef)) throw new ArgumentException("RunRef is required.", nameof(runRef));
        var clean = new string(runRef.Trim().ToUpperInvariant().Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').ToArray());
        if (string.IsNullOrWhiteSpace(clean)) throw new ArgumentException("RunRef does not contain a usable identifier.", nameof(runRef));
        var reference = $"PLAN-{planningDate:yyyyMMdd}-{clean}";
        return reference[..Math.Min(reference.Length, 80)];
    }

    public static PalletCapacityResult Capacity(PlannerPlanRunRequest run)
    {
        decimal standard = 0m, euro = 0m, unknown = 0m;
        foreach (var stop in run.Stops ?? [])
        {
            var pallets = stop.Pallets ?? 0m;
            if (pallets <= 0) continue;
            var type = stop.PalletType?.Trim();
            if (string.Equals(type, "std", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "standard", StringComparison.OrdinalIgnoreCase)) standard += pallets;
            else if (string.Equals(type, "euro", StringComparison.OrdinalIgnoreCase)) euro += pallets;
            else unknown += pallets;
        }
        return PalletCapacityCalculator.Calculate(standard, euro, unknown);
    }

    public static string StopName(PlannerPlanStopRequest stop)
    {
        var collection = string.IsNullOrWhiteSpace(stop.CollectionSite) ? null : stop.CollectionSite.Trim();
        var delivery = string.IsNullOrWhiteSpace(stop.DeliverySite) ? null : stop.DeliverySite.Trim();
        return (collection, delivery) switch
        {
            ({ } c, { } d) => $"{c} → {d}",
            ({ } c, null) => c,
            (null, { } d) => d,
            _ => "Planner stop"
        };
    }

    public static string BuildPlannerNotes(PlannerPlanRunRequest run, PalletCapacityResult capacity)
    {
        var source = run.Source is null ? null : string.Join(" / ", new[] { run.Source.Workbook, run.Source.Sheet }.Where(v => !string.IsNullOrWhiteSpace(v)));
        var parts = new[]
        {
            run.PlannerNote,
            string.IsNullOrWhiteSpace(run.RunType) ? null : $"Run type: {run.RunType}",
            string.IsNullOrWhiteSpace(run.PlannerRun) ? null : $"Planner run: {run.PlannerRun}",
            string.IsNullOrWhiteSpace(run.ReconciliationStatus) ? null : $"Reconciliation: {run.ReconciliationStatus}",
            string.IsNullOrWhiteSpace(source) ? null : $"Source: {source}",
            $"Capacity: {capacity.StandardPallets:0.##} Standard + {capacity.EuroPallets:0.##} Euro + {capacity.UnknownPallets:0.##} unknown = {capacity.UtilisationPercent:0.0}% ({capacity.Status})"
        }.Where(v => !string.IsNullOrWhiteSpace(v));
        var text = string.Join(" | ", parts);
        return text[..Math.Min(text.Length, 1000)];
    }
}
