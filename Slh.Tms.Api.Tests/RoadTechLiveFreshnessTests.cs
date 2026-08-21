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

        await store.PersistAsync([record], CancellationToken.None, markAsLiveReceipt: true);

        var live = await db.VehicleLiveStatuses.SingleAsync();
        Assert.True(live.LastReceivedAtUtc > oldReceivedAt);
        Assert.Equal(eventTime, live.LastEventTimeUtc);
    }

    [Fact]
    public async Task Historical_recovery_never_refreshes_or_creates_live_status()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new TmsDbContext(options);

        var recovered = new DotTelemetryRecord(
            "historical-event",
            "AB12CDE",
            DateTimeOffset.UtcNow.AddHours(-3),
            51.0m,
            -1.2m,
            40,
            true,
            true,
            "Received",
            "{}");

        var store = new DotTrackingTelemetryStore(db, NullLogger<DotTrackingTelemetryStore>.Instance);
        await store.PersistAsync([recovered], CancellationToken.None, markAsLiveReceipt: false);

        Assert.Empty(await db.VehicleLiveStatuses.ToListAsync());
        Assert.Single(await db.VehicleTrackingEvents.ToListAsync());
    }

    [Fact]
    public async Task Current_row_without_gps_does_not_refresh_live_status()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new TmsDbContext(options);

        var originalReceived = DateTimeOffset.UtcNow.AddHours(-2);
        db.VehicleLiveStatuses.Add(new VehicleLiveStatus
        {
            VehicleIdentifier = "AB12CDE",
            LastEventTimeUtc = DateTimeOffset.UtcNow.AddHours(-2),
            LastReceivedAtUtc = originalReceived,
            Latitude = 50.8m,
            Longitude = -1.1m,
            LastKnownStatus = "Received"
        });
        await db.SaveChangesAsync();

        var store = new DotTrackingTelemetryStore(db, NullLogger<DotTrackingTelemetryStore>.Instance);
        var noGps = new DotTelemetryRecord(
            "current-no-gps",
            "AB12CDE",
            DateTimeOffset.UtcNow,
            null,
            null,
            null,
            null,
            null,
            "GPS coordinates unavailable",
            "{}");

        await store.PersistAsync([noGps], CancellationToken.None, markAsLiveReceipt: true);

        var live = await db.VehicleLiveStatuses.SingleAsync();
        Assert.Equal(originalReceived, live.LastReceivedAtUtc);
    }
}
