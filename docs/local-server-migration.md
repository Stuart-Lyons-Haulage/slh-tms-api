# SLH TMS local server / hybrid migration

## Goal

Run the operational SLH TMS close to office users while keeping GitHub as the source of truth and preserving Azure as the current production fallback during migration.

This branch does **not** switch production. It prepares a parallel local-server deployment path.

## Target architecture

```text
GitHub (slh-tms-api + slh-tms-web)
        |
        | CI / tested releases
        v
SLH server
  - reverse proxy / web container
  - API container
  - local SQL Server instance
  - integration jobs
  - backup jobs
        |
        +---- company LAN ---- planner / dispatch / warehouse / wallboards
        |
        +---- outbound HTTPS ---- Entra, Azure Maps, DOT/TachoMaster, Fleetio, Sage HR, Microsoft 365

Azure production remains available during migration and rollback.
```

## Why this can reuse the existing application

The API and web projects are already containerised. The API accepts its SQL connection through `ConnectionStrings__TmsDb`, so the same API can point to a local SQL Server without a code rewrite. The web application is already built with a relative API base (`/tms-api`) in production; only the Nginx upstream needs a local-server variant.

## Proposed SLH server layout

Use these paths unless the actual server layout requires different drives:

```text
D:\SLH-TMS\
  app\
    api\
    web\
  config\
    api.env
    web.env
  data\
    sql\
  backups\
    sql\
    app-config\
  logs\
    api\
    web\
    jobs\
  releases\
```

If the server is Linux, use `/srv/slh-tms` with the same subdirectories.

## Database

Recommended local target: SQL Server 2022 Standard when the existing server licensing/capacity permits it. SQL Server Express is suitable only for a small test because of its database-size and resource limits.

The application already uses EF Core SQL Server and `ConnectionStrings__TmsDb`; no provider conversion should be required.

Example local connection environment value:

```text
ConnectionStrings__TmsDb=Server=SLH-SQL;Database=slh-tms;User Id=slh_tms_app;Password=<secret>;Encrypt=True;TrustServerCertificate=True
```

Do not commit the real password or integration credentials.

## Data migration sequence

1. Take a verified Azure SQL backup/export.
2. Restore/copy into a new local `slh-tms` database while Azure remains authoritative.
3. Start local API against the copied database with background write-producing integrations disabled.
4. Run schema/row-count/reconciliation checks.
5. Start the local web app and test via an internal-only hostname.
6. Validate Planner, Driver Dispatch, Pallet Control, Warehouse, Master Data, wallboards, order review and reporting.
7. Validate Entra authentication and external integrations from the local server.
8. Perform a final short maintenance window, take a fresh data copy, then switch the local database to authoritative.
9. Keep Azure production available for rollback until the agreed proving period is complete.

## Internal DNS / URL

Preferred user-facing address:

```text
https://tms.lyonshaulage.local
```

or a company DNS name approved by IT. Avoid asking users to browse to an IP address.

All office PCs should use this single address. The web app can then be installed as a PWA/shortcut so users do not need separate application updates.

## Authentication

Keep Microsoft Entra authentication. The local hostname will need to be added to the web app registration redirect URI list and, where relevant, CORS/origin configuration.

Do not remove the existing production Azure redirect URIs during migration.

## External integrations

Keep outbound Internet access from the SLH server for:

- Microsoft Entra
- Azure Maps
- DOT / TachoMaster
- Fleetio
- Sage HR
- Microsoft 365 / Power Automate callbacks or API calls used by the TMS

Integrations should write into the same local operational database after cutover. During parallel testing, write-producing scheduled jobs must run on only one environment to avoid duplicate imports/events.

## Backups

Minimum target:

- SQL transaction/log or frequent database backups during the working day.
- Nightly full local SQL backup to a separate disk/NAS.
- Encrypted off-site copy to Azure or another approved cloud target.
- Retain deployment/configuration backups separately from the live server.
- Test restores on a schedule; a backup is not considered proven until it restores successfully.

Suggested retention starting point:

- hourly/differential: 48 hours
- daily: 30 days
- weekly: 12 weeks
- monthly: 12 months

Adjust to SLH policy and storage capacity.

## GitHub deployment model

Keep `main` as source of truth.

Target release flow after proving:

```text
feature branch -> PR -> CI -> main -> build immutable API/web images -> deploy to SLH staging -> health checks -> promote to SLH production
```

The existing Azure production workflows should remain available until local production has a tested rollback and disaster-recovery procedure.

The SLH server should use a dedicated GitHub self-hosted runner or pull signed/versioned release images. Do not put a personal GitHub token on staff PCs.

## Client deployment

Do not install a backend/database on each PC. Users should install/open the same centrally hosted web app.

Preferred client model:

- internal HTTPS URL
- PWA / desktop shortcut named `SLH TMS`
- Entra sign-in
- role-based navigation
- automatic frontend update when a new server release is deployed

This means a future repository change is deployed once to the SLH server and is then seen by all users.

## Local server environment variables

At minimum the API deployment will require:

```text
ASPNETCORE_ENVIRONMENT=Production
ConnectionStrings__TmsDb=<local SQL connection>
Entra__TenantId=<tenant id>
Entra__Audience=<API audience>
Entra__AllowedDomains__0=lyonshaulage.com
Entra__AllowedDomains__1=stuartlyonshaulage.co.uk
Cors__AllowedOrigins__0=https://<internal-tms-hostname>
```

Plus the existing DOT/TachoMaster, Fleetio, Sage HR, Azure Maps and wallboard secrets currently supplied to Azure production.

## Cutover safety rules

- Never point both Azure production and local production write-jobs at the same order sources during migration unless the job is proven idempotent.
- Never run two independent authoritative databases after cutover.
- Keep the current Azure endpoint unchanged until local acceptance testing passes.
- Tag the final Azure production commit and record the final Azure database backup before switching.
- Make DNS/reverse-proxy change reversible.

## Work-mode tasks still required

These depend on access to the actual SLH server/network/Azure configuration and should be completed in Work mode:

1. Inventory server OS, CPU, RAM, disks, RAID/storage, SQL licensing and backup target.
2. Confirm LAN/DNS/domain and choose the internal hostname.
3. Confirm whether containers will run directly on the server or on a dedicated Linux/Windows VM.
4. Install/configure container runtime and SQL Server as appropriate.
5. Create service accounts and least-privilege SQL login.
6. Export/restore a non-authoritative copy of Azure SQL for testing.
7. Add Entra redirect URI and CORS origin for the internal URL.
8. Configure TLS certificate and reverse proxy.
9. Configure GitHub self-hosted deployment runner or release-pull mechanism.
10. Configure backups and prove a restore.
11. Run end-to-end tests against the local stack.
12. Execute controlled cutover only after acceptance.
