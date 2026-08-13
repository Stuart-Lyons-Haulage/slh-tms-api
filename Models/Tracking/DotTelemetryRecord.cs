using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
    string RawPayload)
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
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawPayload)))[..24];
        return new DotTelemetryRecord(fingerprint, string.IsNullOrWhiteSpace(item.VehCode) ? item.VehRtid.ToString() : item.VehCode, eventTime, latitude, longitude, speed, ignitionOn, moving, latitude is null || longitude is null ? "GPS coordinates unavailable" : "Received", rawPayload);
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
