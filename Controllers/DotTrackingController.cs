using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Secure, read-only RoadTech Falcon telemetry preview.
/// It does not create planning records or alter vehicle master data.
/// </summary>
[ApiController]
[Route("api/v1/tracking/dot")]
[Authorize(Policy = "TmsWrite")]
public sealed class DotTrackingController(
    DotTrackingClient trackingClient,
    ILogger<DotTrackingController> logger) : ControllerBase
{
    [HttpGet("telemetry")]
    [ProducesResponseType(typeof(DotTelemetryResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<DotTelemetryResponse>> GetCurrentTelemetry(CancellationToken cancellationToken)
    {
        try
        {
            var telemetry = await trackingClient.GetLatestVehicleEventsAsync(cancellationToken);

            return Ok(new DotTelemetryResponse(
                "RoadTech Falcon",
                DateTimeOffset.UtcNow,
                telemetry.Count,
                telemetry));
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "DOT Tracking configuration is not ready.");
            return Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "DOT Tracking is not configured",
                detail: "The provider settings are incomplete or the integration is disabled.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "RoadTech Falcon telemetry request failed.");
            return Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "DOT Tracking is unavailable",
                detail: "The provider could not be reached or rejected the request.");
        }
    }
}

public sealed record DotTelemetryResponse(
    string Provider,
    DateTimeOffset RetrievedAtUtc,
    int RecordCount,
    IReadOnlyList<RoadTechTelemetryItem> Records);
