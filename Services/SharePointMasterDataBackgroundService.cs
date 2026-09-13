using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

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
    private const string BootstrapEntityType = "sharepointmasterdatabootstrap";
    // Lists is the editable CRM authority; SQL is only the fast operational projection.
    // Ten minutes keeps planner-facing changes reasonably current without hammering Graph.
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(10);

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
                var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
                var sync = scope.ServiceProvider.GetRequiredService<SharePointMasterDataSyncService>();
                var staging = scope.ServiceProvider.GetRequiredService<StagingService>();

                // One transition bootstrap only. This fills every governed List from the complete
                // SQL master before the Lists become the editable authority. The marker prevents
                // a future restart/deploy from overwriting deliberate edits made in Microsoft Lists.
                var bootstrapped = await db.StagedImports.AsNoTracking().AnyAsync(row =>
                    row.EntityType == BootstrapEntityType && row.Status == StagingStatus.Promoted, stoppingToken);
                if (!bootstrapped)
                {
                    var published = await sync.PublishFromSqlAsync(db, stoppingToken);
                    var completed = DateTimeOffset.UtcNow;
                    db.StagedImports.Add(new StagedImport
                    {
                        EntityType = BootstrapEntityType,
                        IdempotencyKey = "sharepointmasterdatabootstrap:v1",
                        PayloadJson = JsonSerializer.Serialize(new
                        {
                            published.ListsWritten,
                            published.RowsWritten,
                            published.RowsByList,
                            completedAtUtc = completed
                        }),
                        Source = "One-time SQL to Microsoft Lists master-data bootstrap",
                        Status = StagingStatus.Promoted,
                        ReceivedAtUtc = completed,
                        ReviewedAtUtc = completed,
                        ReviewedBy = "system:sharepoint-master-data-bootstrap",
                        ReviewNote = "Initial complete master-data population finished. Microsoft Lists is now the editable authority; TMS SQL is an operational projection."
                    });
                    await db.SaveChangesAsync(stoppingToken);
                    logger.LogInformation(
                        "Completed one-time Microsoft Lists master-data bootstrap: {ListsWritten} lists, {RowsWritten} rows.",
                        published.ListsWritten, published.RowsWritten);
                }

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
