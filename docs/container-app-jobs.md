# Scheduled integration jobs

The API no longer owns the TachoMaster, Fleetio or Sage HR timers. `Slh.Tms.Jobs` is a dedicated console host used by four independently scheduled Azure Container Apps Jobs:

| Job kind | Example cron (UTC) | Purpose |
| --- | --- | --- |
| `tachomaster` | `*/5 * * * *` | Five-minute Tacho identity refresh. At/after 04:30 Europe/London it runs the full canonical Driver Master once per local day. |
| `fleetio` | `5 * * * *` | Hourly canonical fleet/trailer sync. |
| `sagehr` | `30 5 * * *` | Daily Sage HR driver sync. Container Apps cron is UTC; adjust if a fixed UK wall-clock time is required across DST. |
| `eta` | `*/5 * * * *` | Recalculate delivery ETAs through the existing live ETA engine and persist precision snapshots. |

Every execution first acquires a SQL row in `dbo.DistributedLease`. The row contains `LeaseId`, `AcquiredAt`, `ExpiresAt` and `InstanceId`. Acquisition uses a serializable transaction and `UPDLOCK/HOLDLOCK`; a crashed execution becomes eligible after `ExpiresAt`. Normal completion and failure release only the row owned by that instance.

There are two lease layers by design. The outer `job:*` lease suppresses duplicate Container Apps Job executions. Integration service methods also use `integration:*` leases so a manual API sync cannot overlap the scheduled job after the process-local `SemaphoreSlim` gates are removed.

The Tacho job keeps the historical 04:30 Europe/London canonical pass without a second in-process timer: the five-minute job checks the durable orchestration ledger and runs the canonical pass only when one successful pass has not yet completed for the current London date.

Build the jobs image with:

```bash
docker build -f Slh.Tms.Jobs/Dockerfile -t slhtmsacrprod.azurecr.io/slh-tms-jobs:<sha> .
```

Deploy the four scheduled resources with `infra/container-app-jobs.bicep`. Production deployment should use an immutable image tag and ensure `Database/040_Distributed_Integration_Lease.sql` has been applied before enabling schedules.
