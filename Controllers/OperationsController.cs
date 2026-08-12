using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/operations")]
[Authorize]
public sealed class OperationsController(TmsDbContext db, AzureMapsRouteClient maps) : ControllerBase
{
    [HttpGet("delivery-etas")]
    public async Task<IActionResult> DeliveryEtas([FromQuery] DateOnly? date, CancellationToken ct)
    {
        var planningDate = date ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var loads = await db.Loads.AsNoTracking().Include(load => load.Stops)
            .Where(load => load.PlanningDate == planningDate && load.Status != LoadStatus.Cancelled)
            .OrderBy(load => load.Reference).Take(200).ToListAsync(ct);
        var orderIds = loads.SelectMany(load => load.Stops).Where(stop => stop.OrderId != null).Select(stop => stop.OrderId!.Value).Distinct().ToList();
        var orders = await db.TransportOrders.AsNoTracking().Where(order => orderIds.Contains(order.Id)).ToDictionaryAsync(order => order.Id, ct);
        var vehicleIds = loads.Where(load => load.VehicleId != null).Select(load => load.VehicleId!.Value).Distinct().ToList();
        var vehicles = await db.Vehicles.AsNoTracking().Where(vehicle => vehicleIds.Contains(vehicle.Id)).ToDictionaryAsync(vehicle => vehicle.Id, ct);
        var statuses = await db.VehicleLiveStatuses.AsNoTracking().ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var records = new List<DeliveryEtaResponse>();

        foreach (var load in loads)
        {
            var vehicle = load.VehicleId is Guid vehicleId && vehicles.TryGetValue(vehicleId, out var matchedVehicle) ? matchedVehicle : null;
            var live = vehicle is null ? null : MatchLive(vehicle, statuses);
            var current = live is null ? ((decimal Longitude, decimal Latitude)?)null : (live.Longitude, live.Latitude);
            var currentEta = now;
            foreach (var stop in load.Stops.OrderBy(stop => stop.Sequence))
            {
                orders.TryGetValue(stop.OrderId ?? Guid.Empty, out var order);
                var eta = stop.PlannedArrivalUtc;
                var source = eta is null ? "Unavailable" : "Planned";
                if (current is not null && stop.Longitude is not null && stop.Latitude is not null && now - live!.LastEventTimeUtc <= TimeSpan.FromMinutes(30))
                {
                    try
                    {
                        currentEta += await maps.TravelTime(current.Value, (stop.Longitude.Value, stop.Latitude.Value), ct);
                        eta = currentEta; source = "Live"; current = (stop.Longitude.Value, stop.Latitude.Value);
                    }
                    catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or Azure.Identity.AuthenticationFailedException)
                    {
                        source = eta is null ? "Unavailable" : "Planned";
                    }
                }
                records.Add(new DeliveryEtaResponse(load.Id, load.Reference, load.Status.ToString(), stop.Id, stop.Sequence, stop.Name,
                    order?.Reference, order?.CustomerCode, vehicle?.Registration, eta, source, order?.DeliveryWindowStartUtc, order?.DeliveryWindowEndUtc,
                    Risk(eta, order?.DeliveryWindowStartUtc, order?.DeliveryWindowEndUtc), live?.LastEventTimeUtc));
            }
        }
        return Ok(new { planningDate, calculatedAtUtc = now, records });
    }

    private static VehicleLiveStatus? MatchLive(Vehicle vehicle, List<VehicleLiveStatus> statuses)
    {
        var keys = new[] { vehicle.Registration, vehicle.FleetNumber, vehicle.Abbreviation }.Where(value => !string.IsNullOrWhiteSpace(value)).Select(Normalise).ToList();
        return statuses.Where(status => keys.Contains(Normalise(status.VehicleIdentifier))).OrderByDescending(status => status.LastEventTimeUtc).FirstOrDefault();
    }
    private static string Normalise(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static string Risk(DateTimeOffset? eta, DateTimeOffset? start, DateTimeOffset? end)
    {
        if (eta is null || end is null) return "Pending";
        if (eta > end) return "Late";
        if (end - eta <= TimeSpan.FromMinutes(30) || start is not null && eta < start.Value - TimeSpan.FromMinutes(30)) return "AtRisk";
        return "OnTrack";
    }
}

public sealed record DeliveryEtaResponse(Guid LoadId, string LoadReference, string LoadStatus, Guid StopId, int Sequence, string StopName, string? OrderReference, string? CustomerCode, string? VehicleRegistration, DateTimeOffset? EtaUtc, string Source, DateTimeOffset? DeliveryWindowStartUtc, DateTimeOffset? DeliveryWindowEndUtc, string Risk, DateTimeOffset? TrackingUpdatedAtUtc);
