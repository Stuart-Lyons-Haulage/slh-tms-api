using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class EmbeddedGeofenceEngineTests
{
    private static string EncodedPayload()
    {
        var payloadType = typeof(EmbeddedGeofenceEngine).Assembly.GetType("Slh.Tms.Api.Services.GeofenceSeedPayload", throwOnError: true)!;
        var field = payloadType.GetField("GzipBase64", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)field.GetRawConstantValue()!;
    }

    [Fact]
    public void Missing_single_padding_character_restores_all_53_falcon_geofences()
    {
        var compact = new string(EncodedPayload().Where(c => !char.IsWhiteSpace(c)).ToArray());
        Assert.Equal(3, compact.Length % 4);
        Assert.DoesNotContain(compact, c => !(char.IsLetterOrDigit(c) || c is '+' or '/' or '='));

        var bytes = Convert.FromBase64String(compact + "=");
        using var input = new MemoryStream(bytes);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        var json = Encoding.UTF8.GetString(output.ToArray());
        using var document = JsonDocument.Parse(json);

        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
        Assert.Equal(53, document.RootElement.GetArrayLength());
        Assert.All(document.RootElement.EnumerateArray(), record =>
        {
            Assert.False(string.IsNullOrWhiteSpace(record.GetProperty("name").GetString()));
            Assert.True(record.GetProperty("points").GetArrayLength() >= 3);
        });
    }
}
