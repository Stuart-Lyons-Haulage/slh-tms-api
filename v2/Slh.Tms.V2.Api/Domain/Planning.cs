namespace Slh.Tms.V2.Api.Domain;

public enum PlanningPeriod
{
    AM,
    PM
}

public enum PlanningRunState
{
    Draft,
    Ready,
    Dispatched,
    Completed,
    Cancelled
}

public sealed class PlanningRun
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string RunNumber { get; set; }
    public DateOnly PlanDate { get; set; }
    public PlanningPeriod Period { get; set; }
    public Guid? DriverId { get; set; }
    public Guid? VehicleId { get; set; }
    public Guid? TrailerId { get; set; }
    public TimeOnly? StartTime { get; set; }
    public bool NightOut { get; set; }
    public string? TrailerSwapNotes { get; set; }
    public string? Notes { get; set; }
    public string? CapacityOverrideReason { get; set; }
    public PlanningRunState State { get; set; } = PlanningRunState.Draft;
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class RunOrderAllocation
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid RunId { get; set; }
    public Guid OrderId { get; set; }
    public int StandardPallets { get; set; }
    public int EuroPallets { get; set; }
    public int Trolleys { get; set; }
    public int Sequence { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record PlanningRunUpdateRequest(
    Guid? DriverId,
    Guid? VehicleId,
    Guid? TrailerId,
    TimeOnly? StartTime,
    bool? NightOut,
    string? TrailerSwapNotes,
    string? Notes,
    string? CapacityOverrideReason);

public sealed record CreatePlanningRunRequest(
    DateOnly PlanDate,
    PlanningPeriod Period,
    string? RunNumber);

public sealed record SetMovementQuantityRequest(
    string MovementKey,
    int StandardPallets,
    int EuroPallets,
    int Trolleys);
