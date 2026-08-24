using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class OperationalGeofencePayloadTests
{
    [Fact]
    public void Embedded_operational_payload_loads_all_approved_geofences()
    {
        var fences = EmbeddedGeofenceEngine.ApprovedFences;

        Assert.True(fences.Count >= OperationalGeofencePayload.ExpectedFenceCount);
        Assert.Contains(fences, fence => fence.Name == "Natures Way Foods Selsey");
        Assert.Contains(fences, fence => fence.Name == "Natures Way Foods Runcton");
        Assert.Contains(fences, fence => fence.Name == "Natures Way Foods Drayton");
        Assert.All(fences, fence =>
        {
            Assert.False(string.IsNullOrWhiteSpace(fence.Name));
            Assert.True(fence.Points.Count >= 3, $"{fence.Name} has an invalid polygon.");
        });
    }
}
