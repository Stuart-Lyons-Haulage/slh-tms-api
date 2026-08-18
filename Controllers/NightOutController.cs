using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/night-outs"), Authorize]
public sealed class NightOutController(TmsDbContext db, TachoMasterClient tachoMaster, ILogger<NightOutController> logger) : ControllerBase
{
    [HttpGet("report")]
    public async Task<IActionResult> Report([FromQuery] DateOnly from, [FromQuery] DateOnly to, CancellationToken ct)
    {
        if (to < from) return BadRequest(new { message = "The to date must be on or after the from date." });
        if (to.DayNumber - from.DayNumber > 62) return BadRequest(new { message = "Night-out reporting is limited to 63 days at a time." });

        var loads = await db.Loads.AsNoTracking().Include(x => x.Stops)
            .Where(x => x.PlanningDate >= from && x.PlanningDate <= to && x.DriverId != null && x.Status != LoadStatus.Cancelled)
            .OrderBy(x => x.PlanningDate).ThenBy(x => x.Reference).ToListAsync(ct);
        loads = loads.Where(x => ReadNightOut(x.PlannerNotes) is not null).ToList();

        var driverIds = loads.Select(x => x.DriverId!.Value).Distinct().ToList();
        var vehicleIds = loads.Where(x => x.VehicleId != null).Select(x => x.VehicleId!.Value).Distinct().ToList();
        var drivers = await db.Drivers.AsNoTracking().Where(x => driverIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var vehicles = await db.Vehicles.AsNoTracking().Where(x => vehicleIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);

        var allIdentifiers = vehicles.Values.SelectMany(VehicleIdentifiers).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var startUtc = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var endUtc = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var trackingEvents = allIdentifiers.Count == 0 ? new List<VehicleTrackingEvent>() : await db.VehicleTrackingEvents.AsNoTracking()
            .Where(x => x.EventTimeUtc >= startUtc && x.EventTimeUtc < endUtc && allIdentifiers.Contains(x.VehicleIdentifier))
            .OrderByDescending(x => x.EventTimeUtc).Take(20000).ToListAsync(ct);

        var tachoByDate = new Dictionary<DateOnly, IReadOnlyDictionary<string, TachoVehicleDriverStatus>>();
        foreach (var date in loads.Select(x => x.PlanningDate).Distinct())
        {
            try { tachoByDate[date] = await tachoMaster.GetCurrentDriverStatusesByVehicleAsync(date, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "TachoMaster night-out validation unavailable for {Date}", date);
                tachoByDate[date] = new Dictionary<string, TachoVehicleDriverStatus>();
            }
        }

        var rows = loads.Select(load =>
        {
            drivers.TryGetValue(load.DriverId!.Value, out var driver);
            var vehicle = load.VehicleId is Guid vehicleId && vehicles.TryGetValue(vehicleId, out var foundVehicle) ? foundVehicle : null;
            var ids = vehicle is null ? new HashSet<string>() : VehicleIdentifiers(vehicle).Select(Normalise).ToHashSet();
            var dayStart = new DateTimeOffset(load.PlanningDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            var dayEnd = dayStart.AddDays(1);
            var lastTrack = trackingEvents.FirstOrDefault(x => x.EventTimeUtc >= dayStart && x.EventTimeUtc < dayEnd && ids.Contains(Normalise(x.VehicleIdentifier)));
            var duties = tachoByDate.TryGetValue(load.PlanningDate, out var dayDuties) ? dayDuties.Values : Array.Empty<TachoVehicleDriverStatus>();
            var duty = duties.FirstOrDefault(x => ids.Contains(Normalise(x.VehicleCode)) || (driver is not null && DriverMatches(driver, x)));
            var requested = ReadNightOut(load.PlannerNotes) == true;
            var final = load.Stops.OrderByDescending(x => x.Sequence).FirstOrDefault();
            var evidenceStatus = !requested ? "No night out" : lastTrack is not null && duty is not null ? "DOT + Tacho evidence captured" : lastTrack is not null ? "DOT evidence captured" : duty is not null ? "Tacho evidence captured" : "Planner confirmation only";
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
                trackerLastEventUtc = lastTrack?.EventTimeUtc,
                trackerLatitude = lastTrack?.Latitude,
                trackerLongitude = lastTrack?.Longitude,
                trackerMoving = lastTrack?.IsMoving,
                tachoDriver = duty?.DriverName,
                tachoDutyStartUtc = duty?.DutyStartUtc,
                tachoDutyEndUtc = duty?.DutyEndUtc,
                evidenceStatus
            };
        }).ToList();

        return Ok(new
        {
            from,
            to,
            generatedAtUtc = DateTimeOffset.UtcNow,
            rows,
            counts = rows.Where(x => x.requested).GroupBy(x => x.driverName ?? "Unknown")
                .Select(g => new { driver = g.Key, nights = g.Count(), fullyEvidenced = g.Count(x => x.evidenceStatus == "DOT + Tacho evidence captured") })
                .OrderByDescending(x => x.nights)
        });
    }

    private static IEnumerable<string> VehicleIdentifiers(Vehicle vehicle)
        => new[] { vehicle.Registration, vehicle.Abbreviation, vehicle.FleetNumber }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim());

    private static bool DriverMatches(Driver driver, TachoVehicleDriverStatus duty)
    {
        var values = new[] { driver.DisplayName, driver.TachoName, driver.EmployeeNumber }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(Normalise).ToHashSet();
        return values.Contains(Normalise(duty.DriverName)) || (!string.IsNullOrWhiteSpace(duty.EmployeeNumber) && values.Contains(Normalise(duty.EmployeeNumber)));
    }

    private static string Normalise(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static bool? ReadNightOut(string? notes)
    {
        var value = (notes ?? string.Empty).Split('·').Select(x => x.Trim()).FirstOrDefault(x => x.StartsWith("Night out:", StringComparison.OrdinalIgnoreCase));
        if (value is null) return null;
        return value.EndsWith("Yes", StringComparison.OrdinalIgnoreCase) ? true : value.EndsWith("No", StringComparison.OrdinalIgnoreCase) ? false : null;
    }
}