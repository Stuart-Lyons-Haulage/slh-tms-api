using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Slh.Tms.Api.Services;

internal static class GeofenceSeedPayload
{
    internal const int ApprovedGeofenceCount = 555;
    internal const int SourceRecordCount = 602;
    internal const string JsonSha256 = "72a11cec497366fc873ea90d5369e1f02d4ffa8c07de9211532735adc41806d9";
    private static readonly Lazy<string> Payload = new(DecodeAndValidate);

    public static string Json => Payload.Value;

    private static string DecodeAndValidate()
    {
        var encoded = string.Concat(
            GeofenceSeedPayloadParts.Part01,
            GeofenceSeedPayloadParts.Part02,
            GeofenceSeedPayloadParts.Part03,
            GeofenceSeedPayloadParts.Part04,
            GeofenceSeedPayloadParts.Part05,
            GeofenceSeedPayloadParts.Part06,
            GeofenceSeedPayloadParts.Part07,
            GeofenceSeedPayloadParts.Part08,
            GeofenceSeedPayloadParts.Part09,
            GeofenceSeedPayloadParts.Part10);
        byte[] compressed;
        try
        {
            compressed = Convert.FromBase64String(encoded);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("The embedded Falcon geofence payload is not valid Base64.", exception);
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
            throw new InvalidDataException("The embedded Falcon geofence payload failed gzip validation.", exception);
        }

        var jsonBytes = output.ToArray();
        var actualHash = Convert.ToHexString(SHA256.HashData(jsonBytes)).ToLowerInvariant();
        if (!string.Equals(actualHash, JsonSha256, StringComparison.Ordinal))
            throw new InvalidDataException($"The embedded Falcon geofence payload hash is invalid. Expected {JsonSha256}, got {actualHash}.");

        var json = Encoding.UTF8.GetString(jsonBytes);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() != ApprovedGeofenceCount)
            throw new InvalidDataException($"The embedded Falcon geofence payload must contain exactly {ApprovedGeofenceCount} unique geofences.");

        return json;
    }
}

internal static partial class GeofenceSeedPayloadParts { }
