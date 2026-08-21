using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models.Tracking;

namespace Slh.Tms.Api.Services;

public sealed class DotTrackingTelemetryStore(TmsDbContext db, ILogger<DotTrackingTelemetryStore> logger)
{
    private static readonly ConcurrentDictionary<string, byte> NormalisedProviderIdentifiers = new(StringComparer.OrdinalIgnoreCase);

    public async Task PersistAsync(IEnumerable<DotTelemetryRecord> records, CancellationToken ct, bool updateLiveStatus = true)
    {
        var batch = records.ToList();
        var receivedAt = DateTimeOffset.UtcNow;

        foreach (var record in batch)
        {
            var rawIdentifier = (record.VehicleIdentifier ?? string.Empty).Trim();
            var canonicalIdentifier = ExecutionIdentityResolver.NormaliseVehicle(rawIdentifier);
            if (canonicalIdentifier.Length == 0) continue;

            // RoadTech identifiers can arrive with spaces/punctuation while planning aliases
            // are canonicalised. Normalise the recent history once per provider identifier so
            // the shared geofence engine can reconstruct today's visits without losing events
            // at the initial SQL filtering stage.
            if (!string.Equals(rawIdentifier, canonicalIdentifier, StringComparison.OrdinalIgnoreCase) &&
                NormalisedProviderIdentifiers.TryAdd(rawIdentifier, 0))
            {
                try
                {
                    var floor = receivedAt.AddHours(-36);
                    await db.VehicleTrackingEvents
                        .Where(item => item.ProviderName == "RoadTech Falcon" &&
                                       item.VehicleIdentifier == rawIdentifier &&
                                       item.EventTimeUtc >= floor)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(item => item.VehicleIdentifier, canonicalIdentifier), ct);

                    var rawLiveRows = await db.VehicleLiveStatuses
                        .Where(item => item.VehicleIdentifier == rawIdentifier)
                        .ToListAsync(ct);
                    foreach (var rawLive in rawLiveRows)
                        rawLive.VehicleIdentifier = canonicalIdentifier;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    db.ChangeTracker.Clear();
                    logger.LogWarning(exception,
                        "RoadTech identifier normalisation was skipped for {VehicleIdentifier}; live ingestion will continue.",
                        rawIdentifier);
                }
            }

            var hasGps = record.Latitude is not null && record.Longitude is not null;
            if (hasGps)
            {
                var exists = await db.VehicleTrackingEvents.AnyAsync(item => item.ProviderName == "RoadTech Falcon" && item.ProviderEventId == record.ProviderEventId, ct);
                if (!exists) db.VehicleTrackingEvents.Add(new VehicleTrackingEvent
                {
                    ProviderName = "RoadTech Falcon",
                    ProviderEventId = record.ProviderEventId,
                    VehicleIdentifier = canonicalIdentifier,
                    EventTimeUtc = record.EventTimeUtc,
                    Latitude = record.Latitude!.Value,
                    Longitude = record.Longitude!.Value,
                    SpeedKph = record.SpeedKph,
                    IgnitionOn = record.IgnitionOn,
                    IsMoving = record.IsMoving,
                    RawPayload = record.RawPayload,
                    MatchStatus = "Received"
                });
            }

            // Historical recovery is evidence for geofence reconstruction only. It must
            // never make yesterday's telemetry look like a current live observation.
            if (!updateLiveStatus || !hasGps) continue;

            var live = await db.VehicleLiveStatuses
                .Where(item => item.VehicleIdentifier == canonicalIdentifier || item.VehicleIdentifier == rawIdentifier)
                .OrderByDescending(item => item.LastEventTimeUtc)
                .FirstOrDefaultAsync(ct);

            if (live is null)
            {
                db.VehicleLiveStatuses.Add(new VehicleLiveStatus
                {
                    VehicleIdentifier = canonicalIdentifier,
                    LastEventTimeUtc = record.EventTimeUtc,
                    LastReceivedAtUtc = receivedAt,
                    Latitude = record.Latitude!.Value,
                    Longitude = record.Longitude!.Value,
                    SpeedKph = record.SpeedKph,
                    IgnitionOn = record.IgnitionOn,
                    IsMoving = record.IsMoving,
                    LastKnownStatus = record.Status,
                    CurrentDriverName = record.DriverName,
                    CurrentDriverCardNumber = record.DriverCardNumber
                });
            }
            else
            {
                live.VehicleIdentifier = canonicalIdentifier;

                // GetCurrentTelemetry is RoadTech's current-fleet observation. A stationary
                // vehicle may retain the same provider event timestamp for a long unload, so
                // receipt freshness must advance on every valid current GPS observation.
                live.LastReceivedAtUtc = receivedAt;

                // Never roll the stored position backwards if a provider page contains an
                // older event. Only the receipt timestamp is refreshed in that case.
                if (record.EventTimeUtc >= live.LastEventTimeUtc)
                {
                    live.LastEventTimeUtc = record.EventTimeUtc;
                    live.Latitude = record.Latitude!.Value;
                    live.Longitude = record.Longitude!.Value;
                    live.SpeedKph = record.SpeedKph;
                    live.IgnitionOn = record.IgnitionOn;
                    live.IsMoving = record.IsMoving;
                    live.LastKnownStatus = record.Status;
                    if (!string.IsNullOrWhiteSpace(record.DriverName)) live.CurrentDriverName = record.DriverName;
                    if (!string.IsNullOrWhiteSpace(record.DriverCardNumber)) live.CurrentDriverCardNumber = record.DriverCardNumber;
                }
            }
        }

        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);

        // Geofence progression is derived from persisted RoadTech history plus the approved
        // embedded SLH geofence set. Ingestion only stores evidence; it does not create or
        // alter geofence schema at runtime.
        if (batch.Count > 0)
            logger.LogDebug("Stored {RecordCount} RoadTech telemetry record(s); liveStatus={UpdateLiveStatus}.", batch.Count, updateLiveStatus);
    }
}
