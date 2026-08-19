using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class HistoricGeofenceReplayTests
{
    [Fact]
    public async Task Historic_tracking_events_reconstruct_confirmed_completed_stop()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase($"historic-geofence-{Guid.NewGuid():N}")
            .Options;

        await using var db = new TmsDbContext(options);

        var fence = Assert.Single(EmbeddedGeofenceEngine.ApprovedFences
            .Where(x => string.Equals(x.Name.Trim(), "Paleo Ridge Havant", StringComparison.OrdinalIgnoreCase)));

        var vehicle = new Vehicle
        {
            Registration = "TEST123",
            FleetNumber = "TEST123",
            Active = true
        };
        db.Vehicles.Add(vehicle);

        // Use the polygon's mean point. Paleo Ridge Havant is a compact convex site
        // polygon, so this is safely inside the operational fence.
        var insideLongitude = (decimal)fence.Points.Average(x => x.Longitude);
        var insideLatitude = (decimal)fence.Points.Average(x => x.Latitude);
        var historicalDate = new DateOnly(2026, 8, 18);
        var entered = new DateTimeOffset(2026, 8, 18, 10, 0, 0, TimeSpan.Zero);

        db.VehicleTrackingEvents.AddRange(
            Tracking("historic-1", vehicle.Registration, entered, insideLatitude, insideLongitude),
            Tracking("historic-2", vehicle.Registration, entered.AddMinutes(11), insideLatitude, insideLongitude),
            Tracking("historic-3", vehicle.Registration, entered.AddMinutes(20), 0m, 0m));
        await db.SaveChangesAsync();

        var load = new Load
        {
            Reference = "HISTORIC-TEST",
            PlanningDate = historicalDate,
            VehicleId = vehicle.Id,
            Status = LoadStatus.Planned
        };
        var stop = new LoadStop
        {
            LoadId = load.Id,
            Sequence = 1,
            Name = "Paleo Ridge Havant",
            PlannedArrivalUtc = entered.AddMinutes(10)
        };
        load.Stops.Add(stop);

        var snapshot = await EmbeddedGeofenceEngine.BuildAsync(
            db,
            historicalDate,
            new[] { load },
            CancellationToken.None);

        Assert.Equal(3, snapshot.TrackingEventCount);
        var visit = Assert.Single(snapshot.Visits.Where(x => x.Fence.Id == fence.Id));
        Assert.Equal(load.Id, visit.LoadId);
        Assert.Equal(stop.Id, visit.LoadStopId);
        Assert.Equal(entered, visit.EnteredAtUtc);
        Assert.NotNull(visit.ConfirmedAtUtc);
        Assert.Equal(entered.AddMinutes(20), visit.ExitedAtUtc);
        Assert.True(visit.DwellMinutes >= 10);
    }

    private static VehicleTrackingEvent Tracking(
        string providerEventId,
        string vehicleIdentifier,
        DateTimeOffset eventTimeUtc,
        decimal latitude,
        decimal longitude) => new()
    {
        ProviderName = "RoadTech Falcon",
        ProviderEventId = providerEventId,
        VehicleIdentifier = vehicleIdentifier,
        EventTimeUtc = eventTimeUtc,
        Latitude = latitude,
        Longitude = longitude,
        RawPayload = "{}",
        MatchStatus = "Test"
    };
}
