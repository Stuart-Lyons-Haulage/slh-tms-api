using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/diagnostics")]
[Authorize]
public sealed class DiagnosticsController(TmsDbContext db) : ControllerBase
{
    [HttpGet("tables")]
    public async Task<IActionResult> Tables(CancellationToken ct)
    {
        var checks = new Dictionary<string, Func<CancellationToken, Task<int>>>
        {
            ["customers"] = token => db.Customers.AsNoTracking().CountAsync(token),
            ["customerContacts"] = token => db.CustomerContacts.AsNoTracking().CountAsync(token),
            ["vehicles"] = token => db.Vehicles.AsNoTracking().CountAsync(token),
            ["drivers"] = token => db.Drivers.AsNoTracking().CountAsync(token),
            ["trailers"] = token => db.Trailers.AsNoTracking().CountAsync(token),
            ["sites"] = token => db.Sites.AsNoTracking().CountAsync(token),
            ["marketContacts"] = token => db.MarketContacts.AsNoTracking().CountAsync(token),
            ["staging"] = token => db.StagedImports.AsNoTracking().CountAsync(token),
            ["orders"] = token => db.TransportOrders.AsNoTracking().CountAsync(token),
            ["loads"] = token => db.Loads.AsNoTracking().CountAsync(token),
            ["loadStops"] = token => db.LoadStops.AsNoTracking().CountAsync(token),
            ["vehicleLiveStatuses"] = token => db.VehicleLiveStatuses.AsNoTracking().CountAsync(token)
        };

        var results = new Dictionary<string, object>();
        foreach (var check in checks)
        {
            try { results[check.Key] = new { ok = true, count = await check.Value(ct) }; }
            catch (Exception ex) { results[check.Key] = new { ok = false, error = ex.GetBaseException().Message }; }
        }

        return Ok(results);
    }
}
