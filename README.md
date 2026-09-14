# Stuart Lyons Haulage TMS API

Production .NET 8 API for the Stuart Lyons Haulage transport management system. The API/SQL layer is the system of record for orders, staging, planning, live run progress, geofence evidence, compliance checks, integrations and audited operational recovery. Microsoft Lists is the governed business source for master data; the API holds its synchronised operational projection.

The React portal lives in `slh-tms-web`. This repository owns the secured backend, Azure SQL data model, live integrations and production health checks.

## Production

| Item | Value |
| --- | --- |
| Portal | `https://slh-tms-portal-prod.gentlepond-08dba66b.uksouth.azurecontainerapps.io/` |
| API | `https://slh-tms-api-prod.gentlepond-08dba66b.uksouth.azurecontainerapps.io` |
| Resource group | `slh-tms-prod-rg` |
| Region | UK South |
| Runtime | Azure Container Apps |
| Database | Azure SQL |
| Authentication | Microsoft Entra JWT bearer tokens |
| Secrets | Azure Key Vault / Container App secret references |

The portal calls the API through the same-origin `/tms-api` Nginx proxy. External callers use versioned routes beneath `/api/v1`.

Deployment is performed by GitHub Actions using Azure OIDC. Do not use publish profiles or long-lived deployment secrets.

## Core Responsibilities

- Receive and stage new transport work.
- Validate and promote reviewed orders into live operational work.
- Import planner source-line JSON into loads, stops and allocations.
- Synchronise governed master-data projections for customers, sites, drivers, vehicles, trailers and planning preferences without making a second editable source of truth.
- Ingest RoadTech/Falcon live vehicle telemetry.
- Read TachoMaster driver, card, duty and legal-hours evidence.
- Process geofence arrival, dwell and departure evidence.
- Calculate live run progress, next stop, ETA and completion state.
- Reconcile walkround, tacho, movement and allocation evidence for compliance.
- Expose secured APIs to the portal, TV wallboard and reporting screens.

## Operational Architecture

The performance and integration reduction plan is documented in [docs/performance-integration-roadmap.md](docs/performance-integration-roadmap.md). It defines which data must remain synchronous, which integrations belong in background jobs, and the acceptance tests required before removing legacy paths.

The production path is deliberately resilient:

1. Planner and order data is validated by the API.
2. Primary SQL tables are used when the operational schema is available.
3. Audited register/staging storage is used as a fallback so planner pages do not fail because one dedicated table is unavailable.
4. RoadTech/Falcon supplies live vehicle location, movement, driver/card identity where available, and source events for geofence processing.
5. TachoMaster supplies driver profiles, card numbers, duty records and legal drive/work availability metrics.
6. Azure Maps is used for route and ETA calculation only where there is enough live execution evidence.
7. The operations wallboard, TV wallboard, live runs and planner screens consume the same run-progress contract.

## RoadTech, Falcon and TachoMaster

RoadTech provides both the Falcon/DOT tracking API and the TachoMaster API under the RoadTech API host family:

| Environment | Base URL |
| --- | --- |
| Live | `https://api-v1.roadtech.co.uk` |
| Staging / alpha | `https://api-v1-alpha.roadtech.co.uk` |

The host is not what separates Falcon from TachoMaster. The endpoint paths and returned data do:

- Falcon/DOT paths provide vehicle telemetry, current location, movement and sometimes live driver/card identity.
- TachoMaster paths provide driver profiles, cards, duty history/open duties and legal-hours metrics.

Production can use dedicated TachoMaster settings. If no dedicated TachoMaster credentials are configured, the API intentionally falls back to the configured RoadTech/DOT credentials because the same RoadTech login/API key can be valid for both API areas.

Important operational rule:

- Falcon live card/driver evidence can confirm that a card/driver is present in a moving vehicle.
- TachoMaster duty/profile metrics are still required before the system can claim legal drive-time and break calculations.
- If Falcon confirms a card but TachoMaster does not return legal-hours metrics, screens must show card confirmed and hours missing, not generic pending.

## Run Progress and Wallboard Rules

Run progress is built from planned load data plus live evidence. The API must not mark a run `ON ROUTE` merely because it is planned or because a vehicle is inside any recognised geofence.

The wallboard status contract follows these rules:

| Evidence | Result |
| --- | --- |
| No planned driver | Explicit no planned driver status |
| No planned vehicle | Explicit no planned vehicle status |
| Falcon/Tacho identity matches allocated driver and vehicle | Signed-on/card-confirmed status with time when known |
| Falcon card present but no TachoMaster hours | Card confirmed, legal hours unavailable |
| TachoMaster duty/profile metrics matched | Legal-hours fields are shown and used for ETA/break assessment |
| Live vehicle location fresh | Live tracking state is shown |
| Vehicle enters linked stop geofence | Current stop/on-site state is shown |
| Vehicle departs linked stop geofence | Stop is completed with actual departure |
| Final linked stop has departed | Run is completed/finished |
| Evidence missing or stale | Clear exception, not planned time disguised as live progress |

Planned start and planned delivery windows are fallback schedule data only. They must never be described as live ETA. Live ETA requires live location and a calculable next stop.

## Geofence Processing

The API uses the approved SLH geofence set and RoadTech/Falcon tracking events to derive:

- arrival time;
- current on-site state;
- dwell time;
- departure time;
- completed stops;
- final run completion; and
- linkage exceptions where a tracker/geofence event cannot be confidently tied to a planned stop.

Production currently uses the approved embedded geofence runtime when the SQL identity does not have DDL permission to mutate runtime geofence tables. New Falcon geofences should be incorporated into the approved seed process rather than written ad hoc in production.

Useful geofence endpoints:

- `GET /api/v1/health/geofences`
- `GET /api/v1/geofences`
- `GET /api/v1/geofences/visits?date=yyyy-MM-dd`

## Planner Imports

All planner JSON schemas used by the live Planner Import page must post to:

`POST /api/v1/planning/import-plan`

This includes `slh-planner-plan-v3-source-lines`. Do not add browser rewrites that divert source-line payloads to the older direct-SQL import path.

The resilient import endpoint supports:

- idempotent re-imports;
- audited fallback storage;
- capacity warnings;
- source-line preservation;
- allocation reconciliation; and
- clear held/excluded run reporting.

The CI suite contains regression coverage for source-line JSON through the live resilient endpoint so this routing cannot silently regress to the previous failure-prone path.

## Clean Re-import of a Planning Day

Use the controlled planning-day reset rather than deleting database history:

- Preview: `GET /api/v1/planning-day/{yyyy-MM-dd}/reset-preview`
- Reset: `DELETE /api/v1/planning-day/{yyyy-MM-dd}?confirm=RESET-{yyyy-MM-dd}`

The reset is approval-protected. It cancels active work for that planning day, removes active stops and archives matching staged/register rows while retaining source evidence and releasing import keys for a clean re-import.

## Email Intake

Mailbox intake is approval-first. Email-derived orders are staged as `PendingReview` with their source evidence and must be reviewed in the TMS before promotion. Mailbox automation must never create live work silently.

The current intake path preserves Outlook message and attachment evidence, uses PO-first duplicate/amendment classification, records append-only SQL-backed staging history, and keeps a durable source link from promoted orders back to the originating staging evidence.

Power Automate should preserve source message ID, subject, sender and attachment identity for idempotency/audit. Failed sends or intake attempts remain retryable without losing the original evidence.

## Compliance and Dispatch Evidence

Dispatch and compliance checks combine:

- TMS allocation;
- live Falcon/DOT vehicle movement;
- Falcon/Tacho live card or driver identity;
- TachoMaster legal-hours metrics where supplied;
- Fleetio walkround evidence;
- Sage HR context where configured; and
- planner acknowledgement for structural warnings.

A moving vehicle alone is not enough to prove the correct allocated driver. A planned allocation alone is not enough to prove sign-on. Live card/driver evidence must be matched to the allocated vehicle and driver, or the API must return a clear mismatch/missing-evidence result.

## Configuration

Set production configuration on the Container App using Key Vault-backed secret references where possible.

### Required Platform Settings

| Setting | Purpose |
| --- | --- |
| `ConnectionStrings__TmsDb` | Azure SQL connection string |
| `Entra__TenantId` | Microsoft Entra tenant ID |
| `Entra__Audience` | API application audience, normally `api://<API-CLIENT-ID>` |
| `Entra__AllowedDomains__0` | Company sign-in email/UPN domain authorised for TMS access, currently `lyonshaulage.com` |
| `Cors__AllowedOrigins__0` | Local development origin |
| `Cors__AllowedOrigins__1` | Production portal origin |
| `Deployment__Revision` | Git SHA exposed by health checks |

### RoadTech / Falcon

| Setting | Purpose |
| --- | --- |
| `Tracking__Dot__Enabled` | Enables RoadTech/Falcon ingestion |
| `Tracking__Dot__BaseUrl` | RoadTech API base URL |
| `Tracking__Dot__ApiKey` | RoadTech API key |
| `Tracking__Dot__Username` | RoadTech username |
| `Tracking__Dot__Password` | RoadTech password |
| `Tracking__Dot__CompanyCode` | RoadTech company code where required |
| `Tracking__Dot__PollIntervalMinutes` | Poll cadence |
| `Tracking__Dot__DataMask` | RoadTech data mask |
| `Tracking__Dot__OnlyLive` | Restricts requests to live/current provider data |

### TachoMaster

| Setting | Purpose |
| --- | --- |
| `Integrations__TachoMaster__Enabled` | Enables TachoMaster client |
| `Integrations__TachoMaster__BaseUrl` | RoadTech API base URL for TachoMaster endpoints |
| `Integrations__TachoMaster__ApiKey` | TachoMaster/RoadTech API key |
| `Integrations__TachoMaster__Username` | TachoMaster/RoadTech username |
| `Integrations__TachoMaster__Password` | TachoMaster/RoadTech password |

Legacy secret names such as `slh-dot-base-url`, `slh-dot-api-key`, `slh-dot-username` and `slh-dot-password` can be used for DOT/Falcon. Dedicated TachoMaster secrets should be used where available, but the app will share RoadTech credentials when no dedicated TachoMaster credentials exist.

### Other Integrations

| Setting | Purpose |
| --- | --- |
| `Integrations__Fleetio__Enabled` | Enables Fleetio walkround integration |
| `Integrations__Fleetio__BaseUrl` | Fleetio API base URL |
| `Integrations__Fleetio__ApiKey` | Fleetio API key |
| `Integrations__Fleetio__AccountToken` | Fleetio account token |
| `Integrations__SageHr__Enabled` | Enables Sage HR integration |
| `Integrations__SageHr__BaseUrl` | Sage HR API base URL |
| `Integrations__SageHr__ApiKey` | Sage HR API key |
| `Integrations__AzureSms__Enabled` | Enables Azure Communication Services SMS dispatch |
| `Integrations__AzureSms__ConnectionString` | ACS connection string |
| `Integrations__AzureSms__From` | Approved sender |
| `Integrations__TextBee__Enabled` | Enables TextBee dispatch route if used |
| `Integrations__OpenAI__Enabled` | Enables assistant integration |

Never commit passwords, SQL connection strings, API keys, customer attachments, live exports or provider payloads.

## Health and Diagnostics

The repository includes `.github/workflows/full-production-health.yml`. It checks the live production environment for:

- core API health;
- Azure SQL readiness;
- operational data-readiness/schema;
- RoadTech current GPS;
- TachoMaster runtime access;
- geofence payload health;
- portal reachability; and
- protected operational routes failing closed without authentication.

RoadTech and TachoMaster also have dedicated runtime verification workflows after deployment.

Useful endpoints:

| Endpoint | Purpose |
| --- | --- |
| `GET /health` | Lightweight liveness |
| `GET /health/ready` | SQL readiness |
| `GET /api/v1/health` | Versioned health plus deployed revision |
| `GET /api/v1/health/ready` | Versioned readiness |
| `GET /api/v1/health/tracking` | RoadTech/Falcon connectivity and freshness |
| `GET /api/v1/health/tachomaster` | TachoMaster connectivity, profile counts, duty counts and metric freshness |
| `GET /api/v1/health/geofences` | Geofence runtime readiness |
| `GET /api/v1/diagnostics/data-readiness` | Operational schema/data readiness |
| `GET /api/v1/integrations/status` | Integration summary for the portal |

## Development

Install the .NET 8 SDK, then run:

```bash
dotnet restore
dotnet build Slh.Tms.Api.csproj -c Release
dotnet test Slh.Tms.Api.Tests/Slh.Tms.Api.Tests.csproj -c Release
```

Local development requires user secrets or environment variables for SQL, Entra and whichever integrations are being exercised. Keep fake/local values in `appsettings.Development.json` or user secrets, not in source control.

## CI and Deployment

Every branch and pull request runs:

- restore;
- Release build; and
- `Slh.Tms.Api.Tests`.

Production `main` deploys through GitHub Actions to Azure Container Apps. A production change is not complete until:

1. API CI passes.
2. CodeQL passes.
3. Container image builds and deploys.
4. Production `/api/v1/health` reports the new `Deployment__Revision`.
5. Readiness, tracking, TachoMaster and geofence health checks pass.
6. The portal/API proxy check passes from the web deploy where applicable.

## Security

- Keep all provider credentials server-side.
- Use Key Vault secret references for production settings.
- Use GitHub OIDC for Azure deployment.
- Require Microsoft Entra authentication for operational routes.
- Preserve staged/import source evidence instead of deleting history.
- Keep TV access keys in server-side configuration only.
- Do not weaken CORS or bearer-token validation for troubleshooting.

## Related Documentation

- `docs/PowerAutomate-InfoMailbox-Order-Intake-Production.md`
- `docs/DRIVER_SMS_DELIVERY.md`
- `docs/plans/`

## Master-data authority and operating boundary

**Current design (verified in this repository):** Microsoft Lists is the
business-maintained, governed master-data source; the portal exposes it as a
read-only operational view. SQL is still the authoritative transactional store
for orders, staging, plans, allocations, tracking, ETAs, integrations and audit
history, and holds a synchronised master-data projection so planning does not
depend on a live Graph/Lists call. Do not describe SQL as an independent
editable master-data system or restore write controls to the portal.

The API reads and normalises the configured Lists, assigns deterministic
idempotency keys from entity, List item and source version, and stages the
change for reconciliation. It also has controlled SQL-to-Lists publish paths
for bootstrap/recovery and learned site aliases. A retrying audit outbox keeps
temporary Graph failure from discarding a learned alias or a governed change.
Conflicts, unknown identities and retirements require review; sync must not
silently delete records or rewrite a planner's deliberate List edit.

Configured List families are `Hub Customers`, `Hub Customer Contacts`, `Hub
Sites`, `Hub Drivers`, `Hub Vehicles`, `Hub Trailers`, `Fuel Cards`, `TMS
Markets` and `Order Email Routes`. The names are configuration defaults, not a
guarantee that a tenant has not renamed a List.

| Entity | Operational fields retained by the projection |
| --- | --- |
| Customers and contacts | stable customer/account key, name/trading name/aliases, invoice and default-contact detail, owner, service notes, default site; contact email/mobile and ETA-recipient flag |
| Sites / geofences | site and customer keys, address/postcode/coordinates, geofence identity/radius, opening hours, collection and driver instructions, aliases, map link and operational region |
| Drivers | employee number and display/Tacho names, email/mobile, grade, driver type/group/skills/agency/coding, licence and expiry detail, **tachograph card number**, TachoMaster member ID, last sync and current allocated vehicle |
| Vehicles / trailers | registration/fleet number/type, abbreviation/transmission/DVS, depot, capacities, MOT/test/tacho-calibration evidence, Fleetio identifiers/status and notes |
| Fuel cards | vehicle/registration relationship, provider, full PIN where operations requires it, PIN secret reference, last four digits and Shell/BP red/BP plain allocation fields. Full PINs belong only in access-controlled Lists/server paths, never browser configuration, logs or ordinary exports. |
| Markets / sender CRM | market, trader/name, stand/location, salesman, sender, optional read-only map PDF; email-route key, customer/site mapping, exact sender/domain/subject matcher, parser type, active and review-required flags |

Driver identity must be treated as a reconciliation problem, not a name match.
The TachoMaster orchestration enriches then canonicalises in this order:
tachograph card number, TachoMaster member code, employee number, then a unique
compatible name. Same-name/different-identity cases remain separate. A worker
is not a driver merely because it has an employee number: office `TM*` records
are excluded; a card, driver role/group, agency or subcontractor evidence is
required. Sage HR filtering is implemented as a driver-team and/or driver
position-keyword filter, but the live Sage configuration and source data cannot
be verified from Git.

### Data-retention contract for the governed Lists

The historical TMS work makes this a non-negotiable continuity requirement: a
name-only List is a failed migration. Retain the complete operational record and
its identity/audit metadata for every entity, with the source payload/audit link
available to recover a field that is not yet first-class.

- **Drivers:** retain email, mobile, grade/coding, employment/agency and group,
  skills, licence/compliance dates, TachoMaster identity, tachograph card,
  current allocation and historical keys. One active driver per normalised card
  is the target; never merge people merely because their names match. Card-less
  workers are reviewed exceptions, using member/employee identity only as a
  temporary reconciliation aid.
- **Sites:** retain each physical site as its own record—e.g. individual Aldi,
  Amazon, Waitrose and Morrisons locations—not a single customer placeholder.
  Preserve address, postcode, coordinates/geofence, map link, timings/cut-offs,
  booking/collection and driver instructions, customer link and aliases. An
  alias supplements a site; it must not collapse two locations into one.
- **Vehicles, trailers and fuel cards:** retain registrations/fleet numbers,
  identifiers, active/compliance/tracking state, capacities, current allocations
  and supporting notes. Fuel Cards retain provider, allocation and—where
  required for ongoing fuelling—the full PIN as well as secret reference, last
  four and Shell/BP fields. Store full PINs only in access-controlled Microsoft
  Lists and approved server-side paths: never VITE configuration, public/browser
  payloads, logs, CI output or routine CSV/PDF exports.
- **Customers, contacts, markets and routes:** retain trading aliases, account
  and service detail, contact names/emails/mobiles and approved ETA-recipient
  state; market sender/seller/stall detail; and email-route matching,
  confidence/review and site/customer association. Inbound order senders are
  learned candidates, not automatic ETA recipients.

Every synchronised record needs stable business identity, SharePoint item ID,
source version, active/retired state, sync status/message and audit provenance.
The first reconciliation may seed missing List rows from a verified legacy
projection, but must not overwrite later approved office edits. Lists-to-SQL is
the normal master-data direction; a SQL-to-List write is an explicit governed
publish/recovery path with audit-outbox retry handling.

**Cadence note:** the inspected current background service polls Lists every ten
minutes. Historical chat decisions describe a once-hourly office-master refresh
while operations use the SQL projection. Treat this as an unresolved operating
choice to confirm before changing the interval; neither cadence should make a
live planning/dispatch request wait on SharePoint.

Useful master-data controls include the operational Master Data, SharePoint
master-data, reconciliation and Tacho driver controllers. Exact routes are
defined by their controller attributes and the OpenAPI document at runtime;
do not invent a direct Graph browser write path.

## Order intake, source evidence and replay

The supported inbound route is `POST /api/v1/order-intake/email`, normally
called by the source-controlled `SLH-TMS | Info Mailbox | Order Intake | PROD`
Power Automate flow. The flow obtains every source attachment via Outlook's
attachment API (not the trigger's attachment string), preserves attachment
metadata including inline/content-ID state, and sends source message,
conversation, sender, subject, body/body preview and web-link identifiers to
the API. Attachment bytes are used for parsing but are intentionally not copied
into SQL; immutable source evidence and identifiers remain traceable to the
mailbox retention system.

The API parses structured body content, HTML-normalised text, tables and
non-inline Excel workbooks. It has specialist parsers for Barfoots/Waitrose
wave, Summer Berry Morrisons/Aldi and legacy Vitacress/Waitrose workbooks, with
generic parsing only as a fallback. All candidates enter staging as review work;
mailbox automation must never promote a live order directly.

Duplicate/amendment handling is intentionally PO-first where a PO exists, with
source message/attachment identity, natural keys and an append-only intake
ledger providing additional evidence. Exact sender rules outrank domain rules;
subject-specific routes outrank generic routes; routing conflicts are marked
`RequiresReview`. Planner approval promotes a reviewed staging item and can
learn the exact sender mapping. If the sender is later approved against another
collection site, keep the customer association but clear the unsafe default site.
Amendments and cancellations remain auditable staging/review events. Historic
replay is safe only through the same intake endpoint with the original source
identity—do not hand-create live orders to "replay" an email.

Power Automate is external to this repository at runtime. The checked-in flow
definition and validation scripts prove the contract, not that the tenant flow,
connector binding, mailbox permissions or parent/child flow topology currently
match it. In particular, a parent/child orchestration, any SharePoint links in
email bodies, and current production connection references need tenant-side
confirmation before being stated as fact.

## Preserved routing and planning rules

The following rules are code-backed safeguards, not mere operational folklore:

- NWF/Nature's Way, Barfoots/Barefoots, Langmeads, Summer Berry, TSBC/COOP,
  Aldi, Morrisons, Waitrose, Amazon, Crosspoint/PCC, IFCO/JS and London-market
  inputs should resolve through governed customer/site/sender mappings and
  planner review. The repository has explicit parser/resolver coverage for only
  some of those names; treat unrecognised mappings as review work rather than a
  production guarantee.
- **Negative safeguard:** Barfoots/Barefoots and Summer Berry must never fall
  through to NWF, Drayton or another generic depot default. A collection/site
  match must be positive and unambiguous. Preserve this when changing lookup or
  importer code.
- Market resolution understands Covent Garden (`COVENT`) and New Spitalfields
  (`SPIT`) labels and resolves a unique market/contact/stand rather than a loose
  city-name match. Market, sender and stall detail lives in the governed
  `TMS Markets` projection.
- Import labels derive AM/PM from explicit run type or first collection time;
  WAVE 1 normalises to AM and WAVE 3 to PM. A date crossing, `O/N`,
  `overnight`, or `night out` produces O/N and night-out evidence. Do not infer
  a night driver merely from a planned load without a reviewed allocation.
- Capacity is calculated from standard/euro/unknown pallets. The calculator is
  the authority for the actual threshold and result. Import rules preserve the
  business pallet conventions: Morrisons and Waitrose standard; Aldi from
  Barfoots/NWF euro; Langmeads-to-Aldi Atherstone euro, other Langmeads standard.
  The requested 26-pallet operational capacity must be confirmed against the
  active vehicle/trailer and capacity-calculator configuration before changing
  an allocation; it is not hard-coded as a universal limit in the inspected
  importer.
- Vehicle, driver and trailer swaps are allocations that need fresh dispatch,
  Tacho and tracking evidence. A planned resource is never proof of a live run.

## Runbooks and recovery sequence

1. **Health/release:** check `/api/v1/health`, `/api/v1/health/ready`, tracking,
   TachoMaster and geofence health; confirm the deployed revision; then check
   the portal proxy. Follow the named production-health, RoadTech, TachoMaster,
   wallboard and final-ETA workflows rather than treating HTTP 200 alone as a
   release proof.
2. **Lists freshness/sync:** verify the Lists configuration and Graph credentials,
   inspect master-data/reconciliation status and audit/outbox failures, correct
   the governed List row, then re-run the controlled sync/reconciliation. Do
   not repair a stale projection by editing SQL directly.
3. **Failed import or email:** retain the staging row and source identity; read
   parser warnings and duplicate decision; fix mapping/parser data; replay the
   original request through the intake/import endpoint; have a planner review
   before promotion. Never delete the evidence just to clear the queue.
4. **Tracking/geofence backfill:** establish vehicle and site/geofence linkage
   first, then use the guarded replay/recovery controllers. Validate stop order,
   arrival/departure and final-completion evidence afterwards; generic geofence
   matches must not complete a run.
5. **Rollback:** deploy an earlier tested immutable image/revision using the
   existing GitHub/Azure workflow or Container Apps revision controls, verify
   health and proxy checks, and preserve SQL/audit history. Roll back app code,
   not operational data, unless an approved recovery plan says otherwise.

## Schema, tests and change discipline

Schema history is deliberately retained in both embedded `Database/000_*.sql`
through `045_*.sql` repair/projection scripts and EF migrations in `Migrations/`.
`SchemaMigrationRunner` and the schema-health tests exist because production has
encountered partially applied or legacy schema states. Add a forward-only,
idempotent migration and its regression coverage; do not renumber or edit an
applied migration, and do not use an ad-hoc production DDL command as a fix.

The test project contains API, schema, intake, planning, master-data,
TachoMaster, geofence, dispatch and resilience regressions. At minimum run the
three .NET commands above before a backend documentation-adjacent change; run
the checked-in Power Automate validators when changing either flow definition.
For a behavioural change, add a narrowly named regression that proves the
negative case as well as the intended happy path (especially duplicate intake,
unsafe site routing, capacity, identity mismatch and stale tracking).

## ChatGPT / new-engineer handover context

This repository is deliberately defensive because dispatch has real operational
consequences. Preserve approval-first intake, audited fallbacks, idempotent
imports, provider-specific evidence labels, SQL runtime resilience and the
Lists governance boundary. The TV board, operations wallboard and live-runs
views must consume the same evidence-derived progress contract: planned times
are schedule context, not live ETA; a final linked geofence departure completes
a run; missing or stale evidence must remain visible as an exception.

What is **not** verified by repository contents: live List data and permissions,
Power Automate deployment/run history, Sage HR population, RoadTech/Falcon and
TachoMaster credentials/data quality, Fleetio tenancy, current customer-specific
commercial instructions, physical 26-pallet fleet limits, production secret
values, and whether every named sender/customer is currently mapped. Confirm
these with operations before changing a rule or relying on it in a release.
