using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/health/geofence-links")]
public sealed class GeofenceMasterDataHealthController(TmsDbContext db, IConfiguration configuration) : ControllerBase
{
    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        if (!TvWallboardAccess.IsAllowed(HttpContext, configuration)) return Unauthorized();

        var statuses = await EmbeddedGeofenceEngine.FenceStatusesAsync(db, ct);
        var linked = statuses.Count(status => status.SiteId is not null);
        var unlinked = statuses
            .Where(status => status.SiteId is null)
            .Select(status => Hash(status.Fence.Name))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        return Ok(new
        {
            total = statuses.Count,
            linked,
            unlinked = statuses.Count - linked,
            unlinkedFenceHashes = unlinked,
            source = "EmbeddedSLHGeofences+SiteMaster",
            checkedAtUtc = DateTimeOffset.UtcNow
        });
    }

    private static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim().ToUpperInvariant()));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..16];
    }
}
