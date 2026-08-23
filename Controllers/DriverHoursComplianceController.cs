using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/driver-hours-compliance"), Authorize]
public sealed class DriverHoursComplianceController(
    TmsDbContext db,
    TachoMasterClient tachoMaster,
    ILogger<DriverHoursComplianceController> logger) : ControllerBase
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    private const double BaseRadiusKm = 1.5;

    [HttpGet("weekly")]
    public async Task<IActionResult> Weekly([FromQuery] DateOnly date, CancellationToken ct)
        => Ok(await Build(date, ct));

    [HttpGet("non-employed.csv")]
    public async Task<IActionResult> NonEmployedCsv([FromQuery] DateOnly date, CancellationToken ct)
    {
        var report = await Build(date, ct);
        var csv = new StringBuilder();
        csv.AppendLine("Week Start,Week End,Date,Driver,Employee Number,Employment Type,Agency,Tacho Duty Start,Tacho Duty End,Tacho Duty Span Minutes,Tacho Activity Minutes,Tracker First Movement,Tracker Last Movement,Tracker Movement Span Minutes,Tracker Vehicle(s),Run(s),Variance Minutes,Invoice Evidence Status");
        foreach (var row in report.NonEmployedHours)
        {
            csv.AppendLine(string.Join(',', new[]
            {
                report.WeekStart.ToString("yyyy-MM-dd"), report.WeekEnd.ToString("yyyy-MM-dd"), row.Date.ToString("yyyy-MM-dd"),
                row.DriverName, row.EmployeeNumber, row.EmploymentType, row.AgencyName ?? string.Empty,
                row.TachoDutyStartUtc?.ToString("O") ?? string.Empty, row.TachoDutyEndUtc?.ToString("O") ?? string.Empty,
                row.TachoDutySpanMinutes?.ToString() ?? string.Empty, row.TachoActivityMinutes?.ToString() ?? string.Empty,
                row.TrackerFirstMovementUtc?.ToString("O") ?? string.Empty, row.TrackerLastMovementUtc?.ToString("O") ?? string.Empty,
                row.TrackerMovementSpanMinutes?.ToString() ?? string.Empty, string.Join("; ", row.TrackerVehicles),
                string.Join("; ", row.Runs), row.VarianceMinutes?.ToString() ?? string.Empty, row.EvidenceStatus
            }.Select(Csv)));
        }
        return File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", $"non-employed-driver-hours-{report.WeekStart:yyyy-MM-dd}-to-{report.WeekEnd:yyyy-MM-dd}.csv");
    }

    private async Task<DriverHoursComplianceReport> Build(DateOnly selectedDate, CancellationToken ct)
    {
        var weekStart = WednesdayWeekStart(selectedDate);
        var weekEnd = weekStart.AddDays(6);
        var evidenceEnd = weekEnd.AddDays(2);

        var drivers = await db.Drivers.AsNoTracking().Where(x => x.Active).OrderBy(x => x.DisplayName).ToListAsync(ct);
        try { await MasterDetailStore.EnrichDriversAsync(db, drivers, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            logger.LogWarning(ex, "Driver detail enrichment unavailable for driver-hours compliance.");
        }

        List<Vehicle> vehicles;
        try { vehicles = await db.Vehicles.AsNoTracking().Where(x => x.Active).ToListAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            logger.LogWarning(ex, "Vehicle master unavailable for driver-hours compliance.");
            vehicles = [];
        }

        var loads = new List<Load>();
        for (var day = weekStart; day <= weekEnd; day = day.AddDays(1))
            loads.AddRange(await PlanningResilience.ReadLoadsAsync(db, day, ct));
        loads = loads.Where(x => x.Status != LoadStatus.Cancelled).GroupBy(x => x.Id).Select(x => x.Last()).ToList();

        var tachoByDate = new Dictionary<DateOnly, IReadOnlyList<TachoDriverDutyStatus>>();
        string? tachoError = null;
        for (var day = weekStart; day <= weekEnd; day = day.AddDays(1))
        {
            if (day > DateOnly.FromDateTime(DateTime.UtcNow)) { tachoByDate[day] = []; continue; }
            try { tachoByDate[day] = await tachoMaster.GetDriverDutyStatusesAsync(day, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                tachoError ??= ex.GetBaseException().Message;
                tachoByDate[day] = [];
                logger.LogWarning(ex, "TachoMaster driver-hours read failed for {Date}.", day);
            }
        }

        var trackingStartUtc = StartOfDayUtc(weekStart);
        var trackingEndUtc = StartOfDayUtc(evidenceEnd);
        List<VehicleTrackingEvent> tracking;
        string? trackerError = null;
        try
        {
            tracking = await db.VehicleTrackingEvents.AsNoTracking()
                .Where(x => x.EventTimeUtc >= trackingStartUtc && x.EventTimeUtc < trackingEndUtc)
                .OrderBy(x => x.EventTimeUtc).Take(150000).ToListAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            trackerError = ex.GetBaseException().Message;
            db.ChangeTracker.Clear();
            tracking = [];
            logger.LogWarning(ex, "Tracker evidence unavailable for driver-hours compliance.");
        }

        var basePoint = await TryBasePoint(ct);
        var nonEmployedRows = new List<NonEmployedHourRow>();
        var nightRows = new List<NightOutEvidenceRow>();

        foreach (var driver in drivers)
        {
            var employment = EmploymentType(driver);
            for (var day = weekStart; day <= weekEnd; day = day.AddDays(1))
            {
                var dayLoads = loads.Where(x => x.DriverId == driver.Id && x.PlanningDate == day).OrderBy(x => x.Reference).ToList();
                var duties = tachoByDate.TryGetValue(day, out var available)
                    ? available.Where(x => DriverMatches(driver, x)).OrderBy(x => x.DutyStartUtc).ToList()
                    : [];
                if (dayLoads.Count == 0 && duties.Count == 0) continue;

                var vehicleKeys = VehicleKeys(dayLoads, duties, vehicles);
                var dutyStart = duties.Count > 0 ? duties.Min(x => x.DutyStartUtc) : FirstPlannedTime(dayLoads);
                var dutyEnd = duties.Count > 0 && duties.All(x => x.DutyEndUtc is not null)
                    ? duties.Max(x => x.DutyEndUtc) : (DateTimeOffset?)null;
                var trackerWindowStart = dutyStart ?? StartOfDayUtc(day);
                var trackerWindowEnd = dutyEnd ?? StartOfDayUtc(day.AddDays(1)).AddHours(12);
                if (trackerWindowEnd <= trackerWindowStart) trackerWindowEnd = trackerWindowStart.AddHours(18);
                var dayTracking = tracking.Where(x => x.EventTimeUtc >= trackerWindowStart && x.EventTimeUtc <= trackerWindowEnd && vehicleKeys.Contains(Normalise(x.VehicleIdentifier))).ToList();
                var movement = dayTracking.Where(IsMovement).ToList();
                var trackerFirst = movement.Count > 0 ? movement.Min(x => x.EventTimeUtc) : (DateTimeOffset?)null;
                var trackerLast = movement.Count > 0 ? movement.Max(x => x.EventTimeUtc) : (DateTimeOffset?)null;
                var trackerSpan = trackerFirst is not null && trackerLast is not null ? Minutes(trackerLast.Value - trackerFirst.Value) : (int?)null;
                var dutySpan = dutyStart is not null && dutyEnd is not null && dutyEnd >= dutyStart ? Minutes(dutyEnd.Value - dutyStart.Value) : (int?)null;
                var tachoActivity = duties.Count == 0 ? (int?)null : duties.Sum(x => x.WorkMinutes + x.DriveMinutes + x.AvailableMinutes);
                var variance = dutySpan is not null && trackerSpan is not null ? trackerSpan - dutySpan : null;

                if (!string.Equals(employment, "Employed", StringComparison.OrdinalIgnoreCase))
                {
                    var evidence = dutySpan is not null && trackerSpan is not null
                        ? Math.Abs(variance ?? 0) <= 90 ? "Confirmed by Tacho + tracker" : "Review variance: Tacho + tracker disagree"
                        : dutySpan is not null ? "Tacho only - tracker confirmation missing"
                        : trackerSpan is not null ? "Tracker only - Tacho hours missing"
                        : "No independent hours evidence";
                    nonEmployedRows.Add(new NonEmployedHourRow(day, driver.Id, driver.DisplayName, driver.EmployeeNumber, employment,
                        driver.AgencyName, dutyStart, dutyEnd, dutySpan, tachoActivity, trackerFirst, trackerLast, trackerSpan,
                        vehicleKeys.OrderBy(x => x).ToArray(), dayLoads.Select(x => x.Reference).Distinct().ToArray(), variance, evidence));
                }

                var plannerTick = dayLoads.Any(x => ReadNightOut(x.PlannerNotes) == true);
                var tachoRest = duties.Any(x => x.RestMinutes >= 60 && SpansOvernight(day, x));
                var overnightEnd = StartOfDayUtc(day.AddDays(1)).AddHours(9);
                var parkedEvidence = dayTracking.Where(x => x.EventTimeUtc <= overnightEnd && x.Latitude is not null && x.Longitude is not null)
                    .OrderByDescending(x => x.EventTimeUtc).FirstOrDefault();
                bool? awayFromBase = null;
                double? distanceKm = null;
                if (parkedEvidence is not null && basePoint is not null)
                {
                    distanceKm = HaversineKm((double)parkedEvidence.Latitude!.Value, (double)parkedEvidence.Longitude!.Value, basePoint.Value.Latitude, basePoint.Value.Longitude);
                    awayFromBase = distanceKm > BaseRadiusKm;
                }

                if (plannerTick || tachoRest || awayFromBase == true)
                {
                    var status = plannerTick && tachoRest && awayFromBase == true ? "Confirmed"
                        : !plannerTick && tachoRest && awayFromBase == true ? "Detected - planner tick missing"
                        : awayFromBase == false && plannerTick ? "Review - tracker indicates base"
                        : "Review - evidence incomplete";
                    var sageExpense = string.Equals(employment, "Employed", StringComparison.OrdinalIgnoreCase) && status.StartsWith("Confirmed", StringComparison.OrdinalIgnoreCase)
                        ? "Expected - Sage HR expense reconciliation not yet connected"
                        : "Not yet reconciled";
                    nightRows.Add(new NightOutEvidenceRow(day, driver.Id, driver.DisplayName, employment,
                        dayLoads.Select(x => x.Reference).Distinct().ToArray(), plannerTick, tachoRest,
                        duties.Sum(x => x.RestMinutes), parkedEvidence?.EventTimeUtc, parkedEvidence?.Latitude,
                        parkedEvidence?.Longitude, awayFromBase, distanceKm, status, sageExpense));
                }
            }
        }

        return new DriverHoursComplianceReport(
            weekStart, weekEnd, DateTimeOffset.UtcNow,
            new DriverHoursPolicy("Wednesday", "Tuesday", "The operating week is Wednesday through Tuesday. A PM run remains attached to its commencement day even when delivery continues after midnight.",
                "A night out is confirmed by Tacho rest evidence while the driver remains out, plus tracker position away from base. Planner Night out = Yes is intent/evidence, not the sole authority.",
                BaseRadiusKm,
                "Fleetio pre-use evidence remains valid across midnight while the same driver retains control; a driver/vehicle/trailer handover creates a new check requirement."),
            new DriverHoursSourceStatus(tachoError is null ? "Available" : $"Partial: {tachoError}", trackerError is null ? "Available" : $"Partial: {trackerError}",
                basePoint is null ? "Base geofence unavailable - tracker location cannot prove away-from-base" : $"Base resolved: {basePoint.Value.Name}",
                "Sage HR employee roster is connected; expense-claim API reconciliation is not yet implemented."),
            nightRows.OrderBy(x => x.Date).ThenBy(x => x.DriverName).ToArray(),
            nonEmployedRows.OrderBy(x => x.Date).ThenBy(x => x.DriverName).ToArray());
    }

    private async Task<(string Name, double Latitude, double Longitude)?> TryBasePoint(CancellationToken ct)
    {
        try
        {
            var sites = await SiteLookupFallback.ReadActiveAsync(db, ct);
            try { await MasterDetailStore.EnrichSitesAsync(db, sites, ct); } catch { db.ChangeTracker.Clear(); }
            var candidates = sites.Where(x => x.Latitude is not null && x.Longitude is not null).ToList();
            var site = candidates.FirstOrDefault(x => Normalise(x.ExternalCode) is "SLH" or "BASE" or "YARD")
                ?? candidates.FirstOrDefault(x => Normalise(x.Name).Contains("STUARTLYONS") || Normalise(x.Name).Contains("LYONSHAULAGE"));
            return site is null ? null : (site.Name, (double)site.Latitude!.Value, (double)site.Longitude!.Value);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Base Site Master lookup unavailable for night-out confirmation.");
            db.ChangeTracker.Clear();
            return null;
        }
    }

    internal static DateOnly WednesdayWeekStart(DateOnly date)
    {
        var delta = ((int)date.DayOfWeek - (int)DayOfWeek.Wednesday + 7) % 7;
        return date.AddDays(-delta);
    }

    private static HashSet<string> VehicleKeys(IEnumerable<Load> loads, IEnumerable<TachoDriverDutyStatus> duties, IEnumerable<Vehicle> vehicles)
    {
        var result = duties.Select(x => Normalise(x.VehicleCode)).Where(x => x.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var vehicleById = vehicles.ToDictionary(x => x.Id);
        foreach (var load in loads)
        {
            if (load.VehicleId is not Guid id || !vehicleById.TryGetValue(id, out var vehicle)) continue;
            foreach (var value in new[] { vehicle.Registration, vehicle.Abbreviation, vehicle.FleetNumber })
                if (!string.IsNullOrWhiteSpace(value)) result.Add(Normalise(value));
        }
        return result;
    }

    private static DateTimeOffset? FirstPlannedTime(IEnumerable<Load> loads) => loads.SelectMany(x => x.Stops)
        .Where(x => x.PlannedArrivalUtc is not null).Select(x => x.PlannedArrivalUtc).OrderBy(x => x).FirstOrDefault();

    private static bool DriverMatches(Driver driver, TachoDriverDutyStatus status)
    {
        if (int.TryParse(driver.TachoMasterDriverId, out var linked) && linked > 0 && linked == status.MemberCode) return true;
        if (SameCard(driver.TachoCardNumber, status.CardNumber)) return true;
        if (!string.IsNullOrWhiteSpace(driver.EmployeeNumber) && !string.IsNullOrWhiteSpace(status.EmployeeNumber)
            && Normalise(driver.EmployeeNumber) == Normalise(status.EmployeeNumber)) return true;
        var names = new[] { driver.TachoName, driver.DisplayName }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => Normalise(x!)).ToHashSet();
        return names.Contains(Normalise(status.DriverName));
    }

    private static bool SpansOvernight(DateOnly day, TachoDriverDutyStatus duty)
    {
        var nextMidnight = StartOfDayUtc(day.AddDays(1));
        return duty.DutyStartUtc < nextMidnight && (duty.DutyEndUtc is null || duty.DutyEndUtc > nextMidnight);
    }

    private static string EmploymentType(Driver driver)
    {
        var value = (driver.DriverType ?? string.Empty).Trim();
        if (value.Contains("agency", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(driver.AgencyName)) return "Agency";
        if (value.Contains("sub", StringComparison.OrdinalIgnoreCase)) return "Subcontractor";
        if (value.Contains("employ", StringComparison.OrdinalIgnoreCase) || value.Contains("permanent", StringComparison.OrdinalIgnoreCase)) return "Employed";
        return string.IsNullOrWhiteSpace(value) ? "Unknown" : value;
    }

    private static bool? ReadNightOut(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return null;
        var parts = notes.Split(['|', '·', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var value = parts.FirstOrDefault(x => x.StartsWith("Night out:", StringComparison.OrdinalIgnoreCase));
        if (value is null) return null;
        return value.EndsWith("Yes", StringComparison.OrdinalIgnoreCase) ? true : value.EndsWith("No", StringComparison.OrdinalIgnoreCase) ? false : null;
    }

    private static bool IsMovement(VehicleTrackingEvent x) => x.IsMoving == true || x.IgnitionOn == true || (x.SpeedKph ?? 0) > 0;
    private static int Minutes(TimeSpan value) => (int)Math.Round(value.TotalMinutes);
    private static DateTimeOffset StartOfDayUtc(DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, London.GetUtcOffset(local)).ToUniversalTime();
    }
    private static string Normalise(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static bool SameCard(string? left, string? right)
    {
        var a = Normalise(left); var b = Normalise(right);
        return a.Length >= 8 && b.Length >= 8 && (a == b || a.EndsWith(b, StringComparison.OrdinalIgnoreCase) || b.EndsWith(a, StringComparison.OrdinalIgnoreCase));
    }
    private static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double radius = 6371d;
        static double Rad(double value) => value * Math.PI / 180d;
        var dLat = Rad(lat2 - lat1); var dLon = Rad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return radius * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
    private static string Csv(string value) => $"\"{(value ?? string.Empty).Replace("\"", "\"\"")}\"";

    public sealed record DriverHoursComplianceReport(DateOnly WeekStart, DateOnly WeekEnd, DateTimeOffset GeneratedAtUtc,
        DriverHoursPolicy Policy, DriverHoursSourceStatus SourceStatus, IReadOnlyList<NightOutEvidenceRow> NightOuts,
        IReadOnlyList<NonEmployedHourRow> NonEmployedHours);
    public sealed record DriverHoursPolicy(string WeekStarts, string WeekEnds, string OperatingDayRule, string NightOutRule, double BaseRadiusKm, string FleetCheckRule);
    public sealed record DriverHoursSourceStatus(string TachoMaster, string Tracker, string BaseSite, string SageHrExpenses);
    public sealed record NightOutEvidenceRow(DateOnly Date, Guid DriverId, string DriverName, string EmploymentType, IReadOnlyList<string> Runs,
        bool PlannerTicked, bool TachoRestEvidence, int TachoRestMinutes, DateTimeOffset? TrackerEvidenceUtc, decimal? TrackerLatitude,
        decimal? TrackerLongitude, bool? TrackerAwayFromBase, double? DistanceFromBaseKm, string Status, string SageExpenseStatus);
    public sealed record NonEmployedHourRow(DateOnly Date, Guid DriverId, string DriverName, string EmployeeNumber, string EmploymentType,
        string? AgencyName, DateTimeOffset? TachoDutyStartUtc, DateTimeOffset? TachoDutyEndUtc, int? TachoDutySpanMinutes,
        int? TachoActivityMinutes, DateTimeOffset? TrackerFirstMovementUtc, DateTimeOffset? TrackerLastMovementUtc,
        int? TrackerMovementSpanMinutes, IReadOnlyList<string> TrackerVehicles, IReadOnlyList<string> Runs, int? VarianceMinutes, string EvidenceStatus);
}
