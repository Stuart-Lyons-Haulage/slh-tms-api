using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class RoadTechHistoryRepairTests
{
    [Fact]
    public async Task Historical_replay_repairs_existing_event_time_and_position()
    {
        await using var db = CreateDb();
        db.VehicleTrackingEvents.Add(new VehicleTrackingEvent
        {
            ProviderName = "RoadTech Falcon",
            ProviderEventId = "event-1",
            VehicleIdentifier = "AB12CDE",
            EventTimeUtc = new DateTimeOffset(2026, 8, 28, 18, 1, 0, TimeSpan.Zero),
            Latitude = 51.0m,
            Longitude = -1.0m,
            RawPayload = "old",
            MatchStatus = "Received"
        });
        await db.SaveChangesAsync();

        var store = new DotTrackingTelemetryStore(db, NullLogger<DotTrackingTelemetryStore>.Instance);
        var corrected = new DotTelemetryRecord(
            "event-1",
            "AB12 CDE",
            new DateTimeOffset(2026, 8, 27, 12, 1, 0, TimeSpan.Zero),
            50.7581m,
            -0.7794m,
            22m,
            true,
            true,
            "Received",
            "corrected");

        await store.PersistAsync([corrected], CancellationToken.None, markAsLiveReceipt: false);

        var stored = await db.VehicleTrackingEvents.SingleAsync();
        Assert.Equal(new DateTimeOffset(2026, 8, 27, 12, 1, 0, TimeSpan.Zero), stored.EventTimeUtc);
        Assert.Equal(50.7581m, stored.Latitude);
        Assert.Equal(-0.7794m, stored.Longitude);
        Assert.Equal("corrected", stored.RawPayload);
    }

    [Fact]
    public async Task Historical_replay_does_not_refresh_live_status()
    {
        await using var db = CreateDb();
        db.VehicleLiveStatuses.Add(new VehicleLiveStatus
        {
            VehicleIdentifier = "AB12CDE",
            LastEventTimeUtc = new DateTimeOffset(2026, 8, 27, 12, 30, 0, TimeSpan.Zero),
            LastReceivedAtUtc = new DateTimeOffset(2026, 8, 27, 12, 30, 0, TimeSpan.Zero),
            Latitude = 51.0m,
            Longitude = -1.0m
        });
        await db.SaveChangesAsync();

        var store = new DotTrackingTelemetryStore(db, NullLogger<DotTrackingTelemetryStore>.Instance);
        var historical = new DotTelemetryRecord(
            "event-2",
            "AB12 CDE",
            new DateTimeOffset(2026, 8, 27, 8, 0, 0, TimeSpan.Zero),
            50.0m,
            -0.5m,
            10m,
            true,
            true,
            "Received",
            "historical");

        await store.PersistAsync([historical], CancellationToken.None, markAsLiveReceipt: false);

        var live = await db.VehicleLiveStatuses.SingleAsync();
        Assert.Equal(new DateTimeOffset(2026, 8, 27, 12, 30, 0, TimeSpan.Zero), live.LastEventTimeUtc);
        Assert.Equal(51.0m, live.Latitude);
        Assert.Equal(-1.0m, live.Longitude);
    }

    private static TmsDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase($"roadtech-history-repair-{Guid.NewGuid()}")
            .Options;
        return new TmsDbContext(options);
    }
}
