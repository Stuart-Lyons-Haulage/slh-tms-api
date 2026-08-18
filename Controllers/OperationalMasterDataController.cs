using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/operational-master-data")]
[Authorize]
public sealed class OperationalMasterDataController(TmsDbContext db) : ControllerBase
{
    [HttpGet("drivers/search")]
    public async Task<IActionResult> SearchDrivers([FromQuery] string q, CancellationToken ct)
    {
        q ??= string.Empty;
        var result = await db.Drivers
            .Where(x => x.Active && x.DisplayName.Contains(q))
            .OrderBy(x => x.DisplayName)
            .Take(25)
            .Select(x => new { x.Id, x.DisplayName, x.EmployeeNumber, x.TachoName, x.Active })
            .ToListAsync(ct);
        return Ok(result);
    }

    [HttpPost("drivers/{id:guid}/archive")]
    [Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> ArchiveDriver(Guid id, CancellationToken ct)
    {
        var driver = await db.Drivers.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (driver is null) return NotFound();
        driver.Active = false;
        db.Add(new MasterDataAudit { EntityType = "Driver", EntityId = id, Action = "Archive", ChangedBy = User.Identity?.Name });
        await db.SaveChangesAsync(ct);
        return Ok(new { archived = true });
    }
}
