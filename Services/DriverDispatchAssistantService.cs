using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;

namespace Slh.Tms.Api.Services;

public sealed record DriverDispatchAssistantSuggestion(
    Guid DriverId,
    Guid? LoadId,
    string? LoadReference,
    Guid? VehicleId,
    string? VehicleRegistration,
    int Score,
    string Reason);

public static class DriverDispatchAssistantService
{
    private const string DriverDetailType = "masterdetail:driver";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    public static async Task<IReadOnlyDictionary<Guid, DriverDispatchAssistantSuggestion>> BuildAsync(
        TmsDbContext db,
        DateOnly planningDate,
        IReadOnlyCollection<Driver> drivers,
        IReadOnlyCollection<Load> targetLoads,
        IReadOnlyCollection<Load> history,
        IReadOnlyCollection<Vehicle> vehicles,
        IReadOnlyCollection<Trailer> trailers,
        IReadOnlySet<Guid> unavailableDriverIds,
        CancellationToken ct)
    {
        if (drivers.Count == 0) return new Dictionary<Guid, DriverDispatchAssistantSuggestion>();

        var vehicleById = vehicles.ToDictionary(vehicle => vehicle.Id);
        var trailerById = trailers.ToDictionary(trailer => trailer.Id);
        var preferredVehicles = await ReadPreferredVehiclesAsync(db, drivers, vehicleById, ct);
        var liveStatuses = await db.VehicleLiveStatuses.AsNoTracking().ToListAsync(ct);
        var liveByVehicleId = BuildLiveLookup(vehicles, liveStatuses);

        var contexts = new Dictionary<Guid, DriverContext>();
        foreach (var driver in drivers)
        {
            if (unavailableDriverIds.Contains(driver.Id)) continue;
            var previous = history.Where(load => load.DriverId == driver.Id)
                .OrderByDescending(load => load.PlanningDate)
                .ThenByDescending(load => load.CreatedAtUtc)
                .FirstOrDefault();
            var previousVehicle = previous?.VehicleId is Guid previousVehicleId && vehicleById.TryGetValue(previousVehicleId, out var foundPreviousVehicle)
                ? foundPreviousVehicle
                : null;
            var preferred = preferredVehicles.GetValueOrDefault(driver.Id);
            var suggestedVehicle = preferred?.Vehicle ?? previousVehicle;
            var liveVehicle = previousVehicle ?? suggestedVehicle;
            VehicleLiveStatus? live = null;
            if (liveVehicle is not null) liveByVehicleId.TryGetValue(liveVehicle.Id, out live);
            var finalStop = previous?.Stops.OrderBy(stop => stop.Sequence).LastOrDefault();
            var latitude = live?.Latitude ?? finalStop?.Latitude;
            var longitude = live?.Longitude ?? finalStop?.Longitude;
            var dayNumber = ConsecutiveWorkedDays(history, driver, planningDate) + 1;

            contexts[driver.Id] = new DriverContext(
                driver,
                dayNumber,
                latitude,
                longitude,
                live is not null ? "live Falcon position" : finalStop?.Name,
                suggestedVehicle,
                preferred?.ConfidencePercent,
                previousVehicle);
        }

        var availableLoads = targetLoads.Where(load => load.DriverId is null && load.Status != LoadStatus.Cancelled).ToList();
        var candidates = new List<Candidate>();
        foreach (var context in contexts.Values)
        {
            foreach (var load in availableLoads)
            {
                var scored = Score(context, load, trailerById);
                if (scored is not null) candidates.Add(scored);
            }
        }

        var usedDrivers = new HashSet<Guid>();
        var usedLoads = new HashSet<Guid>();
        var result = new Dictionary<Guid, DriverDispatchAssistantSuggestion>();
        foreach (var candidate in candidates.OrderByDescending(item => item.Score).ThenBy(item => item.Driver.DisplayName).ThenBy(item => item.Load.Reference))
        {
            if (!usedDrivers.Add(candidate.Driver.Id) || !usedLoads.Add(candidate.Load.Id)) continue;
            var context = contexts[candidate.Driver.Id];
            result[candidate.Driver.Id] = new DriverDispatchAssistantSuggestion(
                candidate.Driver.Id,
                candidate.Load.Id,
                RunDisplayLabel.For(candidate.Load),
                context.SuggestedVehicle?.Id,
                context.SuggestedVehicle?.Registration,
                candidate.Score,
                candidate.Reason);
        }

        foreach (var context in contexts.Values.Where(item => !result.ContainsKey(item.Driver.Id) && item.SuggestedVehicle is not null))
        {
            result[context.Driver.Id] = new DriverDispatchAssistantSuggestion(
                context.Driver.Id,
                null,
                null,
                context.SuggestedVehicle!.Id,
                context.SuggestedVehicle.Registration,
                0,
                VehicleReason(context));
        }

        return result;
    }

    private static Candidate? Score(DriverContext context, Load load, IReadOnlyDictionary<Guid, Trailer> trailers)
    {
        var first = load.Stops.OrderBy(stop => stop.Sequence).FirstOrDefault();
        var last = load.Stops.OrderBy(stop => stop.Sequence).LastOrDefault();
        var market = IsMarket(load);
        var doubleDeck = RequiresDoubleDeck(load, trailers);
        var code = context.Driver.Coding?.Trim();

        if (market && !HasSkill(context.Driver, "M")) return null;
        if (doubleDeck && !HasSkill(context.Driver, "DD")) return null;
        if (string.Equals(code, "3", StringComparison.OrdinalIgnoreCase) && (market || doubleDeck || load.Stops.Count > 3)) return null;

        var score = 100;
        var reasons = new List<string>();

        if (context.Driver.DriverType?.Contains("agency", StringComparison.OrdinalIgnoreCase) == true ||
            context.Driver.DriverGroup?.Contains("agency", StringComparison.OrdinalIgnoreCase) == true ||
            !string.IsNullOrWhiteSpace(context.Driver.AgencyName))
        {
            score -= 18;
            reasons.Add("agency held behind suitable employed/casual drivers");
        }
        else if (context.Driver.DriverType?.Contains("casual", StringComparison.OrdinalIgnoreCase) == true ||
                 context.Driver.DriverType?.Contains("zero", StringComparison.OrdinalIgnoreCase) == true)
        {
            score += 6;
            reasons.Add("casual/zero-hours employed driver");
        }
        else score += 12;

        if (context.Latitude is decimal driverLat && context.Longitude is decimal driverLon && first?.Latitude is decimal firstLat && first.Longitude is decimal firstLon)
        {
            var kilometres = HaversineKm((double)driverLat, (double)driverLon, (double)firstLat, (double)firstLon);
            score -= Math.Min(45, (int)Math.Round(kilometres / 18d));
            reasons.Add($"first collection ≈ {Math.Round(kilometres):0} km from {context.LocationLabel ?? "last known position"}");
        }

        var southbound = first?.Latitude is decimal firstLatitude && last?.Latitude is decimal lastLatitude && lastLatitude < firstLatitude - 0.35m;
        var driverNorth = context.Latitude >= 52.5m;
        if (context.DayNumber >= 5 && driverNorth && southbound)
        {
            score += 85;
            reasons.Add($"Day {context.DayNumber} and north: prioritises a southbound/homeward Run");
        }
        else if (context.DayNumber >= 5 && southbound)
        {
            score += 35;
            reasons.Add($"Day {context.DayNumber}: favours homeward work");
        }
        else if (driverNorth && southbound)
        {
            score += 28;
            reasons.Add("driver is north and the Run travels south");
        }

        if (string.Equals(code, "3", StringComparison.OrdinalIgnoreCase))
        {
            score += load.Stops.Count <= 2 ? 45 : 18;
            reasons.Add("Code 3: straightforward work");
        }
        else if (string.Equals(code, "2", StringComparison.OrdinalIgnoreCase))
        {
            score += load.Stops.Count <= 4 ? 18 : 4;
            reasons.Add("Code 2: established driver");
        }
        else if (string.Equals(code, "1", StringComparison.OrdinalIgnoreCase))
        {
            score += market || doubleDeck || load.Stops.Count >= 4 ? 22 : 8;
            reasons.Add("Code 1: unrestricted work");
        }

        if (market)
        {
            score += 20;
            reasons.Add("Market skill matched");
        }
        if (doubleDeck)
        {
            score += 20;
            reasons.Add("Double Deck skill matched");
        }

        var vehicleReason = VehicleReason(context);
        if (!string.IsNullOrWhiteSpace(vehicleReason)) reasons.Add(vehicleReason);
        return new Candidate(context.Driver, load, score, string.Join(" · ", reasons));
    }

    private static string VehicleReason(DriverContext context)
    {
        if (context.SuggestedVehicle is null) return string.Empty;
        if (context.PreferredVehicleConfidence is decimal confidence)
            return $"regular vehicle {context.SuggestedVehicle.Registration} ({confidence:0}% learned pairing)";
        if (context.PreviousVehicle?.Id == context.SuggestedVehicle.Id)
            return $"keep yesterday's vehicle {context.SuggestedVehicle.Registration}";
        return $"suggest {context.SuggestedVehicle.Registration}";
    }

    private static bool IsMarket(Load load)
    {
        var text = $"{load.Reference} {load.PlannerNotes} {string.Join(' ', load.Stops.Select(stop => stop.Name))}";
        return text.Contains("market", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequiresDoubleDeck(Load load, IReadOnlyDictionary<Guid, Trailer> trailers)
    {
        if (load.TrailerId is Guid trailerId && trailers.TryGetValue(trailerId, out var trailer) &&
            (trailer.Type?.Contains("double", StringComparison.OrdinalIgnoreCase) == true || trailer.Type?.Contains("deck", StringComparison.OrdinalIgnoreCase) == true))
            return true;
        var text = $"{load.Reference} {load.PlannerNotes}";
        return text.Contains("double deck", StringComparison.OrdinalIgnoreCase) || text.Contains("double-deck", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasSkill(Driver driver, string skill)
    {
        var tokens = (driver.Skills ?? string.Empty)
            .Split([',', ';', '/', '|', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.Any(token => string.Equals(token, skill, StringComparison.OrdinalIgnoreCase));
    }

    private static int ConsecutiveWorkedDays(IEnumerable<Load> history, Driver driver, DateOnly planningDate)
    {
        var dates = history.Where(load => load.DriverId == driver.Id && load.Status is LoadStatus.Dispatched or LoadStatus.InProgress or LoadStatus.Completed)
            .Select(load => load.PlanningDate).ToHashSet();
        var count = 0;
        for (var day = planningDate.AddDays(-1); dates.Contains(day) && count < 7; day = day.AddDays(-1)) count++;
        return count;
    }

    private static Dictionary<Guid, VehicleLiveStatus> BuildLiveLookup(IEnumerable<Vehicle> vehicles, IEnumerable<VehicleLiveStatus> statuses)
    {
        var result = new Dictionary<Guid, VehicleLiveStatus>();
        foreach (var vehicle in vehicles)
        {
            var keys = VehicleKeys(vehicle).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var latest = statuses.Where(status => keys.Contains(Normalise(status.VehicleIdentifier)))
                .OrderByDescending(status => status.LastEventTimeUtc).FirstOrDefault();
            if (latest is not null) result[vehicle.Id] = latest;
        }
        return result;
    }

    private static IEnumerable<string> VehicleKeys(Vehicle vehicle) => new[] { vehicle.Registration, vehicle.FleetNumber, vehicle.Abbreviation }
        .Where(value => !string.IsNullOrWhiteSpace(value)).Select(Normalise);

    private static async Task<Dictionary<Guid, PreferredVehicle>> ReadPreferredVehiclesAsync(
        TmsDbContext db,
        IReadOnlyCollection<Driver> drivers,
        IReadOnlyDictionary<Guid, Vehicle> vehicles,
        CancellationToken ct)
    {
        var byEmployee = drivers.ToDictionary(driver => Normalise(driver.EmployeeNumber), driver => driver, StringComparer.OrdinalIgnoreCase);
        var rows = await db.StagedImports.AsNoTracking()
            .Where(row => row.EntityType == DriverDetailType && row.Status == StagingStatus.Promoted)
            .OrderByDescending(row => row.ReviewedAtUtc ?? row.ReceivedAtUtc)
            .Take(5000).ToListAsync(ct);
        var result = new Dictionary<Guid, PreferredVehicle>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            try
            {
                using var document = JsonDocument.Parse(row.PayloadJson);
                var root = document.RootElement;
                var employee = Text(root, "employeeNumber");
                var key = Normalise(employee);
                if (key.Length == 0 || !seen.Add(key) || !byEmployee.TryGetValue(key, out var driver)) continue;
                if (!Guid.TryParse(Text(root, "preferredVehicleId"), out var vehicleId) || !vehicles.TryGetValue(vehicleId, out var vehicle)) continue;
                var confidence = Decimal(root, "preferredVehicleConfidencePercent");
                if (confidence is null || confidence < 55m) continue;
                result[driver.Id] = new PreferredVehicle(vehicle, confidence.Value);
            }
            catch (JsonException) { }
        }
        return result;
    }

    private static string? Text(JsonElement root, string property) => root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static decimal? Decimal(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var numeric)) return numeric;
        return value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), out numeric) ? numeric : null;
    }

    private static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double radius = 6371d;
        static double Radians(double value) => value * Math.PI / 180d;
        var dLat = Radians(lat2 - lat1);
        var dLon = Radians(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(Radians(lat1)) * Math.Cos(Radians(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return radius * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static string Normalise(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private sealed record PreferredVehicle(Vehicle Vehicle, decimal ConfidencePercent);
    private sealed record DriverContext(Driver Driver, int DayNumber, decimal? Latitude, decimal? Longitude, string? LocationLabel, Vehicle? SuggestedVehicle, decimal? PreferredVehicleConfidence, Vehicle? PreviousVehicle);
    private sealed record Candidate(Driver Driver, Load Load, int Score, string Reason);
}