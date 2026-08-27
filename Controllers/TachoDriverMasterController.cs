using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/driver-master")]
[Authorize]
public sealed class TachoDriverMasterController(
    TachoDriverMasterSyncService sync,
    DriverMasterClassificationService classification) : ControllerBase
{
    [HttpPost("tachomaster/sync")]
    [Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> Sync(CancellationToken ct)
    {
        var actor = User.Identity?.Name ?? "TMS user";

        // Repair/normalise persisted Driver Master detail before the canonical pass. In
        // particular this retires duplicate historical Tacho profile staging keys which would
        // otherwise make the sync's identity dictionary fail before any drivers are consolidated.
        await classification.ApplyAsync(actor, ct);

        var result = await sync.SyncAsync(actor, ct);
        if (!result.Success) return StatusCode(StatusCodes.Status502BadGateway, result);

        // Reapply controlled Type/Group/Agency/email rules to the newly canonical population.
        await classification.ApplyAsync(actor, ct);
        return Ok(result);
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