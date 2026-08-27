using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models.Tracking;

namespace Slh.Tms.Api.Services;

public sealed class DotTrackingIngestionService(IServiceScopeFactory scopeFactory, DotTrackingOptions options, ILogger<DotTrackingIngestionService> logger) : BackgroundService
{
    private const int MaximumHistoryRecoveryMinutes = 10;
    private static readonly TimeSpan MaximumFutureSkew = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaximumHistoricalClockCorrection = TimeSpan.FromHours(48);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pollInterval = TimeSpan.FromMinutes(Math.Max(1, options.PollIntervalMinutes));
        var recoveryInterval = HistoryRecoveryInterval(options);
        var nextRecoveryAtUtc = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var client = scope.ServiceProvider.GetRequiredService<DotTrackingClient>();
                var store = scope.ServiceProvider.GetRequiredService<DotTrackingTelemetryStore>();
                var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
                var now = DateTimeOffset.UtcNow;
                var operatingDays = RecoveryDays(now);
                var projectionDays = new HashSet<DateOnly> { operatingDays[0] };

                var records = NormaliseCurrentEventTimes(
                    (await client.GetLatestVehicleEventsAsync(stoppingToken))
                        .Select(DotTelemetryRecord.FromProvider),
                    DateTimeOffset.UtcNow);
                await store.PersistAsync(records, stoppingToken, markAsLiveReceipt: true);
                await TryRepairProviderVehicleMappingsAsync(db, records.Select(record => record.VehicleIdentifier), "current", stoppingToken);

                // Live ENTER/EXIT detection is authoritative from the active SQL SiteGeofences
                // maintained through Site Master. The embedded payload remains a resilience-only
                // fallback when no active SQL geofence catalogue exists.
                try
                {
                    await GeofenceRunProgression.ProcessTelemetryAsync(db, records, stoppingToken);
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    db.ChangeTracker.Clear();
                    logger.LogWarning(exception, "Site Master geofence hit processing failed for current RoadTech telemetry; tracking ingestion will continue.");
                }

                if (now >= nextRecoveryAtUtc)
                {
                    try
                    {
                        foreach (var recoveryDay in operatingDays)
                        {
                            var recovered = NormaliseHistoricalEventTimes(
                                (await client.GetHistoricalVehicleEventsAsync(recoveryDay, stoppingToken))
                                    .Select(DotTelemetryRecord.FromProvider),
                                records,
                                recoveryDay,
                                DateTimeOffset.UtcNow);
                            await store.PersistAsync(recovered, stoppingToken, markAsLiveReceipt: false);

                            // Historical Falcon pages can use provider vehicle keys that differ
                            // in formatting from the latest/live key. Teach the canonical identity
                            // resolver every uniquely matchable exact key before geofence replay,
                            // so the indexed VehicleTrackingEvents query can retrieve the same
                            // multi-sample trail that the bounded health diagnostic sees in memory.
                            await TryRepairProviderVehicleMappingsAsync(
                                db,
                                recovered.Select(record => record.VehicleIdentifier),
                                $"history {recoveryDay:yyyy-MM-dd}",
                                stoppingToken);

                            projectionDays.Add(recoveryDay);
                            logger.LogInformation(
                                "DOT historical recovery persisted {RecordCount} RoadTech record(s) for {RecoveryDay}.",
                                recovered.Count,
                                recoveryDay);
                        }
                    }
                    catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                    {
                        // Historical recovery is supplementary. Do not let a provider/history
                        // fault suppress current RoadTech ingestion or live Site Master hits.
                        logger.LogWarning(exception, "DOT historical tracking recovery failed; continuing with current live geofence processing.");
                    }
                    finally
                    {
                        nextRecoveryAtUtc = now.Add(recoveryInterval);
                    }
                }

                try
                {
                    // Rebuild the durable GeofenceVisits projection from the stored RoadTech
                    // event stream on every current-day cycle and for both recovery days after
                    // a history refresh. EmbeddedGeofenceEngine now reads the active SQL Site
                    // Master polygons first, so this safely backfills records captured before
                    // the geofence interpretation was corrected instead of limiting projection
                    // to the old no-SQL-fences fallback case.
                    await EmbeddedGeofenceSqlProjection.RefreshOperatingDaysAsync(db, projectionDays, stoppingToken);
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    // Projection failure must be visible but must not interrupt current RoadTech
                    // GPS ingestion. The next poll will retry from the persisted event stream.
                    logger.LogWarning(exception, "Geofence history projection failed; current tracking remains available and stored RoadTech history will retry next poll.");
                }
            }
            catch (InvalidOperationException exception) { logger.LogDebug(exception, "DOT tracking ingestion is not configured."); }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested) { logger.LogWarning(exception, "DOT tracking ingestion failed; retrying in {Minutes} minute(s).", pollInterval.TotalMinutes); }
            await Task.Delay(pollInterval, stoppingToken);
        }
    }

    internal static IReadOnlyList<DotTelemetryRecord> NormaliseCurrentEventTimes(
        IEnumerable<DotTelemetryRecord> source,
        DateTimeOffset receivedAtUtc)
    {
        var ceiling = receivedAtUtc.Add(MaximumFutureSkew);
        return source
            .Select(record => record.EventTimeUtc > ceiling
                ? record with { EventTimeUtc = receivedAtUtc }
                : record)
            .ToList();
    }

    internal static IReadOnlyList<DotTelemetryRecord> NormaliseHistoricalEventTimes(
        IEnumerable<DotTelemetryRecord> source,
        IReadOnlyCollection<DotTelemetryRecord> currentRecords,
        DateOnly recoveryDay,
        DateTimeOffset nowUtc)
    {
        var historical = source.ToList();
        if (historical.Count == 0) return historical;

        // Only today's historical page can be calibrated against GetCurrentTelemetry.
        // If Falcon history is systematically future-dated, the current fleet snapshot is
        // the authoritative clock anchor for the same vehicle. Shift that vehicle's entire
        // historical trail by the measured skew so ENTER/EXIT ordering is preserved rather
        // than collapsing every bad point onto receipt time.
        if (RecoveryDays(nowUtc)[0] != recoveryDay) return historical;

        var currentByVehicle = currentRecords
            .GroupBy(record => ExecutionIdentityResolver.NormaliseVehicle(record.VehicleIdentifier), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Key.Length > 0)
            .ToDictionary(group => group.Key, group => group.Max(record => record.EventTimeUtc), StringComparer.OrdinalIgnoreCase);
        var futureCeiling = nowUtc.Add(MaximumFutureSkew);
        var result = new List<DotTelemetryRecord>(historical.Count);

        foreach (var group in historical.GroupBy(record => ExecutionIdentityResolver.NormaliseVehicle(record.VehicleIdentifier), StringComparer.OrdinalIgnoreCase))
        {
            var rows = group.ToList();
            if (group.Key.Length == 0 || !currentByVehicle.TryGetValue(group.Key, out var currentTimeUtc))
            {
                result.AddRange(rows);
                continue;
            }

            var newestHistoricalUtc = rows.Max(record => record.EventTimeUtc);
            var skew = newestHistoricalUtc - currentTimeUtc;
            if (newestHistoricalUtc <= futureCeiling || skew <= MaximumFutureSkew || skew > MaximumHistoricalClockCorrection)
            {
                result.AddRange(rows);
                continue;
            }

            result.AddRange(rows.Select(record => record with { EventTimeUtc = record.EventTimeUtc - skew }));
        }

        return result;
    }

    private async Task TryRepairProviderVehicleMappingsAsync(
        TmsDbContext db,
        IEnumerable<string?> providerIdentifiers,
        string source,
        CancellationToken ct)
    {
        try
        {
            var repaired = await RepairProviderVehicleMappingsAsync(db, providerIdentifiers, ct);
            if (repaired > 0)
                logger.LogInformation("Learned {MappingCount} exact RoadTech vehicle key mapping(s) from {Source} telemetry before geofence replay.", repaired, source);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Identity learning is supplementary to GPS capture. A schema or matching
            // problem must not interrupt current telemetry; projection will remain fail-safe
            // and retry after the next poll/history recovery.
            db.ChangeTracker.Clear();
            logger.LogWarning(exception, "RoadTech vehicle identity learning failed for {Source}; tracking ingestion will continue.", source);
        }
    }

    internal static async Task<int> RepairProviderVehicleMappingsAsync(
        TmsDbContext db,
        IEnumerable<string?> providerIdentifiers,
        CancellationToken ct)
    {
        var identifiers = providerIdentifiers
            .Where(identifier => !string.IsNullOrWhiteSpace(identifier))
            .Select(identifier => identifier!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (identifiers.Count == 0) return 0;

        var vehicles = await db.Vehicles.AsNoTracking()
            .Where(vehicle => vehicle.Active)
            .ToListAsync(ct);
        if (vehicles.Count == 0) return 0;

        return await ExecutionIdentityResolver.RepairDotVehicleMappingsAsync(db, vehicles, identifiers, ct);
    }

    internal static TimeSpan HistoryRecoveryInterval(DotTrackingOptions options)
    {
        var pollMinutes = Math.Max(1, options.PollIntervalMinutes);
        var configuredMinutes = Math.Max(pollMinutes, options.RecoveryIntervalMinutes);
        return TimeSpan.FromMinutes(Math.Min(MaximumHistoryRecoveryMinutes, configuredMinutes));
    }

    internal static IReadOnlyList<DateOnly> RecoveryDays(DateTimeOffset utcNow)
    {
        DateOnly today;
        try
        {
            today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(utcNow, "Europe/London").DateTime);
        }
        catch (TimeZoneNotFoundException)
        {
            today = DateOnly.FromDateTime(utcNow.UtcDateTime);
        }

        // Current day repairs any missed polling/persistence before the recovery run;
        // previous day preserves overnight duties that cross the operating-day boundary.
        return [today, today.AddDays(-1)];
    }
}
