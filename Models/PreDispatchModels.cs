namespace Slh.Tms.Api.Models;

public sealed record PreDispatchCheck(string Code, bool Passed, string Severity, string Message);

public sealed record PreDispatchReadinessResult(
    Guid LoadId,
    string Classification,
    bool CanDispatch,
    bool RequiresAcknowledgement,
    int? EstimatedDriveMinutes,
    DateTimeOffset EvidenceCapturedAtUtc,
    IReadOnlyList<PreDispatchCheck> Checks);

public sealed record ControlledDispatchRequest(bool AcknowledgeUnverified = false);

public sealed record ControlledDispatchResult(
    Guid LoadId,
    string Status,
    PreDispatchReadinessResult Readiness);

public sealed class PreDispatchException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}
