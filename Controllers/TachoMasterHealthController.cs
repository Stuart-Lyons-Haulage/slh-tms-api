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
            var now = DateTimeOffset.UtcNow;
            var today = DateOnly.FromDateTime(now.UtcDateTime);
            var profilesTask = tachoMasterClient.GetDriverProfilesAsync(cancellationToken);
            var dutiesTask = tachoMasterClient.GetCurrentDriverStatusesByVehicleAsync(today, cancellationToken);
            await Task.WhenAll(profilesTask, dutiesTask);
            var profiles = await profilesTask;
            var duties = await dutiesTask;

            var newest = profiles.Select(item => item.MetricsValidAtUtc)
                .Concat(duties.Values.Select(item => item.MetricsValidAtUtc))
                .Concat(duties.Values.Select(item => item.DutyEndUtc))
                .Concat(duties.Values.Select(item => (DateTimeOffset?)item.DutyStartUtc))
                .Where(item => item is not null)
                .Select(item => item!.Value)
                .DefaultIfEmpty()
                .Max();
            var ageMinutes = newest == default ? (double?)null : Math.Max(0, Math.Round((now - newest).TotalMinutes, 1));
            var freshness = ageMinutes switch
            {
                null => "unknown",
                <= 15 => "live",
                <= 60 => "delayed",
                _ => "stale"
            };

            return Ok(new
            {
                status = "healthy",
                configured = true,
                usesSharedRoadTechCredentials = tachoMasterClient.UsesSharedRoadTechCredentials,
                driverProfiles = profiles.Count,
                currentVehicleDuties = duties.Count,
                sourceFreshness = freshness,
                newestSourceTimestampUtc = newest == default ? (DateTimeOffset?)null : newest,
                sourceAgeMinutes = ageMinutes,
                stale = ageMinutes is null || ageMinutes > 60,
                checkedAtUtc = now
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
