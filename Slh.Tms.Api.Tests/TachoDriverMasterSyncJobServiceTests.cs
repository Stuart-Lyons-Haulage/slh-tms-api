using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class TachoDriverMasterSyncJobServiceTests
{
    [Fact]
    public async Task Startup_recovery_releases_recent_legacy_running_job_without_a_worker_lease()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new TmsDbContext(options);

        var now = DateTimeOffset.UtcNow;
        var stranded = new StagedImport
        {
            EntityType = "tachodrivermastersyncjob",
            IdempotencyKey = $"tachodrivermastersyncjob:{Guid.NewGuid():N}",
            PayloadJson = "{\"actor\":\"system:previous-revision\",\"requestedAtUtc\":\"2026-08-28T13:18:00Z\",\"startedAtUtc\":\"2026-08-28T13:18:01Z\"}",
            Source = "System canonical Driver Master queue",
            Status = StagingStatus.Approved,
            ReceivedAtUtc = now.AddMinutes(-3),
            ReviewedAtUtc = now.AddMinutes(-3),
            ReviewedBy = "system:previous-revision",
            ReviewNote = "Canonical TachoMaster Driver Master sync is running."
        };
        db.StagedImports.Add(stranded);
        await db.SaveChangesAsync();

        var service = new TachoDriverMasterSyncJobService(db);
        var recover = typeof(TachoDriverMasterSyncJobService).GetMethod(
            "RecoverInterruptedAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(recover);
        var task = Assert.IsAssignableFrom<Task>(recover!.Invoke(service, [CancellationToken.None]));
        await task;

        var recovered = await db.StagedImports.SingleAsync(row => row.Id == stranded.Id);
        Assert.Equal(StagingStatus.Failed, recovered.Status);

        var replacement = await service.EnqueueAsync("system:new-revision", CancellationToken.None);
        Assert.Equal("queued", replacement.Status);
        Assert.NotEqual(stranded.Id, replacement.JobId);
    }
}
