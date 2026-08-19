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
        Assert.True(EmbeddedGeofenceEngine.ApprovedProgressionFenceCount < fences.Count);
    }
}
