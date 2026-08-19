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
    [HttpGet]
    public async Task<IActionResult> Runs([FromQuery] DateOnly? date, CancellationToken ct)
    {
        var merged = new Dictionary<Guid, Load>();
        try
        {
            var query = db.Loads.AsNoTracking().Include(x => x.Stops).AsQueryable();
            if (date is not null) query = query.Where(x => x.PlanningDate == date.Value);
            foreach (var load in await query.OrderBy(x => x.PlanningDate).ThenBy(x => x.Reference).Take(1000).ToListAsync(ct))
                merged[load.Id] = load;
        }
        catch (Exception ex) when (PlanningResilience.SchemaUnavailable(ex))
        {
            db.ChangeTracker.Clear();
        }

        foreach (var load in await PlanningRegisterStore.ReadLoadsAsync(db, date, ct))
            if (!merged.ContainsKey(load.Id)) merged[load.Id] = load;

        var rows = merged.Values.OrderBy(x => x.PlanningDate).ThenBy(x => x.Reference).Take(1000).ToList();
        await RunOperationalStore.EnrichAsync(db, rows, ct);
        return Ok(rows);
    }

    [HttpPut("{id:guid}/allocation"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Allocate(Guid id, RunAllocationRequest request, CancellationToken ct)
    {
        var (load, register) = await FindLoadAsync(id, ct);
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
        }
        else
        {
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (PlanningResilience.SchemaUnavailable(ex))
            {
                db.ChangeTracker.Clear();
                await PlanningRegisterStore.SaveLoadAsync(db, load, User.Identity?.Name, ct);
            }
        }

        await RunOperationalStore.EnrichAsync(db, [load], ct);
        return Ok(load);
    }

    [HttpPut("{id:guid}/operational"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> UpdateOperational(Guid id, RunOperationalRequest request, CancellationToken ct)
    {
        if (request.PalletSpacesUsed < 0 || request.TotalPalletSpaces < 0)
            return BadRequest(new { message = "Capacity values cannot be negative." });

        var (load, register) = await FindLoadAsync(id, ct);
        if (load is null) return NotFound(new { message = "The run could not be found." });

        var values = new RunOperationalValues(
            request.PalletSpacesUsed,
            request.TotalPalletSpaces,
            Clip(request.CapacityType, 40) ?? "Standard pallets",
            Clip(request.DepotSplits, 1000),
            request.TemperatureC,
            Clip(request.PlannerNotes, 1000));

        await RunOperationalStore.SaveAsync(db, load, values, User.Identity?.Name, ct);
        if (register) await PlanningRegisterStore.SaveLoadAsync(db, load, User.Identity?.Name, ct);
        return Ok(load);
    }

    private async Task<(Load? Load, bool Register)> FindLoadAsync(Guid id, CancellationToken ct)
    {
        try
        {
            var load = await db.Loads.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (load is not null)
            {
                await RunOperationalStore.EnrichAsync(db, [load], ct);
                return (load, false);
            }
        }
        catch (Exception ex) when (PlanningResilience.SchemaUnavailable(ex))
        {
            db.ChangeTracker.Clear();
        }

        var registered = await PlanningRegisterStore.GetLoadAsync(db, id, ct);
        if (registered is not null) await RunOperationalStore.EnrichAsync(db, [registered], ct);
        return (registered, registered is not null);
    }

    private static string? Clip(string? value, int length) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, length)];
}

public sealed record RunAllocationRequest(Guid? VehicleId, Guid? DriverId, Guid? TrailerId);
public sealed record RunOperationalRequest(decimal? PalletSpacesUsed, decimal? TotalPalletSpaces, string? CapacityType, string? DepotSplits, decimal? TemperatureC, string? PlannerNotes);
