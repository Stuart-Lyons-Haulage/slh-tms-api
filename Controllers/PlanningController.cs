using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1")]
[Authorize]
public sealed class PlanningController(TmsDbContext db, AzureMapsRouteClient maps, DriverSmsDispatchService sms) : ControllerBase
{
    [HttpGet("orders")]
    public async Task<IActionResult> Orders([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct)
    {
        try
        {
            var query = db.TransportOrders.AsNoTracking().AsQueryable();
            if (from is not null) query = query.Where(order => order.CollectionDate >= from);
            if (to is not null) query = query.Where(order => order.CollectionDate <= to);
            return Ok(await query.OrderBy(order => order.CollectionDate).ThenBy(order => order.Reference).Take(1000).ToListAsync(ct));
        }
        catch (Exception exception) when (IsSchemaUnavailable(exception))
        {
            return Ok(await PlanningRegisterStore.ReadOrdersAsync(db, from, to, ct));
        }
    }

    [HttpGet("loads")]
    public async Task<IActionResult> Loads([FromQuery] DateOnly? date, CancellationToken ct)
    {
        try
        {
            var query = db.Loads.AsNoTracking().Include(load => load.Stops).AsQueryable();
            if (date is not null) query = query.Where(load => load.PlanningDate == date);
            var loads = await query.OrderBy(load => load.PlanningDate).ThenBy(load => load.Reference).Take(500).ToListAsync(ct);
            await LoadCommercialStore.EnrichAsync(db, loads, ct);
            return Ok(loads);
        }
        catch (Exception exception) when (IsSchemaUnavailable(exception))
        {
            return Ok(await PlanningRegisterStore.ReadLoadsAsync(db, date, ct));
        }
    }

    [HttpPost("loads"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> CreateLoad(CreateLoadRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Reference) || request.Stops.Count == 0) return BadRequest("A reference and at least one stop are required.");
        var load = new Load { Reference = request.Reference.Trim(), PlanningDate = request.PlanningDate, VehicleId = request.VehicleId, DriverId = request.DriverId, TrailerId = request.TrailerId, Status = LoadStatus.Draft,
            Stops = request.Stops.Select((stop, index) => new LoadStop { OrderId = stop.OrderId, Sequence = index + 1, Name = stop.Name.Trim(), Address = stop.Address, Latitude = stop.Latitude, Longitude = stop.Longitude, PlannedArrivalUtc = stop.PlannedArrivalUtc }).ToList() };
        try
        {
            if (await db.Loads.AnyAsync(item => item.Reference == request.Reference, ct)) return Conflict("A load with this reference already exists.");
            db.Loads.Add(load); await db.SaveChangesAsync(ct);
        }
        catch (Exception exception) when (IsSchemaUnavailable(exception))
        {
            db.ChangeTracker.Clear();
            if ((await PlanningRegisterStore.ReadLoadsAsync(db, null, ct)).Any(item => string.Equals(item.Reference, request.Reference, StringComparison.OrdinalIgnoreCase))) return Conflict("A load with this reference already exists.");
            await PlanningRegisterStore.SaveLoadAsync(db, load, User.Identity?.Name, ct);
        }
        return Created($"/api/v1/loads/{load.Id}", load);
    }

    [HttpPut("loads/{id:guid}/allocation"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Allocate(Guid id, UpdateLoadAllocationRequest request, CancellationToken ct)
    {
        Load? load;
        var register = false;
        try { load = await db.Loads.SingleOrDefaultAsync(item => item.Id == id, ct); }
        catch (Exception exception) when (IsSchemaUnavailable(exception)) { db.ChangeTracker.Clear(); load = await PlanningRegisterStore.GetLoadAsync(db, id, ct); register = true; }
        if (load is null) return NotFound();
        if (request.VehicleId is not null && !await db.Vehicles.AnyAsync(vehicle => vehicle.Id == request.VehicleId && vehicle.Active, ct)) return BadRequest("Vehicle is not active.");
        if (request.DriverId is not null && !await db.Drivers.AnyAsync(driver => driver.Id == request.DriverId && driver.Active, ct)) return BadRequest("Driver is not active.");
        if (request.TrailerId is not null && !await db.Trailers.AnyAsync(trailer => trailer.Id == request.TrailerId && trailer.Active, ct)) return BadRequest("Trailer is not active.");
        load.VehicleId = request.VehicleId; load.DriverId = request.DriverId; load.TrailerId = request.TrailerId;
        load.Status = request.VehicleId is not null && request.DriverId is not null ? LoadStatus.Planned : LoadStatus.Draft;
        if (register) await PlanningRegisterStore.SaveLoadAsync(db, load, User.Identity?.Name, ct); else await db.SaveChangesAsync(ct);
        return Ok(load);
    }

    [HttpPut("loads/{id:guid}/commercial"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> UpdateCommercial(Guid id, UpdateLoadCommercialRequest request, CancellationToken ct)
    {
        Load? load;
        var register = false;
        try { load = await db.Loads.SingleOrDefaultAsync(item => item.Id == id, ct); }
        catch (Exception exception) when (IsSchemaUnavailable(exception)) { db.ChangeTracker.Clear(); load = await PlanningRegisterStore.GetLoadAsync(db, id, ct); register = true; }
        if (load is null) return NotFound();
        if (new decimal?[] { request.RevenueAmount, request.FuelSurchargeAmount, request.EstimatedCostAmount, request.ActualCostAmount, request.EstimatedDistanceMiles, request.EmptyMiles }.Any(value => value < 0))
            return BadRequest("Commercial values cannot be negative.");
        var values = new LoadCommercialValues(request.RevenueAmount, request.FuelSurchargeAmount, request.EstimatedCostAmount, request.ActualCostAmount,
            request.EstimatedDistanceMiles, request.EmptyMiles, Clip(request.InvoiceStatus, 40), Clip(request.CommercialNotes, 500));
        if (register)
        {
            load.RevenueAmount = values.RevenueAmount; load.FuelSurchargeAmount = values.FuelSurchargeAmount; load.EstimatedCostAmount = values.EstimatedCostAmount;
            load.ActualCostAmount = values.ActualCostAmount; load.EstimatedDistanceMiles = values.EstimatedDistanceMiles; load.EmptyMiles = values.EmptyMiles;
            load.InvoiceStatus = values.InvoiceStatus; load.CommercialNotes = values.CommercialNotes;
            await PlanningRegisterStore.SaveLoadAsync(db, load, User.Identity?.Name, ct);
        }
        else await LoadCommercialStore.SaveAsync(db, load, values, User.Identity?.Name, ct);
        return Ok(load);
    }

    [HttpPut("loads/{id:guid}/status"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> UpdateStatus(Guid id, UpdateLoadStatusRequest request, CancellationToken ct)
    {
        Load? load;
        var register = false;
        try { load = await db.Loads.Include(item => item.Stops).SingleOrDefaultAsync(item => item.Id == id, ct); }
        catch (Exception exception) when (IsSchemaUnavailable(exception)) { db.ChangeTracker.Clear(); load = await PlanningRegisterStore.GetLoadAsync(db, id, ct); register = true; }
        if (load is null) return NotFound();
        if (!Enum.TryParse<LoadStatus>(request.Status, true, out var next)) return BadRequest("The requested load status is not valid.");
        if (!CanTransition(load.Status, next)) return BadRequest($"A load cannot move from {load.Status} to {next}.");
        if ((next is LoadStatus.Dispatched or LoadStatus.InProgress) && (load.DriverId is null || load.VehicleId is null)) return BadRequest("Allocate both a driver and vehicle before dispatching a load.");

        load.Status = next;
        var orderIds = load.Stops.Where(stop => stop.OrderId is not null).Select(stop => stop.OrderId!.Value).ToList();
        if (!register && orderIds.Count > 0)
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
        if (register) await PlanningRegisterStore.SaveLoadAsync(db, load, User.Identity?.Name, ct); else await db.SaveChangesAsync(ct);
        return Ok(load);
    }

    [HttpPut("loads/{id:guid}/stops"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> UpdateStops(Guid id, List<UpdateLoadStopRequest> request, CancellationToken ct)
    {
        Load? load;
        var register = false;
        try { load = await db.Loads.Include(item => item.Stops).SingleOrDefaultAsync(item => item.Id == id, ct); }
        catch (Exception exception) when (IsSchemaUnavailable(exception)) { db.ChangeTracker.Clear(); load = await PlanningRegisterStore.GetLoadAsync(db, id, ct); register = true; }
        if (load is null) return NotFound();
        if (request.Count == 0 || request.Any(stop => string.IsNullOrWhiteSpace(stop.Name))) return BadRequest("At least one named stop is required.");
        if (!register) db.LoadStops.RemoveRange(load.Stops);
        load.Stops = request.Select((stop, index) => new LoadStop { OrderId = stop.OrderId, Sequence = index + 1, Name = stop.Name.Trim(), Address = stop.Address, Latitude = stop.Latitude, Longitude = stop.Longitude, PlannedArrivalUtc = stop.PlannedArrivalUtc }).ToList();
        if (register) await PlanningRegisterStore.SaveLoadAsync(db, load, User.Identity?.Name, ct); else await db.SaveChangesAsync(ct);
        return Ok(load);
    }

    [HttpGet("loads/{id:guid}/route")]
    public async Task<IActionResult> Route(Guid id, CancellationToken ct)
    {
        List<(decimal Longitude, decimal Latitude)> points;
        try
        {
            var storedPoints = await db.LoadStops.AsNoTracking().Where(stop => stop.LoadId == id && stop.Longitude != null && stop.Latitude != null)
                .OrderBy(stop => stop.Sequence).Select(stop => new { stop.Longitude, stop.Latitude }).ToListAsync(ct);
            points = storedPoints.Select(point => (point.Longitude!.Value, point.Latitude!.Value)).ToList();
        }
        catch (Exception exception) when (IsSchemaUnavailable(exception))
        {
            db.ChangeTracker.Clear();
            var load = await PlanningRegisterStore.GetLoadAsync(db, id, ct);
            if (load is null) return NotFound();
            points = load.Stops.Where(stop => stop.Longitude is not null && stop.Latitude is not null).OrderBy(stop => stop.Sequence)
                .Select(stop => (stop.Longitude!.Value, stop.Latitude!.Value)).ToList();
        }
        return Ok(await maps.Directions(points, ct));
    }

    [HttpGet("loads/{id:guid}/dispatch")]
    public async Task<IActionResult> Dispatch(Guid id, CancellationToken ct)
    {
        Load? load;
        var register = false;
        try { load = await db.Loads.AsNoTracking().Include(item => item.Stops).SingleOrDefaultAsync(item => item.Id == id, ct); }
        catch (Exception exception) when (IsSchemaUnavailable(exception)) { db.ChangeTracker.Clear(); load = await PlanningRegisterStore.GetLoadAsync(db, id, ct); register = true; }
        if (load is null) return NotFound();
        var orderIds = load.Stops.Where(stop => stop.OrderId is not null).Select(stop => stop.OrderId!.Value).Distinct().ToList();
        var orders = register
            ? (await PlanningRegisterStore.ReadOrdersAsync(db, null, null, ct)).Where(order => orderIds.Contains(order.Id)).ToDictionary(order => order.Id)
            : await db.TransportOrders.AsNoTracking().Where(order => orderIds.Contains(order.Id)).ToDictionaryAsync(order => order.Id, ct);
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

    [HttpPost("loads/{id:guid}/dispatch/sms"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> SendDispatchSms(Guid id, CancellationToken ct)
    {
        Load? load;
        var register = false;
        try { load = await db.Loads.Include(item => item.Stops).SingleOrDefaultAsync(item => item.Id == id, ct); }
        catch (Exception exception) when (IsSchemaUnavailable(exception)) { db.ChangeTracker.Clear(); load = await PlanningRegisterStore.GetLoadAsync(db, id, ct); register = true; }
        if (load is null) return NotFound();
        if (load.DriverId is null || load.VehicleId is null) return BadRequest("Allocate both a driver and vehicle before sending a dispatch.");
        var driver = await db.Drivers.SingleOrDefaultAsync(item => item.Id == load.DriverId, ct);
        var vehicle = await db.Vehicles.SingleOrDefaultAsync(item => item.Id == load.VehicleId, ct);
        if (driver is null || vehicle is null) return BadRequest("The allocated driver or vehicle could not be found.");
        if (string.IsNullOrWhiteSpace(driver.MobileNumber)) return BadRequest("The assigned driver has no approved mobile number.");

        var orderIds = load.Stops.Where(stop => stop.OrderId is not null).Select(stop => stop.OrderId!.Value).Distinct().ToList();
        var orders = register
            ? (await PlanningRegisterStore.ReadOrdersAsync(db, null, null, ct)).Where(order => orderIds.Contains(order.Id)).ToDictionary(order => order.Id)
            : await db.TransportOrders.AsNoTracking().Where(order => orderIds.Contains(order.Id)).ToDictionaryAsync(order => order.Id, ct);
        var stops = load.Stops.OrderBy(stop => stop.Sequence).Select(stop =>
        {
            orders.TryGetValue(stop.OrderId ?? Guid.Empty, out var order);
            return string.Join("\n", new[]
            {
                $"{stop.Sequence}. {stop.Name}",
                order?.MarketName is null ? null : $"Market: {order.MarketName}{(string.IsNullOrWhiteSpace(order.StallNumber) ? string.Empty : $" · Stall {order.StallNumber}")}",
                order?.SellerName is null ? null : $"Seller: {order.SellerName}",
                string.IsNullOrWhiteSpace(stop.Address) ? null : $"Address: {stop.Address}",
                string.IsNullOrWhiteSpace(order?.DriverInstructions) ? null : $"Notes: {order!.DriverInstructions}",
                string.IsNullOrWhiteSpace(order?.MapLink) ? null : $"Map: {order!.MapLink}"
            }.Where(line => line is not null));
        });
        var message = string.Join("\n\n", new[] { $"SLH run {load.Reference}", $"Driver: {driver.DisplayName}", $"Vehicle: {vehicle.Registration}", string.Empty, string.Join("\n\n", stops) });
        var receipt = await sms.SendAsync(driver.MobileNumber, message, ct);
        if (load.Status == LoadStatus.Planned) load.Status = LoadStatus.Dispatched;
        if (register) await PlanningRegisterStore.SaveLoadAsync(db, load, User.Identity?.Name, ct); else await db.SaveChangesAsync(ct);
        return Accepted(new { receipt.MessageId, receipt.MobileSuffix, receipt.Provider, load.Status });
    }

    [HttpGet("maps/geocode")]
    public async Task<IActionResult> Geocode([FromQuery] string address, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(address)) return BadRequest("An address is required.");
        return Ok(await maps.SearchAddress(address, ct));
    }

    private static bool IsSchemaUnavailable(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        return exception is InvalidOperationException or DbUpdateException || message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase) || message.Contains("Cannot find the object", StringComparison.OrdinalIgnoreCase) || message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase);
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

    private static string? Clip(string? value, int length) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, length)];
}

public sealed record CreateLoadRequest(string Reference, DateOnly PlanningDate, Guid? VehicleId, Guid? DriverId, Guid? TrailerId, List<CreateLoadStopRequest> Stops);
public sealed record CreateLoadStopRequest(Guid? OrderId, string Name, string? Address, decimal? Latitude, decimal? Longitude, DateTimeOffset? PlannedArrivalUtc);
public sealed record UpdateLoadAllocationRequest(Guid? VehicleId, Guid? DriverId, Guid? TrailerId);
public sealed record UpdateLoadStatusRequest(string Status);
public sealed record UpdateLoadStopRequest(Guid? OrderId, string Name, string? Address, decimal? Latitude, decimal? Longitude, DateTimeOffset? PlannedArrivalUtc);
public sealed record UpdateLoadCommercialRequest(decimal? RevenueAmount, decimal? FuelSurchargeAmount, decimal? EstimatedCostAmount, decimal? ActualCostAmount, decimal? EstimatedDistanceMiles, decimal? EmptyMiles, string? InvoiceStatus, string? CommercialNotes);
