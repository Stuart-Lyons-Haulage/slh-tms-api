using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/site-geofence-sync")]
[Authorize]
public sealed class SiteGeofenceSyncController(TmsDbContext db) : ControllerBase
{
    [HttpGet("sites")]
    public async Task<IActionResult> Sites(CancellationToken ct)
        => Ok(await SiteGeofenceMasterSync.GetStatusAsync(db, ct));

    [HttpPost("sync-sites"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> SyncSites(CancellationToken ct)
        => Ok(await SiteGeofenceMasterSync.SyncAsync(db, ct));

    [HttpPost("geofences/{id:guid}/link"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> LinkGeofence(Guid id, LinkGeofenceRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.SiteCode))
            return BadRequest(new { error = "A canonical SITE### code is required." });

        try
        {
            return Ok(await SiteGeofenceMasterSync.LinkGeofenceAsync(db, id, request.SiteCode, ct));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}

public sealed record LinkGeofenceRequest(string SiteCode);
