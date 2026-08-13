using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Models.Integrations;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/integrations")]
[Authorize]
public sealed class IntegrationsController(SageHrClient sageHr, DotTrackingOptions tracking, DotTrackingClient dotTracking, DriverSmsDispatchService sms, AzureSmsDispatchService azureSms, TextBeeOptions textBee, FleetioOptions fleetio, FleetioClient fleetioClient, IConfiguration configuration, TmsDbContext db, ILogger<IntegrationsController> logger) : ControllerBase
{
    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken ct)
    {
        var latestTracking = await db.VehicleLiveStatuses.AsNoTracking().MaxAsync(status => (DateTimeOffset?)status.LastEventTimeUtc, ct);
        var latestEmailIntake = await db.StagedImports.AsNoTracking().Where(item => item.Source != null && (item.Source.Contains("Power Automate") || item.Source.Contains("Mailbox"))).MaxAsync(item => (DateTimeOffset?)item.ReceivedAtUtc, ct);
        return Ok(new
        {
            roadTech = new { configured = tracking.IsConfigured, latestEventUtc = latestTracking, connected = tracking.IsConfigured && latestTracking is not null && DateTimeOffset.UtcNow - latestTracking < TimeSpan.FromMinutes(30) },
            azureMaps = new { configured = !string.IsNullOrWhiteSpace(configuration["Maps:Endpoint"]) },
            azureSms = new { configured = azureSms.IsConfigured },
            textBee = new { configured = textBee.IsConfigured, dutyPhoneLabel = textBee.DutyPhoneLabel, missingSettings = textBee.MissingSettings },
            driverSms = new { configured = sms.IsConfigured, provider = textBee.IsConfigured ? "TextBee" : azureSms.IsConfigured ? "Azure SMS" : "MightyText copy" },
            fleetio = new { configured = fleetio.IsConfigured, missingSettings = fleetio.MissingSettings },
            sageHr = new { configured = sageHr.IsConfigured },
            emailIntake = new { configured = latestEmailIntake is not null, lastReceivedUtc = latestEmailIntake },
            batchIntake = new { configured = true, endpoint = "/api/v1/staging/batch" }
        });
    }

    [HttpGet("sage-hr/status")]
    public async Task<IActionResult> SageHrStatus(CancellationToken ct)
    {
        if (!sageHr.IsConfigured) return Ok(new { configured = false, connected = false, employeeCount = 0, driverCandidateCount = 0, missingSettings = sageHr.MissingSettings, message = $"Sage HR runtime settings are incomplete: {string.Join(", ", sageHr.MissingSettings)}." });
        try
        {
            var employees = await sageHr.GetActiveEmployeesAsync(ct);
            var candidates = employees.Count(IsDriver);
            return Ok(new { configured = true, connected = true, employeeCount = employees.Count, driverCandidateCount = candidates, missingSettings = Array.Empty<string>(), message = "Sage HR is connected." });
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException)
        {
            logger.LogWarning(exception, "Sage HR status check failed.");
            return Ok(new { configured = true, connected = false, employeeCount = 0, driverCandidateCount = 0, missingSettings = Array.Empty<string>(), message = "Sage HR could not be reached or rejected the API key." });
        }
    }

    [HttpGet("roadtech/status")]
    public async Task<IActionResult> RoadTechStatus(CancellationToken ct)
    {
        if (!tracking.IsConfigured)
        {
            var missing = new List<string>();
            if (!tracking.Enabled) missing.Add("RoadTech enabled flag");
            if (string.IsNullOrWhiteSpace(tracking.BaseUrl)) missing.Add("RoadTech base URL");
            if (string.IsNullOrWhiteSpace(tracking.ApiKey)) missing.Add("RoadTech access token");
            if (string.IsNullOrWhiteSpace(tracking.Username)) missing.Add("RoadTech username");
            if (string.IsNullOrWhiteSpace(tracking.Password)) missing.Add("RoadTech password");
            if (string.IsNullOrWhiteSpace(tracking.CompanyCode)) missing.Add("RoadTech company code");
            return Ok(new { configured = false, connected = false, recordCount = 0, latestEventUtc = (DateTimeOffset?)null, missingSettings = missing, message = $"RoadTech runtime settings are incomplete: {string.Join(", ", missing)}." });
        }

        try
        {
            var items = await dotTracking.GetLatestVehicleEventsAsync(ct);
            var records = items.Select(DotTelemetryRecord.FromProvider).ToList();
            var latest = records.Count == 0 ? (DateTimeOffset?)null : records.Max(record => record.EventTimeUtc);
            return Ok(new { configured = true, connected = records.Count > 0, recordCount = records.Count, latestEventUtc = latest, missingSettings = Array.Empty<string>(), message = records.Count > 0 ? $"RoadTech connected and returned {records.Count} vehicle record(s)." : "RoadTech connected but returned zero live vehicle records." });
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or JsonException)
        {
            logger.LogWarning(exception, "RoadTech status check failed.");
            return Ok(new { configured = true, connected = false, recordCount = 0, latestEventUtc = (DateTimeOffset?)null, missingSettings = Array.Empty<string>(), message = $"RoadTech could not be reached or rejected the credentials: {exception.GetBaseException().Message}" });
        }
    }

    [HttpPost("sage-hr/sync-drivers"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> SyncDrivers(CancellationToken ct)
    {
        if (!sageHr.IsConfigured)
        {
            return BadRequest(new { configured = false, missingSettings = sageHr.MissingSettings, message = $"Sage HR cannot sync until these settings are complete: {string.Join(", ", sageHr.MissingSettings)}." });
        }

        try
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
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException)
        {
            logger.LogWarning(exception, "Sage HR driver sync failed.");
            return StatusCode(StatusCodes.Status502BadGateway, new { configured = true, message = "Sage HR could not be reached or rejected the API key. No driver records were changed." });
        }
    }

    private bool IsDriver(SageHrEmployee employee) =>
        (!string.IsNullOrWhiteSpace(sageHr.DriverTeamName) && string.Equals(employee.Team, sageHr.DriverTeamName, StringComparison.OrdinalIgnoreCase)) ||
        (!string.IsNullOrWhiteSpace(sageHr.DriverPositionKeyword) && employee.Position?.Contains(sageHr.DriverPositionKeyword, StringComparison.OrdinalIgnoreCase) == true);

    [HttpGet("fleetio/status")]
    public async Task<IActionResult> FleetioStatus(CancellationToken ct)
    {
        if (!fleetioClient.IsConfigured) return Ok(new { configured = false, connected = false, sampleVehicleCount = 0, missingSettings = fleetioClient.MissingSettings, message = $"Fleetio runtime settings are incomplete: {string.Join(", ", fleetioClient.MissingSettings)}." });
        try
        {
            var summary = await fleetioClient.GetVehicleSummaryAsync(ct);
            return Ok(new { configured = true, connected = summary.Connected, sampleVehicleCount = summary.SampleVehicleCount, missingSettings = Array.Empty<string>(), message = "Fleetio is connected for vehicle service and VOR data." });
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException)
        {
            logger.LogWarning(exception, "Fleetio status check failed.");
            return Ok(new { configured = true, connected = false, sampleVehicleCount = 0, missingSettings = Array.Empty<string>(), message = $"Fleetio could not be reached or rejected the credentials: {exception.GetBaseException().Message}" });
        }
    }
}
