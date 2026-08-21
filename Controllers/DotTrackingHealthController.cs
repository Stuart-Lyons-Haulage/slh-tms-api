using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/health/tracking")]
public sealed class DotTrackingHealthController(
    IServiceProvider services,
    DotTrackingOptions options,
    ILogger<DotTrackingHealthController> logger) : ControllerBase
{
    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var checkedAtUtc = DateTimeOffset.UtcNow;
        if (!options.IsConfigured)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                status = "unconfigured",
                configured = false,
                dataMask = options.DataMask,
                baseUrlValid = options.BaseUrlConfigurationError is null,
                configurationError = options.BaseUrlConfigurationError,
                checkedAtUtc
            });
        }

        try
        {
            // Resolve inside the guarded block so a malformed runtime setting can never
            // fail during controller construction and surface as an opaque HTTP 500.
            var trackingClient = services.GetRequiredService<DotTrackingClient>();

            var providerRows = await trackingClient.GetLatestVehicleEventsAsync(cancellationToken);
            var records = providerRows.Select(DotTelemetryRecord.FromProvider).ToList();
            var gpsRecords = records
                .Where(record => record.Latitude is not null && record.Longitude is not null)
                .ToList();

            if (gpsRecords.Count == 0)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new
                {
                    status = "no-current-gps",
                    configured = true,
                    dataMask = options.DataMask,
                    providerRecords = records.Count,
                    gpsRecords = 0,
                    checkedAtUtc,
                    message = "RoadTech current telemetry returned no GPS coordinates."
                });
            }

            // Health probes must be read-only. The one-minute ingestion service owns SQL
            // persistence and live-status freshness. Writing the same current telemetry here
            // can race that worker on the provider-event unique key and create a false 503
            // even though Falcon is healthy. Azure SQL readiness is checked independently.
            return Ok(new
            {
                status = "healthy",
                configured = true,
                dataMask = options.DataMask,
                pollIntervalMinutes = options.PollIntervalMinutes,
                providerRecords = records.Count,
                gpsRecords = gpsRecords.Count,
                newestProviderEventUtc = gpsRecords.Max(record => record.EventTimeUtc),
                checkedAtUtc
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "RoadTech live tracking health check failed.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                status = "upstream-failure",
                configured = true,
                dataMask = options.DataMask,
                baseUrlValid = options.BaseUrlConfigurationError is null,
                error = exception.GetType().Name,
                message = exception.Message,
                checkedAtUtc = DateTimeOffset.UtcNow
            });
        }
    }
}
