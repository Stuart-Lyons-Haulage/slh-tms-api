# SLH TMS Local Server Edition

This branch is a completely separate proof-of-concept runtime for the existing SLH TMS. It does not deploy to Azure and it does not use the production Azure SQL database.

## Safety defaults

- API binds to 127.0.0.1:5099 only.
- Portal binds to 127.0.0.1:5173 only.
- Local authentication is loopback-only and impersonates an SLH admin solely on the local machine.
- External background integrations are disabled by default.
- TachoMaster, RoadTech/DOT, Sage HR, Fleetio, OpenAI and messaging integrations are disabled by local configuration.
- The portal displays a permanent LOCAL TEST SYSTEM banner.
- Existing production deployment workflows are untouched.

## Workspace layout

C:\SLH-TMS-LOCAL\source\slh-tms-api
C:\SLH-TMS-LOCAL\source\slh-tms-web
C:\SLH-TMS-LOCAL\data
C:\SLH-TMS-LOCAL\customer-files
C:\SLH-TMS-LOCAL\backups
C:\SLH-TMS-LOCAL\logs
C:\SLH-TMS-LOCAL\run

## Database choice for the first test

The current TMS uses SQL Server-specific migrations, rowversion concurrency, repair SQL and schema guards. For the first local proof we therefore use SQL Server Express LocalDB, but its MDF/LDF files are created inside C:\SLH-TMS-LOCAL\data. This keeps the test isolated from Azure SQL and avoids rewriting the persistence layer before functional parity is proven.

Once the local application is proven against real order traffic, the persistence layer can be assessed separately for SQLite or another embedded engine.

## Local evidence archive

Every email passed to local order intake is also written to a human-readable file archive under customer-files\Customers\<customer>\YYYY\MM\DD\<message-hash>.

The archive includes source-email.json, body.txt/body.html, parsed order JSON files, and copies of non-inline XLS/XLSX/XLSM/CSV/PDF attachments when content bytes are available.

## Prerequisites

- Windows 10 or 11
- Git
- .NET 8 SDK
- Node.js and npm
- SQL Server Express LocalDB (sqllocaldb command)

## Prepare

Run local-server\bootstrap-local.ps1 from a checked-out copy of this branch. It creates C:\SLH-TMS-LOCAL, clones both local-server-edition branches, restores packages and builds both applications.

## Start

Run C:\SLH-TMS-LOCAL\source\slh-tms-api\local-server\start-local.ps1.

Portal: http://127.0.0.1:5173
API: http://127.0.0.1:5099

## Stop

Run C:\SLH-TMS-LOCAL\source\slh-tms-api\local-server\stop-local.ps1.

## Backup

Run C:\SLH-TMS-LOCAL\source\slh-tms-api\local-server\backup-local.ps1. The script stops the local processes and LocalDB before copying the contained data into a timestamped ZIP so the database copy is consistent.

## Intended Work-mode validation

1. Bootstrap and start the local stack.
2. Confirm health and portal startup.
3. Import a controlled snapshot of master data.
4. Replay historic emails and attachments into local intake.
5. Compare Pending Review and Planning against known real jobs.
6. Fix parser/runtime issues only on the local-server-edition branches.
7. Enable external read-only integrations one at a time after the local core is stable.
8. Keep outbound customer communications disabled until explicitly approved.