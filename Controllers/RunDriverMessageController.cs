using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/loads"), Authorize]
public sealed class RunDriverMessageController(TmsDbContext db, DriverSmsDispatchService sms) : ControllerBase
{
    [HttpPost("{id:guid}/driver-message/sms"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Send(Guid id, RunDriverMessageRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Message)) return BadRequest(new { message = "The driver message is empty." });
        if (request.Message.Length > 5000) return BadRequest(new { message = "The driver message is too long." });

        Load? load = null;
        var register = false;
        try
        {
            load = await db.Loads.SingleOrDefaultAsync(item => item.Id == id, ct);
        }
        catch (Exception exception) when (IsSchemaUnavailable(exception))
        {
            db.ChangeTracker.Clear();
            register = true;
        }

        if (load is null)
        {
            load = await PlanningRegisterStore.GetLoadAsync(db, id, ct);
            register = load is not null;
        }
        if (load is null) return NotFound(new { message = "The run could not be found." });
        if (load.DriverId is null || load.VehicleId is null) return BadRequest(new { message = "Allocate both a driver and vehicle before sending the driver text." });

        var driver = await db.Drivers.AsNoTracking().SingleOrDefaultAsync(item => item.Id == load.DriverId, ct);
        if (driver is null) return BadRequest(new { message = "The allocated driver could not be found." });
        if (string.IsNullOrWhiteSpace(driver.MobileNumber)) return BadRequest(new { message = "The assigned driver has no approved mobile number." });

        var receipt = await sms.SendAsync(driver.MobileNumber, request.Message.Trim(), ct);
        if (load.Status == LoadStatus.Planned) load.Status = LoadStatus.Dispatched;

        if (register) await PlanningRegisterStore.SaveLoadAsync(db, load, User.Identity?.Name, ct);
        else await db.SaveChangesAsync(ct);

        return Accepted(new
        {
            receipt.MessageId,
            receipt.MobileSuffix,
            receipt.Provider,
            load.Status
        });
    }

    private static bool IsSchemaUnavailable(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        return exception is InvalidOperationException or DbUpdateException ||
            message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Cannot find the object", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record RunDriverMessageRequest(string Message);
