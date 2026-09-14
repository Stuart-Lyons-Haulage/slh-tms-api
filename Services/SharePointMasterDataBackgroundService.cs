using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Contracts;
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
    private const string CustomerContactsBootstrapEntityType = "sharepointcustomercontactsbootstrap";
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

                var contactsBootstrapped = await db.StagedImports.AsNoTracking().AnyAsync(row =>
                    row.EntityType == CustomerContactsBootstrapEntityType && row.Status == StagingStatus.Promoted, stoppingToken);
                if (!contactsBootstrapped)
                {
                    var rowsWritten = await sync.PublishCustomerContactsFromSqlAsync(db, stoppingToken);
                    var completed = DateTimeOffset.UtcNow;
                    db.StagedImports.Add(new StagedImport
                    {
                        EntityType = CustomerContactsBootstrapEntityType,
                        IdempotencyKey = "sharepointcustomercontactsbootstrap:v1",
                        PayloadJson = JsonSerializer.Serialize(new { rowsWritten, completedAtUtc = completed }),
                        Source = "One-time Customer Contacts SQL to Microsoft Lists bootstrap",
                        Status = StagingStatus.Promoted,
                        ReceivedAtUtc = completed,
                        ReviewedAtUtc = completed,
                        ReviewedBy = "system:sharepoint-customer-contacts-bootstrap",
                        ReviewNote = "Customer Contacts populated once from the operational SQL copy. Hub Customer Contacts is now the editable authority."
                    });
                    await db.SaveChangesAsync(stoppingToken);
                    logger.LogInformation("Seeded {RowsWritten} customer contacts into the governed Microsoft List.", rowsWritten);
                }

                var result = await sync.ReadAsync(stoppingToken);
                foreach (var request in result.Requests)
                {
                    // Fuel cards are projected by the dedicated MasterDataSync service. The legacy
                    // staging promoter has no fuelcard entity and should not abort the whole CRM poll.
                    if (request.EntityType.Equals("fuelcard", StringComparison.OrdinalIgnoreCase)) continue;
                    await staging.PromoteDirect(request.EntityType, request.Payload, stoppingToken);
                }

                await ReconcileCustomerContactSnapshotAsync(db, result.Requests, stoppingToken);
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

    private static async Task ReconcileCustomerContactSnapshotAsync(
        TmsDbContext db,
        IReadOnlyList<StageImportRequest> requests,
        CancellationToken ct)
    {
        var authoritative = requests
            .Where(request => request.EntityType.Equals("customercontact", StringComparison.OrdinalIgnoreCase))
            .Select(request => ContactIdentity(request.Payload))
            .Where(identity => identity is not null)
            .Select(identity => identity!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var contacts = await db.CustomerContacts.Where(contact => contact.Active).ToListAsync(ct);
        var changed = false;
        foreach (var contact in contacts)
        {
            if (authoritative.Contains(ContactIdentity(contact.CustomerCode, contact.Name))) continue;
            contact.Active = false;
            changed = true;
        }
        if (changed) await db.SaveChangesAsync(ct);
    }

    private static string? ContactIdentity(JsonElement payload)
    {
        if (!payload.TryGetProperty("customerCode", out var customer) || !payload.TryGetProperty("name", out var name)) return null;
        return ContactIdentity(customer.ToString(), name.ToString());
    }

    private static string ContactIdentity(string customerCode, string name) =>
        $"{customerCode.Trim()}\u001f{name.Trim()}";
}
