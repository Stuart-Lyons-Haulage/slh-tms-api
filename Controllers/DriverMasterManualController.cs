using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/driver-master")]
[Authorize]
public sealed class DriverMasterManualController(TmsDbContext db) : ControllerBase
{
    [HttpPut("{id:guid}/manual-details")]
    [Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> UpdateManualDetails(Guid id, DriverMasterManualUpdateRequest request, CancellationToken ct)
    {
        var drivers = await db.Drivers.OrderBy(driver => driver.DisplayName).ToListAsync(ct);
        await MasterDetailStore.EnrichDriversAsync(db, drivers, ct);
        var driver = drivers.SingleOrDefault(item => item.Id == id);
        if (driver is null) return NotFound();

        var employeeNumber = CleanRequired(request.EmployeeNumber, 40);
        var displayName = CleanRequired(request.DisplayName, 160);
        if (string.IsNullOrWhiteSpace(employeeNumber) || string.IsNullOrWhiteSpace(displayName))
            return BadRequest(new { message = "Employee number and display name are required." });

        if (drivers.Any(item => item.Id != id && string.Equals(item.EmployeeNumber, employeeNumber, StringComparison.OrdinalIgnoreCase)))
            return Conflict(new { message = $"Employee number {employeeNumber} already exists." });

        var memberCode = Clean(request.TachoMasterDriverId, 80);
        if (!string.IsNullOrWhiteSpace(memberCode) && drivers.Any(item =>
                item.Id != id && item.Active &&
                TachoDriverIdentityRules.MemberMatches(item.TachoMasterDriverId, memberCode)))
            return Conflict(new { message = $"TachoMaster member/DB number {memberCode} is already linked to another active driver." });

        var cardNumber = Clean(request.TachoCardNumber, 80);
        if (!string.IsNullOrWhiteSpace(cardNumber) && drivers.Any(item =>
                item.Id != id && item.Active &&
                TachoDriverIdentityRules.CardsMatch(item.TachoCardNumber, cardNumber)))
            return Conflict(new { message = "That tachograph card number is already linked to another active driver." });

        driver.EmployeeNumber = employeeNumber;
        driver.DisplayName = displayName;
        driver.TachoName = Clean(request.TachoName, 160);
        driver.TachoMasterDriverId = memberCode;
        driver.TachoCardNumber = cardNumber;
        driver.MobileNumber = Clean(request.MobileNumber, 40);
        driver.DriverType = Clean(request.DriverType, 80);
        driver.DriverGroup = Clean(request.DriverGroup, 80);
        driver.Skills = Clean(request.Skills, 160);
        driver.Coding = Clean(request.Coding, 80);
        driver.AgencyName = Clean(request.AgencyName, 160);
        driver.NorthEligible = request.NorthEligible;
        driver.PreloadEligible = request.PreloadEligible;
        driver.Notes = Clean(request.Notes, 500);
        driver.DrivingLicenceNumber = Clean(request.DrivingLicenceNumber, 80);
        driver.LicenceExpiry = request.LicenceExpiry;
        driver.CPCExpiry = request.CPCExpiry;
        driver.DigitalTachoCardExpiry = request.DigitalTachoCardExpiry;
        driver.MedicalExpiry = request.MedicalExpiry;
        driver.LicenceStatus = Clean(request.LicenceStatus, 40);
        driver.Active = request.Active;

        await db.SaveChangesAsync(ct);
        await MasterDetailStore.SaveAsync(
            db,
            "driver",
            employeeNumber,
            JsonSerializer.Serialize(driver),
            "SLH driver editor",
            User.Identity?.Name ?? User.FindFirst("preferred_username")?.Value,
            ct);

        db.MasterDataAudits.Add(new MasterDataAudit
        {
            EntityType = "Driver",
            EntityId = driver.Id,
            Action = "ManualDriverMasterDetailsUpdated",
            ChangedBy = User.Identity?.Name ?? User.FindFirst("preferred_username")?.Value ?? "unknown",
            ChangesJson = JsonSerializer.Serialize(new
            {
                driver.EmployeeNumber,
                driver.DisplayName,
                driver.TachoName,
                driver.TachoMasterDriverId,
                driver.TachoCardNumber,
                driver.DrivingLicenceNumber,
                driver.LicenceExpiry,
                driver.CPCExpiry,
                driver.DigitalTachoCardExpiry,
                driver.MedicalExpiry,
                driver.Active
            })
        });
        await db.SaveChangesAsync(ct);

        return Ok(driver);
    }

    private static string? Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static string CleanRequired(string? value, int maxLength) => Clean(value, maxLength) ?? string.Empty;
}

public sealed record DriverMasterManualUpdateRequest(
    string? EmployeeNumber,
    string? DisplayName,
    string? TachoName,
    string? TachoMasterDriverId,
    string? TachoCardNumber,
    string? MobileNumber,
    string? DriverType,
    string? DriverGroup,
    string? Skills,
    string? Coding,
    string? AgencyName,
    bool? NorthEligible,
    bool? PreloadEligible,
    string? Notes,
    string? DrivingLicenceNumber,
    DateOnly? LicenceExpiry,
    DateOnly? CPCExpiry,
    DateOnly? DigitalTachoCardExpiry,
    DateOnly? MedicalExpiry,
    string? LicenceStatus,
    bool Active);
