using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/health/pre-email-clean-slate")]
public sealed class PreEmailCleanSlateHealthController(TmsDbContext db) : ControllerBase
{
    [HttpGet, AllowAnonymous]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var marker = await db.StagedImports.AsNoTracking()
            .SingleOrDefaultAsync(row => row.IdempotencyKey == PreEmailCleanSlateMaintenance.MarkerKey, ct);
        if (marker is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                status = "pending",
                message = "The one-time pre-email clean slate has not completed."
            });

        PreEmailCleanSlateResult? result = null;
        try { result = JsonSerializer.Deserialize<PreEmailCleanSlateResult>(marker.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (JsonException) { }

        var loads = await SafeCount(() => db.Loads.AsNoTracking().CountAsync(ct));
        var orders = await SafeCount(() => db.TransportOrders.AsNoTracking().CountAsync(ct));
        var activeDrivers = await db.Drivers.AsNoTracking().CountAsync(driver => driver.Active, ct);

        return Ok(new
        {
            status = "completed",
            completedAtUtc = marker.ReviewedAtUtc,
            current = new { loads, orders, activeDrivers },
            result = result is null ? null : new
            {
                result.LoadStopsDeleted,
                result.LoadsDeleted,
                result.OrdersDeleted,
                result.EtaSnapshotsDeleted,
                result.DriverStatusLogsDeleted,
                result.GeofenceVisitsDetached,
                result.OperationalStagingRowsDeleted,
                archivedDrivers = result.ArchivedDrivers.Count,
                result.DriverArchiveSkipped
            }
        });
    }

    private static async Task<int?> SafeCount(Func<Task<int>> action)
    {
        try { return await action(); }
        catch { return null; }
    }
}
