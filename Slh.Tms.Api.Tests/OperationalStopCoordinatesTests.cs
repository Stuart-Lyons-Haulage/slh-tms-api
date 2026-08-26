using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class OperationalStopCoordinatesTests
{
    [Theory]
    [InlineData("NWF-Selsey")]
    [InlineData("Collect · NWF-Selsey")]
    [InlineData("Collection · NWF-Selsey")]
    public void Missing_site_coordinates_fall_back_to_unique_approved_geofence(string stopName)
    {
        var stop = new LoadStop
        {
            Id = Guid.NewGuid(),
            Sequence = 1,
            Name = stopName
        };

        var coordinate = OperationalStopCoordinates.Resolve(stop);

        Assert.NotNull(coordinate);
        var fence = Assert.Single(EmbeddedGeofenceEngine.ApprovedFences
            .Where(candidate => GeofencePlanningMatch.SamePhysicalSite(stop, candidate)));
        var expected = OperationalRunOrigin.FenceCentre(fence);
        Assert.Equal(expected, coordinate);
    }

    [Fact]
    public void Site_master_coordinates_remain_authoritative()
    {
        var stop = new LoadStop
        {
            Id = Guid.NewGuid(),
            Sequence = 1,
            Name = "NWF-Selsey",
            Longitude = -0.12345m,
            Latitude = 50.98765m
        };

        Assert.Equal((-0.12345m, 50.98765m), OperationalStopCoordinates.Resolve(stop));
    }

    [Fact]
    public async Task Planner_alias_resolves_to_site_master_coordinates_for_final_eta()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new TmsDbContext(options);
        var site = new Site
        {
            ExternalCode = "AMZ-MK",
            Name = "Amazon ALT2 - Milton Keynes",
            DriverTextName = "Amazon Milton Keynes",
            Active = true
        };
        db.Sites.Add(site);
        await db.SaveChangesAsync();
        await MasterDetailStore.SaveAsync(
            db,
            "site",
            site.ExternalCode,
            JsonSerializer.Serialize(new
            {
                externalCode = site.ExternalCode,
                aliases = "Amazon - Milton Keynes;Amazon Milton Keynes",
                latitude = 52.02345m,
                longitude = -0.73456m
            }),
            "test",
            "test",
            CancellationToken.None);

        var resolver = await PlannerSourceMasterDataResolver.CreateAsync(db, CancellationToken.None);
        var stop = new LoadStop
        {
            Id = Guid.NewGuid(),
            Sequence = 5,
            Name = "Deliver · Amazon - Milton Keynes"
        };

        Assert.Equal((-0.73456m, 52.02345m), OperationalStopCoordinates.Resolve(stop, resolver));
    }

    [Fact]
    public async Task Manually_linked_geofence_supplies_coordinate_when_site_master_has_no_lat_lon()
    {
        var fence = EmbeddedGeofenceEngine.ApprovedFences.First(candidate =>
            candidate.Name.Contains("Selsey", StringComparison.OrdinalIgnoreCase));
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new TmsDbContext(options);
        var site = new Site
        {
            ExternalCode = "BAR-SELSEY-TEST",
            Name = "BAR Selsey Test",
            DriverTextName = "BAR Selsey Test",
            Active = true
        };
        db.Sites.Add(site);
        db.SiteGeofences.Add(new SiteGeofence
        {
            Name = fence.Name,
            NormalizedName = string.Join(' ', fence.Name.Trim().ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries)),
            SiteNumber = site.ExternalCode,
            SiteId = site.Id,
            PolygonJson = "[]",
            Active = true
        });
        await db.SaveChangesAsync();
        await MasterDetailStore.SaveAsync(
            db,
            "site",
            site.ExternalCode,
            JsonSerializer.Serialize(new
            {
                externalCode = site.ExternalCode,
                aliases = "BAR-Selsey Linked"
            }),
            "test",
            "test",
            CancellationToken.None);

        var resolver = await PlannerSourceMasterDataResolver.CreateAsync(db, CancellationToken.None);
        var stop = new LoadStop
        {
            Id = Guid.NewGuid(),
            Sequence = 1,
            Name = "Collect · BAR-Selsey Linked"
        };

        Assert.Equal(OperationalRunOrigin.FenceCentre(fence), OperationalStopCoordinates.Resolve(stop, resolver));
    }

    [Fact]
    public void Unknown_unmapped_stop_fails_closed()
    {
        var stop = new LoadStop
        {
            Id = Guid.NewGuid(),
            Sequence = 1,
            Name = "Definitely not an SLH approved geofence 999999"
        };

        Assert.Null(OperationalStopCoordinates.Resolve(stop));
    }
}
