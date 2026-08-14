using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Secure, read-only RoadTech Falcon telemetry preview.
/// It does not create planning records or alter vehicle master data.
/// </summary>
[ApiController]
[Route("api/v1/tracking/dot")]
[Authorize(Policy = "TmsWrite")]
public sealed class DotTrackingController(
    DotTrackingClient trackingClient,
    TachoMasterClient tachoMasterClient,
    TmsDbContext db,
    DotTrackingTelemetryStore telemetryStore,
    ILogger<DotTrackingController> logger) : ControllerBase
{
    [HttpGet("telemetry")]
    [ProducesResponseType(typeof(DotTelemetryResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<DotTelemetryResponse>> GetCurrentTelemetry(CancellationToken cancellationToken)
    {
        try
        {
            var telemetry = await trackingClient.GetLatestVehicleEventsAsync(cancellationToken);
            var records = telemetry.Select(DotTelemetryRecord.FromProvider).ToList();
            if (records.Count == 0) return Ok(await StoredTelemetry("RoadTech Falcon · stored fallback", cancellationToken));
            await telemetryStore.PersistAsync(records, cancellationToken);

            return Ok(new DotTelemetryResponse(
                "RoadTech Falcon",
                DateTimeOffset.UtcNow,
                records.Count,
                records));
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "DOT Tracking configuration is not ready.");
            return Ok(await StoredTelemetry("RoadTech Falcon · stored fallback", cancellationToken));
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "RoadTech Falcon telemetry request failed.");
            return Ok(await StoredTelemetry("RoadTech Falcon · stored fallback", cancellationToken));
        }
    }

    private async Task<DotTelemetryResponse> StoredTelemetry(string provider, CancellationToken ct)
    {
        var statuses = await db.VehicleLiveStatuses.AsNoTracking().OrderBy(status => status.VehicleIdentifier).ToListAsync(ct);
        var records = statuses.Select(status => new DotTelemetryRecord($"stored-{status.Id}", status.VehicleIdentifier, status.LastEventTimeUtc,
            status.Latitude, status.Longitude, status.SpeedKph, status.IgnitionOn, status.IsMoving, status.LastKnownStatus ?? "Stored position", "{}")).ToList();
        return new DotTelemetryResponse(provider, DateTimeOffset.UtcNow, records.Count, records);
    }

    [HttpGet("history")]
    public async Task<IActionResult> GetHistory(
        [FromQuery] DateOnly? date,
        [FromQuery] string? vehicle,
        [FromQuery] int take = 1000,
        CancellationToken cancellationToken = default)
    {
        var selectedDate = date ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var from = new DateTimeOffset(selectedDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        var to = from.AddDays(1);
        var query = db.VehicleTrackingEvents.AsNoTracking()
            .Where(item => item.EventTimeUtc >= from && item.EventTimeUtc < to);
        if (!string.IsNullOrWhiteSpace(vehicle))
            query = query.Where(item => item.VehicleIdentifier == vehicle.Trim());

        var records = await query.OrderBy(item => item.EventTimeUtc).Take(Math.Clamp(take, 1, 5000)).Select(item => new
        {
            item.VehicleIdentifier, item.EventTimeUtc, item.Latitude, item.Longitude, item.SpeedKph, item.IsMoving, status = item.MatchStatus
        }).ToListAsync(cancellationToken);
        return Ok(new { provider = "RoadTech Falcon", date = selectedDate, recordCount = records.Count, records });
    }

    [HttpGet("fleet-status")]
    public async Task<ActionResult<FleetStatusResponse>> GetFleetStatus(CancellationToken cancellationToken)
    {
        var freshLiveStatuses = await TryGetProviderLiveStatuses(cancellationToken);

        // Keep tracking available while optional vehicle-master columns are being repaired.
        // Materialising Vehicle would select every mapped column and currently turns one
        // missing fuel/Fleetio column into a 500 for the whole live tracker.
        var vehicles = await db.Vehicles.AsNoTracking()
            .Where(vehicle => vehicle.Active)
            .OrderBy(vehicle => vehicle.Registration)
            .Select(vehicle => new FleetVehicleMaster(vehicle.Id, vehicle.Registration, vehicle.FleetNumber, vehicle.Abbreviation, vehicle.FleetioStatus, vehicle.FleetioVor, vehicle.FleetioPmiDueUtc, vehicle.FleetioMotDueUtc, vehicle.FleetioServiceStatus))
            .ToListAsync(cancellationToken);
        List<VehicleLiveStatus> liveStatuses = freshLiveStatuses;
        if (liveStatuses.Count == 0)
        {
            try
            {
                liveStatuses = await db.VehicleLiveStatuses.AsNoTracking().ToListAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is InvalidOperationException or DbUpdateException)
            {
                logger.LogWarning(ex, "Vehicle live status is unavailable; returning master-data fleet fallback.");
                return Ok(MasterFleetFallback(vehicles, "Master data"));
            }
        }
        var latestByIdentifier = liveStatuses.GroupBy(status => NormaliseIdentifier(status.VehicleIdentifier)).ToDictionary(group => group.Key, group => group.OrderByDescending(status => status.LastEventTimeUtc).First());
        var latestBySuffix = liveStatuses.SelectMany(status => IdentifierAliases(status.VehicleIdentifier).Select(alias => new { Status = status, Alias = alias }))
            .Where(item => item.Alias.Length >= 3)
            .GroupBy(item => item.Alias)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Status).OrderByDescending(status => status.LastEventTimeUtc).First());
        var now = DateTimeOffset.UtcNow;
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        IReadOnlyDictionary<string, TachoVehicleDriverStatus> tachoDrivers = new Dictionary<string, TachoVehicleDriverStatus>();
        try
        {
            tachoDrivers = await tachoMasterClient.GetCurrentDriverStatusesByVehicleAsync(today, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "TachoMaster driver lookup failed; continuing with allocation names.");
        }
        List<Load> assignments;
        try
        {
            assignments = await db.Loads.AsNoTracking().Include(load => load.Stops).Where(load => load.PlanningDate == today && load.VehicleId != null && load.Status != LoadStatus.Cancelled && load.Status != LoadStatus.Completed).ToListAsync(cancellationToken);
        }
        catch (Exception exception) when (IsSchemaUnavailable(exception))
        {
            assignments = [];
        }
        var driverIds = assignments.Where(load => load.DriverId != null).Select(load => load.DriverId!.Value).Distinct().ToList();
        var drivers = await LoadDriverIdentities(driverIds, cancellationToken);
        var matchedLiveIds = new HashSet<Guid>();
        var records = vehicles.Select(vehicle =>
        {
            var keys = new[] { vehicle.Registration, vehicle.FleetNumber, vehicle.Abbreviation }.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => NormaliseIdentifier(value!)).ToList();
            var aliases = keys.SelectMany(IdentifierAliases).Distinct().ToList();
            var exactLive = aliases.Select(key => latestByIdentifier.GetValueOrDefault(key)).Where(status => status is not null);
            var suffixLive = aliases.Select(key => latestBySuffix.GetValueOrDefault(key)).Where(status => status is not null);
            var live = exactLive.Concat(suffixLive).OrderByDescending(status => ObservedAt(status!, now)).FirstOrDefault();
            if (live is not null) matchedLiveIds.Add(live.Id);
            var observedAt = live is null ? (DateTimeOffset?)null : ObservedAt(live, now);
            var age = observedAt is null ? (TimeSpan?)null : now - observedAt;
            var assignment = assignments.Where(load => load.VehicleId == vehicle.Id).OrderByDescending(load => LoadPriority(load.Status)).FirstOrDefault();
            var allocatedDriver = assignment?.DriverId is Guid driverId ? drivers.GetValueOrDefault(driverId) : null;
            var tachoStatus = TachoDriverStatus(aliases, tachoDrivers);
            var tachoName = tachoStatus?.DriverName;
            var condition = DetermineCondition(live, !string.IsNullOrWhiteSpace(tachoName), now);
            var tachoDriver = MatchTachoDriver(tachoName, drivers.Values);
            var driverName = tachoDriver?.DisplayName ?? tachoName ?? allocatedDriver?.DisplayName;
            var driverSource = !string.IsNullOrWhiteSpace(tachoName) ? "TachoMaster" : allocatedDriver is not null ? "Allocation" : null;
            var driverMismatch = !string.IsNullOrWhiteSpace(tachoName) && allocatedDriver is not null && !SameDriver(tachoDriver, tachoName, allocatedDriver);
            var plannedDutyUtc = assignment?.Stops.Where(stop => stop.PlannedArrivalUtc != null).OrderBy(stop => stop.PlannedArrivalUtc).Select(stop => stop.PlannedArrivalUtc).FirstOrDefault();
            return new FleetVehicleStatus(vehicle.Id, vehicle.Registration, vehicle.FleetNumber, live?.VehicleIdentifier, condition, observedAt, live?.IgnitionOn, live?.IsMoving, live?.SpeedKph, LiveLatitude(live), LiveLongitude(live), age is null ? null : (int)Math.Max(0, age.Value.TotalMinutes), assignment?.Id, assignment?.Reference, assignment?.Status.ToString(), tachoDriver?.Id ?? allocatedDriver?.Id, driverName, tachoName, driverSource, allocatedDriver?.DisplayName, driverMismatch, plannedDutyUtc, tachoStatus, null, null, vehicle.FleetioStatus, vehicle.FleetioVor, vehicle.FleetioPmiDueUtc, vehicle.FleetioMotDueUtc, vehicle.FleetioServiceStatus);
        }).ToList();
        records.AddRange(liveStatuses.Where(status => !matchedLiveIds.Contains(status.Id)).OrderBy(status => status.VehicleIdentifier).Select(status =>
        {
            var observedAt = ObservedAt(status, now);
            var age = now - observedAt;
            var tachoStatus = TachoDriverStatus(IdentifierAliases(status.VehicleIdentifier), tachoDrivers);
            var tachoName = tachoStatus?.DriverName;
            var tachoDriver = MatchTachoDriver(tachoName, drivers.Values);
            return new FleetVehicleStatus(status.Id, status.VehicleIdentifier, null, status.VehicleIdentifier, DetermineCondition(status, !string.IsNullOrWhiteSpace(tachoName), now), observedAt, status.IgnitionOn, status.IsMoving, status.SpeedKph, LiveLatitude(status), LiveLongitude(status), (int)Math.Max(0, age.TotalMinutes), null, null, null, tachoDriver?.Id, tachoDriver?.DisplayName ?? tachoName, tachoName, tachoName is null ? null : "TachoMaster", null, false, null, tachoStatus, null, null, null, null, null, null, null);
        }));
        return Ok(new FleetStatusResponse("RoadTech Falcon + TachoMaster", now, records.Count, records.Count(record => record.Condition == "Moving"), records.Count(record => record.Condition != "Moving"), records));
    }

    private async Task<List<VehicleLiveStatus>> TryGetProviderLiveStatuses(CancellationToken cancellationToken)
    {
        try
        {
            var telemetry = await trackingClient.GetLatestVehicleEventsAsync(cancellationToken);
            var records = telemetry.Select(DotTelemetryRecord.FromProvider).ToList();
            if (records.Count == 0) return [];
            try
            {
                await telemetryStore.PersistAsync(records, cancellationToken);
            }
            catch (Exception ex) when (ex is InvalidOperationException or DbUpdateException)
            {
                logger.LogWarning(ex, "RoadTech Falcon cache write failed; using fresh provider telemetry for this response.");
            }
            var receivedAt = DateTimeOffset.UtcNow;
            return records.Select(record => new VehicleLiveStatus
            {
                Id = Guid.NewGuid(),
                VehicleIdentifier = record.VehicleIdentifier,
                LastEventTimeUtc = record.EventTimeUtc,
                LastReceivedAtUtc = receivedAt,
                Latitude = record.Latitude ?? 0,
                Longitude = record.Longitude ?? 0,
                SpeedKph = record.SpeedKph,
                IgnitionOn = record.IgnitionOn,
                IsMoving = record.IsMoving,
                LastKnownStatus = record.Status
            }).ToList();
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "RoadTech Falcon live refresh failed; using stored fleet status.");
            return [];
        }
    }

    private async Task<Dictionary<Guid, FleetDriverIdentity>> LoadDriverIdentities(IReadOnlyCollection<Guid> allocatedDriverIds, CancellationToken ct)
    {
        try
        {
            return await db.Drivers.AsNoTracking()
                .Where(driver => driver.Active || allocatedDriverIds.Contains(driver.Id))
                .Select(driver => new FleetDriverIdentity(driver.Id, driver.EmployeeNumber, driver.DisplayName, driver.TachoName))
                .ToDictionaryAsync(driver => driver.Id, ct);
        }
        catch (Exception exception) when (IsSchemaUnavailable(exception))
        {
            logger.LogWarning(exception, "Driver TachoName column is unavailable; live TachoMaster names will be shown without master-data correlation.");
            try
            {
                return await db.Drivers.AsNoTracking()
                    .Where(driver => driver.Active || allocatedDriverIds.Contains(driver.Id))
                    .Select(driver => new FleetDriverIdentity(driver.Id, driver.EmployeeNumber, driver.DisplayName, null))
                    .ToDictionaryAsync(driver => driver.Id, ct);
            }
            catch (Exception fallbackException) when (IsSchemaUnavailable(fallbackException))
            {
                logger.LogWarning(fallbackException, "Driver master data is unavailable; continuing with raw TachoMaster driver names.");
                return [];
            }
        }
    }

    private static FleetStatusResponse MasterFleetFallback(IReadOnlyList<FleetVehicleMaster> vehicles, string provider)
    {
        var now = DateTimeOffset.UtcNow;
        var records = vehicles.Select(vehicle => new FleetVehicleStatus(vehicle.Id, vehicle.Registration, vehicle.FleetNumber, null, "NotSignedOn", null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, false, null, null, null, null, vehicle.FleetioStatus, vehicle.FleetioVor, vehicle.FleetioPmiDueUtc, vehicle.FleetioMotDueUtc, vehicle.FleetioServiceStatus)).ToList();
        return new FleetStatusResponse(provider, now, records.Count, 0, records.Count, records);
    }

    private static bool IsSchemaUnavailable(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        return exception is InvalidOperationException or DbUpdateException || message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase) || message.Contains("Cannot find the object", StringComparison.OrdinalIgnoreCase) || message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormaliseIdentifier(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static IReadOnlyList<string> IdentifierAliases(string value)
    {
        var normalised = NormaliseIdentifier(value);
        if (string.IsNullOrWhiteSpace(normalised)) return [];
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { normalised };
        aliases.Add(IdentifierSuffix(normalised));
        if (normalised.Length > 3 && char.IsLetter(normalised[^1]) && normalised[^3..].All(char.IsLetter)) aliases.Add(normalised[^3..]);
        if (normalised.EndsWith("H", StringComparison.OrdinalIgnoreCase) && normalised.Length > 4) aliases.Add(normalised[..^1]);
        return aliases.Where(alias => !string.IsNullOrWhiteSpace(alias)).ToList();
    }
    private static string IdentifierSuffix(string value)
    {
        var normalised = NormaliseIdentifier(value);
        return normalised.Length <= 3 ? normalised : normalised[^3..];
    }
    private static int LoadPriority(LoadStatus status) => status switch { LoadStatus.InProgress => 4, LoadStatus.Dispatched => 3, LoadStatus.Planned => 2, LoadStatus.Draft => 1, _ => 0 };
    private static DateTimeOffset ObservedAt(VehicleLiveStatus live, DateTimeOffset now) => live.LastEventTimeUtc;
    private static decimal? LiveLatitude(VehicleLiveStatus? live) => live is null || (live.Latitude == 0 && live.Longitude == 0) ? null : live.Latitude;
    private static decimal? LiveLongitude(VehicleLiveStatus? live) => live is null || (live.Latitude == 0 && live.Longitude == 0) ? null : live.Longitude;
    public static string DetermineCondition(VehicleLiveStatus? live, bool hasDriverCard, DateTimeOffset now)
    {
        if (live is null) return "NotSignedOn";
        var observedAt = ObservedAt(live, now);
        if (observedAt.UtcDateTime.Date < now.UtcDateTime.Date) return "NotSignedOn";
        if (now - observedAt > TimeSpan.FromMinutes(30)) return "Stale";
        if (live.IsMoving == true || live.SpeedKph.GetValueOrDefault() > 3) return "Moving";
        if (live.IgnitionOn == true) return "Started";
        if (hasDriverCard) return "SignedOn";
        return "NotSignedOn";
    }

    private static TachoVehicleDriverStatus? TachoDriverStatus(
        IEnumerable<string> identifiers,
        IReadOnlyDictionary<string, TachoVehicleDriverStatus> drivers) =>
        identifiers.Select(NormaliseIdentifier).Select(identifier => drivers.GetValueOrDefault(identifier)).FirstOrDefault(status => status is not null);

    public static string NormalisePersonName(string? value) => string.Join(' ', (value ?? string.Empty)
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .Select(word => new string(word.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray()))
        .Where(word => word.Length > 0)
        .OrderBy(word => word, StringComparer.Ordinal));

    private static FleetDriverIdentity? MatchTachoDriver(string? tachoName, IEnumerable<FleetDriverIdentity> drivers)
    {
        var key = NormalisePersonName(tachoName);
        if (key.Length == 0) return null;
        return drivers.FirstOrDefault(driver => NormalisePersonName(driver.TachoName) == key)
            ?? drivers.FirstOrDefault(driver => NormalisePersonName(driver.DisplayName) == key);
    }

    private static bool SameDriver(FleetDriverIdentity? tachoDriver, string tachoName, FleetDriverIdentity allocatedDriver) =>
        tachoDriver is not null
            ? tachoDriver.Id == allocatedDriver.Id
            : NormalisePersonName(tachoName) == NormalisePersonName(allocatedDriver.TachoName)
              || NormalisePersonName(tachoName) == NormalisePersonName(allocatedDriver.DisplayName);
}

public sealed record DotTelemetryResponse(
    string Provider,
    DateTimeOffset RetrievedAtUtc,
    int RecordCount,
    IReadOnlyList<DotTelemetryRecord> Records);

public sealed record FleetStatusResponse(string Provider, DateTimeOffset RetrievedAtUtc, int VehicleCount, int ReadyCount, int AttentionCount, IReadOnlyList<FleetVehicleStatus> Vehicles);
public sealed record FleetVehicleStatus(Guid VehicleId, string Registration, string? FleetNumber, string? TrackingIdentifier, string Condition, DateTimeOffset? LastEventTimeUtc, bool? IgnitionOn, bool? IsMoving, decimal? SpeedKph, decimal? Latitude, decimal? Longitude, int? AgeMinutes, Guid? LoadId, string? LoadReference, string? LoadStatus, Guid? DriverId, string? DriverName, string? TachoName, string? DriverSource, string? AllocatedDriverName, bool DriverMismatch, DateTimeOffset? PlannedDutyUtc, TachoVehicleDriverStatus? Tacho, string? FleetioId, string? FleetioName, string? FleetioStatus, bool? FleetioVor, DateTimeOffset? FleetioPmiDueUtc, DateTimeOffset? FleetioMotDueUtc, string? FleetioServiceStatus);
public sealed record FleetVehicleMaster(Guid Id, string Registration, string? FleetNumber, string? Abbreviation, string? FleetioStatus, bool? FleetioVor, DateTimeOffset? FleetioPmiDueUtc, DateTimeOffset? FleetioMotDueUtc, string? FleetioServiceStatus);
public sealed record FleetDriverIdentity(Guid Id, string EmployeeNumber, string DisplayName, string? TachoName);
