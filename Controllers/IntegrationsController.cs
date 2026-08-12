using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/integrations")]
[Authorize]
public sealed class IntegrationsController(SageHrClient sageHr, TmsDbContext db, ILogger<IntegrationsController> logger) : ControllerBase
{
    [HttpGet("sage-hr/status")]
    public async Task<IActionResult> SageHrStatus(CancellationToken ct)
    {
        if (!sageHr.IsConfigured) return Ok(new { configured = false, connected = false, employeeCount = 0, driverCandidateCount = 0, message = "Sage HR runtime settings are incomplete." });
        try
        {
            var employees = await sageHr.GetActiveEmployeesAsync(ct);
            var candidates = employees.Count(IsDriver);
            return Ok(new { configured = true, connected = true, employeeCount = employees.Count, driverCandidateCount = candidates, message = "Sage HR is connected." });
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException)
        {
            logger.LogWarning(exception, "Sage HR status check failed.");
            return Ok(new { configured = true, connected = false, employeeCount = 0, driverCandidateCount = 0, message = "Sage HR could not be reached or rejected the API key." });
        }
    }

    [HttpPost("sage-hr/sync-drivers"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> SyncDrivers(CancellationToken ct)
    {
        var employees = await sageHr.GetActiveEmployeesAsync(ct);
        var candidates = employees.Where(IsDriver).ToList();
        var created = 0; var updated = 0; var skipped = 0;
        foreach (var employee in candidates)
        {
            var employeeNumber = string.IsNullOrWhiteSpace(employee.EmployeeNumber) ? $"SAGE-{employee.Id}" : employee.EmployeeNumber.Trim();
            var displayName = $"{employee.FirstName} {employee.LastName}".Trim();
            if (string.IsNullOrWhiteSpace(displayName)) { skipped++; continue; }
            var driver = await db.Drivers.SingleOrDefaultAsync(item => item.EmployeeNumber == employeeNumber, ct);
            if (driver is null)
            {
                db.Drivers.Add(new Driver { EmployeeNumber = employeeNumber, DisplayName = displayName, MobileNumber = employee.MobilePhone, DriverType = employee.Position, DriverGroup = employee.Team, Active = true });
                created++;
            }
            else
            {
                driver.DisplayName = displayName; driver.MobileNumber = employee.MobilePhone; driver.DriverType = employee.Position; driver.DriverGroup = employee.Team; driver.Active = true;
                updated++;
            }
        }
        await db.SaveChangesAsync(ct);
        return Ok(new { sourceEmployeeCount = employees.Count, driverCandidateCount = candidates.Count, created, updated, skipped, syncedAtUtc = DateTimeOffset.UtcNow });
    }

    private bool IsDriver(SageHrEmployee employee) =>
        (!string.IsNullOrWhiteSpace(sageHr.DriverTeamName) && string.Equals(employee.Team, sageHr.DriverTeamName, StringComparison.OrdinalIgnoreCase)) ||
        (!string.IsNullOrWhiteSpace(sageHr.DriverPositionKeyword) && employee.Position?.Contains(sageHr.DriverPositionKeyword, StringComparison.OrdinalIgnoreCase) == true);
}
