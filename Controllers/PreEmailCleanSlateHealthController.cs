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

        var loads = await CountLoads(ct);
        var loadStops = loads.Stops;
        var orders = await CountOrders(ct);
        var activeDrivers = await db.Drivers.AsNoTracking().CountAsync(driver => driver.Active, ct);
        var blankCanvas = loads.Count == 0 && loadStops == 0 && orders == 0;

        var response = new
        {
            status = blankCanvas ? "completed" : "incomplete",
            completedAtUtc = marker.ReviewedAtUtc,
            blankCanvas,
            current = new
            {
                loads = loads.Count,
                loadStops,
                orders,
                activeDrivers,
                loadStorage = loads.Source
            },
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
        };

        return blankCanvas
            ? Ok(response)
            : StatusCode(StatusCodes.Status503ServiceUnavailable, response);
    }

    private async Task<(int Count, int Stops, string Source)> CountLoads(CancellationToken ct)
    {
        try
        {
            var count = await db.Loads.AsNoTracking().CountAsync(ct);
            var stops = await db.LoadStops.AsNoTracking().CountAsync(ct);
            return (count, stops, "SQL");
        }
        catch (Exception exception) when (SchemaUnavailable(exception))
        {
            db.ChangeTracker.Clear();
            var loads = await PlanningRegisterStore.ReadLoadsAsync(db, null, ct);
            return (loads.Count, loads.Sum(load => load.Stops.Count), "audited-register");
        }
    }

    private async Task<int> CountOrders(CancellationToken ct)
    {
        try { return await db.TransportOrders.AsNoTracking().CountAsync(ct); }
        catch (Exception exception) when (SchemaUnavailable(exception))
        {
            db.ChangeTracker.Clear();
            return (await PlanningRegisterStore.ReadOrdersAsync(db, null, null, ct)).Count;
        }
    }

    private static bool SchemaUnavailable(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        return message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Cannot find the object", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("does not exist", StringComparison.OrdinalIgnoreCase);
    }
}
