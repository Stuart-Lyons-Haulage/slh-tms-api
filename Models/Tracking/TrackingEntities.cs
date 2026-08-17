using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Slh.Tms.Api.Models.Tracking;

/// <summary>
/// Represents a single vehicle tracking event from an external tracking provider.
/// Events are deduplicated by ProviderName + ProviderEventId combination.
/// </summary>
public sealed class VehicleTrackingEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(50)] public required string ProviderName { get; set; }
    [MaxLength(100)] public required string ProviderEventId { get; set; }
    [MaxLength(100)] public required string VehicleIdentifier { get; set; }
    public DateTimeOffset ReceivedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset EventTimeUtc { get; set; }
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    public decimal? SpeedKph { get; set; }
    public bool? IgnitionOn { get; set; }
    public bool? IsMoving { get; set; }
    public required string RawPayload { get; set; }
    [MaxLength(50)] public string? MatchStatus { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Represents the latest known tracking status for a vehicle.
/// Updated whenever a new VehicleTrackingEvent is successfully processed.
/// </summary>
public sealed class VehicleLiveStatus
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(100)] public required string VehicleIdentifier { get; set; }
    public DateTimeOffset LastEventTimeUtc { get; set; }
    public DateTimeOffset LastReceivedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    public decimal? SpeedKph { get; set; }
    public bool? IgnitionOn { get; set; }
    public bool? IsMoving { get; set; }
    [MaxLength(100)] public string? LastKnownStatus { get; set; }

    // Live Falcon driver identity is deliberately transient. It is used for the
    // current fleet response but is not persisted as master data because card
    // holders can change vehicles throughout the day.
    [NotMapped, MaxLength(200)] public string? CurrentDriverName { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
