using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class SiteMasterConsolidationTests
{
    [Fact]
    public async Task Customer_location_is_promoted_to_site_and_matching_geofence_is_linked()
    {
        await using var db = CreateDb();
        db.Customers.Add(new Customer { Code = "ALDI-CDF", Name = "Aldi Cardiff" });
        db.SiteGeofences.Add(new SiteGeofence
        {
            Name = "Aldi Cardiff",
            NormalizedName = "ALDI CARDIFF",
            PolygonJson = "[[0,0],[1,0],[0,1]]",
            Active = true
        });
        await db.SaveChangesAsync();

        var result = await SiteMasterConsolidation.ReconcileAsync(db, "test", CancellationToken.None);

        var site = Assert.Single(db.Sites.Where(x => x.ExternalCode == "ALDI-CDF"));
        Assert.Equal("Aldi Cardiff", site.Name);
        var fence = Assert.Single(db.SiteGeofences);
        Assert.Equal(site.Id, fence.SiteId);
        Assert.Equal("ALDI-CDF", fence.SiteNumber);
        Assert.Equal(1, result.PromotedCustomers);
        Assert.Equal(1, result.LinkedGeofences);
    }

    [Fact]
    public async Task Duplicate_unlinked_site_is_archived_when_same_name_has_the_geofence()
    {
        await using var db = CreateDb();
        var canonical = new Site { ExternalCode = "ALDI-CDF", Name = "Aldi Cardiff", Active = true };
        var duplicate = new Site { ExternalCode = "OLD-ALDI-CDF", Name = "ALDI CARDIFF", Active = true };
        db.Sites.AddRange(canonical, duplicate);
        db.SiteGeofences.Add(new SiteGeofence
        {
            Name = "Aldi Cardiff",
            NormalizedName = "ALDI CARDIFF",
            SiteId = canonical.Id,
            SiteNumber = canonical.ExternalCode,
            PolygonJson = "[[0,0],[1,0],[0,1]]",
            Active = true
        });
        await db.SaveChangesAsync();

        var result = await SiteMasterConsolidation.ReconcileAsync(db, "test", CancellationToken.None);

        Assert.True(canonical.Active);
        Assert.False(duplicate.Active);
        Assert.Equal(1, result.ArchivedDuplicates);
    }

    [Fact]
    public async Task Ambiguous_duplicate_sites_are_flagged_instead_of_guessed()
    {
        await using var db = CreateDb();
        db.Sites.AddRange(
            new Site { ExternalCode = "A1", Name = "Aldi Cardiff", Active = true },
            new Site { ExternalCode = "A2", Name = "ALDI CARDIFF", Active = true });
        await db.SaveChangesAsync();

        var result = await SiteMasterConsolidation.ReconcileAsync(db, "test", CancellationToken.None);

        Assert.Equal(0, result.ArchivedDuplicates);
        Assert.Equal(1, result.NeedsReview);
        var review = Assert.Single(db.StagedImports.Where(x => x.EntityType == "masterdata:site-review"));
        Assert.Equal(StagingStatus.PendingReview, review.Status);
    }

    [Fact]
    public async Task Unlinked_geofence_without_unique_site_match_is_flagged()
    {
        await using var db = CreateDb();
        db.SiteGeofences.Add(new SiteGeofence
        {
            Name = "Aldi Unknown Depot",
            NormalizedName = "ALDI UNKNOWN DEPOT",
            PolygonJson = "[[0,0],[1,0],[0,1]]",
            Active = true
        });
        await db.SaveChangesAsync();

        var result = await SiteMasterConsolidation.ReconcileAsync(db, "test", CancellationToken.None);

        Assert.Equal(0, result.LinkedGeofences);
        Assert.Equal(1, result.NeedsReview);
        var review = Assert.Single(db.StagedImports.Where(x => x.EntityType == "masterdata:geofence-review"));
        Assert.Equal(StagingStatus.PendingReview, review.Status);
    }

    [Fact]
    public async Task Matching_archived_site_code_is_restored_instead_of_duplicated()
    {
        await using var db = CreateDb();
        var archived = new Site { ExternalCode = "ALDI-CDF", Name = "Aldi Cardiff", Active = false };
        db.Sites.Add(archived);
        db.Customers.Add(new Customer { Code = "ALDI-CDF", Name = "Aldi Cardiff" });
        await db.SaveChangesAsync();

        await SiteMasterConsolidation.ReconcileAsync(db, "test", CancellationToken.None);

        var site = Assert.Single(db.Sites.Where(x => x.ExternalCode == "ALDI-CDF"));
        Assert.True(site.Active);
        Assert.Equal(archived.Id, site.Id);
    }

    private static TmsDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase($"site-master-consolidation-{Guid.NewGuid()}")
            .Options;
        return new TmsDbContext(options);
    }
}
