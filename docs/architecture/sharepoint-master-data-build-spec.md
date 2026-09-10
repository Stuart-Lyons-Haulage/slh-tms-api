# SharePoint Hub Master-Data Build Specification

## Objective

Build the SLH Hub as the people-facing business-information layer while keeping SQL authoritative for live TMS transactions.

## Core lists

### Customers
- CustomerKey (required, immutable)
- Name
- TradingName
- Active
- AccountOwner
- ServiceNotes
- TmsCustomerId
- LastSyncStatus
- LastSyncUtc

### Sites
- SiteKey (required, immutable)
- CustomerKey
- SiteName
- BuildingName
- Address1/2
- Town
- County
- Postcode
- Latitude
- Longitude
- AccessWindowStart
- AccessWindowEnd
- GeofenceId
- Active
- TmsSiteId
- SyncStatus

**Identity rule:** postcode is not a unique key. Building/site identity is preserved even where multiple buildings share a postcode.

### Site aliases
- AliasKey
- SiteKey
- Alias
- AliasType
- Active

Aliases resolve inbound text to a canonical site without changing the canonical site's identity.

### Drivers / Vehicles / Trailers
Use immutable business keys and TMS IDs, with SharePoint holding people-facing reference fields and supporting-document links. Operational allocation, availability and live state remain in SQL.

## Document libraries

Recommended libraries/folders:

- Hub / Orders / Inbound
- Hub / Orders / Amendments
- Hub / Planning / Source
- Hub / Drivers / Documents
- Hub / Vehicles / Documents
- Hub / Compliance
- Hub / Incidents & Claims

Original email attachments are retained as source evidence. SQL stores the source item ID/URL and extracted metadata; it does not need to duplicate the binary attachment.

## Synchronisation contract

SharePoint → API synchronisation sends a stable item identifier, entity type, business key, modified timestamp and current governed fields. The API performs an upsert into the SQL projection and records an audit event.

The synchroniser must not overwrite TMS-owned operational fields such as live allocation, run state, actual arrival/departure, ETA or dispatch state.

## Power Automate order intake

The mailbox flow should archive the original message/attachments first, then call the API staging endpoint with the source identifiers and extracted payload. The API returns a staging identifier and processing outcome.

Required behaviour:

- duplicate message/attachment delivery is idempotent;
- transient API failures are retryable;
- extraction failures create a visible review exception;
- missing PO/load references are warnings;
- amendments retain source lineage;
- no flow action creates live orders directly;
- all source evidence remains accessible from TMS Review Orders.

## Governance

Use required columns, controlled choice fields, validation and views for business users. Avoid making SharePoint calculated fields the source of complex operational rules; put those rules in the API so TMS, imports and automation behave consistently.

## Acceptance tests

1. Two sites with the same postcode remain separate.
2. An alias resolves to the intended canonical site.
3. A changed SharePoint site name updates the SQL projection without altering active runs.
4. Replaying the same mailbox message does not create another staged order.
5. Replaying the same attachment does not create another staged order.
6. An amendment links to its source lineage and remains reviewable.
7. Source attachments remain accessible from the staged/promoted order.
8. A failed provider/API call leaves retryable evidence rather than a lost message.
