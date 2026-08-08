using System.ComponentModel.DataAnnotations;

namespace Slh.Tms.Api.Models.Tracking;

/// <summary>
/// Represents a single vehicle tracking event from an external tracking provider.
/// Events are deduplicated by ProviderName + ProviderEventId combination.
/// </summary>
public sealed class VehicleTrackingEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Name of the tracking provider (e.g., "DOT", "Samsara", etc.).
    /// Combined with ProviderEventId to ensure uniqueness.
    /// </summary>
    [MaxLength(50)]
    public required string ProviderName { get; set; }

    /// <summary>
    /// Unique event ID from the tracking provider.
    /// Combined with ProviderName to prevent duplicate imports.
    /// </summary>
    [MaxLength(100)]
    public required string ProviderEventId { get; set; }

    /// <summary>
    /// Vehicle identifier from the tracking provider (e.g., registration plate, device ID).
    /// </summary>
    [MaxLength(100)]
    public required string VehicleIdentifier { get; set; }

    /// <summary>
    /// UTC timestamp when the event was received/processed.
    /// </summary>
    public DateTimeOffset ReceivedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// UTC timestamp when the event actually occurred (GPS fix time).
    /// </summary>
    public DateTimeOffset EventTimeUtc { get; set; }

    /// <summary>
    /// Latitude coordinate.
    /// </summary>
    public decimal Latitude { get; set; }

    /// <summary>
    /// Longitude coordinate.
    /// </summary>
    public decimal Longitude { get; set; }

    /// <summary>
    /// Speed in kilometers per hour (or null if not available).
    /// </summary>
    public decimal? SpeedKph { get; set; }

    /// <summary>
    /// Ignition state (engine on/off), or null if not available.
    /// </summary>
    public bool? IgnitionOn { get; set; }

    /// <summary>
    /// Vehicle movement state (moving/stationary), or null if not available.
    /// </summary>
    public bool? IsMoving { get; set; }

    /// <summary>
    /// Raw payload from the tracking provider (JSON/XML) for audit trail.
    /// </summary>
    public required string RawPayload { get; set; }

    /// <summary>
    /// Matching status (e.g., "Matched", "Unmatched", "Stale", "Conflict").
    /// Null if not yet processed.
    /// </summary>
    [MaxLength(50)]
    public string? MatchStatus { get; set; }

    /// <summary>
    /// UTC timestamp when this record was created in the system.
    /// </summary>
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Represents the latest known tracking status for a vehicle.
/// Updated whenever a new VehicleTrackingEvent is successfully processed.
/// </summary>
public sealed class VehicleLiveStatus
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Vehicle identifier from the tracking provider.
    /// </summary>
    [MaxLength(100)]
    public required string VehicleIdentifier { get; set; }

    /// <summary>
    /// UTC timestamp of the most recent tracking event.
    /// </summary>
    public DateTimeOffset LastEventTimeUtc { get; set; }

    /// <summary>
    /// UTC timestamp when the most recent event was received.
    /// </summary>
    public DateTimeOffset LastReceivedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Latest known latitude coordinate.
    /// </summary>
    public decimal Latitude { get; set; }

    /// <summary>
    /// Latest known longitude coordinate.
    /// </summary>
    public decimal Longitude { get; set; }

    /// <summary>
    /// Latest known speed in kilometers per hour.
    /// </summary>
    public decimal? SpeedKph { get; set; }

    /// <summary>
    /// Latest known ignition state.
    /// </summary>
    public bool? IgnitionOn { get; set; }

    /// <summary>
    /// Latest known movement state.
    /// </summary>
    public bool? IsMoving { get; set; }

    /// <summary>
    /// Human-readable status summary (e.g., "Matched to Load", "Unmatched", "Stale").
    /// </summary>
    [MaxLength(100)]
    public string? LastKnownStatus { get; set; }

    /// <summary>
    /// UTC timestamp when this live status record was last updated.
    /// </summary>
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
