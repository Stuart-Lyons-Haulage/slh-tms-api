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
        var drivers = await db.Drivers.Where(item => item.Active).ToListAsync(ct);
        var loads = await db.Loads
            .AsNoTracking()
            .Where(item => item.PlanningDate == planningDate && item.Status != LoadStatus.Cancelled)
            .ToListAsync(ct);
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
                var throughDate = planningDate;
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

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, London).DateTime);
        var referenceUtc = planningDate <= today
            ? DateTimeOffset.UtcNow
            : ToUtc(planningDate.AddDays(1).ToDateTime(TimeOnly.MinValue).AddTicks(-1));

        var result = drivers.Select(driver =>
        {
            var load = loads
                .Where(item => item.DriverId == driver.Id)
                .OrderByDescending(item => item.UpdatedAtUtc)
                .ThenByDescending(item => item.CreatedAtUtc)
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

            var weekly = DriverWeeklyRestComplianceService.Evaluate(driver, referenceUtc, duties);
            return new DriverDispatchStatusRow(
                driver.Id,
                dispatchStatus,
                latestInbound?.Notes,
                latestInbound?.CapturedAtUtc,
                latestOutbound?.CapturedAtUtc,
                weekly.Status,
                weekly.Message,
                weekly.WeeklyRestDueUtc,
                weekly.LastWeeklyRestEndUtc);
        }).ToList();

        return Ok(new { planningDate, drivers = result });
    }

    private static DateTimeOffset ToUtc(DateTime local)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(unspecified, London), TimeSpan.Zero);
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
    DateTimeOffset? LastWeeklyRestEndUtc);
