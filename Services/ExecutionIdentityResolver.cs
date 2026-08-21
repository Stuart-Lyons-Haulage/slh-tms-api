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
            vehicle => ExpandAliases(new[] { vehicle.Registration, vehicle.FleetNumber, vehicle.Abbreviation }));
 
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
                if (!result.TryGetValue(mapping.TmsEntityId, out var aliases)) continue;
                foreach (var alias in VehicleAliasVariants(mapping.ExternalKey)) aliases.Add(alias);
            }
        }
        catch (Exception exception) when (SchemaUnavailable(exception))
        {
            db.ChangeTracker.Clear();
        }
        return result;
    }
 
    /// <summary>
    /// Returns the same operational aliases used by the Fleet Status screen. This includes
    /// the complete identifier plus safe UK fleet/registration suffixes. Three-character
    /// abbreviations are retained because SLH master data and Falcon both use them.
    /// </summary>
    public static IReadOnlyCollection<string> VehicleAliasVariants(string? value)
    {
        var normalised = NormaliseVehicle(value);
        if (normalised.Length == 0) return [];
 
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { normalised };
        for (var length = 3; length <= Math.Min(6, normalised.Length); length++)
            aliases.Add(normalised[^length..]);
 
        if (normalised.Length == 7 &&
            char.IsLetter(normalised[0]) &&
            char.IsLetter(normalised[1]) &&
            char.IsDigit(normalised[2]) &&
            char.IsDigit(normalised[3]))
        {
            aliases.Add(normalised[2..]);
        }
 
        if (normalised.EndsWith("H", StringComparison.OrdinalIgnoreCase) && normalised.Length > 4)
            aliases.Add(normalised[..^1]);
 
        return aliases;
    }
 
    public static bool MatchesVehicleIdentifier(
        IReadOnlyCollection<string> aliases,
        string? providerIdentifier)
    {
        if (aliases.Count == 0 || string.IsNullOrWhiteSpace(providerIdentifier)) return false;
        var keys = ExpandAliases(aliases);
        return VehicleAliasVariants(providerIdentifier).Any(keys.Contains);
    }
 
    public static VehicleLiveStatus? MatchLive(
        IReadOnlyCollection<string> aliases,
        IEnumerable<VehicleLiveStatus> statuses)
    {
        return statuses
            .Where(status => MatchesVehicleIdentifier(aliases, status.VehicleIdentifier))
            .OrderByDescending(status => status.LastEventTimeUtc)
            .FirstOrDefault();
    }
 
    public static TachoVehicleDriverStatus? MatchTacho(
        IReadOnlyCollection<string> aliases,
        IReadOnlyDictionary<string, TachoVehicleDriverStatus> statuses)
    {
        var keys = ExpandAliases(aliases);
        return statuses
            .Select(pair => new
            {
                Status = pair.Value,
                MatchLength = VehicleAliasVariants(pair.Key)
                    .Where(keys.Contains)
                    .Select(alias => alias.Length)
                    .DefaultIfEmpty(0)
                    .Max()
            })
            // Falcon-only identity records intentionally carry no compliance metrics.
            // ETA calculations must only use a Tacho match when remaining-drive evidence
            // is genuinely available for that same driver, otherwise the ETA remains
            // unadjusted and explicitly reports Tacho as unavailable.
            .Where(item => item.MatchLength > 0 && item.Status.DriveAvailableTodayMinutes is not null)
            .OrderByDescending(item => item.MatchLength)
            .ThenByDescending(item => item.Status.DutyStartUtc)
            .Select(item => item.Status)
            .FirstOrDefault();
    }
 
    /// <summary>
    /// Picks the tacho duty for a specific planned driver on a vehicle, out of every duty that
    /// vehicle had that day (see TachoMasterClient.GetAllDriverStatusesByVehicleAsync). When a
    /// planned driver is supplied this deliberately fails closed if that driver has no matching
    /// Tacho duty: another driver's hours must never be attached to the load. Callers with no
    /// planned driver fall back to the most recent duty on the vehicle, matching MatchTacho.
    /// </summary>
    public static TachoVehicleDriverStatus? MatchTachoForDriver(
        IReadOnlyCollection<string> aliases,
        Driver? driver,
        IReadOnlyDictionary<string, IReadOnlyList<TachoVehicleDriverStatus>> statusesByVehicle)
    {
        var keys = ExpandAliases(aliases);
        var candidates = statusesByVehicle
            .Select(pair => new
            {
                MatchLength = VehicleAliasVariants(pair.Key).Where(keys.Contains).Select(alias => alias.Length).DefaultIfEmpty(0).Max(),
                pair.Value
            })
            .Where(item => item.MatchLength > 0)
            .SelectMany(item => item.Value.Select(status => (item.MatchLength, Status: status)))
            .Where(item => item.Status.DriveAvailableTodayMinutes is not null)
            .ToList();
 
        if (candidates.Count == 0) return null;
 
        if (driver is not null)
        {
            return candidates
                .Where(item => DriverMatches(driver, item.Status))
                .OrderByDescending(item => item.MatchLength)
                .ThenByDescending(item => item.Status.DutyStartUtc)
                .Select(item => item.Status)
                .FirstOrDefault();
        }
 
        return candidates
            .OrderByDescending(item => item.MatchLength)
            .ThenByDescending(item => item.Status.DutyStartUtc)
            .Select(item => item.Status)
            .First();
    }
 
    public static DateTimeOffset? FirstMovement(
        IReadOnlyCollection<string> aliases,
        IEnumerable<VehicleTrackingEvent> events,
        DateTimeOffset? notBeforeUtc = null)
    {
        return events
            .Where(item => MatchesVehicleIdentifier(aliases, item.VehicleIdentifier))
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
 
    private static HashSet<string> ExpandAliases(IEnumerable<string?> values)
    {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
            foreach (var alias in VehicleAliasVariants(value))
                aliases.Add(alias);
        return aliases;
    }
 
    private static bool SchemaUnavailable(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        return exception is InvalidOperationException or DbUpdateException ||
               message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Cannot find the object", StringComparison.OrdinalIgnoreCase);
    }
}
