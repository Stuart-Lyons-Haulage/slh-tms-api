using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Live operational coverage for Ops Control.
/// DOT/RoadTech is the source of vehicle location and movement.
/// TachoMaster is the source of driver/card identity and legal-hours data.
/// When TachoMaster only confirms a few live vehicle/card pairings, the endpoint
/// also shows allocation-backed driver profile coverage so the dashboard can
/// distinguish confirmed live cards from planned-driver Tachomaster enrichment.
/// </summary>
[ApiController]
[Route("api/v1/operations")]
[Authorize]
public sealed class OperationsLiveCoverageController(
    TmsDbContext db,
    DotTrackingClient dotTracking,
    TachoMasterClient tachoMaster,
    DotTrackingOptions tracking,
    ILogger<OperationsLiveCoverageController> logger) : ControllerBase
{
    [HttpGet("live-coverage")]
    public async Task<IActionResult> LiveCoverage(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var operatingDate = UkOperatingDate(now);

        var dotRecords = await GetDotRecords(ct);
        var dotProvider = dotRecords.Provider;
        var latestDotEvent = dotRecords.Records.Count == 0
            ? (DateTimeOffset?)null
            : dotRecords.Records.Max(record => record.EventTimeUtc);

        var movingRecords = dotRecords.Records
            .Where(record => IsMoving(record))
            .GroupBy(record => NormaliseIdentifier(record.VehicleIdentifier))
            .Select(group => group.OrderByDescending(record => record.EventTimeUtc).First())
            .ToList();

        IReadOnlyDictionary<string, TachoVehicleDriverStatus> tachoStatuses = new Dictionary<string, TachoVehicleDriverStatus>();
        IReadOnlyList<TachoDriverProfile> tachoProfiles = [];
        string? tachoError = null;
        if (tachoMaster.IsConfigured)
        {
            try
            {
                tachoStatuses = await tachoMaster.GetCurrentDriverStatusesByVehicleAsync(operatingDate, ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                tachoError = exception.GetBaseException().Message;
                logger.LogWarning(exception, "Live operations coverage could not read TachoMaster vehicle/card identities.");
            }

            try
            {
                tachoProfiles = await tachoMaster.GetDriverProfilesAsync(ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Live operations coverage could not read the TachoMaster driver profile directory.");
            }
        }

        var vehicles = await LoadVehicles(ct);
        var vehicleAliases = BuildVehicleAliasLookup(vehicles);
        var allocations = await LoadAllocations(operatingDate, ct);
        var allocationsByVehicle = allocations
            .GroupBy(allocation => allocation.VehicleId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(allocation => LoadPriority(allocation.LoadStatus)).First());

        var tachoAliases = BuildTachoAliasLookup(tachoStatuses);
        var movingWithLiveTacho = 0;
        var movingWithLiveTachoMember = 0;
        var movingWithTachoProfile = 0;
        var movingWithPlannedAllocation = 0;
        var movingWithLiveCardOrName = 0;
        var movingWithoutTacho = new List<UnmatchedMovingVehicle>();
        var movingVehicleRows = new List<LiveVehicleCoverageRow>();

        foreach (var record in movingRecords.OrderBy(record => record.VehicleIdentifier))
        {
            var aliases = IdentifierAliases(record.VehicleIdentifier);
            var liveTachoStatus = aliases
                .Select(alias => tachoAliases.GetValueOrDefault(alias))
                .Where(value => value is not null)
                .OrderByDescending(value => value!.VehicleCode.Length)
                .FirstOrDefault();

            var vehicle = aliases
                .Select(alias => vehicleAliases.GetValueOrDefault(alias))
                .Where(value => value is not null)
                .OrderByDescending(value => value!.Registration.Length)
                .FirstOrDefault();

            AllocationLite? allocation = null;
            if (vehicle is not null)
                allocationsByVehicle.TryGetValue(vehicle.Id, out allocation);

            if (allocation is not null) movingWithPlannedAllocation++;

            var profile = liveTachoStatus is null
                ? MatchDriverProfile(allocation?.Driver, tachoProfiles)
                : null;

            if (liveTachoStatus is not null)
            {
                movingWithLiveTacho++;
                if (liveTachoStatus.MemberCode > 0) movingWithLiveTachoMember++;
            }
            else if (profile is not null)
            {
                movingWithTachoProfile++;
            }
            else
            {
                movingWithoutTacho.Add(new UnmatchedMovingVehicle(
                    record.VehicleIdentifier,
                    record.EventTimeUtc,
                    record.SpeedKph,
                    record.DriverName,
                    !string.IsNullOrWhiteSpace(record.DriverCardNumber),
                    allocation?.Driver.DisplayName,
                    allocation?.LoadReference,
                    string.IsNullOrWhiteSpace(record.DriverCardNumber)
                        ? allocation is null
                            ? "No live Tachomaster card and no planned TMS allocation matched this moving DOT vehicle."
                            : "No live Tachomaster card and the planned TMS driver did not match the Tachomaster driver directory."
                        : "DOT reports a driver card, but it did not match a Tachomaster member/vehicle identity."));
            }

            if (!string.IsNullOrWhiteSpace(record.DriverName) || !string.IsNullOrWhiteSpace(record.DriverCardNumber))
                movingWithLiveCardOrName++;

            var status = liveTachoStatus is not null
                ? liveTachoStatus.MemberCode > 0 ? "LiveTachoCardConfirmed" : "LiveIdentityOnly"
                : profile is not null
                    ? "AllocationTachoProfile"
                    : "Attention";

            movingVehicleRows.Add(new LiveVehicleCoverageRow(
                record.VehicleIdentifier,
                record.EventTimeUtc,
                record.SpeedKph,
                record.Latitude,
                record.Longitude,
                liveTachoStatus?.DriverName ?? profile?.DriverName,
                liveTachoStatus?.CardNumber ?? profile?.CardNumber,
                liveTachoStatus?.MemberCode > 0 || profile is not null,
                allocation?.Driver.DisplayName,
                allocation?.LoadReference,
                liveTachoStatus is not null ? "LiveTachoCard" : profile is not null ? "PlannedAllocationTachoProfile" : null,
                status));
        }

        var tachoDutyLike = tachoStatuses.Values.Count(status => status.DriveMinutes > 0 || status.WorkMinutes > 0 || status.AvailableMinutes > 0 || status.RestMinutes > 0);
        var tachoMemberIdentities = tachoStatuses.Values.Count(status => status.MemberCode > 0);
        var liveOnlyIdentities = Math.Max(0, tachoStatuses.Count - tachoMemberIdentities);
        var anyTachoCoverage = movingWithLiveTacho + movingWithTachoProfile;

        return Ok(new LiveCoverageResponse(
            now,
            operatingDate,
            new DotCoverage(
                tracking.IsConfigured,
                dotProvider,
                dotRecords.Records.Count,
                movingRecords.Count,
                latestDotEvent),
            new TachoCoverage(
                tachoMaster.IsConfigured,
                tachoError is null && tachoMaster.IsConfigured,
                tachoStatuses.Count,
                tachoDutyLike,
                tachoMemberIdentities,
                liveOnlyIdentities,
                tachoProfiles.Count,
                tachoError),
            new LiveCoverageSummary(
                movingRecords.Count,
                anyTachoCoverage,
                movingWithLiveTachoMember + movingWithTachoProfile,
                movingWithLiveCardOrName,
                Math.Max(0, movingRecords.Count - anyTachoCoverage),
                movingWithoutTacho.Count,
                movingWithLiveTacho,
                movingWithLiveTachoMember,
                movingWithTachoProfile,
                movingWithPlannedAllocation),
            movingVehicleRows,
            movingWithoutTacho));
    }

    private async Task<DotCoverageRecords> GetDotRecords(CancellationToken ct)
    {
        try
        {
            var items = await dotTracking.GetLatestVehicleEventsAsync(ct);
            var records = items.Select(DotTelemetryRecord.FromProvider).ToList();
            if (records.Count > 0) return new DotCoverageRecords("RoadTech Falcon live", records);
        }
        catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(exception, "Live DOT/RoadTech coverage check failed; using stored live-status fallback.");
        }

        try
        {
            var stored = await db.VehicleLiveStatuses.AsNoTracking().ToListAsync(ct);
            var records = stored.Select(status => new DotTelemetryRecord(
                $"stored-{status.Id}",
                status.VehicleIdentifier,
                status.LastEventTimeUtc,
                status.Latitude,
                status.Longitude,
                status.SpeedKph,
                status.IgnitionOn,
                status.IsMoving,
                status.LastKnownStatus ?? "Stored DOT position",
                "{}",
                status.CurrentDriverName,
                null)).ToList();
            return new DotCoverageRecords("RoadTech Falcon stored fallback", records);
        }
        catch (Exception exception) when (IsSchemaUnavailable(exception))
        {
            logger.LogWarning(exception, "Stored DOT/RoadTech live status is unavailable.");
            return new DotCoverageRecords("RoadTech Falcon unavailable", []);
        }
    }

    private async Task<List<VehicleLite>> LoadVehicles(CancellationToken ct)
    {
        try
        {
            return await db.Vehicles.AsNoTracking()
                .Where(vehicle => vehicle.Active)
                .Select(vehicle => new VehicleLite(vehicle.Id, vehicle.Registration, vehicle.FleetNumber, vehicle.Abbreviation))
                .ToListAsync(ct);
        }
        catch (Exception exception) when (IsSchemaUnavailable(exception))
        {
            logger.LogWarning(exception, "Vehicle master data is unavailable for live Tachomaster coverage.");
            return [];
        }
    }

    private async Task<List<AllocationLite>> LoadAllocations(DateOnly operatingDate, CancellationToken ct)
    {
        try
        {
            var loads = await db.Loads.AsNoTracking()
                .Where(load => load.PlanningDate == operatingDate && load.VehicleId != null && load.DriverId != null && load.Status != LoadStatus.Cancelled && load.Status != LoadStatus.Completed)
                .Select(load => new { load.VehicleId, load.DriverId, load.Reference, load.Status })
                .ToListAsync(ct);

            var driverIds = loads.Select(load => load.DriverId!.Value).Distinct().ToList();
            var drivers = await db.Drivers.AsNoTracking()
                .Where(driver => driverIds.Contains(driver.Id))
                .Select(driver => new DriverLite(driver.Id, driver.EmployeeNumber, driver.DisplayName, driver.TachoName))
                .ToDictionaryAsync(driver => driver.Id, ct);

            return loads
                .Where(load => load.VehicleId != null && load.DriverId != null && drivers.ContainsKey(load.DriverId.Value))
                .Select(load => new AllocationLite(
                    load.VehicleId!.Value,
                    load.Reference,
                    load.Status.ToString(),
                    drivers[load.DriverId!.Value]))
                .ToList();
        }
        catch (Exception exception) when (IsSchemaUnavailable(exception))
        {
            logger.LogWarning(exception, "Planning allocations are unavailable for allocation-backed Tachomaster coverage.");
            return [];
        }
    }

    private static Dictionary<string, VehicleLite> BuildVehicleAliasLookup(IEnumerable<VehicleLite> vehicles)
    {
        var lookup = new Dictionary<string, VehicleLite>(StringComparer.OrdinalIgnoreCase);
        foreach (var vehicle in vehicles)
        {
            foreach (var identifier in new[] { vehicle.Registration, vehicle.FleetNumber, vehicle.Abbreviation })
            {
                if (string.IsNullOrWhiteSpace(identifier)) continue;
                foreach (var alias in IdentifierAliases(identifier))
                {
                    if (!lookup.ContainsKey(alias)) lookup[alias] = vehicle;
                }
            }
        }
        return lookup;
    }

    private static Dictionary<string, TachoVehicleDriverStatus> BuildTachoAliasLookup(IReadOnlyDictionary<string, TachoVehicleDriverStatus> statuses)
    {
        var lookup = new Dictionary<string, TachoVehicleDriverStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (var status in statuses.Values)
        {
            foreach (var alias in IdentifierAliases(status.VehicleCode))
            {
                if (!lookup.ContainsKey(alias)) lookup[alias] = status;
            }
        }
        return lookup;
    }

    private static TachoDriverProfile? MatchDriverProfile(DriverLite? driver, IReadOnlyList<TachoDriverProfile> profiles)
    {
        if (driver is null || profiles.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(driver.EmployeeNumber))
        {
            var employee = profiles.FirstOrDefault(profile =>
                !string.IsNullOrWhiteSpace(profile.EmployeeNumber) &&
                string.Equals(profile.EmployeeNumber.Trim(), driver.EmployeeNumber.Trim(), StringComparison.OrdinalIgnoreCase));
            if (employee is not null) return employee;
        }

        var tachoName = NormalisePersonName(driver.TachoName);
        if (tachoName.Length > 0)
        {
            var byTachoName = profiles.FirstOrDefault(profile => NormalisePersonName(profile.DriverName) == tachoName);
            if (byTachoName is not null) return byTachoName;
        }

        var displayName = NormalisePersonName(driver.DisplayName);
        if (displayName.Length > 0)
            return profiles.FirstOrDefault(profile => NormalisePersonName(profile.DriverName) == displayName);

        return null;
    }

    private static bool IsMoving(DotTelemetryRecord record) =>
        record.IsMoving == true || record.SpeedKph.GetValueOrDefault() > 3;

    private static string NormaliseIdentifier(string value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static string NormalisePersonName(string? value) =>
        string.Join(' ', (value ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(word => new string(word.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray()))
            .Where(word => word.Length > 0)
            .OrderBy(word => word, StringComparer.Ordinal));

    private static IReadOnlyList<string> IdentifierAliases(string value)
    {
        var normalised = NormaliseIdentifier(value);
        if (string.IsNullOrWhiteSpace(normalised)) return [];

        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { normalised };
        for (var length = 3; length <= Math.Min(6, normalised.Length); length++)
            aliases.Add(normalised[^length..]);

        if (normalised.Length == 7 && char.IsLetter(normalised[0]) && char.IsLetter(normalised[1]) && char.IsDigit(normalised[2]) && char.IsDigit(normalised[3]))
            aliases.Add(normalised[2..]);

        if (normalised.EndsWith("H", StringComparison.OrdinalIgnoreCase) && normalised.Length > 4)
            aliases.Add(normalised[..^1]);

        return aliases.Where(alias => alias.Length >= 3).ToList();
    }

    private static int LoadPriority(string status) => status switch
    {
        nameof(LoadStatus.InProgress) => 4,
        nameof(LoadStatus.Dispatched) => 3,
        nameof(LoadStatus.Planned) => 2,
        nameof(LoadStatus.Draft) => 1,
        _ => 0
    };

    private static bool IsSchemaUnavailable(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        return exception is InvalidOperationException or DbUpdateException ||
            message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Cannot find the object", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase);
    }

    private static DateOnly UkOperatingDate(DateTimeOffset value)
    {
        try
        {
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, TimeZoneInfo.FindSystemTimeZoneById("Europe/London")).DateTime);
        }
        catch (TimeZoneNotFoundException)
        {
            return DateOnly.FromDateTime(value.UtcDateTime);
        }
    }

    private sealed record DotCoverageRecords(string Provider, IReadOnlyList<DotTelemetryRecord> Records);
    private sealed record VehicleLite(Guid Id, string Registration, string? FleetNumber, string? Abbreviation);
    private sealed record DriverLite(Guid Id, string EmployeeNumber, string DisplayName, string? TachoName);
    private sealed record AllocationLite(Guid VehicleId, string LoadReference, string LoadStatus, DriverLite Driver);
}

public sealed record LiveCoverageResponse(
    DateTimeOffset GeneratedAtUtc,
    DateOnly OperatingDate,
    DotCoverage Dot,
    TachoCoverage TachoMaster,
    LiveCoverageSummary Summary,
    IReadOnlyList<LiveVehicleCoverageRow> Vehicles,
    IReadOnlyList<UnmatchedMovingVehicle> UnmatchedMovingVehicles);

public sealed record DotCoverage(
    bool Configured,
    string Provider,
    int LiveVehicleCount,
    int MovingVehicleCount,
    DateTimeOffset? LatestEventUtc);

public sealed record TachoCoverage(
    bool Configured,
    bool Connected,
    int VehicleIdentityCount,
    int DutyRecordCount,
    int TachoMemberIdentityCount,
    int LiveOnlyIdentityCount,
    int DriverProfileCount,
    string? Error);

public sealed record LiveCoverageSummary(
    int MovingVehicles,
    int MovingWithTachoIdentity,
    int MovingWithTachoMemberMatch,
    int MovingWithLiveCardOrNameFromDot,
    int MovingWithoutTachoIdentity,
    int AttentionCount,
    int MovingWithLiveTachoIdentity,
    int MovingWithLiveTachoMemberMatch,
    int MovingWithTachoDirectoryProfile,
    int MovingWithPlannedAllocation);

public sealed record LiveVehicleCoverageRow(
    string VehicleIdentifier,
    DateTimeOffset LastEventUtc,
    decimal? SpeedKph,
    decimal? Latitude,
    decimal? Longitude,
    string? TachoDriverName,
    string? TachoCardNumber,
    bool TachoMemberMatched,
    string? PlannedDriverName,
    string? PlannedLoadReference,
    string? DriverSource,
    string Status);

public sealed record UnmatchedMovingVehicle(
    string VehicleIdentifier,
    DateTimeOffset LastEventUtc,
    decimal? SpeedKph,
    string? DotDriverName,
    bool DotDriverCardDetected,
    string? PlannedDriverName,
    string? PlannedLoadReference,
    string Reason);
