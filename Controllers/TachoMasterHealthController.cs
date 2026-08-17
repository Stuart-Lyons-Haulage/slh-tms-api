using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/health/tachomaster")]
public sealed class TachoMasterHealthController(
    TachoMasterClient tachoMasterClient,
    ILogger<TachoMasterHealthController> logger) : ControllerBase
{
    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        if (!tachoMasterClient.IsConfigured)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                status = "unconfigured",
                configured = false,
                usesSharedRoadTechCredentials = tachoMasterClient.UsesSharedRoadTechCredentials,
                missingSettings = tachoMasterClient.MissingSettings
            });
        }

        try
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var profiles = await tachoMasterClient.GetDriverProfilesAsync(cancellationToken);
            var duties = await tachoMasterClient.GetCurrentDriverStatusesByVehicleAsync(today, cancellationToken);

            return Ok(new
            {
                status = "healthy",
                configured = true,
                usesSharedRoadTechCredentials = tachoMasterClient.UsesSharedRoadTechCredentials,
                driverProfiles = profiles.Count,
                currentVehicleDuties = duties.Count,
                checkedAtUtc = DateTimeOffset.UtcNow
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "TachoMaster health check failed.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                status = "upstream-failure",
                configured = true,
                usesSharedRoadTechCredentials = tachoMasterClient.UsesSharedRoadTechCredentials,
                error = exception.GetType().Name,
                message = exception.Message,
                checkedAtUtc = DateTimeOffset.UtcNow
            });
        }
    }
}
