# SLH TMS Master Data Sync Function

This is a .NET 8 isolated Azure Function retained for reference only. SQL Server is the TMS master-data authority. SharePoint-to-SQL sync is disabled by default and must not be enabled while the TMS writes operational master data directly to SQL.

Configuration settings:

- MasterDataSync__Enabled: false by default. The timer and manual trigger return without syncing while disabled.
- MasterDataSyncSchedule: NCRONTAB schedule, default 0 0 * * * * (hourly), ignored while disabled.
- MasterDataSync__TenantId, MasterDataSync__ClientId, MasterDataSync__ClientSecret.
- MasterDataSync__Hostname and MasterDataSync__SitePath.
- MasterDataSync__SqlConnectionString: sync-writer SQL credential, not the TMS read-only principal.
- MasterDataSync__DeadLetterQueueName.
- MasterDataSync__TmsCacheInvalidateUrl and MasterDataSync__TmsCacheInvalidateToken.

Manual endpoint: POST /api/sync/trigger/{listName}

Valid keys: depot, customer, customercontact, driver, vehicle, trailer, site, subcontractor, market, fuelcard and fuelprice.

`customercontact` reads the governed **Hub Customer Contacts** list. `ReceivesEtaUpdates` controls whether a contact is presented as an ETA-recipient suggestion. It never causes an email to be sent automatically.

This function is not part of the API production deploy workflow. Do not deploy or enable it for the SQL-authority architecture. The old SharePoint-to-SQL process can overwrite records or mark missing items inactive and therefore conflicts with SQL writes.

Legacy SharePoint projection setup (retained only for rollback reference):

1. Apply Database/040_SharePoint_Master_Projection.sql and the forward-compatible API migrations through 048.
2. Give the Function identity or credential write rights only to master_ tables.
3. Give the TMS API identity SELECT only on active views.
4. Configure cache invalidation only after the Entra fail-closed gate confirms the TMS.Admin app role.
5. Invoke each list manually once and review Application Insights and the dead-letter queue.
6. Enable the timer.

The TachoMaster-to-SQL driver synchronisation in the API only refreshes drivers with a valid Tacho card read dated from the inclusive UK-local six-calendar-month cutoff through today. Missing, invalid, stale, and future dates are excluded. Historical SQL driver records remain stored and are retained as inactive when they do not meet the cutoff.

Market seller entries are keyed by the stable SharePoint list item ID when `MarketKey` is blank during transition. Repeated seller names at different stalls are valid separate rows; the read projection must not impose uniqueness on market plus seller. Email routes carry the selected `MarketKey` into parsed order data so downstream processing can preserve the association.
