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

        var vehiclesById = all
            .GroupBy(item => string.IsNullOrWhiteSpace(item.Id)
                ? $"{Normalise(item.Registration)}|{Normalise(item.Name)}"
                : item.Id,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        // Fleetio keeps preventative-maintenance and statutory renewal dates in
        // reminder resources rather than guaranteeing them on the Vehicle object.
        // Enrich the basic asset list from those feeds where available.
        try
        {
            var due = await GetDueDatesAsync(ct);
            vehiclesById = vehiclesById.Select(vehicle =>
            {
                if (!due.TryGetValue(vehicle.Id, out var dates)) return vehicle;
                return vehicle with
                {
                    PmiDueUtc = dates.PmiDueUtc ?? vehicle.PmiDueUtc,
                    MotDueUtc = dates.MotDueUtc ?? vehicle.MotDueUtc,
                    ServiceStatus = dates.ServiceStatus ?? vehicle.ServiceStatus
                };
            }).ToList();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Fleetio reminder dates were unavailable; returning asset data without due-date enrichment.");
        }

        return vehiclesById;
    }

    public async Task<IReadOnlyDictionary<string, FleetioDueDates>> GetDueDatesAsync(CancellationToken ct)
    {
        var byVehicle = new Dictionary<string, FleetioDueDates>(StringComparer.OrdinalIgnoreCase);
        var service = await ReadPagedRecordsAsync("service_reminders", ct);
        foreach (var item in service)
        {
            var vehicleId = FirstText(item, "vehicle_id") ?? NestedText(item, "vehicle", "id");
            if (string.IsNullOrWhiteSpace(vehicleId)) continue;
            var task = FirstText(item, "service_task_name") ?? NestedText(item, "service_task", "name") ?? string.Empty;
            var due = FirstDate(item, "next_due_at", "forecasted_next_due_at", "forecasted_primary_next_due_at");
            var status = FirstText(item, "service_reminder_status", "service_reminder_status_name");
            var current = byVehicle.GetValueOrDefault(vehicleId) ?? new FleetioDueDates(null, null, null);
            var isMot = task.Contains("MOT", StringComparison.OrdinalIgnoreCase);
            var isPmi = task.Contains("PMI", StringComparison.OrdinalIgnoreCase)
                || task.Contains("prevent", StringComparison.OrdinalIgnoreCase)
                || task.Contains("inspection", StringComparison.OrdinalIgnoreCase)
                || task.Contains("service", StringComparison.OrdinalIgnoreCase);
            byVehicle[vehicleId] = current with
            {
                MotDueUtc = isMot ? Earliest(current.MotDueUtc, due) : current.MotDueUtc,
                PmiDueUtc = !isMot && isPmi ? Earliest(current.PmiDueUtc, due) : current.PmiDueUtc,
                ServiceStatus = WorstStatus(current.ServiceStatus, status)
            };
        }

        var renewals = await ReadPagedRecordsAsync("vehicle_renewal_reminders", ct);
        foreach (var item in renewals)
        {
            var vehicleId = FirstText(item, "vehicle_id") ?? NestedText(item, "vehicle", "id");
            if (string.IsNullOrWhiteSpace(vehicleId)) continue;
            var type = FirstText(item, "vehicle_renewal_type_name", "renewal_type_name")
                ?? NestedText(item, "vehicle_renewal_type", "name")
                ?? NestedText(item, "renewal_type", "name")
                ?? string.Empty;
            if (!type.Contains("MOT", StringComparison.OrdinalIgnoreCase)) continue;
            var due = FirstDate(item, "next_due_at");
            var current = byVehicle.GetValueOrDefault(vehicleId) ?? new FleetioDueDates(null, null, null);
            byVehicle[vehicleId] = current with { MotDueUtc = Earliest(current.MotDueUtc, due) };
        }

        return byVehicle;
    }

    private async Task<List<JsonElement>> ReadPagedRecordsAsync(string resource, CancellationToken ct)
    {
        var result = new List<JsonElement>();
        string? cursor = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 0; page < 100; page++)
        {
            var path = $"{resource}?per_page=100";
            if (!string.IsNullOrWhiteSpace(cursor)) path += $"&start_cursor={Uri.EscapeDataString(cursor)}";
            using var request = CreateRequest(path);
            using var response = await httpClient.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Fleetio {resource} returned {(int)response.StatusCode} ({response.ReasonPhrase}). {body}", null, response.StatusCode);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            IEnumerable<JsonElement> records = root.ValueKind switch
            {
                JsonValueKind.Array => root.EnumerateArray(),
                JsonValueKind.Object when TryFindProperty(root, "records", out var a) && a.ValueKind == JsonValueKind.Array => a.EnumerateArray(),
                JsonValueKind.Object when TryFindProperty(root, "data", out var b) && b.ValueKind == JsonValueKind.Array => b.EnumerateArray(),
                JsonValueKind.Object when TryFindProperty(root, "results", out var c) && c.ValueKind == JsonValueKind.Array => c.EnumerateArray(),
                _ => []
            };
            result.AddRange(records.Select(record => record.Clone()));
            var next = root.ValueKind == JsonValueKind.Object ? FirstText(root, "next_cursor", "nextCursor") : null;
            if (string.IsNullOrWhiteSpace(next) || !seen.Add(next)) break;
            cursor = next;
        }
        return result;
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

    private static string? NestedText(JsonElement element, string objectName, string propertyName)
    {
        if (TryFindProperty(element, objectName, out var nested) && nested.ValueKind == JsonValueKind.Object)
            return FirstText(nested, propertyName);
        return null;
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

    private static DateTimeOffset? Earliest(DateTimeOffset? left, DateTimeOffset? right) =>
        left is null ? right : right is null ? left : left <= right ? left : right;

    private static string? WorstStatus(string? left, string? right)
    {
        static int Rank(string? value) => (value ?? string.Empty).ToLowerInvariant() switch
        {
            "overdue" => 4,
            "due_soon" => 3,
            "snoozed" => 2,
            "ok" => 1,
            _ => 0
        };
        return Rank(right) > Rank(left) ? right : left;
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
public sealed record FleetioDueDates(DateTimeOffset? PmiDueUtc, DateTimeOffset? MotDueUtc, string? ServiceStatus);
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
