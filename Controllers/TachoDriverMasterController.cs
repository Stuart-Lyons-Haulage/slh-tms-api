using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/driver-master")]
[Authorize]
public sealed class TachoDriverMasterController(
    TachoDriverMasterSyncJobService jobs,
    TachoDriverMasterSyncService sync) : ControllerBase
{
    [HttpPost("tachomaster/sync")]
    public async Task<IActionResult> Sync(CancellationToken ct)
    {
        var actor = User.Identity?.Name ?? "TMS user";
        var job = await jobs.EnqueueAsync(actor, ct);
        return AcceptedAtAction(nameof(SyncStatus), new { jobId = job.JobId }, job);
    }

    [HttpGet("tachomaster/sync/{jobId:guid}")]
    public async Task<IActionResult> SyncStatus(Guid jobId, CancellationToken ct)
    {
        var job = await jobs.GetAsync(jobId, ct);
        return job is null ? NotFound() : Ok(job);
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
