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

    [HttpPut("loads/{id:guid}/allocation"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Allocate(Guid id, UpdateLoadAllocationRequest request, CancellationToken ct)
    {
        var load = await db.Loads.SingleOrDefaultAsync(item => item.Id == id, ct);
        if (load is null) return NotFound();
        if (request.VehicleId is not null && !await db.Vehicles.AnyAsync(vehicle => vehicle.Id == request.VehicleId && vehicle.Active, ct)) return BadRequest("Vehicle is not active.");
        if (request.DriverId is not null && !await db.Drivers.AnyAsync(driver => driver.Id == request.DriverId && driver.Active, ct)) return BadRequest("Driver is not active.");
        if (request.TrailerId is not null && !await db.Trailers.AnyAsync(trailer => trailer.Id == request.TrailerId && trailer.Active, ct)) return BadRequest("Trailer is not active.");
        load.VehicleId = request.VehicleId; load.DriverId = request.DriverId; load.TrailerId = request.TrailerId;
        load.Status = request.VehicleId is not null && request.DriverId is not null ? LoadStatus.Planned : LoadStatus.Draft;
        await db.SaveChangesAsync(ct); return Ok(load);
    }

    [HttpPut("loads/{id:guid}/status"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> UpdateStatus(Guid id, UpdateLoadStatusRequest request, CancellationToken ct)
    {
        var load = await db.Loads.Include(item => item.Stops).SingleOrDefaultAsync(item => item.Id == id, ct);
        if (load is null) return NotFound();
        if (!Enum.TryParse<LoadStatus>(request.Status, true, out var next)) return BadRequest("The requested load status is not valid.");
        if (!CanTransition(load.Status, next)) return BadRequest($"A load cannot move from {load.Status} to {next}.");
        if ((next is LoadStatus.Dispatched or LoadStatus.InProgress) && (load.DriverId is null || load.VehicleId is null)) return BadRequest("Allocate both a driver and vehicle before dispatching a load.");

        load.Status = next;
        var orderIds = load.Stops.Where(stop => stop.OrderId is not null).Select(stop => stop.OrderId!.Value).ToList();
        if (orderIds.Count > 0)
        {
            var orders = await db.TransportOrders.Where(order => orderIds.Contains(order.Id)).ToListAsync(ct);
            foreach (var order in orders)
            {
                if (next is LoadStatus.Planned or LoadStatus.Dispatched) order.Status = OrderStatus.Planned;
                else if (next == LoadStatus.InProgress) order.Status = OrderStatus.InTransit;
                else if (next == LoadStatus.Completed) order.Status = OrderStatus.Delivered;
                else if (next == LoadStatus.Cancelled) order.Status = OrderStatus.Cancelled;
            }
        }
        await db.SaveChangesAsync(ct);
        return Ok(load);
    }

    [HttpPut("loads/{id:guid}/stops"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> UpdateStops(Guid id, List<UpdateLoadStopRequest> request, CancellationToken ct)
    {
        var load = await db.Loads.Include(item => item.Stops).SingleOrDefaultAsync(item => item.Id == id, ct);
        if (load is null) return NotFound();
        if (request.Count == 0 || request.Any(stop => string.IsNullOrWhiteSpace(stop.Name))) return BadRequest("At least one named stop is required.");
        db.LoadStops.RemoveRange(load.Stops);
        load.Stops = request.Select((stop, index) => new LoadStop { OrderId = stop.OrderId, Sequence = index + 1, Name = stop.Name.Trim(), Address = stop.Address, Latitude = stop.Latitude, Longitude = stop.Longitude, PlannedArrivalUtc = stop.PlannedArrivalUtc }).ToList();
        await db.SaveChangesAsync(ct); return Ok(load);
    }

    [HttpGet("loads/{id:guid}/route")]
    public async Task<IActionResult> Route(Guid id, CancellationToken ct)
    {
        var points = await db.LoadStops.AsNoTracking().Where(stop => stop.LoadId == id && stop.Longitude != null && stop.Latitude != null)
            .OrderBy(stop => stop.Sequence).Select(stop => new { stop.Longitude, stop.Latitude }).ToListAsync(ct);
        return Ok(await maps.Directions(points.Select(point => (point.Longitude!.Value, point.Latitude!.Value)).ToList(), ct));
    }

    [HttpGet("loads/{id:guid}/dispatch")]
    public async Task<IActionResult> Dispatch(Guid id, CancellationToken ct)
    {
        var load = await db.Loads.AsNoTracking().Include(item => item.Stops).SingleOrDefaultAsync(item => item.Id == id, ct);
        if (load is null) return NotFound();
        var orderIds = load.Stops.Where(stop => stop.OrderId is not null).Select(stop => stop.OrderId!.Value).Distinct().ToList();
        var orders = await db.TransportOrders.AsNoTracking().Where(order => orderIds.Contains(order.Id)).ToDictionaryAsync(order => order.Id, ct);
        var driver = load.DriverId is null ? null : await db.Drivers.AsNoTracking().SingleOrDefaultAsync(item => item.Id == load.DriverId, ct);
        var vehicle = load.VehicleId is null ? null : await db.Vehicles.AsNoTracking().SingleOrDefaultAsync(item => item.Id == load.VehicleId, ct);
        var trailer = load.TrailerId is null ? null : await db.Trailers.AsNoTracking().SingleOrDefaultAsync(item => item.Id == load.TrailerId, ct);
        return Ok(new
        {
            load.Id, load.Reference, load.PlanningDate, load.Status,
            driver = driver is null ? null : new { driver.DisplayName, driver.EmployeeNumber, driver.MobileNumber },
            vehicle = vehicle is null ? null : new { vehicle.Registration, vehicle.FleetNumber },
            trailer = trailer is null ? null : new { trailer.TrailerNumber, trailer.Type },
            stops = load.Stops.OrderBy(stop => stop.Sequence).Select(stop => new
            {
                stop.Id, stop.Sequence, stop.Name, stop.Address, stop.Latitude, stop.Longitude, stop.PlannedArrivalUtc,
                order = stop.OrderId is not null && orders.TryGetValue(stop.OrderId.Value, out var order) ? new
                {
                    order.Reference, order.CustomerCode, order.SellerName, order.MarketName, order.StallNumber, order.DriverInstructions, order.MapLink
                } : null
            })
        });
    }

    [HttpGet("maps/geocode")]
    public async Task<IActionResult> Geocode([FromQuery] string address, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(address)) return BadRequest("An address is required.");
        return Ok(await maps.SearchAddress(address, ct));
    }

    private static bool CanTransition(LoadStatus current, LoadStatus next) => current == next || (current, next) switch
    {
        (LoadStatus.Draft, LoadStatus.Planned) => true,
        (LoadStatus.Draft, LoadStatus.Cancelled) => true,
        (LoadStatus.Planned, LoadStatus.Draft) => true,
        (LoadStatus.Planned, LoadStatus.Dispatched) => true,
        (LoadStatus.Planned, LoadStatus.Cancelled) => true,
        (LoadStatus.Dispatched, LoadStatus.InProgress) => true,
        (LoadStatus.Dispatched, LoadStatus.Cancelled) => true,
        (LoadStatus.InProgress, LoadStatus.Completed) => true,
        _ => false
    };
}

public sealed record CreateLoadRequest(string Reference, DateOnly PlanningDate, Guid? VehicleId, Guid? DriverId, Guid? TrailerId, List<CreateLoadStopRequest> Stops);
public sealed record CreateLoadStopRequest(Guid? OrderId, string Name, string? Address, decimal? Latitude, decimal? Longitude, DateTimeOffset? PlannedArrivalUtc);
public sealed record UpdateLoadAllocationRequest(Guid? VehicleId, Guid? DriverId, Guid? TrailerId);
public sealed record UpdateLoadStatusRequest(string Status);
public sealed record UpdateLoadStopRequest(Guid? OrderId, string Name, string? Address, decimal? Latitude, decimal? Longitude, DateTimeOffset? PlannedArrivalUtc);
