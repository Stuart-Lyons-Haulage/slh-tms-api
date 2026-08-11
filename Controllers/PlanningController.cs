using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1")]
[Authorize]
public sealed class PlanningController(TmsDbContext db, AzureMapsRouteClient maps) : ControllerBase
{
    [HttpGet("orders")]
    public async Task<IActionResult> Orders([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct)
    {
        var query = db.TransportOrders.AsNoTracking().AsQueryable();
        if (from is not null) query = query.Where(order => order.CollectionDate >= from);
        if (to is not null) query = query.Where(order => order.CollectionDate <= to);
        return Ok(await query.OrderBy(order => order.CollectionDate).ThenBy(order => order.Reference).Take(1000).ToListAsync(ct));
    }

    [HttpGet("loads")]
    public async Task<IActionResult> Loads([FromQuery] DateOnly? date, CancellationToken ct)
    {
        var query = db.Loads.AsNoTracking().Include(load => load.Stops).AsQueryable();
        if (date is not null) query = query.Where(load => load.PlanningDate == date);
        return Ok(await query.OrderBy(load => load.PlanningDate).ThenBy(load => load.Reference).Take(500).ToListAsync(ct));
    }

    [HttpPost("loads"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> CreateLoad(CreateLoadRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Reference) || request.Stops.Count == 0) return BadRequest("A reference and at least one stop are required.");
        if (await db.Loads.AnyAsync(load => load.Reference == request.Reference, ct)) return Conflict("A load with this reference already exists.");
        var load = new Load { Reference = request.Reference.Trim(), PlanningDate = request.PlanningDate, VehicleId = request.VehicleId, DriverId = request.DriverId, TrailerId = request.TrailerId, Status = LoadStatus.Draft,
            Stops = request.Stops.Select((stop, index) => new LoadStop { OrderId = stop.OrderId, Sequence = index + 1, Name = stop.Name.Trim(), Address = stop.Address, Latitude = stop.Latitude, Longitude = stop.Longitude, PlannedArrivalUtc = stop.PlannedArrivalUtc }).ToList() };
        db.Loads.Add(load); await db.SaveChangesAsync(ct); return Created($"/api/v1/loads/{load.Id}", load);
    }

    [HttpGet("loads/{id:guid}/route")]
    public async Task<IActionResult> Route(Guid id, CancellationToken ct)
    {
        var points = await db.LoadStops.AsNoTracking().Where(stop => stop.LoadId == id && stop.Longitude != null && stop.Latitude != null)
            .OrderBy(stop => stop.Sequence).Select(stop => new { stop.Longitude, stop.Latitude }).ToListAsync(ct);
        return Ok(await maps.Directions(points.Select(point => (point.Longitude!.Value, point.Latitude!.Value)).ToList(), ct));
    }
}

public sealed record CreateLoadRequest(string Reference, DateOnly PlanningDate, Guid? VehicleId, Guid? DriverId, Guid? TrailerId, List<CreateLoadStopRequest> Stops);
public sealed record CreateLoadStopRequest(Guid? OrderId, string Name, string? Address, decimal? Latitude, decimal? Longitude, DateTimeOffset? PlannedArrivalUtc);
