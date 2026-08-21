using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/health/tracking")]
public sealed class DotTrackingHealthController(
    DotTrackingClient trackingClient,
    DotTrackingTelemetryStore telemetryStore,
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
                checkedAtUtc
            });
        }

        try
        {
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

            // A successful current-telemetry health probe is also a valid live observation,
            // so seed the same cache used by the TV rather than waiting for the next poll.
            await telemetryStore.PersistAsync(records, cancellationToken, updateLiveStatus: true);

            var newestProviderEventUtc = gpsRecords.Max(record => record.EventTimeUtc);
            return Ok(new
            {
                status = "healthy",
                configured = true,
                dataMask = options.DataMask,
                pollIntervalMinutes = options.PollIntervalMinutes,
                providerRecords = records.Count,
                gpsRecords = gpsRecords.Count,
                newestProviderEventUtc,
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
                error = exception.GetType().Name,
                message = exception.Message,
                checkedAtUtc = DateTimeOffset.UtcNow
            });
        }
    }
}
