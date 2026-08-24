# Stuart Lyons Haulage TMS API

Production .NET 8 API for the Stuart Lyons Haulage transport management system. The API is the system of record for orders, staging, planning, live run progress, geofence evidence, compliance checks, integrations and audited operational recovery.

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
- Store master data for customers, sites, drivers, vehicles, trailers and planning preferences.
- Ingest RoadTech/Falcon live vehicle telemetry.
- Read TachoMaster driver, card, duty and legal-hours evidence.
- Process geofence arrival, dwell and departure evidence.
- Calculate live run progress, next stop, ETA and completion state.
- Reconcile walkround, tacho, movement and allocation evidence for compliance.
- Expose secured APIs to the portal, TV wallboard and reporting screens.

## Operational Architecture

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
