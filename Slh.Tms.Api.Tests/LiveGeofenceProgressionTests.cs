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
        var point = fence.Points[0];
        var vehicleId = Guid.NewGuid();
        var loadId = Guid.NewGuid();
        var stopId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var planningDate = UkDate(now);

        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new TmsDbContext(options);

        db.Vehicles.Add(new Vehicle { Id = vehicleId, Registration = "AB12CDE", Active = true });
        db.VehicleTrackingEvents.Add(new VehicleTrackingEvent
        {
            ProviderName = "RoadTech Falcon",
            ProviderEventId = "entry",
            VehicleIdentifier = "AB12CDE",
            EventTimeUtc = now.AddMinutes(-12),
            Latitude = (decimal)point.Latitude,
            Longitude = (decimal)point.Longitude,
            RawPayload = "{}",
            MatchStatus = "Received"
        });
        db.VehicleLiveStatuses.Add(new VehicleLiveStatus
        {
            VehicleIdentifier = "AB12CDE",
            LastEventTimeUtc = now.AddMinutes(-12),
            LastReceivedAtUtc = now,
            Latitude = (decimal)point.Latitude,
            Longitude = (decimal)point.Longitude,
            LastKnownStatus = "Received"
        });
        await db.SaveChangesAsync();

        var load = new Load
        {
            Id = loadId,
            Reference = "TEST-LIVE",
            PlanningDate = planningDate,
            Status = LoadStatus.InProgress,
            VehicleId = vehicleId,
            Stops =
            [
                new LoadStop
                {
                    Id = stopId,
                    LoadId = loadId,
                    Sequence = 1,
                    Name = "Aldi Swindon",
                    PlannedArrivalUtc = now.AddMinutes(-15)
                }
            ]
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
