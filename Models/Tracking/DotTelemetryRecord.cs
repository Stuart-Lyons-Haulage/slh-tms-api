using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Slh.Tms.Api.Models.Tracking;

public sealed class RoadTechTelemetryItem
{
    public string VehCode { get; init; } = string.Empty;
    public long VehRtid { get; init; }
    public JsonElement? DataGps { get; init; }
    public JsonElement? DataCan { get; init; }
    public JsonElement? DataGaz { get; init; }
}

public sealed record DotTelemetryRecord(
    string ProviderEventId,
    string VehicleIdentifier,
    DateTimeOffset EventTimeUtc,
    decimal? Latitude,
    decimal? Longitude,
    decimal? SpeedKph,
    bool? IsMoving,
    string? Status,
    string RawPayload)
{
    public static DotTelemetryRecord FromProvider(RoadTechTelemetryItem item)
    {
        var rawPayload = JsonSerializer.Serialize(item);
        var gps = item.DataGps;
        var latitude = ReadDecimal(gps, "latitude", "lat");
        var longitude = ReadDecimal(gps, "longitude", "lon", "lng");
        var speed = ReadDecimal(gps, "speedKph", "speed", "speedkmh");
        var timestamp = ReadString(gps, "eventTimeUtc", "timestamp", "time", "datetime");
        var eventTime = DateTimeOffset.TryParse(timestamp, out var parsed) ? parsed.ToUniversalTime() : DateTimeOffset.UtcNow;
        var moving = ReadBoolean(gps, "isMoving", "moving");
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawPayload)))[..24];
        return new DotTelemetryRecord(fingerprint, string.IsNullOrWhiteSpace(item.VehCode) ? item.VehRtid.ToString() : item.VehCode, eventTime, latitude, longitude, speed, moving, latitude is null || longitude is null ? "GPS coordinates unavailable" : "Received", rawPayload);
    }

    private static string? ReadString(JsonElement? source, params string[] names)
    {
        if (source is not { ValueKind: JsonValueKind.Object } objectValue) return null;
        foreach (var property in objectValue.EnumerateObject())
            if (names.Contains(property.Name, StringComparer.OrdinalIgnoreCase)) return property.Value.ToString();
        return null;
    }

    private static decimal? ReadDecimal(JsonElement? source, params string[] names) => decimal.TryParse(ReadString(source, names), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;
    private static bool? ReadBoolean(JsonElement? source, params string[] names) => bool.TryParse(ReadString(source, names), out var value) ? value : null;
}
