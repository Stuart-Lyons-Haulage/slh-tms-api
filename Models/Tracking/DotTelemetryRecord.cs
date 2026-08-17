using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Slh.Tms.Api.Models.Tracking;

public sealed class RoadTechTelemetryItem
{
    public string VehCode { get; init; } = string.Empty;
    public long VehRtid { get; init; }
    public bool? Ign { get; init; }
    public bool? Moving { get; init; }
    public JsonElement? DataGps { get; init; }
    public JsonElement? DataCan { get; init; }
    public JsonElement? DataGaz { get; init; }

    // Falcon adds product/version-specific fields to current telemetry. Keep them
    // rather than silently discarding them so live driver identity can be read
    // when RoadTech supplies it alongside the vehicle position.
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Extra { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record DotTelemetryRecord(
    string ProviderEventId,
    string VehicleIdentifier,
    DateTimeOffset EventTimeUtc,
    decimal? Latitude,
    decimal? Longitude,
    decimal? SpeedKph,
    bool? IgnitionOn,
    bool? IsMoving,
    string? Status,
    string RawPayload,
    string? DriverName = null)
{
    public static DotTelemetryRecord FromProvider(RoadTechTelemetryItem item)
    {
        var rawPayload = JsonSerializer.Serialize(item);
        var gps = item.DataGps;
        var latitude = ReadDecimal(gps, "latitude", "lat");
        var longitude = ReadDecimal(gps, "longitude", "long", "lon", "lng");
        var speed = ReadDecimal(gps, "speedKph", "speed", "speedkmh", "kmh");
        var timestamp = ReadString(gps, "eventTimeUtc", "timestamp", "time", "datetime");
        var eventTime = DateTimeOffset.TryParse(timestamp, out var parsed) ? parsed.ToUniversalTime() : DateTimeOffset.UtcNow;
        var moving = item.Moving ?? ReadBoolean(gps, "isMoving", "moving");
        var ignitionOn = item.Ign
            ?? ReadBoolean(item.DataCan, "ignitionOn", "ignition", "ign", "engineOn", "engineRunning")
            ?? ReadBoolean(gps, "ignitionOn", "ignition", "ign", "engineOn", "engineRunning")
            ?? ReadBoolean(item.DataGaz, "ignitionOn", "ignition", "ign", "engineOn", "engineRunning");
        var driverName = ReadProviderDriverName(item);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawPayload)))[..24];
        return new DotTelemetryRecord(
            fingerprint,
            string.IsNullOrWhiteSpace(item.VehCode) ? item.VehRtid.ToString() : item.VehCode,
            eventTime,
            latitude,
            longitude,
            speed,
            ignitionOn,
            moving,
            latitude is null || longitude is null ? "GPS coordinates unavailable" : "Received",
            rawPayload,
            driverName);
    }

    private static string? ReadProviderDriverName(RoadTechTelemetryItem item)
    {
        // RoadTech/Falcon response names have varied between API versions.
        // Prefer explicit driver/card-holder labels and then inspect nested live
        // data objects. Do not infer a driver from unrelated text fields.
        var direct = ReadExtraString(item.Extra,
            "DriverName", "driverName", "CurrentDriver", "currentDriver",
            "CardHolder", "cardHolder", "Driver", "driver");
        if (!string.IsNullOrWhiteSpace(direct)) return CleanDriverName(direct);

        foreach (var source in new[] { item.DataGps, item.DataCan, item.DataGaz })
        {
            var nested = ReadString(source,
                "driverName", "currentDriver", "cardHolder", "driver");
            if (!string.IsNullOrWhiteSpace(nested)) return CleanDriverName(nested);
        }

        // Some Falcon payloads wrap driver identity in a top-level object.
        foreach (var key in new[] { "DriverInfo", "driverInfo", "DriverData", "driverData", "Tacho", "tacho" })
        {
            if (!item.Extra.TryGetValue(key, out var value) || value.ValueKind != JsonValueKind.Object) continue;
            var nested = ReadString(value, "name", "driverName", "currentDriver", "cardHolder", "driver");
            if (!string.IsNullOrWhiteSpace(nested)) return CleanDriverName(nested);
        }
        return null;
    }

    private static string? ReadExtraString(IReadOnlyDictionary<string, JsonElement> values, params string[] names)
    {
        foreach (var name in names)
        {
            var pair = values.FirstOrDefault(item => string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(pair.Key)) continue;
            var value = pair.Value;
            if (value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                return value.ToString();
            if (value.ValueKind == JsonValueKind.Object)
            {
                var nested = ReadString(value, "name", "displayName", "driverName", "fullName");
                if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        return null;
    }

    private static string? CleanDriverName(string? value)
    {
        var cleaned = (value ?? string.Empty).Trim();
        if (cleaned.Length == 0 || cleaned == "0" || cleaned.Equals("unknown", StringComparison.OrdinalIgnoreCase)) return null;
        return cleaned;
    }

    private static string? ReadString(JsonElement? source, params string[] names)
    {
        if (source is not { ValueKind: JsonValueKind.Object } objectValue) return null;
        foreach (var property in objectValue.EnumerateObject())
            if (names.Contains(property.Name, StringComparer.OrdinalIgnoreCase)) return property.Value.ToString();
        return null;
    }

    private static decimal? ReadDecimal(JsonElement? source, params string[] names) => decimal.TryParse(ReadString(source, names), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;
    private static bool? ReadBoolean(JsonElement? source, params string[] names)
    {
        var value = ReadString(source, names)?.Trim();
        if (bool.TryParse(value, out var parsed)) return parsed;
        if (value is "1" or "on" or "ON" or "running" or "RUNNING") return true;
        if (value is "0" or "off" or "OFF" or "stopped" or "STOPPED") return false;
        return null;
    }
}
