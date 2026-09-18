# SLH TMS V2 architecture and rebuild rules

## Why V2 exists

The current application contains proven business knowledge, but several concerns have become coupled through repeated revisions. V2 preserves known-good behaviour while removing duplicated code paths, overloaded staging storage and integration-specific domain logic.

## Deployment approach

V1 remains live. V2 is built in this repository under `v2/` and deployed to separate Azure Container Apps and a separate V2 database until cutover. This avoids copying V1 wholesale into a new repository while still giving V2 an independent build and deployment surface.

Recommended names:
- API app: `slh-tms-api-v2`
- Web app: `slh-tms-portal-v2`
- SQL database: `slh-tms-v2`
- API URL: separate Azure Container Apps FQDN during test
- Portal URL: separate Azure Container Apps FQDN during test

## Canonical data ownership

### Master Data owns
- Customer
- Site
- SiteAlias / external site identities
- Market
- Driver
- Vehicle
- Trailer
- external identities linking these records to TachoMaster, Sage HR, RoadTech/Roadrunner or source systems

No intake parser or integration may insert a duplicate core entity as a side effect.

### Intake owns
- immutable source evidence metadata
- attachment/document metadata
- extraction result
- confidence and validation issues
- resolved canonical master IDs
- review state

### Orders owns
- canonical transport order
- source revision lineage
- references / PO numbers
- collection and delivery master IDs
- dates/times
- pallet/case/crate/tray quantities
- temperature / trailer requirements
- customer notes and planner notes

### Planning owns
- run
- stop
- order allocation
- driver/vehicle/trailer assignment
- operational planning notes
- night-out / trailer-swap state

### Live Operations owns
- telemetry observations
- geofence events
- ETA snapshots
- exceptions and status history

## One-writer rule

Each concern has exactly one canonical writer. Integrations call application services; they do not write domain tables directly.

Examples:
- TachoMaster adapter -> DriverMasterSync -> Master Data
- Sage HR adapter -> WorkforceEnrichment -> Master Data
- Outlook adapter -> IntakePipeline -> Intake
- RoadTech adapter -> TrackingIngest -> Live Operations
- Roadrunner export -> ExportProjection reads Orders + Planning + Master Data

## Intake pipeline

`Evidence -> Extract -> Classify -> Resolve master IDs -> Validate -> Review/Approve -> Promote order`

Customer-specific logic is allowed only in extraction adapters. Persistence, matching, deduplication, review and promotion remain generic.

Resolution order:
1. explicit stable external ID / alias
2. exact canonical code
3. normalized address + customer relationship
4. approved alias
5. unresolved review item

Fuzzy matching may suggest a candidate but must not silently create or merge master records.

## References and updates

PO/reference values are first-class and never discarded. A stable source key identifies a movement. Subsequent emails or files create revisions of that movement. Approved amendments update the order revision while preserving history; exports keep orders separate even when PO or destination alignment is used by Roadrunner for charge/deduplication rules.

## V1 capability classification

### KEEP behaviour, reimplement behind V2 boundaries
- Entra authentication pattern
- Azure Container Apps deployment pattern
- master-data concepts and operational fields
- TachoMaster identity reconciliation rules that have proved correct
- site identity/alias knowledge
- tracking/geofence concepts
- ETA concepts
- planning/run concepts
- Roadrunner CSV mapping knowledge
- customer order-format knowledge
- auditability and source evidence

### REBUILD implementation
- master persistence and master-detail storage
- order intake/staging/review
- TachoMaster scheduling/orchestration
- planner generations into one planner
- integration/background scheduling
- master duplicate handling
- order amendment/deduplication
- frontend API layer and data loading

### DROP from V2
- patch middleware whose purpose is schema drift recovery
- duplicate background writers
- legacy planner variants
- generic `StagedImports` as storage for unrelated operational concerns
- hidden fallback persistence
- production-repair endpoints in normal application surface
- parser-specific master creation
- obsolete feature flags and runtime guards

## Database strategy

Use one Azure SQL database for V2 initially, but separate schemas and EF Core DbContexts by bounded area. This keeps operations simple without recreating the V1 all-in-one DbContext.

Initial schemas:
- `master`
- `intake`
- `ops`
- `live`
- `integration`

V2 migrations are created only from V2 projects and never target the V1 database.

## Acceptance gates

A feature is not considered migrated because the UI exists. It must pass:
- build and static checks
- unit tests for matching/rules
- integration tests against SQL
- representative historic fixture tests
- observable health/telemetry
- no second writer for the same concern
- side-by-side comparison with V1 where applicable
- explicit production smoke test before traffic/cutover

## Initial delivery sequence

1. Foundation: V2 app shell, auth, health, telemetry, isolated config.
2. Master Data: canonical entities, imports, aliases, duplicate suggestions, audit.
3. Intake: immutable evidence, extraction adapters, master resolution, review.
4. Orders: promotion, revisions, amendment rules, PO/reference integrity.
5. Planning: one planning board/run model.
6. Drivers/vehicles: TachoMaster canonical sync + Sage HR enrichment.
7. Tracking/ETA: RoadTech observations, geofences, live run projection.
8. Roadrunner export: deterministic CSV from canonical run/order/master data.
9. Operational dashboard/TV once core read models are stable.
10. Cutover, rollback window, V1 retirement.

## Manual connections required later

Do not put credentials in GitHub.
- Azure: create V2 Container Apps / database / managed identities or grant deployment identity permission.
- Entra ID: register/authorise V2 API and portal redirect URLs if current app registration cannot safely host both.
- Key Vault: add V2 SQL and integration secrets; grant V2 managed identities read access.
- Outlook: authorise the mailbox/Graph connection used for intake.
- TachoMaster, RoadTech/Roadrunner and Sage HR: provide/authorise existing credentials in V2 secret configuration.
- DNS/custom domain: only after V2 acceptance; test on Azure FQDN first.

