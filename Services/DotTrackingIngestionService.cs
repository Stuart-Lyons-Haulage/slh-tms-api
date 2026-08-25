using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models.Tracking;

namespace Slh.Tms.Api.Services;

public sealed class DotTrackingIngestionService(IServiceScopeFactory scopeFactory, DotTrackingOptions options, ILogger<DotTrackingIngestionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pollInterval = TimeSpan.FromMinutes(Math.Max(1, options.PollIntervalMinutes));
        var recoveryInterval = TimeSpan.FromMinutes(Math.Max(options.PollIntervalMinutes, options.RecoveryIntervalMinutes));
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
                    foreach (var recoveryDay in operatingDays)
                    {
                        var recovered = (await client.GetHistoricalVehicleEventsAsync(recoveryDay, stoppingToken))
                            .Select(DotTelemetryRecord.FromProvider);
                        await store.PersistAsync(recovered, stoppingToken, markAsLiveReceipt: false);
                        projectionDays.Add(recoveryDay);
                    }

                    nextRecoveryAtUtc = now.Add(recoveryInterval);
                }

                // Keep SQL as the durable audit projection of the exact same embedded
                // RoadTech/Falcon ENTER/EXIT reconstruction used by the live wallboards.
                await EmbeddedGeofenceSqlProjection.RefreshOperatingDaysAsync(db, projectionDays, stoppingToken);
            }
            catch (InvalidOperationException exception) { logger.LogDebug(exception, "DOT tracking ingestion is not configured."); }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested) { logger.LogWarning(exception, "DOT tracking ingestion failed; retrying in {Minutes} minute(s).", pollInterval.TotalMinutes); }
            await Task.Delay(pollInterval, stoppingToken);
        }
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
