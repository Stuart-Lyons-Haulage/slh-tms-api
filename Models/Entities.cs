using System.ComponentModel.DataAnnotations;

namespace Slh.Tms.Api.Models;
public sealed class Customer { public Guid Id { get; set; } = Guid.NewGuid(); [MaxLength(40)] public required string Code { get; set; } [MaxLength(200)] public required string Name { get; set; } public bool Active { get; set; } = true; }
public sealed class CustomerContact
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(40)] public required string CustomerCode { get; set; }
    [MaxLength(200)] public required string Name { get; set; }
    [EmailAddress, MaxLength(320)] public string? Email { get; set; }
    [MaxLength(40)] public string? MobileNumber { get; set; }
    public bool ReceivesEtaUpdates { get; set; } = true;
    public bool Active { get; set; } = true;
}
public sealed class Vehicle
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(20)] public required string Registration { get; set; }
    [MaxLength(40)] public string? FleetNumber { get; set; }
    [MaxLength(20)] public string? Abbreviation { get; set; }
    [MaxLength(20)] public string? Transmission { get; set; }
    public bool? DvsCompliant { get; set; }
    [MaxLength(30)] public string? FuelProvider { get; set; }
    [MaxLength(120)] public string? FuelPinSecretName { get; set; }
    [MaxLength(4)] public string? FuelCardLastFour { get; set; }
    public bool Active { get; set; } = true;
}
public sealed class Driver
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(40)] public required string EmployeeNumber { get; set; }
    [MaxLength(160)] public required string DisplayName { get; set; }
    [MaxLength(160)] public string? TachoName { get; set; }
    [MaxLength(40)] public string? MobileNumber { get; set; }
    [MaxLength(80)] public string? DriverType { get; set; }
    [MaxLength(80)] public string? DriverGroup { get; set; }
    [MaxLength(160)] public string? Skills { get; set; }
    public bool Active { get; set; } = true;
}
public sealed class Trailer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(40)] public required string TrailerNumber { get; set; }
    [MaxLength(80)] public string? Type { get; set; }
    public int? StandardCapacity { get; set; }
    public int? EuroCapacity { get; set; }
    public bool Active { get; set; } = true;
}
public sealed class Site
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(40)] public required string ExternalCode { get; set; }
    [MaxLength(200)] public required string Name { get; set; }
    [MaxLength(200)] public string? DriverTextName { get; set; }
    [MaxLength(500)] public string? CollectionAddress { get; set; }
    [MaxLength(1000)] public string? CollectionInstructions { get; set; }
    [MaxLength(1000)] public string? MapLink { get; set; }
    public bool Active { get; set; } = true;
}
public sealed class MarketContact
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(80)] public required string Market { get; set; }
    [MaxLength(200)] public required string Name { get; set; }
    [MaxLength(200)] public string? StandOrLocation { get; set; }
    [MaxLength(200)] public string? Salesman { get; set; }
    [MaxLength(200)] public string? Sender { get; set; }
    public bool Active { get; set; } = true;
}
public enum StagingStatus { PendingReview, Approved, Rejected, Promoted, Failed }
public sealed class StagedImport
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(80)] public required string EntityType { get; set; }
    [MaxLength(200)] public required string IdempotencyKey { get; set; }
    public required string PayloadJson { get; set; }
    public StagingStatus Status { get; set; } = StagingStatus.PendingReview;
    [MaxLength(200)] public string? Source { get; set; }
    public DateTimeOffset ReceivedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReviewedAtUtc { get; set; }
    [MaxLength(200)] public string? ReviewedBy { get; set; }
    [MaxLength(1000)] public string? ReviewNote { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
public enum OrderStatus { Draft, ReadyToPlan, Planned, InTransit, Delivered, Cancelled }
public sealed class TransportOrder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(80)] public required string Reference { get; set; }
    [MaxLength(40)] public required string CustomerCode { get; set; }
    public DateOnly CollectionDate { get; set; }
    public DateOnly? DeliveryDate { get; set; }
    public DateTimeOffset? DeliveryWindowStartUtc { get; set; }
    public DateTimeOffset? DeliveryWindowEndUtc { get; set; }
    public int? Pallets { get; set; }
    [MaxLength(200)] public string? SellerName { get; set; }
    [MaxLength(80)] public string? MarketName { get; set; }
    [MaxLength(200)] public string? StallNumber { get; set; }
    [MaxLength(1000)] public string? DriverInstructions { get; set; }
    [MaxLength(1000)] public string? MapLink { get; set; }
    public OrderStatus Status { get; set; } = OrderStatus.ReadyToPlan;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
public enum LoadStatus { Draft, Planned, Dispatched, InProgress, Completed, Cancelled }
public sealed class Load
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(80)] public required string Reference { get; set; }
    public DateOnly PlanningDate { get; set; }
    public LoadStatus Status { get; set; } = LoadStatus.Draft;
    public Guid? VehicleId { get; set; }
    public Guid? DriverId { get; set; }
    public Guid? TrailerId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<LoadStop> Stops { get; set; } = [];
}
public sealed class LoadStop
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LoadId { get; set; }
    public Guid? OrderId { get; set; }
    public int Sequence { get; set; }
    [MaxLength(200)] public required string Name { get; set; }
    [MaxLength(500)] public string? Address { get; set; }
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public DateTimeOffset? PlannedArrivalUtc { get; set; }
}
