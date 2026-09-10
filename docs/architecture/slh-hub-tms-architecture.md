# SLH Hub + TMS Architecture

## Purpose

The SLH digital estate uses a three-layer architecture. The layers have clear ownership so SharePoint remains usable by people while the TMS remains authoritative for high-volume operational transactions.

### 1. SharePoint Hub — people-facing business information

SharePoint is the governed home for business-maintained master data, documents, forms, policies and source evidence. Examples include customer/site reference information, driver and vehicle supporting documents, incident/claims documents, planning source files and original inbound order attachments.

SharePoint records must carry stable business keys and, where applicable, the TMS SQL identifier. SharePoint is not the live run/stop/ETA/optimiser store.

### 2. Azure SQL — operational system of record

SQL owns transactional and high-volume operational state: staged and live orders, loads, runs, stops, allocations, tracking observations, geofence visits, ETA calculations, compliance evidence, integration state and audit history. SQL also contains synchronised/cache representations of SharePoint master data so dispatch screens do not depend on SharePoint availability.

### 3. TMS — unified operational shell

The React TMS is the operational interface and intelligence layer. It presents SQL-backed planning, dispatch, live tracking, wallboards, exceptions, compliance and reporting through one consistent SLH-branded experience.

## Data ownership

| Domain | Human source | Runtime authority |
|---|---|---|
| Customers/sites/master references | SharePoint Hub | SQL synchronised projection |
| Supporting documents | SharePoint | SharePoint original + SQL reference |
| Inbound email/source attachments | Outlook/Power Automate → SharePoint | SQL staging metadata + extracted payload |
| Orders | TMS/API | SQL |
| Loads/runs/stops/allocations | TMS planner | SQL |
| Tracking/geofence/ETA | RoadTech/Falcon + TMS | SQL |
| Tacho/legal-hours evidence | TachoMaster | SQL integration evidence |
| Audit/review/approval history | TMS | SQL append-only history |
| Operational presentation | TMS | React/API |

## Inbound order contract

Power Automate remains the mailbox boundary. Every inbound message is classified and staged before it can become live operational work.

The flow should preserve:

- source mailbox;
- Outlook message ID and internet/message identifiers where available;
- sender and subject;
- received timestamp;
- attachment name, content type and source identifier;
- SharePoint source-document URL/item ID after archival;
- extracted PO/SO/load reference;
- customer/site candidate;
- extracted order lines;
- parser/version metadata; and
- processing status/error information.

The API treats the source message/attachment identity as an idempotency key. Retries update the existing staged evidence rather than silently creating a second order. Amendments remain linked to the same source lineage.

## Review-first lifecycle

`Inbound email → Power Automate → SharePoint source archive → API staging → Pending Review → Planner Review/approval → Live Order → Load/Run → Dispatch → Tracking/ETA → Complete`

Power Automate must never promote an email directly to live work.

Missing PO/load references are warnings, not automatic blockers. Weightrose, Markets, NWF, NWAY and other customer-specific rules are applied during extraction/normalisation and surfaced for review when confidence is insufficient.

## Master-data synchronisation

SharePoint is the human-maintained source for governed master-data fields. A scheduled/event-driven synchronisation projects approved changes into SQL. Synchronisation must be:

1. idempotent;
2. keyed by stable SharePoint item ID plus business key;
3. auditable;
4. tolerant of aliases/legacy names;
5. non-destructive for fields owned by TMS operations; and
6. explicit about conflicts.

For sites, postcode alone is never a unique identity. Distinct buildings/geofences sharing a postcode must remain distinct records. Aliases may resolve to a canonical site but must not overwrite another building.

## Branding/design system

SharePoint Hub and TMS use one SLH visual language: Lyons blue/green brand treatment, consistent typography, navigation, cards, buttons, icons, status semantics and accessible contrast. The TMS remains optimised for operational density; SharePoint remains optimised for people-facing business content.

## Integration boundary

RoadTech/Falcon, TachoMaster, Fleetio and Sage HR remain external integration sources. Provider credentials stay server-side. Their data is normalised into SQL and exposed through the TMS. SharePoint is not used as a polling/cache substitute for live telemetry.

## Reliability rules

- Preserve source evidence rather than deleting it.
- Use idempotent keys for every inbound integration.
- Retry transient provider failures; do not duplicate operational records.
- Distinguish planned schedule times from live ETA.
- Never infer legal-hours compliance from vehicle movement alone.
- Keep operational APIs authenticated through Microsoft Entra.
- Keep production secrets in Azure/Key Vault-backed configuration.
- Health checks must expose dependency freshness and readiness separately.

## Deployment acceptance

A production architecture change is accepted only after CI, CodeQL and container deployment succeed and the live API reports the new revision. Readiness, tracking, TachoMaster, geofence and portal/proxy checks must then pass.
