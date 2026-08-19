using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class LiveGeofenceProgressionTests
{
    [Fact]
    public async Task Fresh_live_status_extends_stationary_inside_dwell_and_links_reordered_site_name()
    {
        var fence = Assert.Single(EmbeddedGeofenceEngine.ApprovedFences.Where(x => x.Name.Trim() == "Swindon (Aldi)"));
        var longitude = fence.Points.Average(x => x.Longitude);
        var latitude = fence.Points.Average(x => x.Latitude);
        var vehicleId = Guid.NewGuid();
        var loadId = Guid.NewGuid();
        var stopId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var planningDate = UkDate(now);

        var options = new DbContextOptionsBuilder<TmsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new TmsDbContext(options);

        db.Vehicles.Add(new Vehicle { Id = vehicleId, Registration = "AB12CDE", Active = true });
        db.VehicleTrackingEvents.Add(Tracking("entry", "AB12CDE", now.AddMinutes(-12), latitude, longitude));
        db.VehicleLiveStatuses.Add(new VehicleLiveStatus
        {
            VehicleIdentifier = "AB12CDE", LastEventTimeUtc = now.AddMinutes(-12), LastReceivedAtUtc = now,
            Latitude = (decimal)latitude, Longitude = (decimal)longitude, LastKnownStatus = "Received"
        });
        await db.SaveChangesAsync();

        var load = new Load
        {
            Id = loadId, Reference = "TEST-LIVE", PlanningDate = planningDate, Status = LoadStatus.InProgress, VehicleId = vehicleId,
            Stops = [new LoadStop { Id = stopId, LoadId = loadId, Sequence = 1, Name = "Aldi Swindon", PlannedArrivalUtc = now.AddMinutes(-15) }]
        };

        var snapshot = await EmbeddedGeofenceEngine.BuildAsync(db, planningDate, [load], CancellationToken.None);
        var visit = Assert.Single(snapshot.Visits);
        Assert.NotNull(visit.ConfirmedAtUtc);
        Assert.Null(visit.ExitedAtUtc);
        Assert.Equal(loadId, visit.LoadId);
        Assert.Equal(stopId, visit.LoadStopId);
        Assert.True(visit.DwellMinutes >= 10);
        Assert.Single(snapshot.ActiveVisits);
    }

    [Fact]
    public async Task Run1am_style_nwf_visit_links_and_clears_consecutive_same_site_jobs()
    {
        var fence = Assert.Single(EmbeddedGeofenceEngine.ApprovedFences.Where(x => x.Name.Contains("Runcton", StringComparison.OrdinalIgnoreCase)));
        var longitude = fence.Points.Average(x => x.Longitude);
        var latitude = fence.Points.Average(x => x.Latitude);
        var vehicleId = Guid.NewGuid();
        var loadId = Guid.NewGuid();
        var firstRuncton = Guid.NewGuid();
        var secondRuncton = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var planningDate = UkDate(now);

        var options = new DbContextOptionsBuilder<TmsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new TmsDbContext(options);
        db.Vehicles.Add(new Vehicle { Id = vehicleId, Registration = "KY71CVP", Active = true });
        db.VehicleTrackingEvents.AddRange(
            Tracking("nwf-entry", "KY71CVP", now.AddMinutes(-35), latitude, longitude),
            Tracking("nwf-confirm", "KY71CVP", now.AddMinutes(-20), latitude, longitude),
            Tracking("nwf-exit", "KY71CVP", now.AddMinutes(-10), 0d, 0d));
        await db.SaveChangesAsync();

        var load = new Load
        {
            Id = loadId, Reference = "RUN-1-AM", PlanningDate = planningDate, Status = LoadStatus.Planned, VehicleId = vehicleId,
            Stops =
            [
                new LoadStop { Id = firstRuncton, LoadId = loadId, Sequence = 1, Name = "NWF-Runcton", PlannedArrivalUtc = now.AddMinutes(-30) },
                new LoadStop { Id = secondRuncton, LoadId = loadId, Sequence = 2, Name = "NWF-Runcton", PlannedArrivalUtc = now.AddMinutes(-25) },
                new LoadStop { Id = Guid.NewGuid(), LoadId = loadId, Sequence = 3, Name = "Aldi-Darlington", PlannedArrivalUtc = now.AddHours(1) }
            ]
        };

        var matchingLoad = GeofencePlanningMatch.PrepareLoad(load);
        Assert.Equal("RUNCTON Natures Way", matchingLoad.Stops[0].Name, ignoreCase: true);

        var snapshot = await EmbeddedGeofenceEngine.BuildAsync(db, planningDate, [matchingLoad], CancellationToken.None);
        var visit = Assert.Single(snapshot.Visits);
        Assert.Equal(loadId, visit.LoadId);
        Assert.Equal(firstRuncton, visit.LoadStopId);
        Assert.NotNull(visit.ConfirmedAtUtc);
        Assert.NotNull(visit.ExitedAtUtc);

        var completed = GeofencePlanningMatch.CompletedStopIds(load, snapshot.Visits);
        Assert.Equal(2, completed.Count);
        Assert.Contains(firstRuncton, completed);
        Assert.Contains(secondRuncton, completed);
    }

    [Theory]
    [InlineData("NWF-Merston", "MERSTON Natures Way")]
    [InlineData("NWF-Selsey", "SELSEY Natures Way")]
    public void Planner_nwf_labels_are_presented_to_falcon_with_natures_way_identity(string planner, string expected)
    {
        Assert.Equal(expected, GeofencePlanningMatch.MatchText(planner), ignoreCase: true);
    }

    private static VehicleTrackingEvent Tracking(string id, string vehicle, DateTimeOffset at, double latitude, double longitude) => new()
    {
        ProviderName = "RoadTech Falcon", ProviderEventId = id, VehicleIdentifier = vehicle, EventTimeUtc = at,
        Latitude = (decimal)latitude, Longitude = (decimal)longitude, RawPayload = "{}", MatchStatus = "Received"
    };

    private static DateOnly UkDate(DateTimeOffset value)
    {
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, zone).DateTime);
        }
        catch (TimeZoneNotFoundException)
        {
            return DateOnly.FromDateTime(value.UtcDateTime);
        }
    }
}
