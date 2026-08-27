using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/health/driver-master")]
[AllowAnonymous]
public sealed class DriverMasterHealthController(TachoDriverMasterSyncService sync) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var quality = await sync.QualityAsync(ct);
        return Ok(new
        {
            status = quality.DuplicateMemberGroups == 0 && quality.DuplicateCardGroups == 0 && quality.ActiveWithoutMember == 0
                ? "healthy"
                : "attention",
            quality.ActiveDrivers,
            quality.ActiveWithMember,
            quality.ActiveWithCard,
            quality.DuplicateMemberGroups,
            quality.DuplicateCardGroups,
            quality.ActiveWithoutMember,
            quality.ActiveWithoutCard,
            quality.LatestCanonicalSyncUtc
        });
    }
}
