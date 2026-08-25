using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models.Tracking;

namespace Slh.Tms.Api.Services;

public sealed class DotTrackingIngestionService(IServiceScopeFactory scopeFactory, DotTrackingOptions options, ILogger<DotTrackingIngestionService> logger) : BackgroundService
{
    private const int MaximumHistoryRecoveryMinutes = 10;

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

                var records = (await client.GetLatestVehicleEventsAsync(stoppingToken)).Select(DotTelemetryRecord.FromProvider);
                await store.PersistAsync(records, stoppingToken, markAsLiveReceipt: true);

                if (now >= nextRecoveryAtUtc)
                {
                    try
                    {
                        foreach (var recoveryDay in operatingDays)
                        {
                            var recovered = (await client.GetHistoricalVehicleEventsAsync(recoveryDay, stoppingToken))
                                .Select(DotTelemetryRecord.FromProvider)
                                .ToList();
                            await store.PersistAsync(recovered, stoppingToken, markAsLiveReceipt: false);
                            projectionDays.Add(recoveryDay);
                            logger.LogInformation(
                                "DOT historical recovery persisted {RecordCount} RoadTech record(s) for {RecoveryDay}; linked geofences will be replayed immediately.",
                                recovered.Count,
                                recoveryDay);
                        }
                    }
                    catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                    {
                        // Historical recovery is supplementary. Do not let a provider/history
                        // fault suppress the current-day embedded geofence projection, because
                        // current GPS may still be healthy and already persisted locally.
                        logger.LogWarning(exception, "DOT historical tracking recovery failed; continuing with current-day geofence projection.");
                    }
                    finally
                    {
                        nextRecoveryAtUtc = now.Add(recoveryInterval);
                    }
                }

                try
                {
                    // Keep SQL as the durable audit projection of the exact same embedded
                    // RoadTech/Falcon ENTER/EXIT reconstruction used by the live wallboards.
                    // Projection runs immediately after every successful history replay so a
                    // newly linked fence can recover earlier crossings from the same day.
                    await EmbeddedGeofenceSqlProjection.RefreshOperatingDaysAsync(db, projectionDays, stoppingToken);
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    // Projection failure must be visible but must not interrupt current RoadTech
                    // GPS ingestion. The next poll will retry the idempotent projection.
                    logger.LogWarning(exception, "Embedded geofence SQL projection failed; current tracking remains available and projection will retry next poll.");
                }
            }
            catch (InvalidOperationException exception) { logger.LogDebug(exception, "DOT tracking ingestion is not configured."); }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested) { logger.LogWarning(exception, "DOT tracking ingestion failed; retrying in {Minutes} minute(s).", pollInterval.TotalMinutes); }
            await Task.Delay(pollInterval, stoppingToken);
        }
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
