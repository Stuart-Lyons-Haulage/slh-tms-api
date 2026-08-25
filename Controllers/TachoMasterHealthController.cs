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
            var today = UkOperatingDate(now);
            var profilesTask = tachoMasterClient.GetDriverProfilesAsync(cancellationToken);
            var openDutiesTask = tachoMasterClient.GetOpenDriverStatusesByVehicleAsync(today, cancellationToken);
            var dayDutiesTask = tachoMasterClient.GetDriverDutyStatusesAsync(today, cancellationToken);
            await Task.WhenAll(profilesTask, openDutiesTask, dayDutiesTask);
            var profiles = await profilesTask;
            var duties = await openDutiesTask;
            var dayDuties = await dayDutiesTask;
            var lastSuccessfulPollUtc = DateTimeOffset.UtcNow;
            var openDuties = duties.Values.SelectMany(items => items).ToList();

            var newestMetric = profiles.Select(item => item.MetricsValidAtUtc)
                .Concat(openDuties.Select(item => item.MetricsValidAtUtc))
                .Where(item => item is not null)
                .Select(item => item!.Value)
                .DefaultIfEmpty()
                .Max();
            var metricsAgeMinutes = newestMetric == default ? (double?)null : Math.Max(0, Math.Round((lastSuccessfulPollUtc - newestMetric).TotalMinutes, 1));
            var metricsFreshness = metricsAgeMinutes switch
            {
                null => "unknown",
                <= 15 => "live",
                <= 60 => "delayed",
                _ => "stale"
            };
            var metricsStale = metricsAgeMinutes is null || metricsAgeMinutes > 60;
            var latestDutyStartUtc = dayDuties.Count == 0
                ? (DateTimeOffset?)null
                : dayDuties.Max(item => item.DutyStartUtc);
            var latestDutyEndUtc = dayDuties
                .Where(item => item.DutyEndUtc is not null)
                .Select(item => item.DutyEndUtc)
                .OrderByDescending(value => value)
                .FirstOrDefault();

            return Ok(new
            {
                status = "healthy",
                configured = true,
                usesSharedRoadTechCredentials = tachoMasterClient.UsesSharedRoadTechCredentials,
                operatingDate = today,
                driverProfiles = profiles.Count,
                dayDutyRecords = dayDuties.Count,
                dayDutyVehicles = dayDuties.Select(item => item.VehicleCode).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                dayDutiesWithEnd = dayDuties.Count(item => item.DutyEndUtc is not null),
                dayDutiesWithoutEnd = dayDuties.Count(item => item.DutyEndUtc is null),
                latestDutyStartUtc,
                latestDutyEndUtc,
                currentVehicleDuties = openDuties.Count,
                openVehicleDuties = openDuties.Count,
                connectionFreshness = "live",
                lastSuccessfulPollUtc,
                metricsFreshness,
                newestMetricsTimestampUtc = newestMetric == default ? (DateTimeOffset?)null : newestMetric,
                metricsAgeMinutes,
                metricsStale,
                sourceFreshness = metricsFreshness,
                newestSourceTimestampUtc = newestMetric == default ? (DateTimeOffset?)null : newestMetric,
                sourceAgeMinutes = metricsAgeMinutes,
                stale = metricsStale,
                checkedAtUtc = lastSuccessfulPollUtc
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

    private static DateOnly UkOperatingDate(DateTimeOffset value)
    {
        try
        {
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, TimeZoneInfo.FindSystemTimeZoneById("Europe/London")).DateTime);
        }
        catch (TimeZoneNotFoundException)
        {
            return DateOnly.FromDateTime(value.UtcDateTime);
        }
    }
}
