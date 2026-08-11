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
    [HttpGet("customer-contacts")] public async Task<IActionResult> CustomerContacts([FromQuery] string? q, CancellationToken ct) => Ok(await db.CustomerContacts.AsNoTracking().Where(x => x.Active && (q == null || x.CustomerCode.Contains(q) || x.Name.Contains(q) || (x.Email != null && x.Email.Contains(q)))).OrderBy(x => x.CustomerCode).ThenBy(x => x.Name).Take(500).ToListAsync(ct));
    [HttpGet("vehicles")] public async Task<IActionResult> Vehicles([FromQuery] string? q, CancellationToken ct) => Ok(await db.Vehicles.AsNoTracking().Where(x => x.Active && (q == null || x.Registration.Contains(q) || (x.FleetNumber != null && x.FleetNumber.Contains(q)))).OrderBy(x => x.Registration).Take(100).ToListAsync(ct));
    [HttpGet("drivers")] public async Task<IActionResult> Drivers([FromQuery] string? q, CancellationToken ct) => Ok(await db.Drivers.AsNoTracking().Where(x => x.Active && (q == null || x.EmployeeNumber.Contains(q) || x.DisplayName.Contains(q))).OrderBy(x => x.DisplayName).Take(100).ToListAsync(ct));
    [HttpGet("trailers")] public async Task<IActionResult> Trailers([FromQuery] string? q, CancellationToken ct) => Ok(await db.Trailers.AsNoTracking().Where(x => x.Active && (q == null || x.TrailerNumber.Contains(q) || (x.Type != null && x.Type.Contains(q)))).OrderBy(x => x.TrailerNumber).Take(100).ToListAsync(ct));
    [HttpGet("sites")] public async Task<IActionResult> Sites([FromQuery] string? q, CancellationToken ct) => Ok(await db.Sites.AsNoTracking().Where(x => x.Active && (q == null || x.Name.Contains(q) || (x.DriverTextName != null && x.DriverTextName.Contains(q)))).OrderBy(x => x.Name).Take(100).ToListAsync(ct));
    [HttpGet("market-contacts")] public async Task<IActionResult> MarketContacts([FromQuery] string? q, CancellationToken ct) => Ok(await db.MarketContacts.AsNoTracking().Where(x => x.Active && (q == null || x.Name.Contains(q) || x.Market.Contains(q))).OrderBy(x => x.Market).ThenBy(x => x.Name).Take(100).ToListAsync(ct));
}
