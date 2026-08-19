using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class OperationalGeofencePayloadTests
{
    [Fact]
    public void Embedded_operational_payload_loads_all_approved_geofences()
    {
        var fences = EmbeddedGeofenceEngine.ApprovedFences;

        Assert.Equal(314, fences.Count);
        Assert.All(fences, fence =>
        {
            Assert.False(string.IsNullOrWhiteSpace(fence.Name));
            Assert.True(fence.Points.Count >= 3, $"{fence.Name} has an invalid polygon.");
        });
    }
}
