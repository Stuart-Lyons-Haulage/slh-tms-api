using System.Net.Http.Headers;
using System.Text.Json;
using Slh.Tms.Api.Models.Integrations;

namespace Slh.Tms.Api.Services;

public sealed class FleetioClient(HttpClient httpClient, FleetioOptions options, ILogger<FleetioClient> logger)
{
    public bool IsConfigured => options.IsConfigured;
    public string[] MissingSettings => options.MissingSettings;

    public async Task<FleetioVehicleSummary> GetVehicleSummaryAsync(CancellationToken ct)
    {
        var vehicles = await GetVehiclesAsync(1, ct);
        return new FleetioVehicleSummary(true, vehicles.Count);
    }

    public async Task<IReadOnlyList<FleetioVehicle>> GetVehiclesAsync(int perPage, CancellationToken ct)
    {
        if (!IsConfigured) throw new InvalidOperationException("Fleetio runtime settings are incomplete.");
        using var request = CreateRequest($"vehicles?per_page={Math.Clamp(perPage, 1, 100)}");
        using var response = await httpClient.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Fleetio vehicles returned {(int)response.StatusCode} ({response.ReasonPhrase}). {body}", null, response.StatusCode);

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
            return document.RootElement.EnumerateArray().Select(ParseVehicle).Where(item => !string.IsNullOrWhiteSpace(item.Registration) || !string.IsNullOrWhiteSpace(item.Name)).ToList();
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "Fleetio vehicle response could not be parsed.");
            return [];
        }
    }

    private HttpRequestMessage CreateRequest(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{options.BaseUrl.TrimEnd('/')}/{path.TrimStart('/')}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", options.ApiKey);
        if (!string.IsNullOrWhiteSpace(options.AccountToken)) request.Headers.Add("Account-Token", options.AccountToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static FleetioVehicle ParseVehicle(JsonElement element)
    {
        var registration = FirstText(element, "license_plate", "licensePlate", "registration", "plate_number", "plateNumber");
        var name = FirstText(element, "name", "vehicle_name", "vehicleName");
        var vin = FirstText(element, "vin", "vin_sn", "vinSn");
        var fleetNumber = FirstText(element, "number", "vehicle_number", "vehicleNumber", "asset_number", "assetNumber");
        var status = FirstText(element, "vehicle_status_name", "status", "status_name", "statusName");
        return new FleetioVehicle(FirstText(element, "id") ?? string.Empty, registration, name, fleetNumber, vin, status);
    }

    private static string? FirstText(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
            if (TryFindProperty(element, name, out var value) && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
                return value.ToString().Trim();
        return null;
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
}

public sealed record FleetioVehicleSummary(bool Connected, int SampleVehicleCount);
public sealed record FleetioVehicle(string Id, string? Registration, string? Name, string? FleetNumber, string? Vin, string? Status);
