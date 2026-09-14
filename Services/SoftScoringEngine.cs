using System.Text.Json;
using Slh.Tms.Api.Models.Planning;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Scores each optimisation suggestion against the 20 soft factors in the brief.
/// Uses the active ScoringWeights from the current ScoringConfiguration.
/// Applies learned preference adjustments only when the minimum decision threshold
/// has been reached and the configuration has been admin-approved.
/// </summary>
public sealed class SoftScoringEngine(ILogger<SoftScoringEngine> logger)
{
    public sealed record ScoreResult(
        decimal TotalBenefit,
        decimal ConfidenceScore,
        IReadOnlyList<ScoreComponentDto> Components,
        string Explanation);

    public sealed record LearningContext(
        int AcceptedCount,
        int RejectedCount,
        int TotalComparableDecisions,
        double AcceptanceRate,
        bool HasSufficientData,
        string? LearnedRationale);

    /// <summary>
    /// Score a suggestion given current and proposed route metrics.
    /// </summary>
    public ScoreResult Score(
        ScoringWeights weights,
        decimal currentMiles,
        decimal proposedMiles,
        int currentDriveMinutes,
        int proposedDriveMinutes,
        int stopsBefore,
        int stopsAfter,
        bool reducesBacktracking,
        bool improvesGeographicCluster,
        decimal? deadlineBufferMinutes,
        bool riskOfLateDelivery,
        decimal trailerUtilisationChange,
        bool eliminatesTrailerSwap,
        bool matchesHistoricalCombination,
        bool followsPlannerPreference,
        bool repeatsPriorRejection,
        bool isCustomerPriority,
        LearningContext? learning,
        string routingSource)
    {
        var components = new List<ScoreComponentDto>();
        var total = 0.0;

        // ── SF-1 & SF-2: Mileage and drive time savings ───────────────────────
        var milesSaved = (double)(currentMiles - proposedMiles);
        if (milesSaved != 0)
        {
            var mileagePoints = milesSaved * weights.MileageSavingPerMile;
            total += mileagePoints;
            components.Add(new ScoreComponentDto("MileageSaving",
                Math.Round(mileagePoints, 2),
                $"{milesSaved:0.#} miles saved × {weights.MileageSavingPerMile} = {mileagePoints:0.##} pts"));
        }

        var minutesSaved = currentDriveMinutes - proposedDriveMinutes;
        if (minutesSaved != 0)
        {
            var timePoints = minutesSaved * weights.DriveTimeSavingPerMinute;
            total += timePoints;
            components.Add(new ScoreComponentDto("DriveTimeSaving",
                Math.Round(timePoints, 2),
                $"{minutesSaved} min saved × {weights.DriveTimeSavingPerMinute} = {timePoints:0.##} pts"));
        }

        // ── SF-4 & SF-5: Backtracking and heading changes ─────────────────────
        if (reducesBacktracking)
        {
            total += weights.BacktrackPenalty;  // reward for eliminating a backtrack
            components.Add(new ScoreComponentDto("BacktrackEliminated",
                weights.BacktrackPenalty,
                $"Eliminates backtracking: +{weights.BacktrackPenalty} pts"));
        }

        // ── SF-6: Geographic clustering ───────────────────────────────────────
        if (improvesGeographicCluster)
        {
            total += weights.ClusterBonus;
            components.Add(new ScoreComponentDto("GeographicCluster",
                weights.ClusterBonus,
                $"Improves geographic cluster: +{weights.ClusterBonus} pts"));
        }

        // ── SF-8: Stop count reduction ────────────────────────────────────────
        var stopsReduced = stopsBefore - stopsAfter;
        if (stopsReduced > 0)
        {
            var stopPoints = stopsReduced * weights.StopReductionBonus;
            total += stopPoints;
            components.Add(new ScoreComponentDto("StopReduction",
                Math.Round(stopPoints, 2),
                $"{stopsReduced} stops removed × {weights.StopReductionBonus} = {stopPoints:0.##} pts"));
        }

        // ── SF-9 & SF-10: Deadline buffer and late-delivery risk ──────────────
        if (deadlineBufferMinutes.HasValue && deadlineBufferMinutes.Value > 0)
        {
            var bufferPoints = (double)deadlineBufferMinutes.Value * weights.DeadlineBufferBonusPerMinute;
            total += bufferPoints;
            components.Add(new ScoreComponentDto("DeadlineBuffer",
                Math.Round(bufferPoints, 2),
                $"{deadlineBufferMinutes.Value:0} min deadline buffer × {weights.DeadlineBufferBonusPerMinute} = {bufferPoints:0.##} pts"));
        }

        if (riskOfLateDelivery)
        {
            total -= weights.LatePenalty;
            components.Add(new ScoreComponentDto("LateRisk",
                -weights.LatePenalty,
                $"Risk of late delivery: -{weights.LatePenalty} pts"));
        }

        // ── SF-12 & SF-13: Trailer utilisation and swap avoidance ────────────
        if (trailerUtilisationChange > 0)
        {
            var utilPoints = (double)trailerUtilisationChange * weights.TrailerUtilisationBonusPerPercent;
            total += utilPoints;
            components.Add(new ScoreComponentDto("TrailerUtilisation",
                Math.Round(utilPoints, 2),
                $"+{trailerUtilisationChange:0.#}% utilisation × {weights.TrailerUtilisationBonusPerPercent} = {utilPoints:0.##} pts"));
        }

        if (eliminatesTrailerSwap)
        {
            total += weights.TrailerSwapPenalty;
            components.Add(new ScoreComponentDto("TrailerSwapEliminated",
                weights.TrailerSwapPenalty,
                $"Eliminates unnecessary trailer swap: +{weights.TrailerSwapPenalty} pts"));
        }

        // ── SF-15 & SF-16: Historical combinations and planner preferences ────
        if (matchesHistoricalCombination)
        {
            total += weights.HistoricalCombinationBonus;
            components.Add(new ScoreComponentDto("HistoricalCombination",
                weights.HistoricalCombinationBonus,
                $"Matches historical driver/vehicle/trailer combination: +{weights.HistoricalCombinationBonus} pts"));
        }

        if (followsPlannerPreference)
        {
            total += weights.PlannerPreferenceBonus;
            components.Add(new ScoreComponentDto("PlannerPreference",
                weights.PlannerPreferenceBonus,
                $"Follows a previously accepted planner preference: +{weights.PlannerPreferenceBonus} pts"));
        }

        // ── SF-17: Prior rejection penalty ───────────────────────────────────
        if (repeatsPriorRejection)
        {
            total -= weights.PriorRejectionPenalty;
            components.Add(new ScoreComponentDto("PriorRejection",
                -weights.PriorRejectionPenalty,
                $"Repeats a previously rejected suggestion pattern: -{weights.PriorRejectionPenalty} pts"));
        }

        // ── SF-18: Customer priority ──────────────────────────────────────────
        if (isCustomerPriority)
        {
            total += weights.CustomerPriorityBonus;
            components.Add(new ScoreComponentDto("CustomerPriority",
                weights.CustomerPriorityBonus,
                $"Customer-priority or service-critical work: +{weights.CustomerPriorityBonus} pts"));
        }

        // ── Learning adjustment ───────────────────────────────────────────────
        var confidence = CalculateConfidence(learning, routingSource, components);
        string explanation;

        if (learning is { HasSufficientData: true } && !string.IsNullOrWhiteSpace(learning.LearnedRationale))
        {
            explanation = learning.LearnedRationale;
            // Small recency-weighted bonus for high acceptance rate
            if (learning.AcceptanceRate >= 0.8)
            {
                var learnedBonus = weights.PlannerPreferenceBonus * 0.5;
                total += learnedBonus;
                components.Add(new ScoreComponentDto("LearnedPreference",
                    Math.Round(learnedBonus, 2),
                    $"Learned preference (acceptance rate {learning.AcceptanceRate:P0} from {learning.TotalComparableDecisions} decisions): +{learnedBonus:0.##} pts"));
            }
        }
        else
        {
            explanation = BuildExplanation(components, routingSource);
        }

        return new ScoreResult(
            Math.Round((decimal)total, 2),
            confidence,
            components,
            explanation);
    }

    private static decimal CalculateConfidence(
        LearningContext? learning,
        string routingSource,
        IReadOnlyList<ScoreComponentDto> components)
    {
        // Start at 70 for a routing-backed suggestion
        var confidence = routingSource == "AzureMaps" ? 75.0 : 55.0;

        // Learning data raises or lowers confidence
        if (learning is not null)
        {
            if (learning.HasSufficientData)
                confidence += Math.Min(20.0, learning.TotalComparableDecisions * 0.5);
            else
                confidence -= 10.0;  // insufficient data — lower confidence
        }

        // Hard rule: if prior rejection pattern is present, confidence drops
        if (components.Any(c => c.Code == "PriorRejection"))
            confidence -= 15.0;

        // Late risk tanks confidence
        if (components.Any(c => c.Code == "LateRisk"))
            confidence -= 20.0;

        return Math.Round(Math.Clamp((decimal)confidence, 0m, 100m), 1);
    }

    private static string BuildExplanation(IReadOnlyList<ScoreComponentDto> components, string routingSource)
    {
        var positive = components.Where(c => c.Value > 0).OrderByDescending(c => c.Value).Take(3)
            .Select(c => c.Explanation);
        var negative = components.Where(c => c.Value < 0).OrderBy(c => c.Value).Take(2)
            .Select(c => c.Explanation);

        var parts = positive.Concat(negative).ToList();
        var source = routingSource == "AzureMaps" ? "Azure Maps live traffic" : "road estimate";
        return parts.Count > 0
            ? $"Scored using {source}. Key factors: {string.Join("; ", parts)}."
            : $"Scored using {source}. No significant factors identified.";
    }
}
