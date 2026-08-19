using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/planning-intelligence"), Authorize]
public sealed class PlanningIntelligenceController(TmsDbContext db, TachoMasterClient tachoMaster, ILogger<PlanningIntelligenceController> logger) : ControllerBase
{
    [HttpGet("loads/{id:guid}")]
    public async Task<IActionResult> LoadIntelligence(Guid id, CancellationToken ct)
    {
        var load = await PlanningResilience.ReadLoadAsync(db, id, ct);
        if (load is null) return NotFound("Run not found.");
        try { await LoadCommercialStore.EnrichAsync(db, new[] { load }, ct); }
        catch (Exception ex) when (PlanningResilience.SchemaUnavailable(ex)) { db.ChangeTracker.Clear(); }

        var orderedStops = load.Stops.OrderBy(x => x.Sequence).ToList();
        var firstStop = orderedStops.FirstOrDefault();
        var lastStop = orderedStops.LastOrDefault();
        var firstPoint = firstStop?.Latitude is not null && firstStop.Longitude is not null
            ? (Lat: firstStop.Latitude.Value, Lon: firstStop.Longitude.Value)
            : ((decimal Lat, decimal Lon)?)null;
        var plannedSpanMinutes = PlannedSpanMinutes(orderedStops);
        var projectedShiftMinutes = plannedSpanMinutes is null ? null : plannedSpanMinutes + 15;
        var projectedShiftRisk = ShiftLengthRisk(projectedShiftMinutes);

        IReadOnlyDictionary<string, TachoVehicleDriverStatus> tacho = new Dictionary<string, TachoVehicleDriverStatus>();
        try { tacho = await tachoMaster.GetCurrentDriverStatusesByVehicleAsync(load.PlanningDate, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "TachoMaster planning enrichment unavailable for {LoadId}", id); }

        List<Driver> drivers;
        List<Vehicle> vehicles;
        List<VehicleLiveStatus> live;
        try { drivers = await db.Drivers.AsNoTracking().Where(x => x.Active).OrderBy(x => x.DisplayName).ToListAsync(ct); }
        catch (Exception ex) when (PlanningResilience.SchemaUnavailable(ex)) { db.ChangeTracker.Clear(); drivers = []; }
        try { vehicles = await db.Vehicles.AsNoTracking().Where(x => x.Active).OrderBy(x => x.Registration).ToListAsync(ct); }
        catch (Exception ex) when (PlanningResilience.SchemaUnavailable(ex)) { db.ChangeTracker.Clear(); vehicles = []; }
        try { live = await db.VehicleLiveStatuses.AsNoTracking().OrderByDescending(x => x.LastEventTimeUtc).Take(1000).ToListAsync(ct); }
        catch (Exception ex) when (PlanningResilience.SchemaUnavailable(ex)) { db.ChangeTracker.Clear(); live = []; }

        var previousLoads = (await PlanningResilience.ReadLoadsAsync(db, null, ct))
            .Where(x => x.PlanningDate < load.PlanningDate && x.PlanningDate >= load.PlanningDate.AddDays(-7) && x.Status != LoadStatus.Cancelled)
            .OrderByDescending(x => x.PlanningDate).ThenByDescending(x => x.CreatedAtUtc).Take(1000).ToList();

        var driverSuggestions = drivers.Select(driver =>
        {
            var tachoMatch = tacho.Values.FirstOrDefault(status => DriverMatches(driver, status));
            var previous = previousLoads.FirstOrDefault(x => x.DriverId == driver.Id);
            var final = previous?.Stops.OrderByDescending(x => x.Sequence).FirstOrDefault(x => x.Latitude is not null && x.Longitude is not null);
            decimal? reposition = firstPoint is not null && final?.Latitude is not null && final.Longitude is not null
                ? EstimatedRoadMiles((final.Latitude.Value, final.Longitude.Value), firstPoint.Value)
                : null;
            var daily = tachoMatch?.DriveAvailableTodayMinutes ?? driver.TachoDriveAvailableTodayMinutes;
            var weekly = tachoMatch?.DriveAvailableWeekMinutes ?? driver.TachoDriveAvailableWeekMinutes;
            var weeklyWork = tachoMatch?.WorkAvailableWeekMinutes ?? driver.TachoWorkAvailableWeekMinutes;
            var score = 100m - Math.Min(reposition ?? 40m, 40m);
            if (weekly is int weeklyMinutes && weeklyMinutes < 600) score -= 25;
            if (daily is int dailyMinutes && dailyMinutes < 240) score -= 35;
            if (projectedShiftMinutes is int shift && shift >= 13 * 60) score -= shift >= 15 * 60 ? 35 : 15;
            if (previous?.PlanningDate == load.PlanningDate.AddDays(-1)) score += 10;
            var availabilityRisk = ShiftRisk(daily, weekly);
            var combinedRisk = WorstRisk(availabilityRisk, projectedShiftRisk);
            return new
            {
                driver.Id,
                driver.DisplayName,
                driver.EmployeeNumber,
                driver.TachoName,
                dailyRemainingMinutes = daily,
                weeklyRemainingMinutes = weekly,
                weeklyWorkRemainingMinutes = weeklyWork,
                tachoVehicle = tachoMatch?.VehicleCode,
                previousRun = previous?.Reference,
                previousDate = previous?.PlanningDate,
                previousEnd = final?.Name,
                estimatedRepositionMiles = reposition,
                projectedShiftMinutes,
                projectedShiftRisk,
                score = Math.Round(score, 1),
                shiftRisk = combinedRisk,
                reason = DriverReason(previous, final, reposition, tachoMatch, projectedShiftMinutes)
            };
        }).OrderByDescending(x => x.score).ThenBy(x => x.estimatedRepositionMiles ?? 999m).Take(12).ToList();

        var vehicleSuggestions = vehicles.Select(vehicle =>
        {
            var keys = new[] { vehicle.Registration, vehicle.Abbreviation, vehicle.FleetNumber }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => Normalise(x!)).ToHashSet();
            var status = live.FirstOrDefault(x => keys.Contains(Normalise(x.VehicleIdentifier)));
            var previous = previousLoads.FirstOrDefault(x => x.VehicleId == vehicle.Id);
            var final = previous?.Stops.OrderByDescending(x => x.Sequence).FirstOrDefault(x => x.Latitude is not null && x.Longitude is not null);
            var start = status is not null && DateTimeOffset.UtcNow - status.LastEventTimeUtc < TimeSpan.FromHours(6)
                ? ((decimal Lat, decimal Lon)?)(status.Latitude, status.Longitude)
                : final?.Latitude is not null && final.Longitude is not null ? (final.Latitude.Value, final.Longitude.Value) : null;
            decimal? reposition = firstPoint is not null && start is not null ? EstimatedRoadMiles(start.Value, firstPoint.Value) : null;
            tacho.TryGetValue(keys.FirstOrDefault(k => tacho.ContainsKey(k)) ?? string.Empty, out var currentDuty);
            var score = 100m - Math.Min(reposition ?? 40m, 40m) + (status?.IsMoving == true ? 5 : 0);
            return new
            {
                vehicle.Id,
                vehicle.Registration,
                vehicle.FleetNumber,
                vehicle.Abbreviation,
                liveUpdatedAtUtc = status?.LastEventTimeUtc,
                status?.IsMoving,
                status?.LastKnownStatus,
                currentDriver = currentDuty?.DriverName,
                previousRun = previous?.Reference,
                previousEnd = final?.Name,
                estimatedEmptyMiles = reposition,
                score = Math.Round(score, 1),
                reason = status is not null ? "DOT live position used for the positioning estimate." : previous is not null ? "Previous run end used because no fresh DOT point is available." : "No recent positioning point is available."
            };
        }).OrderByDescending(x => x.score).ThenBy(x => x.estimatedEmptyMiles ?? 999m).Take(12).ToList();

        return Ok(new
        {
            load.Id,
            load.Reference,
            load.PlanningDate,
            firstStop = firstStop is null ? null : new { firstStop.Id, firstStop.Name, firstStop.Latitude, firstStop.Longitude, firstStop.PlannedArrivalUtc },
            lastStop = lastStop is null ? null : new { lastStop.Id, lastStop.Name, lastStop.Latitude, lastStop.Longitude, lastStop.PlannedArrivalUtc },
            projectedShiftMinutes,
            projectedShiftRisk,
            walkroundMinutes = 15,
            nightOutRequired = ReadNightOut(load.PlannerNotes),
            driverSuggestions,
            vehicleSuggestions,
            generatedAtUtc = DateTimeOffset.UtcNow
        });
    }

    [HttpPut("loads/{id:guid}/night-out"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> SetNightOut(Guid id, NightOutRequest request, CancellationToken ct)
    {
        var load = await PlanningResilience.ReadLoadAsync(db, id, ct);
        if (load is null) return NotFound("Run not found.");
        try { await LoadCommercialStore.EnrichAsync(db, new[] { load }, ct); }
        catch (Exception ex) when (PlanningResilience.SchemaUnavailable(ex)) { db.ChangeTracker.Clear(); }
        load.PlannerNotes = UpsertTag(load.PlannerNotes, "Night out", request.Required ? "Yes" : "No");

        var savedToRegister = false;
        try
        {
            var tracked = await db.Loads.Include(x => x.Stops).SingleOrDefaultAsync(x => x.Id == id, ct);
            if (tracked is not null)
            {
                tracked.PlannerNotes = load.PlannerNotes;
                await LoadCommercialStore.SaveAsync(db, tracked, new LoadCommercialValues(tracked.RevenueAmount, tracked.FuelSurchargeAmount, tracked.EstimatedCostAmount, tracked.ActualCostAmount,
                    tracked.EstimatedDistanceMiles, tracked.EmptyMiles, tracked.InvoiceStatus, tracked.CommercialNotes, tracked.PalletSpacesUsed, tracked.TotalPalletSpaces, tracked.CapacityType,
                    tracked.DepotSplits, tracked.TemperatureC, tracked.PlannerNotes), User.Identity?.Name, ct);
            }
            else savedToRegister = true;
        }
        catch (Exception ex) when (PlanningResilience.SchemaUnavailable(ex)) { db.ChangeTracker.Clear(); savedToRegister = true; }
        if (savedToRegister) await PlanningRegisterStore.SaveLoadAsync(db, load, User.Identity?.Name, ct);
        return Ok(new { load.Id, request.Required, load.PlannerNotes });
    }

    [HttpGet("night-outs")]
    public async Task<IActionResult> NightOutReport([FromQuery] DateOnly from, [FromQuery] DateOnly to, CancellationToken ct)
    {
        var loads = (await PlanningResilience.ReadLoadsAsync(db, null, ct))
            .Where(x => x.PlanningDate >= from && x.PlanningDate <= to && x.DriverId != null && x.Status != LoadStatus.Cancelled).ToList();
        try { await LoadCommercialStore.EnrichAsync(db, loads, ct); }
        catch (Exception ex) when (PlanningResilience.SchemaUnavailable(ex)) { db.ChangeTracker.Clear(); }
        var driverIds = loads.Where(x => x.DriverId != null).Select(x => x.DriverId!.Value).Distinct().ToList();
        var drivers = new Dictionary<Guid, Driver>();
        try { drivers = await db.Drivers.AsNoTracking().Where(x => driverIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct); }
        catch (Exception ex) when (PlanningResilience.SchemaUnavailable(ex)) { db.ChangeTracker.Clear(); }
        var vehicleIds = loads.Where(x => x.VehicleId != null).Select(x => x.VehicleId!.Value).Distinct().ToList();
        var vehicles = new Dictionary<Guid, Vehicle>();
        try { vehicles = await db.Vehicles.AsNoTracking().Where(x => vehicleIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct); }
        catch (Exception ex) when (PlanningResilience.SchemaUnavailable(ex)) { db.ChangeTracker.Clear(); }
        var rows = loads.Where(x => ReadNightOut(x.PlannerNotes) is not null).Select(load =>
        {
            drivers.TryGetValue(load.DriverId!.Value, out var driver);
            var vehicle = load.VehicleId is Guid vehicleId && vehicles.TryGetValue(vehicleId, out var matchedVehicle) ? matchedVehicle : null;
            var requested = ReadNightOut(load.PlannerNotes) == true;
            var final = load.Stops.OrderByDescending(x => x.Sequence).FirstOrDefault();
            return new
            {
                load.Id,
                load.Reference,
                load.PlanningDate,
                driverId = load.DriverId,
                driverName = driver?.DisplayName,
                vehicle = vehicle?.Registration,
                requested,
                finalStop = final?.Name,
                finalLatitude = final?.Latitude,
                finalLongitude = final?.Longitude,
                status = requested ? "Planner confirmed - validate against DOT/Tacho where available" : "No night out"
            };
        }).OrderBy(x => x.PlanningDate).ThenBy(x => x.driverName).ToList();
        return Ok(new { from, to, rows, counts = rows.Where(x => x.requested).GroupBy(x => x.driverName ?? "Unknown").Select(g => new { driver = g.Key, nights = g.Count() }).OrderByDescending(x => x.nights) });
    }

    private static int? PlannedSpanMinutes(IReadOnlyList<LoadStop> stops)
    {
        var timed = stops.Where(x => x.PlannedArrivalUtc is not null).OrderBy(x => x.Sequence).ToList();
        if (timed.Count < 2) return null;
        var first = timed.First().PlannedArrivalUtc!.Value;
        var last = timed.Last().PlannedArrivalUtc!.Value;
        if (last < first) return null;
        return (int)Math.Ceiling((last - first).TotalMinutes);
    }

    private static string ShiftLengthRisk(int? projectedMinutes)
    {
        if (projectedMinutes is null) return "Unknown";
        if (projectedMinutes >= 15 * 60) return "Red";
        if (projectedMinutes >= 13 * 60) return "Amber";
        return "Green";
    }

    private static string ShiftRisk(int? today, int? week)
    {
        if (today is int d && d < 240 || week is int w && w < 600) return "Red";
        if (today is int da && da < 360 || week is int we && we < 900) return "Amber";
        return today is null && week is null ? "Unknown" : "Green";
    }

    private static string WorstRisk(string a, string b)
    {
        static int Rank(string risk) => risk switch { "Red" => 3, "Amber" => 2, "Green" => 1, _ => 0 };
        return Rank(a) >= Rank(b) ? a : b;
    }

    private static string DriverReason(Load? previous, LoadStop? final, decimal? miles, TachoVehicleDriverStatus? tacho, int? projectedShiftMinutes)
    {
        var parts = new List<string>();
        if (previous is not null) parts.Add($"Last planned on {previous.Reference}{(final is null ? string.Empty : $" ending at {final.Name}")}");
        if (miles is not null) parts.Add($"about {miles:0} reposition miles to the first stop");
        if (tacho is not null) parts.Add($"TachoMaster live duty is in vehicle {tacho.VehicleCode}");
        if (projectedShiftMinutes is int shift) parts.Add($"planned run span plus 15-minute walkround is about {shift / 60}h {shift % 60:00}m");
        return parts.Count == 0 ? "No recent run position or TachoMaster duty was matched." : string.Join("; ", parts) + ".";
    }

    private static bool DriverMatches(Driver driver, TachoVehicleDriverStatus status)
    {
        var names = new[] { driver.DisplayName, driver.TachoName, driver.EmployeeNumber }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(Normalise).ToHashSet();
        return names.Contains(Normalise(status.DriverName)) || (!string.IsNullOrWhiteSpace(status.EmployeeNumber) && names.Contains(Normalise(status.EmployeeNumber)));
    }

    private static decimal EstimatedRoadMiles((decimal Lat, decimal Lon) a, (decimal Lat, decimal Lon) b)
    {
        const double radiusMiles = 3958.7613;
        var lat1 = DegreesToRadians((double)a.Lat); var lat2 = DegreesToRadians((double)b.Lat);
        var dLat = lat2 - lat1; var dLon = DegreesToRadians((double)(b.Lon - a.Lon));
        var h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var crow = 2 * radiusMiles * Math.Asin(Math.Min(1, Math.Sqrt(h)));
        return Math.Round((decimal)(crow * 1.18), 1);
    }
    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;
    private static string Normalise(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static bool? ReadNightOut(string? notes)
    {
        var value = (notes ?? string.Empty).Split('·').Select(x => x.Trim()).FirstOrDefault(x => x.StartsWith("Night out:", StringComparison.OrdinalIgnoreCase));
        if (value is null) return null;
        return value.EndsWith("Yes", StringComparison.OrdinalIgnoreCase) ? true : value.EndsWith("No", StringComparison.OrdinalIgnoreCase) ? false : null;
    }
    private static string UpsertTag(string? notes, string tag, string value)
    {
        var parts = (notes ?? string.Empty).Split('·', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(x => !x.StartsWith(tag + ":", StringComparison.OrdinalIgnoreCase)).ToList();
        parts.Add($"{tag}: {value}");
        return string.Join(" · ", parts);
    }
}

public sealed record NightOutRequest(bool Required);
