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

### Markets
- MarketKey (required, immutable)
- MarketName
- ContactName
- StandOrLocation
- Salesman
- Sender
- Active
- TmsMarketContactId
- SyncStatus
- LastSyncUtc

### Fuel cards
- FuelCardKey (required, immutable)
- VehicleRegistration (required, indexed)
- TmsVehicleId
- FuelProvider
- CardReference / LastFour
- PinReference or approved protected secret reference
- Active
- SyncStatus
- LastSyncUtc

Fuel-card rows are always linked to a vehicle. Full card numbers and PINs are restricted fields and must not appear in general exports, audit messages or user-facing comments.

## Hub document structure

The production Team Portal contains the following Hub folders beneath `Planning/Transport Operations System/00 SLH Hub`:

- Customers
- Sites
- Site Aliases
- Drivers
- Vehicles
- Trailers
- Orders/Inbound
- Orders/Amendments
- Planning Source
- Compliance
- Incidents & Claims
- Integration Logs

The Hub build pack and provisioning script are stored at the Hub root.

## Synchronisation contract

SharePoint → API synchronisation sends a stable item identifier, entity type, business key, modified timestamp and current governed fields. The API performs an upsert into the SQL projection and records an audit event.

The synchroniser must not overwrite TMS-owned operational fields such as live allocation, run state, actual arrival/departure, ETA or dispatch state.

## Power Automate order intake

The existing production mailbox flow remains deliberately unchanged. It sends inbound mail into TMS staging, keeps SQL as the approval/history store and retains the original message and attachments in the Info mailbox. SharePoint is not introduced as a live-order approval store.

Required behaviour remains:

- duplicate message/attachment delivery is idempotent;
- transient API failures are retryable;
- extraction failures create a visible review exception;
- missing PO/load references are warnings;
- amendments retain source lineage;
- no flow action creates live orders directly.

## Governance

Use required columns, controlled choice fields, validation and views for business users. Avoid making SharePoint calculated fields the source of complex operational rules; put those rules in the API so TMS, imports and automation behave consistently.

## Acceptance tests

1. Two sites with the same postcode remain separate.
2. An alias resolves to the intended canonical site.
3. A changed SharePoint site name updates the SQL projection without altering active runs.
4. Replaying the same mailbox message does not create another staged order.
5. Replaying the same attachment does not create another staged order.
6. An amendment links to its source lineage and remains reviewable.
7. Supporting documents remain accessible from the relevant Hub record.
8. A failed provider/API call leaves retryable evidence rather than a lost message.
