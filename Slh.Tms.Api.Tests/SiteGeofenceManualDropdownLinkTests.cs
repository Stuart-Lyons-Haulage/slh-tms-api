using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class SiteGeofenceManualDropdownLinkTests
{
    [Fact]
    public async Task Manual_dropdown_link_accepts_selected_site_when_names_do_not_match()
    {
        await using var db = CreateDb();
        var site = new Site { ExternalCode = "SITE023", Name = "Barfoots Sefter", Active = true };
        var fence = new SiteGeofence
        {
            Name = "Selsey Despatch",
            NormalizedName = "SELSEY DESPATCH",
            PolygonJson = "[[0,0],[1,0],[0,1]]",
            Active = true
        };
        db.Sites.Add(site);
        db.SiteGeofences.Add(fence);
        await db.SaveChangesAsync();

        var status = await SiteGeofenceMasterSync.LinkGeofenceAsync(db, fence.Id, site.ExternalCode, CancellationToken.None);

        Assert.Equal(site.Id, fence.SiteId);
        Assert.Equal(site.ExternalCode, fence.SiteNumber);
        Assert.True(status.GeofenceLinked);
        Assert.False(status.NeedsReview);
    }

    [Fact]
    public async Task Sync_sites_preserves_explicit_manual_dropdown_link()
    {
        await using var db = CreateDb();
        var site = new Site { ExternalCode = "SITE023", Name = "Barfoots Sefter", Active = true };
        var fence = new SiteGeofence
        {
            Name = "Selsey Despatch",
            NormalizedName = "SELSEY DESPATCH",
            SiteId = site.Id,
            SiteNumber = site.ExternalCode,
            PolygonJson = "[[0,0],[1,0],[0,1]]",
            Active = true
        };
        db.Sites.Add(site);
        db.SiteGeofences.Add(fence);
        await db.SaveChangesAsync();

        var result = await SiteGeofenceMasterSync.SyncAsync(db, CancellationToken.None);

        Assert.Equal(site.Id, fence.SiteId);
        Assert.Equal(site.ExternalCode, fence.SiteNumber);
        Assert.Equal(0, result.GeofencesUnlinked);
        Assert.Contains(result.Sites, x => x.SiteId == site.Id && x.GeofenceLinked && !x.NeedsReview);
    }

    private static TmsDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase($"site-geofence-manual-dropdown-{Guid.NewGuid()}")
            .Options;
        return new TmsDbContext(options);
    }
}
