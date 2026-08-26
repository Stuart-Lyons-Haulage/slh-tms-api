using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class SiteGeofenceMasterSyncTests
{
    [Fact]
    public async Task Sync_assigns_SITE_codes_and_links_unique_name_confirmed_geofence()
    {
        await using var db = CreateDb();
        var chelmsford = new Site { ExternalCode = "ALDI-CHELMSFORD", Name = "Aldi - Chelmsford", Active = true };
        var cardiff = new Site { ExternalCode = "ALDI-CARDIFF", Name = "Aldi Cardiff", Active = true };
        db.Sites.AddRange(chelmsford, cardiff);
        db.SiteGeofences.Add(new SiteGeofence
        {
            Name = "Chelmsford (Aldi)",
            NormalizedName = "CHELMSFORD (ALDI)",
            PolygonJson = "[[0,0],[1,0],[0,1]]",
            Active = true
        });
        await db.SaveChangesAsync();

        var result = await SiteGeofenceMasterSync.SyncAsync(db, CancellationToken.None);

        Assert.Equal(new[] { "SITE001", "SITE002" }, new[] { chelmsford.ExternalCode, cardiff.ExternalCode }.OrderBy(x => x).ToArray());
        var fence = Assert.Single(db.SiteGeofences);
        Assert.Equal(chelmsford.Id, fence.SiteId);
        Assert.Equal(chelmsford.ExternalCode, fence.SiteNumber);
        Assert.Equal(1, result.GeofencesLinked);
        Assert.Contains(result.Sites, x => x.SiteId == chelmsford.Id && x.GeofenceLinked && !x.NeedsReview);
    }

    [Fact]
    public async Task Sync_removes_stale_link_when_geofence_name_does_not_confirm_site()
    {
        await using var db = CreateDb();
        var southbound = new Site { ExternalCode = "1", Name = "Southbound", Active = true };
        db.Sites.Add(southbound);
        db.SiteGeofences.Add(new SiteGeofence
        {
            Name = "Chelmsford (Aldi)",
            NormalizedName = "CHELMSFORD (ALDI)",
            SiteId = southbound.Id,
            SiteNumber = "1",
            PolygonJson = "[[0,0],[1,0],[0,1]]",
            Active = true
        });
        await db.SaveChangesAsync();

        var result = await SiteGeofenceMasterSync.SyncAsync(db, CancellationToken.None);

        var fence = Assert.Single(db.SiteGeofences);
        Assert.Null(fence.SiteId);
        Assert.Null(fence.SiteNumber);
        Assert.Equal("SITE001", southbound.ExternalCode);
        Assert.Equal(1, result.GeofencesUnlinked);
        Assert.True(Assert.Single(result.Sites).NeedsReview);
    }

    [Fact]
    public async Task Brand_only_match_is_not_linked_when_multiple_sites_share_brand()
    {
        await using var db = CreateDb();
        db.Sites.AddRange(
            new Site { ExternalCode = "A", Name = "Aldi Cardiff", Active = true },
            new Site { ExternalCode = "B", Name = "Aldi Chelmsford", Active = true });
        db.SiteGeofences.Add(new SiteGeofence
        {
            Name = "Aldi Depot",
            NormalizedName = "ALDI DEPOT",
            PolygonJson = "[[0,0],[1,0],[0,1]]",
            Active = true
        });
        await db.SaveChangesAsync();

        var result = await SiteGeofenceMasterSync.SyncAsync(db, CancellationToken.None);

        var fence = Assert.Single(db.SiteGeofences);
        Assert.Null(fence.SiteId);
        Assert.Equal(0, result.GeofencesLinked);
        Assert.Equal(2, result.SitesMissingGeofence);
    }

    private static TmsDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase($"site-geofence-master-sync-{Guid.NewGuid()}")
            .Options;
        return new TmsDbContext(options);
    }
}
