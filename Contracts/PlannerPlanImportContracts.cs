namespace Slh.Tms.Api.Contracts;

public sealed record PlannerPlanImportRequest(
    string? Schema,
    DateOnly PlanningDate,
    List<PlannerPlanRunRequest> Runs,
    List<PlannerPlanExceptionRequest>? Exceptions = null);

public sealed record PlannerPlanRunRequest(
    string RunRef,
    string? PlannerRun,
    string? RunType,
    DateOnly PlanningDate,
    string? Driver,
    string? Vehicle,
    string? Trailer,
    string? PlannerNote,
    bool IncludeInImport,
    string? ReconciliationStatus,
    PlannerPlanSourceRequest? Source,
    List<PlannerPlanStopRequest> Stops);

public sealed record PlannerPlanStopRequest(
    int Sequence,
    string? CollectionSite,
    string? DeliverySite,
    decimal? Pallets,
    string? Reference,
    string? PalletType,
    string? CollectFrom,
    string? CollectTo,
    string? Deadline,
    int? SourceRow);

public sealed record PlannerPlanSourceRequest(string? Workbook, string? Sheet);
public sealed record PlannerPlanExceptionRequest(string? Severity, string? RunRef, string? Code, string? Detail, string? Source);

public sealed record PlannerPlanImportSummary(
    DateOnly PlanningDate,
    int Received,
    int Created,
    int Updated,
    int Unchanged,
    int Held,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> UnresolvedDrivers,
    IReadOnlyList<string> UnresolvedVehicles,
    IReadOnlyList<string> UnresolvedTrailers,
    IReadOnlyList<PlannerPlanRunResult> Runs);

public sealed record PlannerPlanRunResult(
    string RunRef,
    string TmsReference,
    string Outcome,
    string CapacityStatus,
    decimal UtilisationPercent,
    string? Detail);
