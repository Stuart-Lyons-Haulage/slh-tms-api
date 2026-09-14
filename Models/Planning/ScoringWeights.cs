namespace Slh.Tms.Api.Models.Planning;

/// <summary>
/// Versioned soft-optimisation scoring weights. Serialised into ScoringConfiguration.WeightsJson.
/// All weights are positive — the optimiser subtracts costs and adds benefits.
/// Changing a weight requires admin approval before it becomes active.
/// </summary>
public sealed class ScoringWeights
{
    // ─── Soft optimisation factor weights (0–100 each) ───────────────────────

    /// <summary>Road mileage reduction (miles saved → points)</summary>
    public double MileageSavingPerMile { get; set; } = 0.5;

    /// <summary>Drive time reduction (minutes saved → points)</summary>
    public double DriveTimeSavingPerMinute { get; set; } = 0.2;

    /// <summary>Penalty per significant heading reversal (backtrack)</summary>
    public double BacktrackPenalty { get; set; } = 8.0;

    /// <summary>Bonus for geographic clustering (stops within same region)</summary>
    public double ClusterBonus { get; set; } = 10.0;

    /// <summary>Penalty per mile of empty running / dead mileage</summary>
    public double EmptyMileagePenalty { get; set; } = 0.8;

    /// <summary>Bonus per stop removed (run consolidation)</summary>
    public double StopReductionBonus { get; set; } = 3.0;

    /// <summary>Bonus per minute of ETA buffer against a deadline</summary>
    public double DeadlineBufferBonusPerMinute { get; set; } = 0.1;

    /// <summary>Penalty when a suggestion risks a late delivery</summary>
    public double LatePenalty { get; set; } = 50.0;

    /// <summary>Bonus when driver workload becomes more balanced</summary>
    public double WorkloadBalanceBonus { get; set; } = 5.0;

    /// <summary>Bonus for improved trailer utilisation (per percent point)</summary>
    public double TrailerUtilisationBonusPerPercent { get; set; } = 0.3;

    /// <summary>Penalty per unnecessary trailer swap</summary>
    public double TrailerSwapPenalty { get; set; } = 15.0;

    /// <summary>Bonus for matching historical driver/vehicle/trailer combinations</summary>
    public double HistoricalCombinationBonus { get; set; } = 6.0;

    /// <summary>Bonus for following a previously accepted planner preference</summary>
    public double PlannerPreferenceBonus { get; set; } = 12.0;

    /// <summary>Penalty for repeating a previously rejected suggestion pattern</summary>
    public double PriorRejectionPenalty { get; set; } = 20.0;

    /// <summary>Bonus for customer-priority / service-critical work</summary>
    public double CustomerPriorityBonus { get; set; } = 15.0;

    /// <summary>
    /// Minimum number of comparable decisions before a learned weight is applied.
    /// Below this threshold, the standard weights above are used unchanged.
    /// </summary>
    public int MinDecisionsForLearning { get; set; } = 10;

    /// <summary>Half-life in days for recency weighting of historical decisions.</summary>
    public double RecencyWeightHalfLifeDays { get; set; } = 30.0;
}
