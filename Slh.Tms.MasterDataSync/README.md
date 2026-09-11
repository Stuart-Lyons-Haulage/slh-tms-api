# SLH TMS Master Data Sync Function

This is a .NET 8 isolated Azure Function. SharePoint Online is the master-data source and SQL Server is the normalised read projection used by the TMS.

Configuration settings:

- MasterDataSyncSchedule: NCRONTAB schedule, default 0 */5 * * * *.
- MasterDataSync__TenantId, MasterDataSync__ClientId, MasterDataSync__ClientSecret.
- MasterDataSync__Hostname and MasterDataSync__SitePath.
- MasterDataSync__SqlConnectionString: sync-writer SQL credential, not the TMS read-only principal.
- MasterDataSync__DeadLetterQueueName.
- MasterDataSync__TmsCacheInvalidateUrl and MasterDataSync__TmsCacheInvalidateToken.

Manual endpoint: POST /api/sync/trigger/{listName}

Valid keys: depot, customer, driver, vehicle, trailer, site, subcontractor, market, fuelcard and fuelprice.

Deployment order:

1. Apply Database/040_SharePoint_Master_Projection.sql.
2. Give the Function identity or credential write rights only to master_ tables.
3. Give the TMS API identity SELECT only on active views.
4. Configure cache invalidation only after the Entra fail-closed gate confirms the TMS.Admin app role.
5. Invoke each list manually once and review Application Insights and the dead-letter queue.
6. Enable the timer.
