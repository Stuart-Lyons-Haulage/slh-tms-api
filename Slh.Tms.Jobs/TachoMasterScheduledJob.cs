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
    ILogger<TachoMasterScheduledJob> logger)
{
    public async Task<JobExecutionResult> RunAsync(CancellationToken ct)
    {
        if (await CanonicalDueAsync(ct))
        {
            logger.LogInformation("TachoMasterCanonicalDue LocalSchedule=04:30 Europe/London");
            var result = await canonical.RunAsync("system:aca-job:tachomaster-canonical", ct);
            return new JobExecutionResult(result.Success, result.Message, result.Canonical.Created + result.Canonical.Updated + result.Canonical.DuplicateRecordsRetired);
        }

        var observed = await observedDrivers.SyncAsync("system:aca-job:tachomaster-live-identity", ct);
        var sync = await integration.SyncTachoMasterAsync("system:aca-job:tachomaster", ct);
        var message = observed.Created > 0
            ? $"{sync.Message} Live Tacho evidence created {observed.Created} previously unseen driver record(s) in the SQL Driver Master; the change was recorded in the master-data audit outbox."
            : sync.Message;
        return new JobExecutionResult(sync.Success, message, sync.Changed + observed.Created);
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
