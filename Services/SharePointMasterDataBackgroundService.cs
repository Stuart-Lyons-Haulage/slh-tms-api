using Slh.Tms.Api.Data;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Keeps the operational SQL copy current from the governed Lists CRM. It is deliberately
/// isolated from request handling: a SharePoint outage must never prevent planning or dispatch.
/// </summary>
public sealed class SharePointMasterDataBackgroundService(
    IServiceScopeFactory scopeFactory,
    SharePointMasterDataOptions options,
    ILogger<SharePointMasterDataBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("SharePoint CRM polling is disabled by configuration.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var sync = scope.ServiceProvider.GetRequiredService<SharePointMasterDataSyncService>();
                var staging = scope.ServiceProvider.GetRequiredService<StagingService>();
                var result = await sync.ReadAsync(stoppingToken);
                foreach (var request in result.Requests)
                    await staging.PromoteDirect(request.EntityType, request.Payload, stoppingToken);
                logger.LogInformation("Applied {RowsRead} Microsoft Lists CRM rows to the TMS operational copy.", result.RowsRead);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Microsoft Lists CRM sync failed; the TMS remains available and will retry.");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
