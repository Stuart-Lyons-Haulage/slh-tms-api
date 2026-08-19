using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/site-master-register")]
[Authorize]
public sealed class SiteMasterRegisterController(TmsDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] bool includeInactive = false, CancellationToken ct = default)
    {
        var rows = await db.Sites.AsNoTracking()
            .Where(site => includeInactive || site.Active)
            .OrderBy(site => site.Name)
            .Take(5000)
            .ToListAsync(ct);

        await MasterDetailStore.EnrichSitesAsync(db, rows, ct);
        return Ok(rows);
    }
}
