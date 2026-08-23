using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class OperationalGeofencePayloadTests
{
    [Fact]
    public void Embedded_payload_loads_the_full_validated_Falcon_source()
    {
        var fences = EmbeddedGeofenceEngine.ApprovedFences;

        Assert.Equal(602, GeofenceSeedPayload.SourceRecordCount);
        Assert.Equal(555, GeofenceSeedPayload.ApprovedGeofenceCount);
        Assert.Equal(555, fences.Count);
        Assert.Equal(335, EmbeddedGeofenceEngine.ApprovedProgressionFenceCount);
        Assert.Equal("72a11cec497366fc873ea90d5369e1f02d4ffa8c07de9211532735adc41806d9", GeofenceSeedPayload.JsonSha256);
        Assert.All(fences, fence =>
        {
            Assert.False(string.IsNullOrWhiteSpace(fence.Name));
            Assert.True(fence.Points.Count >= 3, $"{fence.Name} has an invalid polygon.");
        });
    }
}
