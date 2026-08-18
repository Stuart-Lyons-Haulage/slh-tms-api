using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/tachomaster/drivers")]
[Authorize]
public sealed class TachoMasterDriverHistoryController(
    TmsDbContext db,
    TachoMasterClient tachoMaster,
    ILogger<TachoMasterDriverHistoryController> logger) : ControllerBase
{
    [HttpGet("{driverId:guid}/history")]
    public async Task<IActionResult> History(
        Guid driverId,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        CancellationToken ct)
    {
        if (!tachoMaster.IsConfigured)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                configured = false,
                missingSettings = tachoMaster.MissingSettings,
                message = "TachoMaster is not configured."
            });

        var driver = await db.Drivers.AsNoTracking().SingleOrDefaultAsync(item => item.Id == driverId, ct);
        if (driver is null) return NotFound(new { message = "Driver was not found." });

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var end = to ?? today;
        var start = from ?? end.AddDays(-13);
        if (end > today) end = today;
        if (start > end) (start, end) = (end, start);
        if (end.DayNumber - start.DayNumber > 30) start = end.AddDays(-30);

        try
        {
            var profiles = await tachoMaster.GetDriverProfilesAsync(ct);
            var profile = profiles.FirstOrDefault(item => DriverMatches(driver, item));
            var duties = new List<TachoDriverDutyStatus>();
            for (var date = start; date <= end; date = date.AddDays(1))
            {
                var day = await tachoMaster.GetDriverDutyStatusesAsync(date, ct);
                duties.AddRange(day.Where(item => DriverMatches(driver, item)));
            }

            var ordered = duties.OrderByDescending(item => item.DutyStartUtc).ToList();
            return Ok(new
            {
                configured = true,
                driverId = driver.Id,
                driverName = driver.DisplayName,
                driver.EmployeeNumber,
                linkedTachoMemberId = driver.TachoMasterDriverId,
                linkedTachoCard = driver.TachoCardNumber,
                from = start,
                to = end,
                profile,
                summary = new
                {
                    dutyCount = ordered.Count,
                    daysWithDuty = ordered.Select(item => DateOnly.FromDateTime(item.DutyStartUtc.UtcDateTime)).Distinct().Count(),
                    driveMinutes = ordered.Sum(item => item.DriveMinutes),
                    workMinutes = ordered.Sum(item => item.WorkMinutes),
                    availableMinutes = ordered.Sum(item => item.AvailableMinutes),
                    restMinutes = ordered.Sum(item => item.RestMinutes),
                    breakCount = ordered.Sum(item => item.BreakCount),
                    breakMinutes = ordered.Where(item => item.BreakMinutes is not null).Sum(item => item.BreakMinutes ?? 0)
                },
                duties = ordered
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "TachoMaster history failed for driver {DriverId}.", driverId);
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                configured = true,
                driverId,
                message = $"TachoMaster history could not be returned: {ex.GetBaseException().Message}"
            });
        }
    }

    private static bool DriverMatches(Driver driver, TachoDriverProfile profile)
    {
        if (int.TryParse(driver.TachoMasterDriverId, out var linkedMember) && linkedMember > 0 && linkedMember == profile.MemberCode)
            return true;
        if (SameCard(driver.TachoCardNumber, profile.CardNumber)) return true;
        if (!string.IsNullOrWhiteSpace(driver.EmployeeNumber) && !string.IsNullOrWhiteSpace(profile.EmployeeNumber) &&
            string.Equals(Normalise(driver.EmployeeNumber), Normalise(profile.EmployeeNumber), StringComparison.OrdinalIgnoreCase))
            return true;
        return SameName(driver, profile.DriverName);
    }

    private static bool DriverMatches(Driver driver, TachoDriverDutyStatus duty)
    {
        if (int.TryParse(driver.TachoMasterDriverId, out var linkedMember) && linkedMember > 0 && linkedMember == duty.MemberCode)
            return true;
        if (SameCard(driver.TachoCardNumber, duty.CardNumber)) return true;
        if (!string.IsNullOrWhiteSpace(driver.EmployeeNumber) && !string.IsNullOrWhiteSpace(duty.EmployeeNumber) &&
            string.Equals(Normalise(driver.EmployeeNumber), Normalise(duty.EmployeeNumber), StringComparison.OrdinalIgnoreCase))
            return true;
        return SameName(driver, duty.DriverName);
    }

    private static bool SameName(Driver driver, string name)
    {
        var source = Normalise(name);
        return new[] { driver.TachoName, driver.DisplayName }
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Any(item => string.Equals(Normalise(item!), source, StringComparison.OrdinalIgnoreCase));
    }

    private static bool SameCard(string? left, string? right)
    {
        var a = Normalise(left ?? string.Empty);
        var b = Normalise(right ?? string.Empty);
        if (a.Length < 8 || b.Length < 8) return false;
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ||
               a.EndsWith(b, StringComparison.OrdinalIgnoreCase) ||
               b.EndsWith(a, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalise(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
