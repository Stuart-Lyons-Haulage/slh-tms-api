using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Slh.Tms.Api.Services;

internal static class OperationalGeofencePayload
{
    internal const int ExpectedFenceCount = 314;
    internal const string ExpectedJsonSha256 = "f4622cdfba2d35e20596398ab4fe6d02a9f3946f888ed9675ae7ff4491a329a9";

    private static readonly Lazy<string> Payload = new(DecodeAndValidate);

    internal static string Json => Payload.Value;

    private static string DecodeAndValidate()
    {
        var encoded = string.Concat(
            GeofencePayloadChunk01.Value,
            GeofencePayloadChunk02.Value,
            GeofencePayloadChunk03.Value,
            GeofencePayloadChunk04.Value,
            GeofencePayloadChunk05.Value,
            GeofencePayloadChunk06.Value,
            GeofencePayloadChunk07.Value,
            GeofencePayloadChunk08.Value,
            GeofencePayloadChunk09.Value,
            GeofencePayloadChunk10.Value);

        var compressed = Convert.FromBase64String(encoded);
        using var input = new MemoryStream(compressed);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        var bytes = output.ToArray();
        var checksum = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(checksum, ExpectedJsonSha256, StringComparison.Ordinal))
            throw new InvalidDataException($"Operational Falcon geofence payload checksum mismatch. Expected {ExpectedJsonSha256}, got {checksum}.");
        return Encoding.UTF8.GetString(bytes);
    }
}
