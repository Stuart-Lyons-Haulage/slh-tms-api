using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/driver-dispatch-status"), Authorize]
public sealed class DriverDispatchStatusController(
    TmsDbContext db,
    TachoMasterClient tachoMaster,
    ILogger<DriverDispatchStatusController> logger) : ControllerBase
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] DateOnly? date, CancellationToken ct)
    {
        var planningDate = date ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, London).DateTime);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, London).DateTime);
        var drivers = await db.Drivers.Where(item => item.Active).ToListAsync(ct);
        await MasterDetailStore.EnrichDriversAsync(db, drivers, ct);

        // Driver Dispatch workbench and allocation writes can use the resilient planning register
        // when the core planning schema is unavailable. Status must read the same authoritative
        // run source or a genuinely allocated driver can incorrectly remain "No Run".
        var loads = (await PlanningResilience.ReadLoadsAsync(db, planningDate, ct))
            .Where(item => item.Status != LoadStatus.Cancelled)
            .ToList();
        var loadIds = loads.Select(item => item.Id).ToList();
        IReadOnlyList<DriverStatusLog> logs = loadIds.Count == 0
            ? Array.Empty<DriverStatusLog>()
            : await db.DriverStatusLogs
                .AsNoTracking()
                .Where(item => item.LoadId != Guid.Empty && loadIds.Contains(item.LoadId))
                .OrderByDescending(item => item.CapturedAtUtc)
                .ToListAsync(ct);

        IReadOnlyList<TachoDriverDutyStatus> duties = [];
        if (tachoMaster.IsConfigured)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                // Never ask TachoMaster for a future duty date. Tomorrow planning must be projected
                // from duties that actually exist today, otherwise a future/empty response can make
                // the current driver cycle look unavailable or reset to Day 1.
                var throughDate = planningDate < today ? planningDate : today;
                var fromDate = throughDate.AddDays(-8);
                var all = new List<TachoDriverDutyStatus>();
                for (var dutyDate = fromDate; dutyDate <= throughDate; dutyDate = dutyDate.AddDays(1))
                    all.AddRange(await tachoMaster.GetDriverDutyStatusesAsync(dutyDate, timeout.Token));
                duties = all;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning("TachoMaster exceeded the Driver Dispatch status budget for {PlanningDate}.", planningDate);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "TachoMaster weekly-rest history was unavailable for Driver Dispatch status on {PlanningDate}.", planningDate);
            }
        }

        var result = drivers.Select(driver =>
        {
            var load = loads
                .Where(item => item.DriverId == driver.Id)
                .OrderByDescending(item => item.CreatedAtUtc)
                .FirstOrDefault();
            IReadOnlyList<DriverStatusLog> loadLogs = load is null
                ? Array.Empty<DriverStatusLog>()
                : logs.Where(item => item.LoadId == load.Id).OrderByDescending(item => item.CapturedAtUtc).ToList();
            var latestOutbound = loadLogs.FirstOrDefault(item => item.Status is "Driver dispatched" or "Driver text update sent");
            var latestInbound = latestOutbound is null
                ? null
                : loadLogs.FirstOrDefault(item => item.Status == "Driver response received" && item.CapturedAtUtc > latestOutbound.CapturedAtUtc);

            var dispatchStatus = load is null
                ? "No Run"
                : latestInbound is not null
                    ? "Confirmed"
                    : latestOutbound is not null
                        ? "Sent Awaiting Response"
                        : "Awaiting Dispatch";

            var matchedDuties = duties
                .Where(item => DriverDayCycleCalculator.MatchesDriver(driver, item))
                .OrderByDescending(item => item.MetricsValidAtUtc ?? item.DutyStartUtc)
                .ThenByDescending(item => item.DutyStartUtc)
                .ToList();
            var latestDuty = matchedDuties.FirstOrDefault();
            var projectedDayNumber = matchedDuties.Count == 0
                ? (int?)null
                : DriverDayCycleCalculator.Calculate(planningDate, matchedDuties);
            var referenceUtc = planningDate <= today
                ? DateTimeOffset.UtcNow
                : ProjectedPlanningReferenceUtc(planningDate, matchedDuties);
            var weekly = DriverWeeklyRestComplianceService.Evaluate(driver, referenceUtc, duties);
            var driveAvailablePlanningDayMinutes = planningDate == today
                ? latestDuty?.DriveAvailableTodayMinutes ?? driver.TachoDriveAvailableTodayMinutes
                : planningDate == today.AddDays(1)
                    ? latestDuty?.DriveAvailableTomorrowMinutes
                    : null;
            var workAvailableWeekMinutes = latestDuty?.WorkAvailableWeekMinutes ?? driver.TachoWorkAvailableWeekMinutes;
            var earliest = EarliestPlanningStart(planningDate, today, latestDuty);
            var availability = Availability(weekly, driveAvailablePlanningDayMinutes, workAvailableWeekMinutes, planningDate, today, tachoMaster.IsConfigured, projectedDayNumber);

            return new DriverDispatchStatusRow(
                driver.Id,
                dispatchStatus,
                latestInbound?.Notes,
                latestInbound?.CapturedAtUtc,
                latestOutbound?.CapturedAtUtc,
                weekly.Status,
                weekly.Message,
                weekly.WeeklyRestDueUtc,
                weekly.LastWeeklyRestEndUtc,
                availability.Status,
                availability.Message,
                driveAvailablePlanningDayMinutes,
                workAvailableWeekMinutes,
                projectedDayNumber,
                earliest.StartUtc,
                earliest.Source,
                earliest.IsAssumption);
        }).ToList();

        return Ok(new { planningDate, drivers = result });
    }

    private static DriverAvailability Availability(
        WeeklyRestComplianceResult weekly,
        int? driveAvailablePlanningDayMinutes,
        int? workAvailableWeekMinutes,
        DateOnly planningDate,
        DateOnly today,
        bool tachoConfigured,
        int? projectedDayNumber)
    {
        if (weekly.Status == "Overdue")
        {
            // A historic late weekly-rest event must not permanently make a driver unavailable
            // after a later qualifying rest has reset the current duty cycle. The day-cycle evidence
            // is the cross-check: Days 1-6 mean the current cycle has restarted, so retain a warning
            // for compliance review rather than blocking tomorrow's allocation.
            if (projectedDayNumber is >= 1 and <= 6)
                return new("Unverified", $"TachoMaster weekly-rest history contains overdue evidence, but the current projected duty cycle is Day {projectedDayNumber}. Keep the historic event for compliance review; do not hard-block the current allocation from that older event alone.");
            return new("Unavailable", weekly.Message);
        }

        if (driveAvailablePlanningDayMinutes is <= 0)
            return new("Unavailable", "TachoMaster shows no driving time available for this planning day.");

        if (workAvailableWeekMinutes is <= 0)
            return new("Unavailable", "TachoMaster shows no working time available for the current week.");

        if (!tachoConfigured)
            return new("Unverified", "TachoMaster is unavailable, so availability cannot be verified until final dispatch.");

        if (weekly.Status is "Unverified" or "Unknown")
            return new("Unverified", weekly.Message);

        if (planningDate <= today.AddDays(1) && driveAvailablePlanningDayMinutes is null)
            return new("Unverified", "TachoMaster did not return planning-day driving availability. Final dispatch will re-check live hours.");

        return new("Available", weekly.Status == "DueSoon"
            ? $"Available for planning, but weekly rest is due soon. {weekly.Message}"
            : "TachoMaster shows the driver as available for planning. Final dispatch still validates the selected route against live remaining hours.");
    }

    private static DateTimeOffset ProjectedPlanningReferenceUtc(DateOnly planningDate, IReadOnlyList<TachoDriverDutyStatus> matchedDuties)
    {
        var previous = matchedDuties
            .Where(item => LondonDate(item.DutyStartUtc) < planningDate)
            .OrderByDescending(item => item.DutyStartUtc)
            .FirstOrDefault();
        var localTime = previous is null
            ? TimeOnly.MinValue
            : TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(previous.DutyStartUtc, London).DateTime);
        return ToUtc(planningDate.ToDateTime(localTime));
    }

    private static EarliestStartEvidence EarliestPlanningStart(DateOnly planningDate, DateOnly today, TachoDriverDutyStatus? latestDuty)
    {
        if (planningDate <= today || latestDuty is null)
            return EarliestStartEvidence.Empty;

        var planningFloor = ToUtc(planningDate.ToDateTime(TimeOnly.MinValue));
        if (latestDuty.DutyEndUtc is DateTimeOffset dutyEnd)
        {
            var shortRestsUsed = Math.Max(0, latestDuty.ShortDailyRestTakenThisWeek ?? 0);
            var restHours = shortRestsUsed < 3 ? 9 : 11;
            var start = dutyEnd.AddHours(restHours);
            if (start < planningFloor) start = planningFloor;
            return new(start, $"Tacho duty ended {LocalTime(dutyEnd):dd/MM HH:mm}; earliest after {(restHours == 9 ? "9h reduced" : "11h regular")} daily rest.", false);
        }

        // Today's duty is still open while tomorrow is being planned. Do not pretend this is an
        // authoritative legal start. Use a deliberately conservative planning assumption: today's
        // duty ends no earlier than the later of 'now' or 13 hours after sign-on, followed by an
        // 11-hour regular daily rest. Re-running Calculate Starts after sign-off replaces this.
        var now = DateTimeOffset.UtcNow;
        var assumedDutyEnd = latestDuty.DutyStartUtc.AddHours(13);
        if (assumedDutyEnd < now) assumedDutyEnd = now;
        var assumedStart = assumedDutyEnd.AddHours(11);
        if (assumedStart < planningFloor) assumedStart = planningFloor;
        return new(assumedStart, $"ASSUMPTION · today's Tacho duty is still open. Using assumed duty end {LocalTime(assumedDutyEnd):dd/MM HH:mm} then 11h regular daily rest. Recalculate when the duty closes.", true);
    }

    private static DateOnly LondonDate(DateTimeOffset value)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, London).DateTime);

    private static DateTimeOffset LocalTime(DateTimeOffset value)
        => TimeZoneInfo.ConvertTime(value, London);

    private static DateTimeOffset ToUtc(DateTime local)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(unspecified, London), TimeSpan.Zero);
    }

    private sealed record DriverAvailability(string Status, string Message);
    private sealed record EarliestStartEvidence(DateTimeOffset? StartUtc, string? Source, bool IsAssumption)
    {
        public static EarliestStartEvidence Empty { get; } = new(null, null, false);
    }
}

public sealed record DriverDispatchStatusRow(
    Guid DriverId,
    string DispatchStatus,
    string? LastDriverReply,
    DateTimeOffset? LastDriverReplyAtUtc,
    DateTimeOffset? LastDispatchSentAtUtc,
    string WeeklyRestStatus,
    string WeeklyRestMessage,
    DateTimeOffset? WeeklyRestDueUtc,
    DateTimeOffset? LastWeeklyRestEndUtc,
    string AvailabilityStatus,
    string AvailabilityMessage,
    int? DriveAvailablePlanningDayMinutes,
    int? WorkAvailableWeekMinutes,
    int? ProjectedDayNumber,
    DateTimeOffset? EarliestStartUtc,
    string? EarliestStartSource,
    bool EarliestStartIsAssumption);