using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class EmbeddedGeofenceEngineTests
{
    [Fact]
    public void Approved_falcon_source_loads_all_555_unique_valid_polygons()
    {
        var fences = EmbeddedGeofenceEngine.ApprovedFences;

        Assert.Equal(555, fences.Count);
        Assert.Equal(555, fences.Select(fence => fence.Id).Distinct().Count());
        Assert.Equal(335, EmbeddedGeofenceEngine.ApprovedProgressionFenceCount);
        Assert.All(fences, fence =>
        {
            Assert.False(string.IsNullOrWhiteSpace(fence.Name));
            Assert.True(fence.Points.Count >= 3);
            Assert.True(fence.MinLongitude <= fence.MaxLongitude);
            Assert.True(fence.MinLatitude <= fence.MaxLatitude);
            Assert.All(fence.Points, point =>
            {
                Assert.InRange(point.Longitude, -180d, 180d);
                Assert.InRange(point.Latitude, -90d, 90d);
            });
        });
    }

    [Fact]
    public void Same_named_multi_polygon_sites_keep_distinct_stable_ids()
    {
        var tamworth = EmbeddedGeofenceEngine.ApprovedFences
            .Where(fence => string.Equals(fence.Name.Trim(), "Tamworth (Greencore)", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Equal(2, tamworth.Count);
        Assert.Equal(2, tamworth.Select(fence => fence.Id).Distinct().Count());
    }

    [Fact]
    public void Safety_and_roadside_categories_are_kept_but_not_used_for_run_stop_progression()
    {
        var fences = EmbeddedGeofenceEngine.ApprovedFences;

        Assert.Contains(fences, fence => fence.Category == "Restricted Access");
        Assert.Contains(fences, fence => fence.Category == "DVS");
        Assert.Contains(fences, fence => fence.Category == "DVSA Checkpoint");
        Assert.Contains(fences, fence => fence.Category == "Service Centre");
        Assert.Contains(fences, fence => fence.Category == "Service Station");
        Assert.Equal(335, EmbeddedGeofenceEngine.ApprovedProgressionFenceCount);
    }

    [Fact]
    public void Known_uploaded_fences_preserve_longitude_latitude_order()
    {
        var fences = EmbeddedGeofenceEngine.ApprovedFences;

        var aylesford = Assert.Single(fences.Where(x => x.Name.Trim() == "Aylesford (Waitrose)"));
        Assert.Equal("RDC", aylesford.Category);
        Assert.InRange(aylesford.Points[0].Longitude, 0.49d, 0.50d);
        Assert.InRange(aylesford.Points[0].Latitude, 51.30d, 51.31d);

        var bracknell = Assert.Single(fences.Where(x => x.Name.Trim() == "Bracknell Traywash"));
        Assert.Equal("Traywash", bracknell.Category);
        Assert.InRange(bracknell.Points[0].Longitude, -0.77d, -0.76d);
        Assert.InRange(bracknell.Points[0].Latitude, 51.41d, 51.42d);
    }

    [Fact]
    public async Task Falcon_site_number_one_never_overrides_a_name_match()
    {
        var fence = EmbeddedGeofenceEngine.ApprovedFences.First(item => item.SiteNumber?.Trim() == "1");
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new TmsDbContext(options);

        var wrongCodeMatch = new Site { ExternalCode = "1", Name = "Wrong site", Active = true };
        var correctNameMatch = new Site { ExternalCode = "MATCH-001", Name = fence.Name, Active = true };
        db.Sites.AddRange(wrongCodeMatch, correctNameMatch);
        await db.SaveChangesAsync();

        var statuses = await EmbeddedGeofenceEngine.FenceStatusesAsync(db, CancellationToken.None);
        var status = Assert.Single(statuses.Where(item => item.Fence.Id == fence.Id));

        Assert.Equal(correctNameMatch.Id, status.SiteId);
        Assert.NotEqual(wrongCodeMatch.Id, status.SiteId);
    }
}
