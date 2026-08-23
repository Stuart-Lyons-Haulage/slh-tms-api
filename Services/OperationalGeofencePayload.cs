using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Slh.Tms.Api.Services;

internal static class OperationalGeofencePayload
{
    internal const int ExpectedSourceRecordCount = 602;
    internal const int ExpectedFenceCount = 555;
    internal const int ExpectedProgressionFenceCount = 335;
    internal const string ExpectedJsonSha256 = "72a11cec497366fc873ea90d5369e1f02d4ffa8c07de9211532735adc41806d9";
    private static readonly Lazy<string> Payload = new(DecodeAndValidate);

    public static string Json => Payload.Value;

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

        byte[] compressed;
        try
        {
            compressed = Convert.FromBase64String(encoded);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("The embedded Falcon geofence source is not valid Base64.", exception);
        }

        using var input = new MemoryStream(compressed);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        try
        {
            gzip.CopyTo(output);
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException("The embedded Falcon geofence source failed gzip validation.", exception);
        }

        var jsonBytes = output.ToArray();
        var actualHash = Convert.ToHexString(SHA256.HashData(jsonBytes)).ToLowerInvariant();
        if (!string.Equals(actualHash, ExpectedJsonSha256, StringComparison.Ordinal))
            throw new InvalidDataException($"The embedded Falcon geofence source checksum is invalid. Expected {ExpectedJsonSha256}, got {actualHash}.");

        var json = Encoding.UTF8.GetString(jsonBytes);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() != ExpectedFenceCount)
            throw new InvalidDataException($"The embedded Falcon geofence source must contain exactly {ExpectedFenceCount} unique polygons.");

        return json;
    }
}
