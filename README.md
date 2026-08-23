# Stuart Lyons Haulage TMS API

Production .NET 8 API for the Stuart Lyons Haulage transport management system. The API is hosted in Azure Container Apps, uses Azure SQL for operational and audited fallback storage, Microsoft Entra for authenticated access, and Key Vault/managed identity for integration secrets.

## Production

- Portal: `https://slh-tms-portal-prod.gentlepond-08dba66b.uksouth.azurecontainerapps.io/`
- API: `https://slh-tms-api-prod.gentlepond-08dba66b.uksouth.azurecontainerapps.io`
- Resource group: `slh-tms-prod-rg`
- Region: UK South

The portal proxies API requests through `/tms-api`. Deployment is performed by GitHub Actions using Azure OIDC; do not use publish profiles or long-lived deployment secrets.

## Operational architecture

The current production path is deliberately resilient:

1. Planner and order data is validated by the API.
2. Primary SQL tables are used when the operational schema is available.
3. Audited register/staging storage is used as a fallback so planner pages do not fail because one dedicated table is unavailable.
4. RoadTech/Falcon supplies live vehicle telemetry and geofence evidence.
5. TachoMaster supplies driver duty and legal-hours evidence.
6. Azure Maps is used for live route/ETA calculation where fresh execution evidence is available.
7. The TV wallboard consumes the same live tracking, geofence progression and ETA evidence as the TMS.

## Planner imports

All planner JSON schemas used by the live Planner Import page must post to:

`POST /api/v1/planning/import-plan`

This includes `slh-planner-plan-v3-source-lines`. Do not add browser rewrites that divert source-line payloads to the older direct-SQL import path. The resilient import endpoint supports idempotent re-imports, audited fallback storage, capacity warnings and allocation reconciliation.

The CI suite contains regression coverage for source-line JSON through the live resilient endpoint so this routing cannot silently regress to the previous 500-prone path.

### Clean re-import of a planning day

Use the controlled planning-day reset rather than deleting database history:

- Preview: `GET /api/v1/planning-day/{yyyy-MM-dd}/reset-preview`
- Reset: `DELETE /api/v1/planning-day/{yyyy-MM-dd}?confirm=RESET-{yyyy-MM-dd}`

The reset is approval-protected. It cancels active work for that planning day, removes active stops and archives matching staged/register rows while retaining source evidence and releasing import keys for a clean re-import.

## Info mailbox / email attachments

Mailbox intake is productionised on `main` and remains approval-first. Email-derived orders are staged as `PendingReview` with their source evidence and must be reviewed in the TMS before promotion; mailbox automation never creates live work silently.

The current intake path preserves Outlook message and attachment evidence, uses PO-first duplicate/amendment classification, records append-only SQL-backed staging history, and keeps a durable source link from promoted orders back to the originating staging evidence. The production Power Automate runbook uses Outlook plus the existing TMS API/custom connector and must not write directly to live Orders.

Power Automate should preserve the source message ID, subject, sender and attachment identity for idempotency/audit. Failed sends or intake attempts remain retryable without losing the original evidence.

## Production health checks

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

Useful endpoints include:

- `GET /health`
- `GET /health/ready`
- `GET /api/v1/diagnostics/data-readiness`
- `GET /api/v1/health/tracking`
- `GET /api/v1/health/tachomaster`
- `GET /api/v1/health/geofences`

## Security

- Never commit passwords, SQL connection strings, API keys, customer attachments or operational exports.
- Integration credentials belong in Azure Key Vault or managed configuration.
- Deployment uses GitHub OIDC.
- Entra scopes/roles protect read, write, approval and administrative operations.
- Intake and reset operations retain auditable evidence rather than deleting history.

## Development

```bash
dotnet restore
dotnet build Slh.Tms.Api.csproj -c Release
dotnet test Slh.Tms.Api.Tests/Slh.Tms.Api.Tests.csproj -c Release
```

Before merging production changes, require the normal build/test and CodeQL checks to pass. After deployment, use the full production health workflow for the final runtime check.
