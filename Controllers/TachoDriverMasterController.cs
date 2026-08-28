using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/driver-master")]
[Authorize]
public sealed class TachoDriverMasterController(
    TachoCanonicalDriverMasterOrchestrator orchestrator,
    TachoDriverMasterSyncService sync) : ControllerBase
{
    [HttpPost("tachomaster/sync")]
    [Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> Sync(CancellationToken ct)
    {
        var actor = User.Identity?.Name ?? "TMS user";
        var result = await orchestrator.RunAsync(actor, ct);

        // Preserve the existing Master Data endpoint response contract so the portal does not
        // need to change. Manual and scheduled syncs now execute the same canonical orchestration.
        if (!result.Success) return StatusCode(StatusCodes.Status502BadGateway, result.Canonical);
        return Ok(result.Canonical);
    }

    [HttpGet("tachomaster/quality")]
    public async Task<IActionResult> Quality(CancellationToken ct)
        => Ok(await sync.QualityAsync(ct));

    [HttpGet("{driverId:guid}/tachomaster-profile")]
    public async Task<IActionResult> Profile(Guid driverId, CancellationToken ct)
    {
        var profile = await sync.ProfileAsync(driverId, ct);
        return profile is null ? NotFound() : Ok(profile);
    }
}
