using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;

namespace Slh.Tms.Api.Services;

public sealed record RunTachoEvidence(
    string Status,
    string? DriverName,
    string? VehicleCode,
    DateTimeOffset? SignOnUtc,
    DateTimeOffset? DutyEndUtc,
    int? DriveAvailableTodayMinutes,
    int? DriveAvailableWeekMinutes,
    int? WorkAvailableWeekMinutes,
    bool CardConfirmed,
    bool LegalHoursAvailable,
    string? EvidenceSource,
    string Explanation);

public sealed record RunTachoEvidenceResult(
    IReadOnlyDictionary<Guid, RunTachoEvidence> ByLoadId,
    bool Available,
    string? Warning,
    int ProviderVehicles = 0,
    int ProviderEvidenceRecords = 0,
    int TachoDutyRecords = 0,
    int FalconCardRecords = 0,
    IReadOnlyDictionary<string, int>? StatusCounts = null);

public static class RunTachoEvidenceResolver
{
    public static async Task<RunTachoEvidenceResult> ResolveAsync(
        TmsDbContext db,
        TachoMasterClient tachoMaster,
        IReadOnlyCollection<Load> loads,
        DateOnly planningDate,
        ILogger logger,
        CancellationToken ct)
    {
        if (loads.Count == 0)
            return new RunTachoEvidenceResult(new Dictionary<Guid, RunTachoEvidence>(), tachoMaster.IsConfigured, null);

        var drivers = await LoadDriversAsync(db, loads, ct);
        var vehicles = await LoadVehiclesAsync(db, loads, ct);
        var aliasesByVehicle = await ExecutionIdentityResolver.VehicleAliasesAsync(db, vehicles.Values, ct);

        IReadOnlyDictionary<string, IReadOnlyList<TachoVehicleDriverStatus>> statuses = new Dictionary<string, IReadOnlyList<TachoVehicleDriverStatus>>();
        var available = tachoMaster.IsConfigured;
        string? warning = null;

        if (!tachoMaster.IsConfigured)
        {
            warning = $"TachoMaster sign-on evidence is not configured: {string.Join(", ", tachoMaster.MissingSettings)}.";
        }
        else
        {
            try
            {
                statuses = await tachoMaster.GetLiveDriverStatusesByVehicleAsync(planningDate, ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                available = false;
                warning = "TachoMaster sign-on evidence is unavailable on this refresh.";
                logger.LogWarning(exception, "TachoMaster sign-on lookup failed for run evidence on {PlanningDate}.", planningDate);
            }
        }

        var providerEvidence = statuses.Values.SelectMany(items => items).ToList();
        var tachoDutyRecords = providerEvidence.Count(item => string.Equals(item.EvidenceSource, "TachoMasterDuty", StringComparison.OrdinalIgnoreCase));
        var falconCardRecords = providerEvidence.Count(item => string.Equals(item.EvidenceSource, "FalconLiveCard", StringComparison.OrdinalIgnoreCase));

        var result = new Dictionary<Guid, RunTachoEvidence>();
        foreach (var load in loads)
        {
            var driver = load.DriverId is Guid driverId && drivers.TryGetValue(driverId, out var matchedDriver) ? matchedDriver : null;
            var vehicle = load.VehicleId is Guid vehicleId && vehicles.TryGetValue(vehicleId, out var matchedVehicle) ? matchedVehicle : null;
            var aliases = vehicle is not null && aliasesByVehicle.TryGetValue(vehicle.Id, out var knownAliases)
                ? knownAliases
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var tacho = available && aliases.Count > 0
                ? ExecutionIdentityResolver.MatchLiveDriverIdentityForVehicle(aliases, driver, statuses)
                : null;
            var status = !available
                ? "Unavailable"
                : driver is null
                    ? "NoPlannedDriver"
                    : vehicle is null
                        ? "NoPlannedVehicle"
                        : EvidenceStatus(driver, tacho);

            result[load.Id] = new RunTachoEvidence(
                status,
                tacho?.DriverName,
                tacho?.VehicleCode,
                tacho?.DutyStartUtc,
                tacho?.DutyEndUtc,
                tacho?.DriveAvailableTodayMinutes,
                tacho?.DriveAvailableWeekMinutes,
                tacho?.WorkAvailableWeekMinutes,
                tacho is not null,
                tacho?.DriveAvailableTodayMinutes is not null,
                tacho?.EvidenceSource,
                Explanation(available, driver, vehicle, tacho));
        }

        var statusCounts = result.Values
            .GroupBy(item => item.Status, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        logger.LogInformation(
            "Run Tacho evidence for {PlanningDate}: providerVehicles={ProviderVehicles}, providerEvidence={ProviderEvidence}, TachoDuties={TachoDutyRecords}, FalconCards={FalconCardRecords}, runStatuses={RunStatuses}.",
            planningDate,
            statuses.Count,
            providerEvidence.Count,
            tachoDutyRecords,
            falconCardRecords,
            string.Join(", ", statusCounts.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}")));

        return new RunTachoEvidenceResult(
            result,
            available,
            warning,
            statuses.Count,
            providerEvidence.Count,
            tachoDutyRecords,
            falconCardRecords,
            statusCounts);
    }

    private static async Task<Dictionary<Guid, Driver>> LoadDriversAsync(TmsDbContext db, IReadOnlyCollection<Load> loads, CancellationToken ct)
    {
        var ids = loads.Where(load => load.DriverId is not null).Select(load => load.DriverId!.Value).Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, Driver>();

        var drivers = await db.Drivers.AsNoTracking().Where(driver => ids.Contains(driver.Id)).ToListAsync(ct);
        await MasterDetailStore.EnrichDriversAsync(db, drivers, ct);
        return drivers.ToDictionary(driver => driver.Id);
    }

    private static async Task<Dictionary<Guid, Vehicle>> LoadVehiclesAsync(TmsDbContext db, IReadOnlyCollection<Load> loads, CancellationToken ct)
    {
        var ids = loads.Where(load => load.VehicleId is not null).Select(load => load.VehicleId!.Value).Distinct().ToList();
        return ids.Count == 0
            ? new Dictionary<Guid, Vehicle>()
            : await db.Vehicles.AsNoTracking().Where(vehicle => ids.Contains(vehicle.Id)).ToDictionaryAsync(vehicle => vehicle.Id, ct);
    }

    private static string Explanation(bool available, Driver? driver, Vehicle? vehicle, TachoVehicleDriverStatus? tacho)
    {
        if (!available) return "TachoMaster could not be reached for this refresh.";
        if (driver is null) return "No planned driver is allocated to this run.";
        if (vehicle is null) return "No planned vehicle is allocated to this run.";
        if (tacho is null) return "No live driver card, Falcon driver identity or open TachoMaster duty was matched to the planned driver and vehicle.";
        if (!ExecutionIdentityResolver.DriverMatches(driver, tacho))
            return $"Live card/driver evidence is present for {tacho.DriverName}, but it does not match the planned driver.";
        if (tacho.EvidenceSource == "FalconLiveCard")
            return tacho.DriveAvailableTodayMinutes is null
                ? $"{tacho.DriverName} is confirmed by Falcon live card/driver evidence at {tacho.DutyStartUtc:O}; TachoMaster did not return legal-hours metrics."
                : $"{tacho.DriverName} is confirmed by Falcon live card/driver evidence at {tacho.DutyStartUtc:O}; TachoMaster profile metrics are attached for hours checks.";
        return $"{tacho.DriverName} signed on in TachoMaster at {tacho.DutyStartUtc:O}.";
    }

    private static string EvidenceStatus(Driver? driver, TachoVehicleDriverStatus? tacho)
    {
        if (driver is null) return "NoPlannedDriver";
        if (tacho is null) return "NoTachoDuty";
        if (!ExecutionIdentityResolver.DriverMatches(driver, tacho)) return "Mismatch";
        return tacho.EvidenceSource == "FalconLiveCard" ? "CardConfirmed" : "Matched";
    }
}
