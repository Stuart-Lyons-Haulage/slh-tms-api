using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class MasterDataBulkDeleteTests : IClassFixture<CustomWebFactory>
{
    private const string LyonsUser = "planner@lyonshaulage.com";
    private readonly CustomWebFactory _factory;

    public MasterDataBulkDeleteTests(CustomWebFactory factory) => _factory = factory;

    [Fact]
    public async Task Bulk_delete_requires_admin_phrase()
    {
        var client = _factory.CreateClientWithUser(LyonsUser);

        var response = await client.PostAsJsonAsync("/api/v1/master-data-cleanup/drivers/bulk-delete", new
        {
            ids = new[] { Guid.NewGuid() },
            adminPassword = "wrong"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Bulk_delete_removes_active_unused_driver()
    {
        var driverId = Guid.NewGuid();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.Drivers.Add(new Driver { Id = driverId, EmployeeNumber = $"BULK-{Guid.NewGuid():N}"[..16], DisplayName = "Bulk Delete Driver", Active = true });
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClientWithUser(LyonsUser);
        var response = await client.PostAsJsonAsync("/api/v1/master-data-cleanup/drivers/bulk-delete", new
        {
            ids = new[] { driverId },
            adminPassword = "DELETE"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<TmsDbContext>();
        Assert.False(verifyDb.Drivers.Any(driver => driver.Id == driverId));
    }

    [Fact]
    public async Task Bulk_delete_blocks_site_linked_to_geofence()
    {
        var siteId = Guid.NewGuid();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.Sites.Add(new Site { Id = siteId, ExternalCode = $"BD{Guid.NewGuid():N}"[..12], Name = "Bulk Delete Site", Active = true });
            db.SiteGeofences.Add(new SiteGeofence
            {
                Name = "Bulk Delete Site",
                NormalizedName = $"BULK DELETE SITE {Guid.NewGuid():N}",
                SiteId = siteId,
                SiteNumber = "BD",
                PolygonJson = "[]",
                Active = true
            });
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClientWithUser(LyonsUser);
        var response = await client.PostAsJsonAsync("/api/v1/master-data-cleanup/sites/bulk-delete", new
        {
            ids = new[] { siteId },
            adminPassword = "DELETE"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"blocked\":1", body);
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<TmsDbContext>();
        Assert.True(verifyDb.Sites.Any(site => site.Id == siteId));
    }
}
