using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
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

    private static string NormalizeName(string value) => string.Join(' ', value.Trim().ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
