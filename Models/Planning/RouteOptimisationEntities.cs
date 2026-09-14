using System.ComponentModel.DataAnnotations;

namespace Slh.Tms.Api.Models.Planning;

// ─── Optimisation analysis (read-only — never mutates a live run) ────────────

public enum OptimisationStatus
{
    Pending,
    Ready,
    PartiallyAccepted,
    Applied,
    Rejected,
    SupersededByPlanChange
}

public enum SuggestionStatus
{
    Pending,
    Accepted,
    Rejected,
    ManuallyEdited,
    CannotApply,
    Applied,
    Failed
}

public enum SuggestionKind
{
    ResequenceStops,
    MoveOrderToRun,
    MergeRuns,
    SplitRun,
    KeepAsIs,
    Warning,
    HardConstraintViolation
}

public sealed class OptimisationAnalysis
{
    public Guid AnalysisId { get; set; } = Guid.NewGuid();
    public DateOnly PlanningDate { get; set; }
    [MaxLength(10)] public required string Period { get; set; }
    public Guid CorrelationId { get; set; } = Guid.NewGuid();
    [MaxLength(40)] public required string OptimiserVersion { get; set; }
    [MaxLength(40)] public string? ScoringConfigVersion { get; set; }
    public OptimisationStatus Status { get; set; } = OptimisationStatus.Pending;
    [MaxLength(80)] public required string OverallResult { get; set; }

    // Aggregate metrics — before state
    public int CurrentRunCount { get; set; }
    public decimal CurrentTotalMiles { get; set; }
    public int CurrentTotalDriveMinutes { get; set; }

    // Aggregate metrics — proposed state
    public int ProposedRunCount { get; set; }
    public decimal ProposedTotalMiles { get; set; }
    public int ProposedTotalDriveMinutes { get; set; }

    public int RunsAffected { get; set; }
    public required string EvidenceJson { get; set; }  // snapshot of input hashes
    public DateTimeOffset AnalysedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? AppliedAtUtc { get; set; }
    [MaxLength(200)] public string? CreatedBy { get; set; }
    [MaxLength(200)] public string? AppliedBy { get; set; }
    [MaxLength(200)] public string? RejectedBy { get; set; }
    public DateTimeOffset? RejectedAtUtc { get; set; }
    [MaxLength(500)] public string? RejectionReason { get; set; }

    // Concurrency: plan version hash at analysis time — stale proposals are blocked
    [MaxLength(128)] public required string PlanVersionHash { get; set; }

    public List<OptimisationSuggestion> Suggestions { get; set; } = [];
    public List<OptimisationAuditEvent> AuditEvents { get; set; } = [];
}

public sealed class OptimisationSuggestion
{
    public Guid SuggestionId { get; set; } = Guid.NewGuid();
    public Guid AnalysisId { get; set; }
    public OptimisationAnalysis Analysis { get; set; } = null!;

    public SuggestionKind Kind { get; set; }
    public SuggestionStatus Status { get; set; } = SuggestionStatus.Pending;

    // Source run (may be null for merge target or split)
    public Guid? SourceRunId { get; set; }
    [MaxLength(80)] public string? SourceRunReference { get; set; }

    // Target run (for move / merge)
    public Guid? TargetRunId { get; set; }
    [MaxLength(80)] public string? TargetRunReference { get; set; }

    // Order being moved (for MoveOrderToRun)
    public Guid? OrderId { get; set; }
    [MaxLength(80)] public string? OrderReference { get; set; }

    [MaxLength(80)] public required string ConstraintClass { get; set; } // Hard / Soft / Warning
    [MaxLength(200)] public required string Title { get; set; }
    public required string Rationale { get; set; }

    // Before/after metrics for this suggestion
    public decimal? CurrentMiles { get; set; }
    public decimal? ProposedMiles { get; set; }
    public int? CurrentDriveMinutes { get; set; }
    public int? ProposedDriveMinutes { get; set; }

    public required string BeforeStopSequenceJson { get; set; } // serialised stop list
    public required string AfterStopSequenceJson { get; set; }

    public decimal ConfidenceScore { get; set; }   // 0–100
    public decimal BenefitScore { get; set; }      // weighted improvement score
    [MaxLength(80)] public string? RoutingSource { get; set; } // AzureMaps / Resilient / Estimated

    [MaxLength(1000)] public string? CannotApplyReason { get; set; }

    public required string ScoreComponentsJson { get; set; }

    // Planner decision
    public DateTimeOffset? DecidedAtUtc { get; set; }
    [MaxLength(200)] public string? DecidedBy { get; set; }
    [MaxLength(500)] public string? DecisionReason { get; set; }
    public required string ManualEditJson { get; set; }  // "{}" when no manual edit

    public int Sequence { get; set; }  // display order within the analysis
}

// ─── Decision history (learning input) ──────────────────────────────────────

public sealed class OptimisationDecisionHistory
{
    public Guid HistoryId { get; set; } = Guid.NewGuid();
    public Guid AnalysisId { get; set; }
    public Guid SuggestionId { get; set; }
    public DateOnly PlanningDate { get; set; }
    [MaxLength(10)] public required string Period { get; set; }
    public SuggestionKind Kind { get; set; }
    [MaxLength(80)] public string? SourceRunReference { get; set; }
    [MaxLength(80)] public string? TargetRunReference { get; set; }
    [MaxLength(80)] public string? CustomerCode { get; set; }
    [MaxLength(80)] public string? CollectionSite { get; set; }
    [MaxLength(80)] public string? DeliverySite { get; set; }
    [MaxLength(80)] public string? RunType { get; set; }
    public SuggestionStatus Decision { get; set; }
    [MaxLength(200)] public string? DecidedBy { get; set; }
    [MaxLength(500)] public string? DecisionReason { get; set; }
    public decimal BenefitScoreAtDecision { get; set; }
    public decimal ConfidenceAtDecision { get; set; }

    // Metrics at time of decision
    public decimal? MilesBefore { get; set; }
    public decimal? MilesAfter { get; set; }
    public int? DriveMinutesBefore { get; set; }
    public int? DriveMinutesAfter { get; set; }

    // Operational outcome (populated post-run by a background job)
    public bool? ActuallyLate { get; set; }
    public decimal? ActualMiles { get; set; }
    public bool? RouteCompleted { get; set; }
    [MaxLength(200)] public string? OutcomeNote { get; set; }
    public DateTimeOffset? OutcomeRecordedAtUtc { get; set; }

    [MaxLength(40)] public required string OptimiserVersion { get; set; }
    [MaxLength(40)] public string? ScoringConfigVersion { get; set; }
    public Guid CorrelationId { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

// ─── Versioned scoring configuration ────────────────────────────────────────

public sealed class ScoringConfiguration
{
    public Guid ConfigId { get; set; } = Guid.NewGuid();
    [MaxLength(40)] public required string Version { get; set; }
    [MaxLength(200)] public required string Description { get; set; }
    public bool IsActive { get; set; }
    public bool IsAdminApproved { get; set; }
    [MaxLength(200)] public string? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAtUtc { get; set; }
    public int MinDecisionsForLearning { get; set; } = 10;
    public double RecencyWeightHalfLifeDays { get; set; } = 30;
    public required string WeightsJson { get; set; }  // serialised ScoringWeights
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    [MaxLength(200)] public string? CreatedBy { get; set; }
}

// ─── Audit events ────────────────────────────────────────────────────────────

public enum OptimisationAuditAction
{
    AnalysisRequested,
    AnalysisCompleted,
    SuggestionAccepted,
    SuggestionRejected,
    SuggestionManuallyEdited,
    AllSuggestionsAccepted,
    AnalysisRejected,
    ApplicationStarted,
    ApplicationCompleted,
    ApplicationFailed,
    OverrideApplied
}

public sealed class OptimisationAuditEvent
{
    public Guid EventId { get; set; } = Guid.NewGuid();
    public Guid AnalysisId { get; set; }
    public OptimisationAnalysis Analysis { get; set; } = null!;
    public Guid? SuggestionId { get; set; }
    public Guid CorrelationId { get; set; }
    public OptimisationAuditAction Action { get; set; }
    public DateOnly PlanningDate { get; set; }
    [MaxLength(200)] public string? Actor { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public required string BeforeStateJson { get; set; }
    public required string AfterStateJson { get; set; }
    [MaxLength(500)] public string? Note { get; set; }
    [MaxLength(40)] public required string OptimiserVersion { get; set; }
}
