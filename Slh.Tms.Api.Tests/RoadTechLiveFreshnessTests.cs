using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class RoadTechLiveFreshnessTests
{
    [Fact]
    public void RoadTech_data_mask_always_includes_gps()
    {
        var options = new DotTrackingOptions { DataMask = 0 };
        Assert.Equal(0x01, options.DataMask);

        options.DataMask = 0x04;
        Assert.Equal(0x05, options.DataMask);
    }

    [Fact]
    public async Task Current_observation_refreshes_receipt_time_even_when_provider_event_is_unchanged()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new TmsDbContext(options);

        var eventTime = DateTimeOffset.UtcNow.AddHours(-2);
        var oldReceivedAt = DateTimeOffset.UtcNow.AddHours(-1);
        db.VehicleLiveStatuses.Add(new VehicleLiveStatus
        {
            VehicleIdentifier = "AB12CDE",
            LastEventTimeUtc = eventTime,
            LastReceivedAtUtc = oldReceivedAt,
            Latitude = 50.8m,
            Longitude = -1.1m,
            SpeedKph = 0,
            LastKnownStatus = "Received"
        });
        await db.SaveChangesAsync();

        var store = new DotTrackingTelemetryStore(db, NullLogger<DotTrackingTelemetryStore>.Instance);
        var record = new DotTelemetryRecord(
            "current-same-event",
            "AB12CDE",
            eventTime,
            50.8m,
            -1.1m,
            0,
            true,
            false,
            "Received",
            "{}");

        await store.PersistAsync([record], CancellationToken.None, updateLiveStatus: true);

        var live = await db.VehicleLiveStatuses.SingleAsync();
        Assert.True(live.LastReceivedAtUtc > oldReceivedAt);
        Assert.Equal(eventTime, live.LastEventTimeUtc);
    }

    [Fact]
    public async Task Historical_recovery_never_refreshes_live_status()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new TmsDbContext(options);

        var originalEvent = DateTimeOffset.UtcNow.AddHours(-3);
        var originalReceived = DateTimeOffset.UtcNow.AddHours(-2);
        db.VehicleLiveStatuses.Add(new VehicleLiveStatus
        {
            VehicleIdentifier = "AB12CDE",
            LastEventTimeUtc = originalEvent,
            LastReceivedAtUtc = originalReceived,
            Latitude = 50.8m,
            Longitude = -1.1m,
            LastKnownStatus = "Received"
        });
        await db.SaveChangesAsync();

        var store = new DotTrackingTelemetryStore(db, NullLogger<DotTrackingTelemetryStore>.Instance);
        var recovered = new DotTelemetryRecord(
            "historical-newer-event",
            "AB12CDE",
            originalEvent.AddMinutes(30),
            51.0m,
            -1.2m,
            40,
            true,
            true,
            "Received",
            "{}");

        await store.PersistAsync([recovered], CancellationToken.None, updateLiveStatus: false);

        var live = await db.VehicleLiveStatuses.SingleAsync();
        Assert.Equal(originalReceived, live.LastReceivedAtUtc);
        Assert.Equal(originalEvent, live.LastEventTimeUtc);
        Assert.Equal(50.8m, live.Latitude);
        Assert.Equal(-1.1m, live.Longitude);
        Assert.Single(await db.VehicleTrackingEvents.ToListAsync());
    }
}
