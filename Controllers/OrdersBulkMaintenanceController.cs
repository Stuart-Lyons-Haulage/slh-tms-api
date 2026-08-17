using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/orders"), Authorize]
public sealed class OrdersBulkMaintenanceController(TmsDbContext db, ILogger<OrdersBulkMaintenanceController> logger) : ControllerBase
{
    [HttpDelete("open"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> CancelAllOpen(CancellationToken ct)
    {
        var ids = await db.TransportOrders.AsNoTracking()
            .Where(order => order.Status != OrderStatus.Cancelled && order.Status != OrderStatus.Delivered)
            .Select(order => order.Id)
            .ToListAsync(ct);

        if (ids.Count == 0)
            return Ok(new { cancelled = 0, removedStops = 0, message = "There are no open jobs to clear." });

        var removedStops = 0;
        try
        {
            removedStops = await db.LoadStops.Where(stop => stop.OrderId != null && ids.Contains(stop.OrderId.Value)).ExecuteDeleteAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Linked load stops could not all be removed while clearing open jobs. The orders will still be cancelled.");
        }

        var cancelled = await db.TransportOrders
            .Where(order => ids.Contains(order.Id))
            .ExecuteUpdateAsync(setters => setters.SetProperty(order => order.Status, OrderStatus.Cancelled), ct);

        return Ok(new
        {
            cancelled,
            removedStops,
            message = $"Cleared {cancelled} open job(s) from planning and removed {removedStops} linked run stop(s). Delivered history was retained."
        });
    }
}
