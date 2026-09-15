using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Jobs;

public sealed class TachoMasterScheduledJob(
    TmsDbContext db,
    IntegrationSyncCoordinator integration,
    TachoObservedDriverSyncService observedDrivers,
    TachoCanonicalDriverMasterOrchestrator canonical,
    DistributedLeaseManager leases,
    ILogger<TachoMasterScheduledJob> logger)
{
    public async Task<JobExecutionResult> RunAsync(CancellationToken ct)
    {
        var canonicalDue = await CanonicalDueAsync(ct);

        // Routine five-minute TachoMaster enrichment must never be blocked by the daily canonical
        // cleanse remaining due or failing its stricter identity gate. Run the live/regular sync first
        // under the shared Tacho writer lease, release it, then attempt the canonical cleanse separately.
        JobExecutionResult routine;
        {
            await using var lease = await leases.TryAcquireAsync(IntegrationLeaseNames.TachoMaster, TimeSpan.FromMinutes(2), ct);
            if (lease is null)
                return new JobExecutionResult(false, "TachoMaster scheduled sync skipped because another distributed writer currently holds the integration lease.");

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.LostToken);
            var observed = await observedDrivers.SyncAsync("system:aca-job:tachomaster-live-identity", linked.Token);
            var sync = await integration.SyncTachoMasterCoreAsync("system:aca-job:tachomaster", linked.Token);
            var message = observed.Created > 0
                ? $"{sync.Message} Live Tacho evidence created {observed.Created} previously unseen driver record(s) in the SQL Driver Master; the change was recorded in the master-data audit outbox."
                : sync.Message;
            routine = new JobExecutionResult(sync.Success, message, sync.Changed + observed.Created);
        }

        if (!routine.Success || !canonicalDue)
            return routine;

        logger.LogInformation("TachoMasterCanonicalDue LocalSchedule=04:30 Europe/London RoutineSyncCompleted=true");
        var canonicalResult = await canonical.RunAsync("system:aca-job:tachomaster-canonical", ct);
        var canonicalChanged = canonicalResult.Canonical.Created + canonicalResult.Canonical.Updated + canonicalResult.Canonical.DuplicateRecordsRetired;

        if (!canonicalResult.Success)
        {
            // The strict daily cleanse remains independently visible through Driver Master health and
            // orchestration diagnostics, but it must not suppress successfully refreshed Tacho data.
            logger.LogWarning(
                "TachoMasterCanonicalFailedAfterRoutineSync Message={Message}",
                canonicalResult.Message);
            return new JobExecutionResult(
                true,
                $"{routine.Message} Daily canonical Driver Master cleanse remains due and failed safely: {canonicalResult.Message}",
                routine.Changed);
        }

        return new JobExecutionResult(
            true,
            $"{routine.Message} {canonicalResult.Message}",
            routine.Changed + canonicalChanged);
    }

    private async Task<bool> CanonicalDueAsync(CancellationToken ct)
    {
        var zone = LondonTimeZone();
        var localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone);
        if (localNow.TimeOfDay < new TimeSpan(4, 30, 0)) return false;

        var localDate = DateOnly.FromDateTime(localNow.DateTime);
        var startLocal = localDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var endLocal = localDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var startUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(startLocal, zone), TimeSpan.Zero);
        var endUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(endLocal, zone), TimeSpan.Zero);

        return !await db.StagedImports.AsNoTracking().AnyAsync(row =>
            row.EntityType == "tachodrivermasterorchestration" &&
            row.Status == StagingStatus.Promoted &&
            row.ReviewedAtUtc >= startUtc &&
            row.ReviewedAtUtc < endUtc, ct);
    }

    private static TimeZoneInfo LondonTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/London"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time"); }
    }
}
