using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;

namespace Slh.Tms.Api.Controllers;
[ApiController, Route("api/v1")]
[Authorize]
public sealed class LookupsController(TmsDbContext db) : ControllerBase
{
    [HttpGet("customers")] public async Task<IActionResult> Customers([FromQuery] string? q, CancellationToken ct) => Ok(await db.Customers.AsNoTracking().Where(x => x.Active && (q == null || x.Code.Contains(q) || x.Name.Contains(q))).OrderBy(x => x.Name).Take(100).ToListAsync(ct));
    [HttpGet("vehicles")] public async Task<IActionResult> Vehicles([FromQuery] string? q, CancellationToken ct) => Ok(await db.Vehicles.AsNoTracking().Where(x => x.Active && (q == null || x.Registration.Contains(q) || (x.FleetNumber != null && x.FleetNumber.Contains(q)))).OrderBy(x => x.Registration).Take(100).ToListAsync(ct));
    [HttpGet("drivers")] public async Task<IActionResult> Drivers([FromQuery] string? q, CancellationToken ct) => Ok(await db.Drivers.AsNoTracking().Where(x => x.Active && (q == null || x.EmployeeNumber.Contains(q) || x.DisplayName.Contains(q))).OrderBy(x => x.DisplayName).Take(100).ToListAsync(ct));
}
