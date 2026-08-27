using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models.Tracking;

namespace Slh.Tms.Api.Services;

public sealed class DotTrackingTelemetryStore(TmsDbContext db, ILogger<DotTrackingTelemetryStore> logger)
{
    private static readonly ConcurrentDictionary<string, byte> NormalisedProviderIdentifiers = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan MaximumLiveFutureSkew = TimeSpan.FromMinutes(5);

    public async Task PersistAsync(IEnumerable<DotTelemetryRecord> records, CancellationToken ct, bool markAsLiveReceipt = true)
    {
        var batch = records.ToList();
        var receivedAt = DateTimeOffset.UtcNow;

        foreach (var record in batch)
        {
            var rawIdentifier = (record.VehicleIdentifier ?? string.Empty).Trim();
            var canonicalIdentifier = ExecutionIdentityResolver.NormaliseVehicle(rawIdentifier);
            if (canonicalIdentifier.Length == 0) continue;

            if (!string.Equals(rawIdentifier, canonicalIdentifier, StringComparison.OrdinalIgnoreCase) &&
                NormalisedProviderIdentifiers.TryAdd(rawIdentifier, 0))
            {
                try
                {
                    var floor = receivedAt.AddHours(-36);
                    await db.VehicleTrackingEvents
                        .Where(item =>
                            item.ProviderName == "RoadTech Falcon" &&
                            item.VehicleIdentifier == rawIdentifier &&
                            item.EventTimeUtc >= floor)
                        .ExecuteUpdateAsync(
                            setters => setters.SetProperty(item => item.VehicleIdentifier, canonicalIdentifier),
                            ct);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    db.ChangeTracker.Clear();
                    logger.LogWarning(
                        exception,
                        "RoadTech history identifier normalisation was skipped for {VehicleIdentifier}; live ingestion will continue.",
                        rawIdentifier);
                }
            }

            // GetCurrentTelemetry is a receipt-time assertion that this is the vehicle's
            // current position. A provider timestamp materially in the future must never
            // poison the operating-day history or make a wallboard wait until tomorrow.
            // In that exceptional case use the current receipt as the safe live timestamp;
            // historical replay remains provider-time based so its movement chronology is
            // preserved and can repair previously stored rows from the authoritative page.
            var eventTimeUtc = record.EventTimeUtc;
            if (markAsLiveReceipt && eventTimeUtc > receivedAt.Add(MaximumLiveFutureSkew))
            {
                logger.LogWarning(
                    "RoadTech returned future event time {ProviderEventTimeUtc} for {VehicleIdentifier}; normalising current telemetry to receipt time {ReceivedAtUtc}.",
                    eventTimeUtc,
                    canonicalIdentifier,
                    receivedAt);
                eventTimeUtc = receivedAt;
            }

            var hasGps = record.Latitude is not null && record.Longitude is not null;
            if (hasGps)
            {
                var existing = db.VehicleTrackingEvents.Local.FirstOrDefault(item =>
                    item.ProviderName == "RoadTech Falcon" &&
                    item.ProviderEventId == record.ProviderEventId)
                    ?? await db.VehicleTrackingEvents.FirstOrDefaultAsync(
                        item =>
                            item.ProviderName == "RoadTech Falcon" &&
                            item.ProviderEventId == record.ProviderEventId,
                        ct);

                if (existing is null)
                {
                    db.VehicleTrackingEvents.Add(new VehicleTrackingEvent
                    {
                        ProviderName = "RoadTech Falcon",
                        ProviderEventId = record.ProviderEventId,
                        VehicleIdentifier = canonicalIdentifier,
                        EventTimeUtc = eventTimeUtc,
                        Latitude = record.Latitude!.Value,
                        Longitude = record.Longitude!.Value,
                        SpeedKph = record.SpeedKph,
                        IgnitionOn = record.IgnitionOn,
                        IsMoving = record.IsMoving,
                        RawPayload = record.RawPayload,
                        MatchStatus = "Received"
                    });
                }
                else if (!markAsLiveReceipt)
                {
                    // Historical replay is a repair pass, not insert-only ingestion. If a
                    // provider event already exists with an earlier parsing/normalisation
                    // mistake, overwrite the telemetry facts from the newly fetched Falcon
                    // history. This is what allows today's replay to move bad future-dated
                    // events back into the correct operating-day window before geofence
                    // projection is rebuilt.
                    existing.VehicleIdentifier = canonicalIdentifier;
                    existing.EventTimeUtc = eventTimeUtc;
                    existing.Latitude = record.Latitude!.Value;
                    existing.Longitude = record.Longitude!.Value;
                    existing.SpeedKph = record.SpeedKph;
                    existing.IgnitionOn = record.IgnitionOn;
                    existing.IsMoving = record.IsMoving;
                    existing.RawPayload = record.RawPayload;
                    existing.MatchStatus = "Received";
                }
            }

            // Historical recovery is geofence evidence only. It must never create or
            // refresh a live-status row. Likewise, a current Falcon row without GPS does
            // not prove that the vehicle position itself is current.
            if (!markAsLiveReceipt || !hasGps) continue;

            var live = await ResolveLiveStatusAsync(rawIdentifier, canonicalIdentifier, ct);

            if (live is null)
            {
                live = new VehicleLiveStatus
                {
                    VehicleIdentifier = canonicalIdentifier,
                    LastEventTimeUtc = eventTimeUtc,
                    LastReceivedAtUtc = receivedAt,
                    Latitude = record.Latitude!.Value,
                    Longitude = record.Longitude!.Value,
                    SpeedKph = record.SpeedKph,
                    IgnitionOn = record.IgnitionOn,
                    IsMoving = record.IsMoving,
                    LastKnownStatus = record.Status,
                    CurrentDriverName = record.DriverName,
                    CurrentDriverCardNumber = record.DriverCardNumber,
                    UpdatedAtUtc = receivedAt
                };
                db.VehicleLiveStatuses.Add(live);
            }
            else
            {
                live.VehicleIdentifier = canonicalIdentifier;

                // Receipt freshness answers whether GetCurrentTelemetry is actively
                // confirming this GPS position. Stationary vehicles may keep the same
                // provider event timestamp throughout a long unload.
                live.LastReceivedAtUtc = receivedAt;
                live.UpdatedAtUtc = receivedAt;

                // Do not roll the stored position backwards if RoadTech includes an older
                // event in the current-fleet response.
                if (eventTimeUtc >= live.LastEventTimeUtc)
                {
                    live.LastEventTimeUtc = eventTimeUtc;
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
        if (batch.Count > 0)
            logger.LogDebug("Stored {RecordCount} RoadTech telemetry record(s) for table-free geofence progression.", batch.Count);
    }

    private async Task<VehicleLiveStatus?> ResolveLiveStatusAsync(
        string rawIdentifier,
        string canonicalIdentifier,
        CancellationToken ct)
    {
        var localCandidates = db.VehicleLiveStatuses.Local
            .Where(item =>
                SameCanonical(item.VehicleIdentifier, canonicalIdentifier) ||
                string.Equals(item.VehicleIdentifier, rawIdentifier, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var databaseCandidates = await db.VehicleLiveStatuses
            .Where(item =>
                item.VehicleIdentifier == canonicalIdentifier ||
                item.VehicleIdentifier == rawIdentifier)
            .ToListAsync(ct);

        var candidates = localCandidates
            .Concat(databaseCandidates)
            .GroupBy(item => item.Id)
            .Select(group => group.First())
            .ToList();

        if (candidates.Count == 0) return null;

        // Prefer an already-persisted canonical row. If historical/raw aliases have
        // created more than one logical live row, collapse them before renaming so the
        // unique VehicleIdentifier index cannot fail during SaveChanges.
        var keeper = candidates
            .OrderByDescending(item => db.Entry(item).State != EntityState.Added)
            .ThenByDescending(item => string.Equals(item.VehicleIdentifier, canonicalIdentifier, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(item => item.LastReceivedAtUtc)
            .ThenByDescending(item => item.LastEventTimeUtc)
            .First();

        var duplicates = candidates.Where(item => item.Id != keeper.Id).ToList();
        var hasPersistedDuplicates = duplicates.Any(item => db.Entry(item).State != EntityState.Added);

        foreach (var duplicate in duplicates)
        {
            if (duplicate.LastReceivedAtUtc > keeper.LastReceivedAtUtc)
            {
                keeper.LastReceivedAtUtc = duplicate.LastReceivedAtUtc;
                keeper.UpdatedAtUtc = duplicate.UpdatedAtUtc;
            }

            if (duplicate.LastEventTimeUtc > keeper.LastEventTimeUtc)
            {
                keeper.LastEventTimeUtc = duplicate.LastEventTimeUtc;
                keeper.Latitude = duplicate.Latitude;
                keeper.Longitude = duplicate.Longitude;
                keeper.SpeedKph = duplicate.SpeedKph;
                keeper.IgnitionOn = duplicate.IgnitionOn;
                keeper.IsMoving = duplicate.IsMoving;
                keeper.LastKnownStatus = duplicate.LastKnownStatus;
            }

            db.VehicleLiveStatuses.Remove(duplicate);
        }

        if (hasPersistedDuplicates)
        {
            // Delete aliases before changing the keeper to the canonical identifier.
            // This avoids a transient unique-key collision in SQL Server.
            await db.SaveChangesAsync(ct);
        }

        keeper.VehicleIdentifier = canonicalIdentifier;
        return keeper;
    }

    private static bool SameCanonical(string value, string canonicalIdentifier) =>
        string.Equals(
            ExecutionIdentityResolver.NormaliseVehicle(value),
            canonicalIdentifier,
            StringComparison.OrdinalIgnoreCase);
}
