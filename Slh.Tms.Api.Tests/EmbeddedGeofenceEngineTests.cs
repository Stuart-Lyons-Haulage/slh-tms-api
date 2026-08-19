using System.Reflection;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class EmbeddedGeofenceEngineTests
{
    [Fact]
    public void Embedded_geofence_payload_reports_base64_shape()
    {
        var payloadType = typeof(EmbeddedGeofenceEngine).Assembly.GetType("Slh.Tms.Api.Services.GeofenceSeedPayload", throwOnError: true)!;
        var field = payloadType.GetField("GzipBase64", BindingFlags.NonPublic | BindingFlags.Static)!;
        var encoded = (string)field.GetRawConstantValue()!;
        var compact = new string(encoded.Where(c => !char.IsWhiteSpace(c)).ToArray());
        var invalid = compact
            .Select((character, index) => new { character, index })
            .Where(x => !(char.IsLetterOrDigit(x.character) || x.character is '+' or '/' or '='))
            .Take(20)
            .ToList();
        var diagnostic = $"Length={compact.Length}; Mod4={compact.Length % 4}; InvalidCount={invalid.Count}; Tail={compact[^Math.Min(12, compact.Length)..]}";
        Assert.True(false, diagnostic);
    }

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
