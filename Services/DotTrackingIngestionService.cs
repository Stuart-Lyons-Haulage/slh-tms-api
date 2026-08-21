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
                var records = (await client.GetLatestVehicleEventsAsync(stoppingToken)).Select(DotTelemetryRecord.FromProvider);
                await store.PersistAsync(records, stoppingToken, markAsLiveReceipt: true);

                if (DateTimeOffset.UtcNow >= nextRecoveryAtUtc)
                {
                    foreach (var recoveryDay in RecoveryDays(DateTimeOffset.UtcNow))
                    {
                        var recovered = (await client.GetHistoricalVehicleEventsAsync(recoveryDay, stoppingToken))
                            .Select(DotTelemetryRecord.FromProvider);
                        await store.PersistAsync(recovered, stoppingToken, markAsLiveReceipt: false);
                    }

                    nextRecoveryAtUtc = DateTimeOffset.UtcNow.Add(recoveryInterval);
                }
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
