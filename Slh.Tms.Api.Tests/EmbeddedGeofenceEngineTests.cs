using System.IO.Compression;
using System.Reflection;
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
    public void Locate_corruption_in_padded_geofence_stream()
    {
        var compact = new string(EncodedPayload().Where(c => !char.IsWhiteSpace(c)).ToArray());
        var bytes = Convert.FromBase64String(compact + "=");
        using var input = new MemoryStream(bytes);
        using var output = new MemoryStream();
        try
        {
            using var gzip = new GZipStream(input, CompressionMode.Decompress, leaveOpen: true);
            var buffer = new byte[256];
            while (true)
            {
                var read = gzip.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                output.Write(buffer, 0, read);
            }
            Assert.Fail($"Unexpectedly decompressed full stream. compressedPosition={input.Position}; outputLength={output.Length}");
        }
        catch (InvalidDataException exception)
        {
            Assert.Fail($"Corruption: compressedBytes={bytes.Length}; compressedPosition={input.Position}; outputLength={output.Length}; message={exception.Message}");
        }
    }
}
