using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
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

    [HttpPost("sync"), Authorize(Policy = "TmsMasterData")]
    public async Task<IActionResult> Sync(CancellationToken ct)
    {
        if (!TvWallboardAccess.IsAllowed(HttpContext, configuration)) return Unauthorized();

        var result = await SiteMasterConsolidation.ReconcileAsync(db, "production-geofence-site-sync", ct);
        var changed = result.PromotedCustomers > 0 || result.ArchivedDuplicates > 0 ||
                      result.LinkedGeofences > 0 || result.CanonicalizedGeofences > 0;
        var reprojected = false;
        DateOnly? planningDate = null;

        if (changed)
        {
            var london = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
            planningDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, london).DateTime);
            await EmbeddedGeofenceSqlProjection.RefreshOperatingDaysAsync(db, new[] { planningDate.Value }, ct);
            reprojected = true;
        }

        var pendingSiteReviews = await db.StagedImports.AsNoTracking()
            .CountAsync(x => x.EntityType == "masterdata:site-review" && x.Status == StagingStatus.PendingReview, ct);
        var pendingGeofenceReviews = await db.StagedImports.AsNoTracking()
            .CountAsync(x => x.EntityType == "masterdata:geofence-review" && x.Status == StagingStatus.PendingReview, ct);

        return Ok(new
        {
            result.PromotedCustomers,
            result.ArchivedDuplicates,
            result.LinkedGeofences,
            result.CanonicalizedGeofences,
            result.NeedsReview,
            pendingSiteReviews,
            pendingGeofenceReviews,
            reprojected,
            planningDate,
            source = "SiteMasterConsolidation+EmbeddedGeofenceProjection",
            syncedAtUtc = DateTimeOffset.UtcNow
        });
    }

    private static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim().ToUpperInvariant()));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..16];
    }
}
