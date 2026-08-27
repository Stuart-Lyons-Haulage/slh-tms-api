using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/driver-dispatch"), Authorize]
public sealed class DriverDispatchController(
    TmsDbContext db,
    SageHrClient sageHr,
    TachoMasterClient tachoMaster,
    ILogger<DriverDispatchController> logger) : ControllerBase
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] DateOnly? date, CancellationToken ct)
    {
        var planningDate = date ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, London).DateTime).AddDays(1);
        var loads = (await PlanningResilience.ReadLoadsAsync(db, planningDate, ct))
            .Where(load => load.Status != LoadStatus.Cancelled)
            .OrderBy(load => FirstPlanned(load) ?? DateTimeOffset.MaxValue)
            .ThenBy(load => load.Reference)
            .ToList();
        try { await LoadCommercialStore.EnrichAsync(db, loads, ct); }
        catch (Exception exception) when (exception is not OperationCanceledException) { db.ChangeTracker.Clear(); }

        var drivers = await db.Drivers.AsNoTracking().Where(driver => driver.Active).OrderBy(driver => driver.DisplayName).ToListAsync(ct);
        await MasterDetailStore.EnrichDriversAsync(db, drivers, ct);
        var vehicles = await db.Vehicles.AsNoTracking().Where(vehicle => vehicle.Active).OrderBy(vehicle => vehicle.Registration).ToListAsync(ct);
        var trailers = await db.Trailers.AsNoTracking().Where(trailer => trailer.Active).OrderBy(trailer => trailer.TrailerNumber).ToListAsync(ct);
        var vehicleById = vehicles.ToDictionary(vehicle => vehicle.Id);

        var history = new List<Load>();
        for (var offset = 1; offset <= 7; offset++)
        {
            var historyDate = planningDate.AddDays(-offset);
            try
            {
                history.AddRange((await PlanningResilience.ReadLoadsAsync(db, historyDate, ct))
                    .Where(load => load.Status != LoadStatus.Cancelled && load.DriverId is not null));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Driver Dispatch could not read run history for {Date}.", historyDate);
                db.ChangeTracker.Clear();
            }
        }

        var tachoWorkEvidenceTask = ReadTachoWorkEvidenceAsync(planningDate, ct);
        var leaveTask = LeaveByEmployeeNumber(planningDate, ct);
        var dispatchStatesTask = DriverDispatchStateStore.ReadAsync(db, loads.Select(load => load.Id), ct);
        await Task.WhenAll(tachoWorkEvidenceTask, leaveTask, dispatchStatesTask);
        var tachoWorkEvidence = tachoWorkEvidenceTask.Result;
        var leave = leaveTask.Result;
        var dispatchStates = dispatchStatesTask.Result;
        var southboundRuns = loads.Where(IsSouthbound).Where(load => load.DriverId is null).ToList();

        var rows = new List<DriverDispatchDriver>();
        foreach (var driver in drivers)
        {
            var allocated = loads.Where(load => load.DriverId == driver.Id).OrderBy(load => FirstPlanned(load) ?? DateTimeOffset.MaxValue).ToList();
            var previous = history.Where(load => load.DriverId == driver.Id)
                .OrderByDescending(load => load.PlanningDate)
                .ThenByDescending(load => load.CreatedAtUtc)
                .FirstOrDefault();
            var previousVehicle = previous?.VehicleId is Guid previousVehicleId && vehicleById.TryGetValue(previousVehicleId, out var foundVehicle) ? foundVehicle : null;
            var consecutive = ConsecutiveWorkedDays(history, driver, planningDate, tachoWorkEvidence);
            var dayNumber = consecutive + 1;
            var employeeKey = Normalise(driver.EmployeeNumber);
            leave.TryGetValue(employeeKey, out var absence);

            Guid? suggestedRunId = null;
            string? suggestedRunReference = null;
            string? suggestion = null;
            var previousFinal = previous?.Stops.OrderBy(stop => stop.Sequence).LastOrDefault();
            if (absence is not null)
            {
                suggestion = $"Unavailable · Sage HR {absence.PolicyName ?? "leave"}{(absence.IsPartOfDay ? " (part day)" : string.Empty)}.";
            }
            else if (allocated.Count > 0)
            {
                suggestion = allocated.Count == 1 ? "Already allocated." : $"{allocated.Count} runs already allocated.";
            }
            else if (dayNumber >= 5 && previousFinal?.Latitude >= 52.5m && southboundRuns.FirstOrDefault() is { } southbound)
            {
                suggestedRunId = southbound.Id;
                suggestedRunReference = RunDisplayLabel.For(southbound);
                suggestion = $"Day {dayNumber} · finished north · prioritise {suggestedRunReference} to bring the driver south/home.";
            }
            else if (string.Equals(driver.Coding?.Trim(), "3", StringComparison.OrdinalIgnoreCase))
            {
                suggestion = $"Day {dayNumber} · Code 3 · keep to straightforward work.";
            }
            else if (string.Equals(driver.Coding?.Trim(), "4", StringComparison.OrdinalIgnoreCase) || IsAgency(driver))
            {
                suggestion = $"Agency{(string.IsNullOrWhiteSpace(driver.AgencyName) ? string.Empty : $" · {driver.AgencyName}")} · use after suitable employed/casual drivers.";
            }
            else if (previousVehicle is not null)
            {
                suggestion = $"Suggest {previousVehicle.Registration} · driver used it on {previous!.PlanningDate:dd/MM}.";
            }

            rows.Add(new DriverDispatchDriver(
                driver.Id,
                driver.EmployeeNumber,
                driver.DisplayName,
                CanonicalType(driver),
                driver.DriverGroup,
                driver.Skills,
                driver.Coding,
                driver.AgencyName,
                driver.TachoMasterDriverId,
                driver.TachoCardNumber,
                driver.LicenceExpiry,
                driver.LicenceStatus,
                dayNumber,
                absence is not null,
                absence?.PolicyName,
                absence?.Details,
                absence?.IsPartOfDay ?? false,
                previous?.Id,
                previous is null ? null : RunDisplayLabel.For(previous),
                previousVehicle?.Id,
                previousVehicle?.Registration,
                previousFinal?.Name,
                previousFinal?.Latitude,
                allocated.FirstOrDefault()?.Id,
                allocated.Count,
                suggestedRunId,
                suggestedRunReference,
                suggestion));
        }

        return Ok(new
        {
            planningDate,
            generatedAtUtc = DateTimeOffset.UtcNow,
            leaveSource = sageHr.IsConfigured ? "Sage HR" : "Unavailable",
            dayNumberSource = tachoWorkEvidence.Count > 0 ? "TachoMaster duty with TMS executed-run fallback" : "TMS executed-run fallback",
            drivers = rows.OrderBy(row => TypeOrder(row.DriverType)).ThenBy(row => row.DisplayName),
            vehicles,
            trailers,
            loads = loads.Select(load => new
            {
                load.Id,
                reference = RunDisplayLabel.For(load),
                rawReference = load.Reference,
                load.PlanningDate,
                status = load.Status.ToString(),
                load.DriverId,
                load.VehicleId,
                load.TrailerId,
                load.PalletSpacesUsed,
                load.TotalPalletSpaces,
                load.CapacityType,
                load.PlannerNotes,
                southbound = IsSouthbound(load),
                plannedStartUtc = dispatchStates.GetValueOrDefault(load.Id)?.PlannedStartUtc,
                stops = load.Stops.OrderBy(stop => stop.Sequence).Select(stop => new
                {
                    stop.Id,
                    stop.Sequence,
                    stop.Name,
                    stop.Address,
                    stop.Latitude,
                    stop.Longitude,
                    stop.PlannedArrivalUtc,
                    stop.PlannerNote
                })
            })
        });
    }

    [HttpPut("{loadId:guid}/start-time"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> SetStartTime(Guid loadId, DriverDispatchStartTimeRequest request, CancellationToken ct)
    {
        var load = await PlanningResilience.ReadLoadAsync(db, loadId, ct);
        if (load is null) return NotFound(new { message = "The run could not be found." });

        DateTimeOffset? plannedStartUtc = null;
        if (!string.IsNullOrWhiteSpace(request.StartTime))
        {
            if (!TimeOnly.TryParse(request.StartTime.Trim(), out var time)) return BadRequest(new { message = "Start time must be a valid HH:mm time." });
            var local = DateTime.SpecifyKind(load.PlanningDate.ToDateTime(time), DateTimeKind.Unspecified);
            plannedStartUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, London), TimeSpan.Zero);
        }

        var actor = User.Identity?.Name ?? "TMS planner";
        var state = await DriverDispatchStateStore.SetPlannedStartAsync(db, loadId, plannedStartUtc, actor, ct);
        try
        {
            db.DriverStatusLogs.Add(new DriverStatusLog
            {
                LoadId = loadId,
                DriverId = load.DriverId,
                Status = plannedStartUtc is null ? "Planned start cleared" : "Planned start set",
                Notes = plannedStartUtc is null
                    ? "Driver Dispatch planned start time cleared."
                    : $"Planned start {TimeZoneInfo.ConvertTime(plannedStartUtc.Value, London):HH:mm} for {load.PlanningDate:dd/MM/yyyy}.",
                CapturedBy = actor
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not append planned start audit event for run {LoadId}; dispatch state was retained.", loadId);
            db.ChangeTracker.Clear();
        }
        return Ok(state);
    }

    private async Task<Dictionary<string, LeaveState>> LeaveByEmployeeNumber(DateOnly date, CancellationToken ct)
    {
        var result = new Dictionary<string, LeaveState>(StringComparer.OrdinalIgnoreCase);
        if (!sageHr.IsConfigured) return result;
        try
        {
            var employeeTask = sageHr.GetActiveEmployeesAsync(ct);
            var leaveTask = sageHr.GetOutOfOfficeAsync(date, ct);
            await Task.WhenAll(employeeTask, leaveTask);
            var employees = employeeTask.Result.ToDictionary(employee => employee.Id);
            foreach (var item in leaveTask.Result)
            {
                if (!employees.TryGetValue(item.EmployeeId, out var employee) || string.IsNullOrWhiteSpace(employee.EmployeeNumber)) continue;
                result[Normalise(employee.EmployeeNumber)] = new LeaveState(item.Policy?.Name, item.Details, item.IsPartOfDay);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Sage HR leave was unavailable for Driver Dispatch on {Date}; allocations remain editable but no leave suggestion will be made.", date);
        }
        return result;
    }

    private async Task<Dictionary<DateOnly, HashSet<string>>> ReadTachoWorkEvidenceAsync(DateOnly planningDate, CancellationToken ct)
    {
        var result = new Dictionary<DateOnly, HashSet<string>>();
        if (!tachoMaster.IsConfigured) return result;

        // Limit concurrent history calls so the workbench stays responsive without hammering the upstream service.
        using var gate = new SemaphoreSlim(3, 3);
        var days = Enumerable.Range(1, 7).Select(offset => planningDate.AddDays(-offset)).ToArray();
        var tasks = days.Select(async day =>
        {
            await gate.WaitAsync(ct);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(12));
                var duties = await tachoMaster.GetDriverDutyStatusesAsync(day, timeout.Token);
                var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var duty in duties)
                {
                    AddIdentity(identities, "M", duty.MemberCode > 0 ? duty.MemberCode.ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
                    AddIdentity(identities, "C", duty.CardNumber);
                    AddIdentity(identities, "E", duty.EmployeeNumber);
                    AddIdentity(identities, "N", duty.DriverName);
                }
                return (Day: day, Success: true, Identities: identities);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning("TachoMaster history timed out for Driver Dispatch day-number evidence on {Date}; executed TMS run history will be used for that date.", day);
                return (Day: day, Success: false, Identities: new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "TachoMaster history was unavailable for Driver Dispatch day-number evidence on {Date}; executed TMS run history will be used for that date.", day);
                return (Day: day, Success: false, Identities: new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            }
            finally
            {
                gate.Release();
            }
        });

        foreach (var item in await Task.WhenAll(tasks))
            if (item.Success) result[item.Day] = item.Identities;
        return result;
    }

    private static int ConsecutiveWorkedDays(
        IEnumerable<Load> history,
        Driver driver,
        DateOnly planningDate,
        IReadOnlyDictionary<DateOnly, HashSet<string>> tachoWorkEvidence)
    {
        var executedTmsDates = history
            .Where(load => load.DriverId == driver.Id && load.Status is LoadStatus.Dispatched or LoadStatus.InProgress or LoadStatus.Completed)
            .Select(load => load.PlanningDate)
            .ToHashSet();

        var count = 0;
        for (var day = planningDate.AddDays(-1); ; day = day.AddDays(-1))
        {
            var worked = tachoWorkEvidence.TryGetValue(day, out var identities)
                ? DriverPresentInTacho(driver, identities)
                : executedTmsDates.Contains(day);
            if (!worked) break;
            count++;
            if (count >= 7) break;
        }
        return count;
    }

    private static bool DriverPresentInTacho(Driver driver, HashSet<string> identities)
    {
        if (StableIdentityMatch(identities, "M", driver.TachoMasterDriverId)) return true;
        if (StableIdentityMatch(identities, "C", driver.TachoCardNumber)) return true;
        if (StableIdentityMatch(identities, "E", driver.EmployeeNumber)) return true;

        var hasStableIdentity = !string.IsNullOrWhiteSpace(driver.TachoMasterDriverId)
            || !string.IsNullOrWhiteSpace(driver.TachoCardNumber)
            || !string.IsNullOrWhiteSpace(driver.EmployeeNumber);
        if (hasStableIdentity) return false;

        return StableIdentityMatch(identities, "N", driver.TachoName)
            || StableIdentityMatch(identities, "N", driver.DisplayName);
    }

    private static bool StableIdentityMatch(HashSet<string> identities, string prefix, string? value)
    {
        var normalized = Normalise(value);
        return normalized.Length > 0 && identities.Contains($"{prefix}:{normalized}");
    }

    private static void AddIdentity(HashSet<string> identities, string prefix, string? value)
    {
        var normalized = Normalise(value);
        if (normalized.Length > 0) identities.Add($"{prefix}:{normalized}");
    }

    private static DateTimeOffset? FirstPlanned(Load load) => load.Stops.OrderBy(stop => stop.Sequence).Select(stop => stop.PlannedArrivalUtc).FirstOrDefault(value => value is not null);
    private static string CanonicalType(Driver driver) => IsAgency(driver) ? "Agency" : driver.DriverType?.Contains("casual", StringComparison.OrdinalIgnoreCase) == true ? "Casual" : "Employed";
    private static bool IsAgency(Driver driver) => new[] { driver.DriverType, driver.DriverGroup, driver.AgencyName }.Any(value => value?.Contains("agency", StringComparison.OrdinalIgnoreCase) == true) || string.Equals(driver.Coding?.Trim(), "4", StringComparison.OrdinalIgnoreCase);
    private static int TypeOrder(string type) => type == "Employed" ? 0 : type == "Casual" ? 1 : 2;
    private static bool IsSouthbound(Load load)
    {
        var text = $"{load.Reference} {load.PlannerNotes}";
        return text.Contains("southbound", StringComparison.OrdinalIgnoreCase) ||
            text.Split([' ', '-', '_', ':', '|'], StringSplitOptions.RemoveEmptyEntries).Any(token => token.Equals("SB", StringComparison.OrdinalIgnoreCase));
    }
    private static string Normalise(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private sealed record LeaveState(string? PolicyName, string? Details, bool IsPartOfDay);
}

public sealed record DriverDispatchStartTimeRequest(string? StartTime);
public sealed record DriverDispatchDriver(
    Guid DriverId,
    string EmployeeNumber,
    string DisplayName,
    string DriverType,
    string? DriverGroup,
    string? Skills,
    string? Coding,
    string? AgencyName,
    string? TachoMasterDriverId,
    string? TachoCardNumber,
    DateOnly? LicenceExpiry,
    string? LicenceStatus,
    int DayNumber,
    bool OnLeave,
    string? LeaveType,
    string? LeaveDetails,
    bool PartDayLeave,
    Guid? PreviousLoadId,
    string? PreviousRunReference,
    Guid? PreviousVehicleId,
    string? PreviousVehicleRegistration,
    string? PreviousFinalStop,
    decimal? PreviousFinalLatitude,
    Guid? AssignedLoadId,
    int AssignedRunCount,
    Guid? SuggestedRunId,
    string? SuggestedRunReference,
    string? Suggestion);