using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/runs"), Authorize]
public sealed class RunAllocationResilienceController(TmsDbContext db) : ControllerBase
{
    [HttpPut("{id:guid}/allocation"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Allocate(Guid id, RunAllocationRequest request, CancellationToken ct)
    {
        Load? load = null;
        var register = false;

        try
        {
            load = await db.Loads.SingleOrDefaultAsync(x => x.Id == id, ct);
        }
        catch (Exception ex) when (PlanningResilience.SchemaUnavailable(ex))
        {
            db.ChangeTracker.Clear();
        }

        if (load is null)
        {
            load = await PlanningRegisterStore.GetLoadAsync(db, id, ct);
            register = load is not null;
        }
        if (load is null) return NotFound(new { message = "The run could not be found." });

        if (request.VehicleId is Guid vehicleId && !await db.Vehicles.AsNoTracking().AnyAsync(x => x.Id == vehicleId && x.Active, ct))
            return BadRequest(new { message = "Vehicle is not active." });
        if (request.DriverId is Guid driverId && !await db.Drivers.AsNoTracking().AnyAsync(x => x.Id == driverId && x.Active, ct))
            return BadRequest(new { message = "Driver is not active." });
        if (request.TrailerId is Guid trailerId && !await db.Trailers.AsNoTracking().AnyAsync(x => x.Id == trailerId && x.Active, ct))
            return BadRequest(new { message = "Trailer is not active." });

        load.VehicleId = request.VehicleId;
        load.DriverId = request.DriverId;
        load.TrailerId = request.TrailerId;
        load.Status = request.VehicleId is not null && request.DriverId is not null ? LoadStatus.Planned : LoadStatus.Draft;

        if (register)
        {
            await PlanningRegisterStore.SaveLoadAsync(db, load, User.Identity?.Name, ct);
            return Ok(load);
        }

        try
        {
            await db.SaveChangesAsync(ct);
            return Ok(load);
        }
        catch (Exception ex) when (PlanningResilience.SchemaUnavailable(ex))
        {
            db.ChangeTracker.Clear();
            await PlanningRegisterStore.SaveLoadAsync(db, load, User.Identity?.Name, ct);
            return Ok(load);
        }
    }
}

public sealed record RunAllocationRequest(Guid? VehicleId, Guid? DriverId, Guid? TrailerId);
