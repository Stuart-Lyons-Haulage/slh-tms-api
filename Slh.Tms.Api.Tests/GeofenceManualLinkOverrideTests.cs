using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class GeofenceManualLinkOverrideTests
{
    [Fact]
    public async Task Manual_site_code_override_links_embedded_geofence_to_master_site()
    {
        var fence = Assert.Single(EmbeddedGeofenceEngine.ApprovedFences.Where(x => x.Name == "Selsey Despatch"));
        var site = new Site { Id = Guid.NewGuid(), ExternalCode = "SITE-0023", Name = "Barfoots Sefter", Active = true };
        var options = new DbContextOptionsBuilder<TmsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new TmsDbContext(options);
        db.Sites.Add(site);
        db.SiteGeofences.Add(new SiteGeofence
        {
            Name = fence.Name,
            NormalizedName = NormalizeName(fence.Name),
            SiteNumber = "SITE-0023",
            SiteId = site.Id,
            PolygonJson = "[]",
            Active = true
        });
        await db.SaveChangesAsync();

        var statuses = await EmbeddedGeofenceEngine.FenceStatusesAsync(db, CancellationToken.None);
        var status = Assert.Single(statuses.Where(x => x.Fence.Id == fence.Id));

        Assert.Equal(site.Id, status.SiteId);
        Assert.Equal("Barfoots Sefter", status.SiteName);
        Assert.Equal("SITE-0023", status.SiteCode);
        Assert.True(status.ManualOverride);
    }

    [Fact]
    public async Task Manual_site_link_is_authoritative_when_progression_names_overlap()
    {
        var fence = Assert.Single(EmbeddedGeofenceEngine.ApprovedFences.Where(x => x.Name == "Selsey Despatch"));
        var longitude = fence.Points.Average(point => point.Longitude);
        var latitude = fence.Points.Average(point => point.Latitude);
        var options = new DbContextOptionsBuilder<TmsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new TmsDbContext(options);

        var nwfSite = new Site
        {
            Id = Guid.NewGuid(),
            ExternalCode = "NWF-SELSEY",
            Name = "Selsey (Natures Way)",
            DriverTextName = "NWF-Selsey",
            Active = true
        };
        var barSite = new Site
        {
            Id = Guid.NewGuid(),
            ExternalCode = "SITE-0023",
            Name = "Barfoots Sefter",
            DriverTextName = "BAR-Sefter South (+3°C)",
            Active = true
        };
        var vehicleId = Guid.NewGuid();
        db.Sites.AddRange(nwfSite, barSite);
        db.SiteGeofences.Add(new SiteGeofence
        {
            Name = fence.Name,
            NormalizedName = NormalizeName(fence.Name),
            SiteNumber = barSite.ExternalCode,
            SiteId = barSite.Id,
            PolygonJson = "[]",
            Active = true
        });
        db.Vehicles.Add(new Vehicle { Id = vehicleId, Registration = "SLH225", Active = true });

        var entered = new DateTimeOffset(2026, 8, 26, 6, 0, 0, TimeSpan.Zero);
        db.VehicleTrackingEvents.AddRange(
            Tracking("manual-link-entry", entered, latitude, longitude),
            Tracking("manual-link-confirm", entered.AddMinutes(15), latitude, longitude),
            Tracking("manual-link-exit", entered.AddMinutes(25), 0d, 0d));
        await db.SaveChangesAsync();

        var loadId = Guid.NewGuid();
        var nwfStopId = Guid.NewGuid();
        var barStopId = Guid.NewGuid();
        var load = new Load
        {
            Id = loadId,
            Reference = "RUN-MANUAL-LINK",
            PlanningDate = new DateOnly(2026, 8, 26),
            Status = LoadStatus.Planned,
            VehicleId = vehicleId,
            Stops =
            [
                new LoadStop { Id = nwfStopId, LoadId = loadId, Sequence = 1, Name = "NWF-Selsey", PlannedArrivalUtc = entered },
                new LoadStop { Id = barStopId, LoadId = loadId, Sequence = 2, Name = "BAR-Sefter South (+3°C)", PlannedArrivalUtc = entered.AddMinutes(10) }
            ]
        };

        var snapshot = await EmbeddedGeofenceEngine.BuildAsync(
            db,
            load.PlanningDate,
            GeofencePlanningMatch.PrepareLoads([load]),
            CancellationToken.None);

        var visit = Assert.Single(snapshot.Visits);
        Assert.Equal(loadId, visit.LoadId);
        Assert.Equal(barStopId, visit.LoadStopId);
        Assert.NotEqual(nwfStopId, visit.LoadStopId);
    }

    [Fact]
    public async Task Location_only_override_suppresses_automatic_site_linking()
    {
        var fence = Assert.Single(EmbeddedGeofenceEngine.ApprovedFences.Where(x => x.Name == "Selsey Despatch"));
        var site = new Site { Id = Guid.NewGuid(), ExternalCode = "SITE-0023", Name = "Selsey Despatch", Active = true };
        var options = new DbContextOptionsBuilder<TmsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new TmsDbContext(options);
        db.Sites.Add(site);
        db.SiteGeofences.Add(new SiteGeofence
        {
            Name = fence.Name,
            NormalizedName = NormalizeName(fence.Name),
            SiteNumber = "LOCATION_ONLY",
            SiteId = null,
            PolygonJson = "[]",
            Active = true
        });
        await db.SaveChangesAsync();

        var statuses = await EmbeddedGeofenceEngine.FenceStatusesAsync(db, CancellationToken.None);
        var status = Assert.Single(statuses.Where(x => x.Fence.Id == fence.Id));

        Assert.Null(status.SiteId);
        Assert.Null(status.SiteName);
        Assert.Null(status.SiteCode);
        Assert.True(status.ManualOverride);
    }

    private static VehicleTrackingEvent Tracking(string id, DateTimeOffset at, double latitude, double longitude) => new()
    {
        ProviderName = "RoadTech Falcon",
        ProviderEventId = id,
        VehicleIdentifier = "SLH225",
        EventTimeUtc = at,
        Latitude = (decimal)latitude,
        Longitude = (decimal)longitude,
        RawPayload = "{}",
        MatchStatus = "Received"
    };

    private static string NormalizeName(string value) => string.Join(' ', value.Trim().ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
