using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class RunCompletionPersistenceGuardTests
{
    [Fact]
    public async Task Direct_completion_without_RunCompleted_evidence_is_rejected()
    {
        await using var db = CreateDb();
        var load = NewLoad();
        db.Loads.Add(load);
        await db.SaveChangesAsync();

        load.Status = LoadStatus.Completed;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Contains("evidence-controlled", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Completion_with_RunCompleted_evidence_is_allowed()
    {
        await using var db = CreateDb();
        var load = NewLoad();
        db.Loads.Add(load);
        await db.SaveChangesAsync();

        load.Status = LoadStatus.Completed;
        db.DriverStatusLogs.Add(new DriverStatusLog
        {
            LoadId = load.Id,
            Status = "RunCompleted",
            Notes = "All planned stops have confirmed geofence departures.",
            CapturedBy = "RoadTech Geofence Engine"
        });

        await db.SaveChangesAsync();

        Assert.Equal(LoadStatus.Completed, (await db.Loads.SingleAsync()).Status);
        Assert.True(await db.DriverStatusLogs.AnyAsync(log => log.LoadId == load.Id && log.Status == "RunCompleted"));
    }

    [Fact]
    public async Task Persisted_RunCompleted_evidence_allows_idempotent_completion_transition()
    {
        await using var db = CreateDb();
        var load = NewLoad();
        db.Loads.Add(load);
        db.DriverStatusLogs.Add(new DriverStatusLog
        {
            LoadId = load.Id,
            Status = "RunCompleted",
            CapturedBy = "RoadTech Geofence Engine"
        });
        await db.SaveChangesAsync();

        load.Status = LoadStatus.Completed;
        await db.SaveChangesAsync();

        Assert.Equal(LoadStatus.Completed, (await db.Loads.SingleAsync()).Status);
    }

    private static TmsDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new TmsDbContext(options);
    }

    private static Load NewLoad() => new()
    {
        Reference = $"RUN-{Guid.NewGuid():N}",
        PlanningDate = new DateOnly(2026, 8, 23),
        Status = LoadStatus.InProgress,
        Stops = [new LoadStop { Sequence = 1, Name = "Delivery" }]
    };
}
