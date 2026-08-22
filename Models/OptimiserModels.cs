namespace Slh.Tms.Api.Models;

public sealed record GeneratePlanProposalRequest(DateOnly PlanningDate, string? Period = null);
public sealed record PlanProposalWarning(string Code, string Severity, string Message);
public sealed record PlanProposalAllocationResult(
    Guid Id,
    Guid SourceLineId,
    int Pallets,
    string? PalletType,
    string? CollectionSite,
    string? DeliverySite,
    int CollectionSequence,
    int DeliverySequence);
public sealed record PlanProposalRunResult(
    Guid Id,
    int Sequence,
    string Reference,
    string Classification,
    int CapacityPallets,
    int PlannedPallets,
    decimal Score,
    IReadOnlyList<string> Explanations,
    IReadOnlyList<PlanProposalAllocationResult> Allocations);
public sealed record PlanProposalResult(
    Guid Id,
    DateOnly PlanningDate,
    string Period,
    int Version,
    string Status,
    string Classification,
    string InputHash,
    DateTimeOffset EvidenceCapturedAtUtc,
    DateTimeOffset CreatedAtUtc,
    string? CreatedBy,
    IReadOnlyList<PlanProposalWarning> Warnings,
    IReadOnlyList<PlanProposalRunResult> Runs);

public sealed record PlanningDriverEvidence(
    Guid DriverId,
    int RequiredDriveMinutes,
    int? DriveAvailableTodayMinutes,
    DateTimeOffset? TachoObservedAtUtc,
    DateTimeOffset EvidenceCapturedAtUtc,
    int ConsecutiveDays,
    bool AlternatingSixthDayAllowed);
public sealed record PlanningConstraintResult(string Code, bool Passed, string Severity, string Explanation);
public sealed record PlanningConstraintEvaluation(string Classification, IReadOnlyList<PlanningConstraintResult> Results);
