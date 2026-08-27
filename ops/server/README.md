# SLH TMS Local Server Runtime

The SLH local server should run the same API code as GitHub. Do not fork business logic for the server. Change configuration only.

## Runtime Shape

| Component | Server responsibility |
| --- | --- |
| `Slh.Tms.Api` | Secured API, database access, migrations/startup repairs, health checks |
| Background workers | RoadTech/DOT tracking, TachoMaster/Sage HR/Fleetio sync, geofence processing, ETA evidence |
| SQL database | Central authoritative TMS data store |
| Desktop app | Native Windows shell that points at this API |
| Azure | Off-site backup/disaster recovery and any cloud services still approved for production |

Do not create per-PC master data. Desktop clients must point at the central API/database.

## Configuration

Use environment variables, Windows service settings, or a secure local secret store for secrets. Keep these out of Git:

```text
ConnectionStrings__TmsDb
Tracking__Dot__ApiKey
Tracking__Dot__Username
Tracking__Dot__Password
Integrations__TachoMaster__ApiKey
Integrations__SageHr__ApiKey
Integrations__Fleetio__ApiKey
Integrations__MicrosoftGraphEmail__ClientSecret
```

Non-secret server paths can follow:

```text
TmsRuntime__EnvironmentName=ProductionServer
TmsRuntime__DataPath=D:\SLH-TMS\Data
TmsRuntime__ExportPath=D:\SLH-TMS\Exports
TmsRuntime__BackupPath=D:\SLH-TMS\Backups
TmsRuntime__LoggingPath=D:\SLH-TMS\Logs
Workers__DotTrackingIngestion=true
Workers__IntegrationBackgroundSync=true
```

`appsettings.ProductionServer.example.json` documents the expected shape without embedding real credentials.

## Verification

After deployment:

1. Start the API service.
2. Open `GET /api/v1/health` to confirm the revision.
3. Open `GET /api/v1/health/ready` to confirm SQL readiness.
4. Sign in to the TMS and open `GET /api/v1/runtime/status`.
5. Confirm paths are writable and worker switches match the intended environment.
6. Open `GET /api/v1/integrations/status` and check RoadTech, TachoMaster, Sage HR, Fleetio, mailbox intake and SMS readiness.

Manual ETA emails should use delegated Microsoft Graph send as the signed-in user when authorised, automatically CC the Info mailbox according to customer rules, save to Sent Items, and audit sender, recipients, order/run, ETA, timestamp and Graph message ID. Fully automated sends must use the approved shared/service identity, not user impersonation.
