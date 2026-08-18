using System.Text.Json.Serialization;

namespace Slh.Tms.Api.Contracts;

public sealed record PlannerPlanImportRequest(
    string? Schema,
    DateOnly PlanningDate,
    List<PlannerPlanRunRequest> Runs,
    List<PlannerPlanExceptionRequest>? Exceptions = null);

public sealed record PlannerPlanRunRequest(
    [property: JsonConverter(typeof(FlexibleStringJsonConverter))] string RunRef,
    [property: JsonConverter(typeof(FlexibleStringJsonConverter))] string? PlannerRun,
    [property: JsonConverter(typeof(FlexibleStringJsonConverter))] string? RunType,
    DateOnly PlanningDate,
    [property: JsonConverter(typeof(FlexibleStringJsonConverter))] string? Driver,
    [property: JsonConverter(typeof(FlexibleStringJsonConverter))] string? Vehicle,
    [property: JsonConverter(typeof(FlexibleStringJsonConverter))] string? Trailer,
    [property: JsonConverter(typeof(FlexibleStringJsonConverter))] string? PlannerNote,
    bool IncludeInImport,
    [property: JsonConverter(typeof(FlexibleStringJsonConverter))] string? ReconciliationStatus,
    PlannerPlanSourceRequest? Source,
    List<PlannerPlanStopRequest> Stops);

public sealed record PlannerPlanStopRequest(
    int Sequence,
    [property: JsonConverter(typeof(FlexibleStringJsonConverter))] string? CollectionSite,
    [property: JsonConverter(typeof(FlexibleStringJsonConverter))] string? DeliverySite,
    decimal? Pallets,
    [property: JsonConverter(typeof(FlexibleStringJsonConverter))] string? Reference,
    [property: JsonConverter(typeof(FlexibleStringJsonConverter))] string? PalletType,
    [property: JsonConverter(typeof(FlexibleStringJsonConverter))] string? CollectFrom,
    [property: JsonConverter(typeof(FlexibleStringJsonConverter))] string? CollectTo,
    [property: JsonConverter(typeof(FlexibleStringJsonConverter))] string? Deadline,
    int? SourceRow);

public sealed record PlannerPlanSourceRequest(
    [property: JsonConverter(typeof(FlexibleStringJsonConverter))] string? Workbook,
    [property: JsonConverter(typeof(FlexibleStringJsonConverter))] string? Sheet);

public sealed record PlannerPlanExceptionRequest(
    [property: JsonConverter(typeof(FlexibleStringJsonConverter))] string? Severity,
    [property: JsonConverter(typeof(FlexibleStringJsonConverter))] string? RunRef,
    [property: JsonConverter(typeof(FlexibleStringJsonConverter))] string? Code,
    [property: JsonConverter(typeof(FlexibleStringJsonConverter))] string? Detail,
    [property: JsonConverter(typeof(FlexibleStringJsonConverter))] string? Source);

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
