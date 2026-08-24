using System.Text.Json.Nodes;

namespace Slh.Tms.Api.Services;

internal static class GeofenceSeedPayload
{
    // Canonical operational geofence payload rebuilt from the 15 Falcon category
    // exports supplied by SLH on 19 August 2026. Selsey and Drayton are supplemental
    // operational fences because they are absent from that export. Runcton already
    // exists in the approved payload as "Vitacress Runcton", so it must not be
    // duplicated with an overlapping Nature's Way polygon.
    private static readonly Lazy<string> Payload = new(Build);

    internal static string Json => Payload.Value;

    private static string Build()
    {
        var root = JsonNode.Parse(OperationalGeofencePayload.Json)?.AsArray()
            ?? throw new InvalidDataException("Operational geofence payload was not a JSON array.");

        AddIfMissing(root, Fence(
            "Natures Way Foods Selsey",
            new[]
            {
                Point(-0.78175, 50.74216), Point(-0.77663, 50.74216),
                Point(-0.77663, 50.74540), Point(-0.78175, 50.74540)
            }));
        AddIfMissing(root, Fence(
            "Natures Way Foods Drayton",
            new[]
            {
                Point(-0.74706, 50.82140), Point(-0.74194, 50.82140),
                Point(-0.74194, 50.82464), Point(-0.74706, 50.82464)
            }));

        return root.ToJsonString();
    }

    private static JsonObject Fence(string name, JsonObject[] points) => new()
    {
        ["name"] = name,
        ["category"] = "SLH supplemental",
        ["category_max_wait_time"] = null,
        ["max_wait_time"] = null,
        ["pending_entry_minutes"] = 0,
        ["pending_exit_minutes"] = 0,
        ["site_no"] = null,
        ["points"] = new JsonArray(points.Cast<JsonNode?>().ToArray())
    };

    private static JsonObject Point(double longitude, double latitude) => new()
    {
        ["longitude"] = longitude,
        ["latitude"] = latitude
    };

    private static void AddIfMissing(JsonArray root, JsonObject fence)
    {
        var name = fence["name"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(name)) return;
        var key = Normalize(name);
        var exists = root.OfType<JsonObject>()
            .Any(item => Normalize(item["name"]?.GetValue<string>()) == key);
        if (!exists) root.Add(fence);
    }

    private static string Normalize(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
