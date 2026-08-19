using System.Reflection;
using Slh.Tms.Api.Services;
using Xunit;
using Xunit.Abstractions;

namespace Slh.Tms.Api.Tests;

public sealed class EmbeddedGeofenceEngineTests(ITestOutputHelper output)
{
    [Fact]
    public void Embedded_geofence_payload_is_valid_base64()
    {
        var payloadType = typeof(EmbeddedGeofenceEngine).Assembly.GetType("Slh.Tms.Api.Services.GeofenceSeedPayload", throwOnError: true)!;
        var field = payloadType.GetField("GzipBase64", BindingFlags.NonPublic | BindingFlags.Static)!;
        var encoded = (string)field.GetRawConstantValue()!;
        var invalid = encoded
            .Select((character, index) => new { character, index })
            .Where(x => !(char.IsLetterOrDigit(x.character) || x.character is '+' or '/' or '=' || char.IsWhiteSpace(x.character)))
            .Take(20)
            .ToList();

        output.WriteLine($"Length={encoded.Length}; Mod4={encoded.Length % 4}; InvalidCount={invalid.Count}");
        foreach (var item in invalid) output.WriteLine($"Invalid at {item.index}: U+{(int)item.character:X4} '{item.character}'");

        Assert.Empty(invalid);
        Assert.NotEqual(1, encoded.Where(c => !char.IsWhiteSpace(c)).Count() % 4);
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
