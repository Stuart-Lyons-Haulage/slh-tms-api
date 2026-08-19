using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Canonical identity correlation used by TachoMaster, DOT/Falcon, geofence and ETA execution.
/// Keeps the same TMS vehicle/driver identity through the operational evidence chain.
/// </summary>
public static class ExecutionIdentityResolver
{
    public static async Task<Dictionary<Guid, HashSet<string>>> VehicleAliasesAsync(
        TmsDbContext db,
        IReadOnlyCollection<Vehicle> vehicles,
        CancellationToken ct)
    {
        var result = vehicles.ToDictionary(
            vehicle => vehicle.Id,
            vehicle => new[] { vehicle.Registration, vehicle.FleetNumber, vehicle.Abbreviation }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => NormaliseVehicle(value!))
                .Where(value => value.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase));

        if (vehicles.Count == 0) return result;
        var ids = vehicles.Select(vehicle => vehicle.Id).ToList();
        try
        {
            var mappings = await db.IntegrationMappings.AsNoTracking()
                .Where(mapping => mapping.Active &&
                                  mapping.TmsEntityType == "Vehicle" &&
                                  ids.Contains(mapping.TmsEntityId) &&
                                  (mapping.Provider == "DotTracking" || mapping.Provider == "TachoMaster"))
                .Select(mapping => new { mapping.TmsEntityId, mapping.ExternalKey })
                .ToListAsync(ct);
            foreach (var mapping in mappings)
            {
                var alias = NormaliseVehicle(mapping.ExternalKey);
                if (alias.Length > 0 && result.TryGetValue(mapping.TmsEntityId, out var aliases)) aliases.Add(alias);
            }
        }
        catch (Exception exception) when (SchemaUnavailable(exception))
        {
            db.ChangeTracker.Clear();
        }
        return result;
    }

    public static VehicleLiveStatus? MatchLive(
        IReadOnlyCollection<string> aliases,
        IEnumerable<VehicleLiveStatus> statuses)
    {
        var keys = aliases.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return statuses
            .Where(status => keys.Contains(NormaliseVehicle(status.VehicleIdentifier)))
            .OrderByDescending(status => status.LastEventTimeUtc)
            .FirstOrDefault();
    }

    public static TachoVehicleDriverStatus? MatchTacho(
        IReadOnlyCollection<string> aliases,
        IReadOnlyDictionary<string, TachoVehicleDriverStatus> statuses)
    {
        foreach (var alias in aliases)
            if (statuses.TryGetValue(alias, out var status)) return status;

        // Some providers retain formatting in dictionary keys. Normalise once as a safe fallback.
        var normalised = statuses
            .GroupBy(pair => NormaliseVehicle(pair.Key), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.OrdinalIgnoreCase);
        foreach (var alias in aliases)
            if (normalised.TryGetValue(alias, out var status)) return status;
        return null;
    }

    public static DateTimeOffset? FirstMovement(
        IReadOnlyCollection<string> aliases,
        IEnumerable<VehicleTrackingEvent> events,
        DateTimeOffset? notBeforeUtc = null)
    {
        var keys = aliases.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return events
            .Where(item => keys.Contains(NormaliseVehicle(item.VehicleIdentifier)))
            .Where(item => notBeforeUtc is null || item.EventTimeUtc >= notBeforeUtc.Value)
            .Where(item => item.IsMoving == true || item.SpeedKph.GetValueOrDefault() > 2)
            .OrderBy(item => item.EventTimeUtc)
            .Select(item => (DateTimeOffset?)item.EventTimeUtc)
            .FirstOrDefault();
    }

    public static bool DriverMatches(Driver? allocatedDriver, TachoVehicleDriverStatus? tacho)
    {
        if (allocatedDriver is null || tacho is null) return false;
        if (!string.IsNullOrWhiteSpace(tacho.EmployeeNumber) &&
            string.Equals(NormaliseVehicle(tacho.EmployeeNumber), NormaliseVehicle(allocatedDriver.EmployeeNumber), StringComparison.OrdinalIgnoreCase))
            return true;

        var tachoName = NormalisePerson(tacho.DriverName);
        if (tachoName.Length == 0) return false;
        var plannedNames = new[] { allocatedDriver.TachoName, allocatedDriver.DisplayName }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(NormalisePerson)
            .Where(value => value.Length > 0);
        return plannedNames.Any(value => string.Equals(value, tachoName, StringComparison.OrdinalIgnoreCase));
    }

    public static string DriverEvidenceStatus(Driver? allocatedDriver, TachoVehicleDriverStatus? tacho)
    {
        if (allocatedDriver is null) return "NoPlannedDriver";
        if (tacho is null) return "NoTachoDuty";
        return DriverMatches(allocatedDriver, tacho) ? "Matched" : "Mismatch";
    }

    public static string NormaliseVehicle(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    public static string NormalisePerson(string? value) =>
        string.Join(' ', (value ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(word => new string(word.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray()))
            .Where(word => word.Length > 0)
            .OrderBy(word => word, StringComparer.Ordinal));

    private static bool SchemaUnavailable(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        return exception is InvalidOperationException or DbUpdateException ||
               message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Cannot find the object", StringComparison.OrdinalIgnoreCase);
    }
}
