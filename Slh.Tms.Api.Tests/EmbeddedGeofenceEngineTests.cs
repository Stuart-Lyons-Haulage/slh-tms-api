using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class EmbeddedGeofenceEngineTests
{
    [Fact]
    public void Approved_falcon_seed_initialises_all_53_geofences()
    {
        var fences = EmbeddedGeofenceEngine.ApprovedFences;

        Assert.Equal(53, fences.Count);
        Assert.All(fences, fence =>
        {
            Assert.False(string.IsNullOrWhiteSpace(fence.Name));
            Assert.True(fence.Points.Count >= 3);
        });
    }
}
