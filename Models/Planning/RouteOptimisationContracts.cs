using System.ComponentModel.DataAnnotations;

namespace Slh.Tms.Api.Models.Planning;

// ─── Request DTOs ────────────────────────────────────────────────────────────

/// <summary>
/// Triggers read-only analysis of the current plan for the given date and period.
/// No live run, order or allocation is altered until the planner explicitly applies.
/// </summary>
public sealed record AnalyseRouteRequest(
    DateOnly PlanningDate,
    string? Period = null,
    bool IncludeLockedRuns = false);

/// <summary>Accept one suggestion by id, with an optional manual edit payload.</summary>
public sealed record AcceptSuggestionRequest(
    Guid SuggestionId,
    string? ManualEditJson = null,
    string? Reason = null);

/// <summary>Reject one suggestion by id with a selectable + free-text reason.</summary>
public sealed record RejectSuggestionRequest(
    Guid SuggestionId,
    string RejectCode,
    string? FreeText = null);

/// <summary>Bulk accept all currently-Pending suggestions in one call.</summary>
public sealed record AcceptAllSuggestionsRequest(
    Guid AnalysisId,
    bool AcknowledgeWarnings = false,
    bool AcknowledgeDriverHoursRisk = false);

/// <summary>Apply the accepted subset of suggestions transactionally to live runs.</summary>
public sealed record ApplyOptimisationRequest(
    Guid AnalysisId,
    bool AcknowledgeWarnings = false,
    bool AcknowledgeDriverHoursRisk = false,
    string? ConfirmationNote = null);

/// <summary>Reject the entire analysis without applying anything.</summary>
public sealed record RejectAnalysisRequest(
    Guid AnalysisId,
    string RejectCode,
    string? FreeText = null);

/// <summary>
/// Override a hard constraint for a specific suggestion.
/// Requires elevated permission (TmsAdmin).
/// </summary>
public sealed record OverrideSuggestionRequest(
    Guid SuggestionId,
    string OverrideReason);

// ─── Response DTOs ───────────────────────────────────────────────────────────

public sealed record StopDto(
    Guid? SiteId,
    string SiteName,
    int Sequence,
    string StopType,           // Collection / Delivery / Depot
    DateTimeOffset? PlannedArrival,
    DateTimeOffset? AccessWindowOpen,
    DateTimeOffset? AccessWindowClose,
    DateTimeOffset? LastDespatch,
    bool IsUnrestricted);      // true when window is 00:01 (unrestricted)

public sealed record RunSummaryDto(
    Guid RunId,
    string Reference,
    string Status,
    string? DriverName,
    string? VehicleReg,
    string? TrailerNumber,
    int StopCount,
    int OrderCount,
    int Pallets,
    int Trolleys,
    int Trays,
    decimal CapacityUnits,
    decimal? EstimatedMiles,
    int? EstimatedDriveMinutes,
    string? RoutingSource,
    IReadOnlyList<StopDto> Stops);

public sealed record SuggestionDto(
    Guid SuggestionId,
    string Kind,
    string Status,
    string ConstraintClass,
    string Title,
    string Rationale,
    string? SourceRunReference,
    string? TargetRunReference,
    string? OrderReference,
    decimal? CurrentMiles,
    decimal? ProposedMiles,
    int? CurrentDriveMinutes,
    int? ProposedDriveMinutes,
    IReadOnlyList<StopDto> BeforeStops,
    IReadOnlyList<StopDto> AfterStops,
    decimal ConfidenceScore,
    decimal BenefitScore,
    string? RoutingSource,
    string? CannotApplyReason,
    IReadOnlyList<ScoreComponentDto> ScoreComponents,
    string? DecisionReason,
    int Sequence);

public sealed record ScoreComponentDto(string Code, double Value, string Explanation);

public sealed record OptimisationAnalysisDto(
    Guid AnalysisId,
    DateOnly PlanningDate,
    string Period,
    Guid CorrelationId,
    string OptimiserVersion,
    string? ScoringConfigVersion,
    string Status,
    string OverallResult,
    int CurrentRunCount,
    decimal CurrentTotalMiles,
    int CurrentTotalDriveMinutes,
    int ProposedRunCount,
    decimal ProposedTotalMiles,
    int ProposedTotalDriveMinutes,
    int RunsAffected,
    DateTimeOffset AnalysedAtUtc,
    IReadOnlyList<SuggestionDto> Improvements,
    IReadOnlyList<SuggestionDto> Warnings,
    IReadOnlyList<SuggestionDto> HardFailures,
    IReadOnlyList<SuggestionDto> Unchanged,
    IReadOnlyList<SuggestionDto> ManualReviewRequired,
    IReadOnlyList<SuggestionDto> Accepted,
    IReadOnlyList<SuggestionDto> Rejected,
    IReadOnlyList<RunSummaryDto> AffectedRuns);

public sealed record ApplyOptimisationResult(
    Guid AnalysisId,
    string Status,
    int SuggestionsApplied,
    int RunsModified,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Failures);

public sealed record SuggestionDecisionResult(
    Guid SuggestionId,
    string NewStatus,
    string Message);

// ─── Rejection reason catalogue ───────────────────────────────────────────────

public static class RejectReasonCodes
{
    public const string PlannerJudgement = "PlannerJudgement";
    public const string CustomerInstructions = "CustomerInstructions";
    public const string SiteRestriction = "SiteRestriction";
    public const string TrailerRestriction = "TrailerRestriction";
    public const string DriverPreference = "DriverPreference";
    public const string OperationalPracticality = "OperationalPracticality";
    public const string TimingConflict = "TimingConflict";
    public const string InsufficientConfidence = "InsufficientConfidence";
    public const string DataQuality = "DataQuality";
    public const string Other = "Other";

    public static readonly IReadOnlyList<string> All =
    [
        PlannerJudgement, CustomerInstructions, SiteRestriction,
        TrailerRestriction, DriverPreference, OperationalPracticality,
        TimingConflict, InsufficientConfidence, DataQuality, Other
    ];

    public static bool IsValid(string code) => All.Contains(code, StringComparer.OrdinalIgnoreCase);
}
