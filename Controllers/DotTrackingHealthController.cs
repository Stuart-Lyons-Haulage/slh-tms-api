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
            var driverNameRecords = records.Count(record => !string.IsNullOrWhiteSpace(record.DriverName));
            var driverCardRecords = records.Count(record => !string.IsNullOrWhiteSpace(record.DriverCardNumber));
            var driverEvidenceRecords = records.Count(record =>
                !string.IsNullOrWhiteSpace(record.DriverName) ||
                !string.IsNullOrWhiteSpace(record.DriverCardNumber));
            var extraPayloadSections = providerRows
                .SelectMany(row => row.Extra.Keys)
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                .Take(50)
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
                    driverEvidenceRecords,
                    driverNameRecords,
                    driverCardRecords,
                    extraPayloadSections,
                    checkedAtUtc,
                    message = "RoadTech current telemetry returned no GPS coordinates."
                });
            }

            // Health probes are deliberately read-only. The one-minute ingestion worker owns
            // persistence/live-status freshness; writing here could race the worker and make a
            // healthy RoadTech feed fail its own diagnostic because of a SQL write collision.
            // Driver diagnostics remain aggregate-only: do not expose names/card numbers here.
            return Ok(new
            {
                status = "healthy",
                configured = true,
                dataMask = options.DataMask,
                pollIntervalMinutes = options.PollIntervalMinutes,
                providerRecords = records.Count,
                gpsRecords = gpsRecords.Count,
                driverEvidenceRecords,
                driverNameRecords,
                driverCardRecords,
                extraPayloadSections,
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
