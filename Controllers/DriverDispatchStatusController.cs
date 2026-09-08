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
    private static readonly LoadStatus[] ExecutedStatuses = [LoadStatus.Dispatched, LoadStatus.InProgress, LoadStatus.Completed];

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

        // Tacho is the preferred day-cycle source. A short TMS execution history is also retained
        // as a cross-check for the occasional Tacho history response that contains only the current
        // duty/profile row. It is not allowed to override a usable multi-duty Tacho cycle.
        var historyStart = planningDate.AddDays(-7);
        var recentActivity = await db.Loads.AsNoTracking()
            .Where(item => item.DriverId != null && item.PlanningDate >= historyStart && item.PlanningDate < planningDate && ExecutedStatuses.Contains(item.Status))
            .Select(item => new { DriverId = item.DriverId!.Value, item.PlanningDate })
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
            var tachoProjectedDay = matchedDuties.Count == 0
                ? (int?)null
                : DriverDayCycleCalculator.Calculate(planningDate, matchedDuties);

            var tmsDates = recentActivity
                .Where(item => item.DriverId == driver.Id)
                .Select(item => item.PlanningDate)
                .ToHashSet();
            var tmsConsecutiveDays = 0;
            for (var day = planningDate.AddDays(-1); tmsConsecutiveDays < 7 && tmsDates.Contains(day); day = day.AddDays(-1))
                tmsConsecutiveDays++;
            var tmsProjectedDay = Math.Clamp(tmsConsecutiveDays + 1, 1, 7);

            // Two or more matched duties are enough for Tacho to prove a rest gap/cycle directly.
            // With zero/one matched row, use executed TMS continuity as a conservative cross-check;
            // this prevents a current-profile-only response from incorrectly showing Day 1.
            var projectedDayNumber = matchedDuties.Count >= 2
                ? tachoProjectedDay
                : Math.Max(tachoProjectedDay ?? 1, tmsProjectedDay);

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
            // SLH planning policy is deliberately more conservative than the legal reduced-rest
            // allowance: Calculate Starts must always give the driver a full 11-hour regular daily
            // rest before the next planned duty. A 9-hour reduced rest may remain valid compliance
            // evidence, but it must never be used to bring a planned start forward.
            var start = dutyEnd.AddHours(11);
            if (start < planningFloor) start = planningFloor;
            return new(start, $"Tacho duty ended {LocalTime(dutyEnd):dd/MM HH:mm}; planning start uses 11h regular daily rest. Reduced daily rest is not used for planning.", false);
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