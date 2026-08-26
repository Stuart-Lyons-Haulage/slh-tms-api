using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class GeofenceSitePromotionTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory _factory;

    public GeofenceSitePromotionTests(CustomWebFactory factory)
    {
        _factory = factory;
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        db.Database.EnsureDeleted();
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task Repair_links_promotes_coded_geofence_to_canonical_site_master()
    {
        var fenceId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.SiteGeofences.Add(new SiteGeofence
            {
                Id = fenceId,
                Name = "Aldi Cardiff",
                NormalizedName = "ALDI CARDIFF",
                SiteNumber = "9101",
                SiteId = null,
                PolygonJson = "[[0,0],[1,0],[0,1]]",
                Active = true
            });
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClientWithUser("planner@lyonshaulage.com");
        var response = await client.PostAsync("/api/v1/geofences/repair-links", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var finalScope = _factory.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var site = Assert.Single(finalDb.Sites.Where(x => x.ExternalCode == "9101"));
        Assert.True(site.Active);
        Assert.Equal("Aldi Cardiff", site.Name);
        Assert.Equal("Aldi Cardiff", site.DriverTextName);

        var fence = finalDb.SiteGeofences.Single(x => x.Id == fenceId);
        Assert.Equal(site.Id, fence.SiteId);
        Assert.Equal("9101", fence.SiteNumber);
        Assert.Contains(finalDb.MasterDataAudits, x => x.EntityType == "Site" && x.EntityId == site.Id && x.Action == "CreatedFromOperationalGeofence");
        Assert.Contains(finalDb.MasterDataAudits, x => x.EntityType == "Geofence" && x.EntityId == fenceId && x.Action == "PromotedToSiteMaster");
    }

    [Fact]
    public async Task Repair_links_reuses_inactive_numeric_site_alias_without_duplicate()
    {
        var siteId = Guid.NewGuid();
        var fenceId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.Sites.Add(new Site { Id = siteId, ExternalCode = "009102", Name = "Existing Delivery", Active = false });
            db.SiteGeofences.Add(new SiteGeofence
            {
                Id = fenceId,
                Name = "Existing Delivery Geofence",
                NormalizedName = "EXISTING DELIVERY GEOFENCE",
                SiteNumber = "9102",
                SiteId = null,
                PolygonJson = "[[0,0],[1,0],[0,1]]",
                Active = true
            });
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClientWithUser("planner@lyonshaulage.com");
        var response = await client.PostAsync("/api/v1/geofences/repair-links", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var finalScope = _factory.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<TmsDbContext>();
        Assert.Single(finalDb.Sites);
        var site = finalDb.Sites.Single();
        Assert.Equal(siteId, site.Id);
        Assert.True(site.Active);
        var fence = finalDb.SiteGeofences.Single(x => x.Id == fenceId);
        Assert.Equal(siteId, fence.SiteId);
        Assert.Equal("009102", fence.SiteNumber);
    }
}
