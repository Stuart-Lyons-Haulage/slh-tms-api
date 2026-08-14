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
        var checks = new Dictionary<string, Func<CancellationToken, Task<object>>>
        {
            ["customers"] = token => Verify(db.Customers.AsNoTracking(), token),
            ["customerContacts"] = token => Verify(db.CustomerContacts.AsNoTracking(), token),
            ["vehicles"] = token => Verify(db.Vehicles.AsNoTracking(), token),
            ["drivers"] = token => Verify(db.Drivers.AsNoTracking(), token),
            ["trailers"] = token => Verify(db.Trailers.AsNoTracking(), token),
            ["sites"] = token => Verify(db.Sites.AsNoTracking(), token),
            ["marketContacts"] = token => Verify(db.MarketContacts.AsNoTracking(), token),
            ["staging"] = token => Verify(db.StagedImports.AsNoTracking(), token),
            ["orders"] = token => Verify(db.TransportOrders.AsNoTracking(), token),
            ["loads"] = token => Verify(db.Loads.AsNoTracking(), token),
            ["loadStops"] = token => Verify(db.LoadStops.AsNoTracking(), token),
            ["vehicleLiveStatuses"] = token => Verify(db.VehicleLiveStatuses.AsNoTracking(), token)
        };

        var results = new Dictionary<string, object>();
        foreach (var check in checks)
        {
            try { results[check.Key] = await check.Value(ct); }
            catch (Exception ex) { results[check.Key] = new { ok = false, error = ex.GetBaseException().Message }; }
        }

        return Ok(results);
    }

    private static async Task<object> Verify<TEntity>(IQueryable<TEntity> query, CancellationToken ct)
    {
        var count = await query.CountAsync(ct);
        // COUNT(*) can succeed even when mapped columns are missing. Reading one
        // row verifies the same shape used by the portal and Master Import.
        await query.Take(1).ToListAsync(ct);
        return new { ok = true, count };
    }
}
