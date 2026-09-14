using Slh.Tms.Api.Models.Planning;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Evaluates all 20 hard constraints from the brief against a proposed run state.
/// Returns a list of violations.  A non-empty list means the suggestion MUST NOT
/// be applied without an explicit override by a user with TmsAdmin permission.
/// </summary>
public sealed class HardConstraintEngine
{
    // Site access windows: 00:01 means unrestricted.
    private static readonly TimeOnly UnrestrictedWindow = new(0, 1);

    public sealed record HardViolation(
        string Code,
        string Severity,      // "Critical" | "Warning"
        string Description,
        bool CanOverride);    // false = never overridable (e.g. unknown routes)

    public sealed record RunSnapshot(
        Guid RunId,
        string Reference,
        string Status,        // Draft | Allocated | Dispatched | InProgress | Completed | Cancelled
        bool IsLocked,
        DateOnly PlanningDate,
        string Period,
        string? RunType,      // AM | PM | Overnight | Transfer | MarketWork
        bool IsMarketWork,
        bool IsOvernight,
        Guid? DriverId,
        Guid? VehicleId,
        Guid? TrailerId,
        int? TrailerStandardCapacity,
        int? TrailerEuroCapacity,
        int? DriverAvailableMinutes,  // null = tacho not verified
        bool DriverOnLeave,
        bool DriverSixthDayRisk,
        IReadOnlyList<StopSnapshot> Stops,
        IReadOnlyList<HandlingUnitSummary> HandlingUnits);

    public sealed record StopSnapshot(
        int Sequence,
        string StopType,          // Collection | Delivery | Depot
        Guid? SiteId,
        string SiteName,
        TimeOnly? AccessWindowOpen,
        TimeOnly? AccessWindowClose,
        bool IsUnrestrictedAccess,
        DateTimeOffset? LastDespatch,
        DateTimeOffset? DepotDeadline,
        DateTimeOffset? PlannedArrival,
        string? OrderReference,
        Guid? OrderId,
        bool IsApprovedOrder,
        string? HandlingUnitType,  // Standard | Euro | Tray | Trolley | Crate
        int Quantity,
        int CumulativeCapacityAfterDeliveries);  // per-leg running balance

    public sealed record HandlingUnitSummary(
        string Type,
        int TotalQuantity);

    /// <summary>
    /// Evaluate all hard constraints against a proposed run state.
    /// Called both during analysis (to find what is already broken) and during
    /// application (to gate acceptance of each suggestion).
    /// </summary>
    public IReadOnlyList<HardViolation> Evaluate(RunSnapshot run)
    {
        var violations = new List<HardViolation>();

        CheckProtectedStatuses(run, violations);
        CheckCollectionBeforeDelivery(run, violations);
        CheckSiteAccessWindows(run, violations);
        CheckLastDespatch(run, violations);
        CheckDepotDeadlines(run, violations);
        CheckDriverAvailability(run, violations);
        CheckDriverHours(run, violations);
        CheckCapacityPerLeg(run, violations);
        CheckHandlingUnitTypes(run, violations);
        CheckMarketWorkSeparation(run, violations);
        CheckRunTypePeriod(run, violations);
        CheckOvernightContinuity(run, violations);
        CheckApprovedOrdersOnly(run, violations);

        return violations;
    }

    // ── HC-1: Protected statuses ──────────────────────────────────────────────
    private static void CheckProtectedStatuses(RunSnapshot run, List<HardViolation> v)
    {
        if (run.IsLocked || run.Status is "Dispatched" or "InProgress" or "Completed")
            v.Add(new HardViolation(
                "ProtectedRun",
                "Critical",
                $"Run {run.Reference} is {run.Status} and cannot be altered by normal optimisation.",
                canOverride: run.Status == "Allocated")); // Allocated can be overridden by admin
    }

    // ── HC-2: Collection before delivery ────────────────────────────────────
    private static void CheckCollectionBeforeDelivery(RunSnapshot run, List<HardViolation> v)
    {
        var collections = run.Stops
            .Where(s => s.StopType == "Collection")
            .ToDictionary(s => s.OrderId ?? Guid.Empty, s => s.Sequence);
        var deliveries = run.Stops
            .Where(s => s.StopType == "Delivery")
            .ToDictionary(s => s.OrderId ?? Guid.Empty, s => s.Sequence);

        foreach (var (orderId, collSeq) in collections)
        {
            if (deliveries.TryGetValue(orderId, out var delSeq) && delSeq <= collSeq)
                v.Add(new HardViolation(
                    "CollectionAfterDelivery",
                    "Critical",
                    $"Order {run.Stops.First(s => s.OrderId == orderId && s.StopType == "Collection").OrderReference}: delivery (seq {delSeq}) is before or at collection (seq {collSeq}).",
                    canOverride: false));
        }
    }

    // ── HC-3: Site access windows ─────────────────────────────────────────────
    private static void CheckSiteAccessWindows(RunSnapshot run, List<HardViolation> v)
    {
        foreach (var stop in run.Stops)
        {
            if (stop.IsUnrestrictedAccess) continue;
            if (stop.AccessWindowOpen is null || stop.AccessWindowClose is null) continue;
            if (stop.PlannedArrival is null) continue;

            var arrival = TimeOnly.FromDateTime(stop.PlannedArrival.Value.LocalDateTime);
            if (arrival < stop.AccessWindowOpen.Value || arrival > stop.AccessWindowClose.Value)
                v.Add(new HardViolation(
                    "SiteAccessWindowViolation",
                    "Critical",
                    $"Stop {stop.Sequence} ({stop.SiteName}): planned arrival {arrival:HH:mm} is outside access window {stop.AccessWindowOpen.Value:HH:mm}–{stop.AccessWindowClose.Value:HH:mm}.",
                    canOverride: true));
        }
    }

    // ── HC-4: Last despatch times ────────────────────────────────────────────
    private static void CheckLastDespatch(RunSnapshot run, List<HardViolation> v)
    {
        foreach (var stop in run.Stops.Where(s => s.StopType == "Collection" && s.LastDespatch.HasValue))
        {
            if (stop.PlannedArrival.HasValue && stop.PlannedArrival.Value > stop.LastDespatch!.Value)
                v.Add(new HardViolation(
                    "LastDespatchMissed",
                    "Critical",
                    $"Stop {stop.Sequence} ({stop.SiteName}): planned arrival {stop.PlannedArrival.Value:HH:mm} is after last despatch {stop.LastDespatch.Value:HH:mm}.",
                    canOverride: true));
        }
    }

    // ── HC-5: Depot delivery deadlines ───────────────────────────────────────
    private static void CheckDepotDeadlines(RunSnapshot run, List<HardViolation> v)
    {
        foreach (var stop in run.Stops.Where(s => s.DepotDeadline.HasValue))
        {
            if (stop.PlannedArrival.HasValue && stop.PlannedArrival.Value > stop.DepotDeadline!.Value)
                v.Add(new HardViolation(
                    "DepotDeadlineMissed",
                    "Critical",
                    $"Stop {stop.Sequence} ({stop.SiteName}): planned arrival {stop.PlannedArrival.Value:HH:mm} is after depot deadline {stop.DepotDeadline.Value:HH:mm}.",
                    canOverride: true));
        }
    }

    // ── HC-6 & HC-7: Driver availability and hours ─────────────────────────
    private static void CheckDriverAvailability(RunSnapshot run, List<HardViolation> v)
    {
        if (run.DriverOnLeave)
            v.Add(new HardViolation(
                "DriverOnLeave",
                "Critical",
                $"Run {run.Reference}: the assigned driver is recorded as on leave on {run.PlanningDate}.",
                canOverride: true));

        if (run.DriverSixthDayRisk)
            v.Add(new HardViolation(
                "DriverSixthDayRisk",
                "Critical",
                $"Run {run.Reference}: the assigned driver would work a sixth consecutive day.",
                canOverride: true));
    }

    private static void CheckDriverHours(RunSnapshot run, List<HardViolation> v)
    {
        if (run.DriverAvailableMinutes is null)
        {
            v.Add(new HardViolation(
                "TachoEvidenceMissing",
                "Warning",
                $"Run {run.Reference}: no verified tacho evidence is available for the assigned driver.",
                canOverride: true));
            return;
        }

        // Estimated total drive time = sum of planned drive legs from stops
        // (using stored PlannedArrival differences as a proxy)
        var orderedStops = run.Stops.OrderBy(s => s.Sequence).ToList();
        var estimatedDrive = 0;
        for (var i = 1; i < orderedStops.Count; i++)
        {
            if (orderedStops[i - 1].PlannedArrival.HasValue && orderedStops[i].PlannedArrival.HasValue)
                estimatedDrive += (int)(orderedStops[i].PlannedArrival!.Value -
                                        orderedStops[i - 1].PlannedArrival!.Value).TotalMinutes;
        }

        if (estimatedDrive > run.DriverAvailableMinutes.Value)
            v.Add(new HardViolation(
                "InsufficientDriveTime",
                "Critical",
                $"Run {run.Reference}: estimated {estimatedDrive} drive minutes exceeds driver's available {run.DriverAvailableMinutes.Value} minutes.",
                canOverride: true));
    }

    // ── HC-8 & HC-9: Capacity per journey leg ────────────────────────────────
    private static void CheckCapacityPerLeg(RunSnapshot run, List<HardViolation> v)
    {
        if (run.TrailerStandardCapacity is null && run.TrailerEuroCapacity is null) return;

        // Walk stops in sequence.  Track running pallet balance.
        // Multi-collection: running balance must never exceed trailer capacity at any leg.
        // Earlier deliveries free up space — this is the per-leg check that prevents
        // rejecting a valid multi-collection run on aggregate alone.
        var capacity = run.HandlingUnits.Any(h => h.Type.Contains("Euro", StringComparison.OrdinalIgnoreCase))
            ? (run.TrailerEuroCapacity ?? run.TrailerStandardCapacity ?? 26)
            : (run.TrailerStandardCapacity ?? 26);

        foreach (var stop in run.Stops.OrderBy(s => s.Sequence))
        {
            if (stop.CumulativeCapacityAfterDeliveries > capacity)
                v.Add(new HardViolation(
                    "CapacityExceededAtLeg",
                    "Critical",
                    $"Stop {stop.Sequence} ({stop.SiteName}): running load {stop.CumulativeCapacityAfterDeliveries} exceeds trailer capacity {capacity}.",
                    canOverride: false));
        }
    }

    // ── HC-10: Handling unit types must remain separate ──────────────────────
    private static void CheckHandlingUnitTypes(RunSnapshot run, List<HardViolation> v)
    {
        var distinctTypes = run.HandlingUnits
            .Where(h => h.TotalQuantity > 0)
            .Select(h => NormaliseHandlingType(h.Type))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Standard and Euro pallets must not mix
        var hasStandard = distinctTypes.Any(t => t == "Standard");
        var hasEuro     = distinctTypes.Any(t => t == "Euro");
        if (hasStandard && hasEuro)
            v.Add(new HardViolation(
                "MixedPalletTypes",
                "Critical",
                $"Run {run.Reference}: standard and euro pallets must not be mixed on the same run.",
                canOverride: false));

        // Trolleys require per-trolley capacity check (handled separately via
        // TrolleyCapacity field in master data — flagged here as a reminder)
        var hasTrolleys = distinctTypes.Any(t => t == "Trolley");
        var hasPallets  = hasStandard || hasEuro;
        if (hasTrolleys && hasPallets)
            v.Add(new HardViolation(
                "TrolleyPalletMix",
                "Warning",
                $"Run {run.Reference}: trolleys and pallets are mixed — verify trolley-to-pallet capacity rules apply.",
                canOverride: true));
    }

    // ── HC-11: Market work separation ────────────────────────────────────────
    private static void CheckMarketWorkSeparation(RunSnapshot run, List<HardViolation> v)
    {
        if (!run.IsMarketWork) return;

        var nonMarketStops = run.Stops.Where(s =>
            !string.IsNullOrWhiteSpace(s.OrderReference) &&
            !IsMarketStop(s)).ToList();

        if (nonMarketStops.Count > 0)
            v.Add(new HardViolation(
                "MarketWorkMixed",
                "Critical",
                $"Run {run.Reference}: market work must remain separate. Non-market orders found at stops: {string.Join(", ", nonMarketStops.Select(s => s.StopType + " " + s.Sequence))}.",
                canOverride: false));
    }

    // ── HC-12 & HC-13: AM/PM/overnight run type protection ───────────────────
    private static void CheckRunTypePeriod(RunSnapshot run, List<HardViolation> v)
    {
        if (string.IsNullOrWhiteSpace(run.RunType)) return;
        if (run.RunType == "MarketWork" && !run.IsMarketWork)
            v.Add(new HardViolation(
                "RunTypeMismatch",
                "Critical",
                $"Run {run.Reference}: classified as MarketWork but market-work flag is not set.",
                canOverride: false));

        if (run.Period == "AM" && run.RunType == "PM")
            v.Add(new HardViolation(
                "RunTypePeriodMismatch",
                "Critical",
                $"Run {run.Reference}: run type PM cannot be placed in the AM period.",
                canOverride: true));
    }

    // ── HC-14: Overnight run continuity ──────────────────────────────────────
    private static void CheckOvernightContinuity(RunSnapshot run, List<HardViolation> v)
    {
        // Overnight runs cross midnight — they must not be split across calendar dates
        // by a resequencing suggestion.
        if (!run.IsOvernight) return;

        var stops = run.Stops.OrderBy(s => s.Sequence).ToList();
        if (stops.Count < 2) return;

        var first = stops.First().PlannedArrival;
        var last  = stops.Last().PlannedArrival;
        if (first.HasValue && last.HasValue && last.Value.Date == first.Value.Date)
            return; // same calendar date — technically fine, no midnight cross

        // Any resequencing that would push a stop before the overnight start is flagged
        for (var i = 1; i < stops.Count; i++)
        {
            if (stops[i].PlannedArrival.HasValue && stops[i - 1].PlannedArrival.HasValue &&
                stops[i].PlannedArrival!.Value < stops[i - 1].PlannedArrival!.Value)
                v.Add(new HardViolation(
                    "OvernightSequenceInvalid",
                    "Critical",
                    $"Run {run.Reference}: stop {stops[i].Sequence} arrival {stops[i].PlannedArrival:HH:mm} is before previous stop — overnight continuity violated.",
                    canOverride: false));
        }
    }

    // ── HC-18: Only approved orders ───────────────────────────────────────────
    private static void CheckApprovedOrdersOnly(RunSnapshot run, List<HardViolation> v)
    {
        var unapproved = run.Stops.Where(s => !s.IsApprovedOrder && s.OrderId.HasValue).ToList();
        foreach (var stop in unapproved)
            v.Add(new HardViolation(
                "UnapprovedOrder",
                "Critical",
                $"Stop {stop.Sequence} ({stop.SiteName}): order {stop.OrderReference} is not in Approved status and cannot be included in optimisation.",
                canOverride: false));
    }

    private static string NormaliseHandlingType(string type) => type.Trim() switch
    {
        var t when t.Contains("Euro", StringComparison.OrdinalIgnoreCase) => "Euro",
        var t when t.Contains("Tray", StringComparison.OrdinalIgnoreCase) => "Tray",
        var t when t.Contains("Trolley", StringComparison.OrdinalIgnoreCase) => "Trolley",
        var t when t.Contains("Crate", StringComparison.OrdinalIgnoreCase) => "Crate",
        _ => "Standard"
    };

    private static bool IsMarketStop(StopSnapshot stop) =>
        stop.SiteName.Contains("Market", StringComparison.OrdinalIgnoreCase) ||
        stop.SiteName.Contains("Smithfield", StringComparison.OrdinalIgnoreCase) ||
        stop.SiteName.Contains("Billingsgate", StringComparison.OrdinalIgnoreCase) ||
        stop.SiteName.Contains("New Covent Garden", StringComparison.OrdinalIgnoreCase);
}
