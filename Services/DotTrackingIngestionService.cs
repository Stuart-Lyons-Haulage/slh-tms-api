using Slh.Tms.Api.Models.Tracking;

namespace Slh.Tms.Api.Services;

public sealed class DotTrackingIngestionService(IServiceScopeFactory scopeFactory, DotTrackingOptions options, ILogger<DotTrackingIngestionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pollInterval = TimeSpan.FromMinutes(Math.Max(1, options.PollIntervalMinutes));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var client = scope.ServiceProvider.GetRequiredService<DotTrackingClient>();
                var store = scope.ServiceProvider.GetRequiredService<DotTrackingTelemetryStore>();
                var records = (await client.GetLatestVehicleEventsAsync(stoppingToken)).Select(DotTelemetryRecord.FromProvider);
                await store.PersistAsync(records, stoppingToken);
                var recoveryDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow, "Europe/London").DateTime).AddDays(-1);
                var recovered = (await client.GetHistoricalVehicleEventsAsync(recoveryDay, stoppingToken)).Select(DotTelemetryRecord.FromProvider);
                await store.PersistAsync(recovered, stoppingToken);
            }
            catch (InvalidOperationException exception)
            {
                logger.LogDebug(exception, "DOT tracking ingestion is not configured.");
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "DOT tracking ingestion failed; retrying in {Minutes} minutes.", pollInterval.TotalMinutes);
            }
            await Task.Delay(pollInterval, stoppingToken);
        }
    }
}
