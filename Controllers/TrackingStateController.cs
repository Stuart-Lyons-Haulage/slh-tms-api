using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/tracking/state")]
[Authorize]
public sealed class TrackingStateController(
    DotTrackingClient trackingClient,
    IConfiguration configuration,
    ILogger<TrackingStateController> logger) : ControllerBase
{
    private static readonly TimeSpan ProbeBudget = TimeSpan.FromSeconds(4);

    [HttpGet, AllowAnonymous]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        if (!TvWallboardAccess.IsAllowed(HttpContext, configuration)) return Unauthorized();

        var checkedAtUtc = DateTimeOffset.UtcNow;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ProbeBudget);
            var records = await trackingClient.GetLatestVehicleEventsAsync(timeout.Token);
            return Ok(new
            {
                trackingState = "Available",
                checkedAtUtc,
                provider = "RoadTech",
                recordCount = records.Count
            });
        }
        catch (OperationCanceledException exception) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(exception, "RoadTech availability probe timed out.");
            return Ok(new
            {
                trackingState = "Unavailable",
                checkedAtUtc,
                provider = "RoadTech",
                recordCount = (int?)null,
                warning = "RoadTech tracking is temporarily unavailable. Stored TMS planning and tracking evidence remains authoritative until the provider recovers."
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "RoadTech availability probe failed.");
            return Ok(new
            {
                trackingState = "Unavailable",
                checkedAtUtc,
                provider = "RoadTech",
                recordCount = (int?)null,
                warning = "RoadTech tracking is temporarily unavailable. Stored TMS planning and tracking evidence remains authoritative until the provider recovers."
            });
        }
    }
}
