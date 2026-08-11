namespace Slh.Tms.Api.Services;

public sealed class DotTrackingIngestionService(IServiceScopeFactory scopeFactory, ILogger<DotTrackingIngestionService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var client = scope.ServiceProvider.GetRequiredService<DotTrackingClient>();
                var store = scope.ServiceProvider.GetRequiredService<DotTrackingTelemetryStore>();
                var records = (await client.GetLatestVehicleEventsAsync(stoppingToken)).Select(DotTelemetryRecord.FromProvider);
                await store.PersistAsync(records, stoppingToken);
            }
            catch (InvalidOperationException exception)
            {
                logger.LogDebug(exception, "DOT tracking ingestion is not configured.");
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "DOT tracking ingestion failed; retrying in five minutes.");
            }
            await Task.Delay(PollInterval, stoppingToken);
        }
    }
}
