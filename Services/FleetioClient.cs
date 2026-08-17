using System.Text.Json;
using Slh.Tms.Api.Models.Integrations;

namespace Slh.Tms.Api.Services;

public sealed class FleetioClient(HttpClient httpClient, FleetioOptions options, ILogger<FleetioClient> logger)
{
    public bool IsConfigured => options.IsConfigured;
    public string[] MissingSettings => options.MissingSettings;

    public async Task<FleetioVehicleSummary> GetVehicleSummaryAsync(CancellationToken ct)
    {
        var vehicles = await GetVehiclesAsync(100, ct);
        return new FleetioVehicleSummary(true, vehicles.Count);
    }

    public async Task<IReadOnlyList<FleetioVehicle>> GetVehiclesAsync(int perPage, CancellationToken ct)
    {
        if (!IsConfigured) throw new InvalidOperationException("Fleetio runtime settings are incomplete.");

        var pageSize = Math.Clamp(perPage, 2, 100);
        var all = new List<FleetioVehicle>();
        string? cursor = null;
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);

        for (var page = 0; page < 100; page++)
        {
            var path = $"vehicles?per_page={pageSize}";
            if (!string.IsNullOrWhiteSpace(cursor))
                path += $"&start_cursor={Uri.EscapeDataString(cursor)}";

            using var request = CreateRequest(path);
            using var response = await httpClient.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Fleetio vehicles returned {(int)response.StatusCode} ({response.ReasonPhrase}). {body}", null, response.StatusCode);

            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                IEnumerable<JsonElement> vehicles = root.ValueKind switch
                {
                    JsonValueKind.Array => root.EnumerateArray(),
                    JsonValueKind.Object when TryFindProperty(root, "records", out var records) && records.ValueKind == JsonValueKind.Array => records.EnumerateArray(),
                    JsonValueKind.Object when TryFindProperty(root, "vehicles", out var nestedVehicles) && nestedVehicles.ValueKind == JsonValueKind.Array => nestedVehicles.EnumerateArray(),
                    JsonValueKind.Object when TryFindProperty(root, "data", out var data) && data.ValueKind == JsonValueKind.Array => data.EnumerateArray(),
                    JsonValueKind.Object when TryFindProperty(root, "results", out var results) && results.ValueKind == JsonValueKind.Array => results.EnumerateArray(),
                    _ => []
                };

                var parsed = vehicles.Select(ParseVehicle)
                    .Where(item => !string.IsNullOrWhiteSpace(item.Registration) || !string.IsNullOrWhiteSpace(item.Name))
                    .ToList();
                all.AddRange(parsed);

                // Current Fleetio API uses cursor pagination and returns next_cursor in the body.
                // Older array responses do not expose a cursor; in that case this is the only page.
                var nextCursor = root.ValueKind == JsonValueKind.Object
                    ? FirstText(root, "next_cursor", "nextCursor")
                    : null;

                if (string.IsNullOrWhiteSpace(nextCursor)) break;
                if (!seenCursors.Add(nextCursor)) break;
                cursor = nextCursor;
            }
            catch (JsonException exception)
            {
                logger.LogWarning(exception, "Fleetio vehicle response could not be parsed.");
                break;
            }
        }

        return all
            .GroupBy(item => string.IsNullOrWhiteSpace(item.Id)
                ? $"{Normalise(item.Registration)}|{Normalise(item.Name)}"
                : item.Id,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private HttpRequestMessage CreateRequest(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{options.BaseUrl.TrimEnd('/')}/{path.TrimStart('/')}");
        request.Headers.TryAddWithoutValidation("Authorization", $"Token {options.ApiKey}");
        request.Headers.TryAddWithoutValidation("Account-Token", options.AccountToken);
        request.Headers.TryAddWithoutValidation("X-Api-Version", options.ApiVersion);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        return request;
    }

    private static FleetioVehicle ParseVehicle(JsonElement element)
    {
        var registration = FirstText(element, "license_plate", "licensePlate", "registration", "plate_number", "plateNumber");
        var name = FirstText(element, "name", "vehicle_name", "vehicleName");
        var vin = FirstText(element, "vin", "vin_sn", "vinSn", "serial_number", "serialNumber");
        var fleetNumber = FirstText(element, "number", "vehicle_number", "vehicleNumber", "asset_number", "assetNumber");
        var status = FirstText(element, "vehicle_status_name", "status", "status_name", "statusName");
        var type = FirstText(element, "vehicle_type_name", "vehicleTypeName", "type_name", "typeName", "type", "vehicle_type", "vehicleType");
        var vor = FirstBool(element, "out_of_service", "outOfService", "is_out_of_service", "isOutOfService", "vor", "is_vor");
        var pmi = FirstDate(element, "pmi_due", "pmiDue", "next_pmi", "nextPmi", "service_due", "serviceDue", "next_service_due");
        var mot = FirstDate(element, "mot_due", "motDue", "next_mot", "nextMot", "inspection_due", "inspectionDue", "annual_inspection_due");
        var serviceStatus = FirstText(element, "service_status", "serviceStatus", "maintenance_status", "maintenanceStatus");
        return new FleetioVehicle(FirstText(element, "id") ?? string.Empty, registration, name, fleetNumber, vin, status, type, vor, pmi, mot, serviceStatus);
    }

    private static string? FirstText(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
            if (TryFindProperty(element, name, out var value) && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
                return value.ToString().Trim();
        return null;
    }

    private static bool? FirstBool(JsonElement element, params string[] names)
    {
        var value = FirstText(element, names);
        return bool.TryParse(value, out var parsed) ? parsed : null;
    }

    private static DateTimeOffset? FirstDate(JsonElement element, params string[] names)
    {
        var value = FirstText(element, names);
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }

    private static bool TryFindProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
            if (property.Value.ValueKind == JsonValueKind.Object && TryFindProperty(property.Value, name, out value)) return true;
        }
        value = default;
        return false;
    }

    private static string Normalise(string? value) => new((value ?? string.Empty)
        .Where(char.IsLetterOrDigit)
        .Select(char.ToUpperInvariant)
        .ToArray());
}

public sealed record FleetioVehicleSummary(bool Connected, int SampleVehicleCount);
public sealed record FleetioVehicle(
    string Id,
    string? Registration,
    string? Name,
    string? FleetNumber,
    string? Vin,
    string? Status,
    string? Type,
    bool? Vor,
    DateTimeOffset? PmiDueUtc,
    DateTimeOffset? MotDueUtc,
    string? ServiceStatus);
