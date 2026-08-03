using System.ComponentModel.DataAnnotations;

namespace Slh.Tms.Api.Models;
public sealed class Customer { public Guid Id { get; set; } = Guid.NewGuid(); [MaxLength(40)] public required string Code { get; set; } [MaxLength(200)] public required string Name { get; set; } public bool Active { get; set; } = true; }
public sealed class Vehicle { public Guid Id { get; set; } = Guid.NewGuid(); [MaxLength(20)] public required string Registration { get; set; } [MaxLength(40)] public string? FleetNumber { get; set; } public bool Active { get; set; } = true; }
public sealed class Driver { public Guid Id { get; set; } = Guid.NewGuid(); [MaxLength(40)] public required string EmployeeNumber { get; set; } [MaxLength(160)] public required string DisplayName { get; set; } public bool Active { get; set; } = true; }
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
