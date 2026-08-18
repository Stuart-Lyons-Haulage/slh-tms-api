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
public sealed class OperationsController(TmsDbContext db, AzureMapsRouteClient maps, TachoMasterClient tachoMaster, ILogger<OperationsController> logger) : ControllerBase
{
    [HttpGet("delivery-etas")]
    public async Task<IActionResult> DeliveryEtas([FromQuery] DateOnly? date, CancellationToken ct)
    {
        var planningDate = date ?? DateOnly.FromDateTime(DateTime.UtcNow);
        List<Load> loads;
        try
        {
            loads = await db.Loads.AsNoTracking().Include(load => load.Stops)
                .Where(load => load.PlanningDate == planningDate && load.Status != LoadStatus.Cancelled)
                .OrderBy(load => load.Reference).Take(200).ToListAsync(ct);
        }
        catch (Exception exception) when (IsSchemaUnavailable(exception))
        {
            db.ChangeTracker.Clear();
            loads = await PlanningRegisterStore.ReadLoadsAsync(db, planningDate, ct);
        }
        var orderIds = loads.SelectMany(load => load.Stops).Where(stop => stop.OrderId != null).Select(stop => stop.OrderId!.Value).Distinct().ToList();
        var orders = await SafeDictionary(db.TransportOrders.AsNoTracking().Where(order => orderIds.Contains(order.Id)), order => order.Id, ct);
        if (orders.Count == 0 && orderIds.Count > 0) orders = (await PlanningRegisterStore.ReadOrdersAsync(db, null, null, ct)).Where(order => orderIds.Contains(order.Id)).ToDictionary(order => order.Id);
        var vehicleIds = loads.Where(load => load.VehicleId != null).Select(load => load.VehicleId!.Value).Distinct().ToList();
        var vehicles = await SafeDictionary(db.Vehicles.AsNoTracking().Where(vehicle => vehicleIds.Contains(vehicle.Id)), vehicle => vehicle.Id, ct);
        var statuses = await SafeList(db.VehicleLiveStatuses.AsNoTracking(), ct);
        IReadOnlyDictionary<string, TachoVehicleDriverStatus> tachoStatuses = new Dictionary<string, TachoVehicleDriverStatus>();
        try { tachoStatuses = await tachoMaster.GetCurrentDriverStatusesByVehicleAsync(planningDate, ct); }
        catch (Exception exception) { logger.LogWarning(exception, "TachoMaster data was unavailable for tacho-aware ETA calculations."); }
        var now = DateTimeOffset.UtcNow;
        var records = new List<DeliveryEtaResponse>();

        foreach (var load in loads)
        {
            var vehicle = load.VehicleId is Guid vehicleId && vehicles.TryGetValue(vehicleId, out var matchedVehicle) ? matchedVehicle : null;
            var live = vehicle is null ? null : MatchLive(vehicle, statuses);
            var tacho = vehicle is null ? null : MatchTacho(vehicle, tachoStatuses);
            var current = live is null ? ((decimal Longitude, decimal Latitude)?)null : (live.Longitude, live.Latitude);
            var currentEta = now;
            var cumulativeDrivingMinutes = 0d;
            var breakDelayMinutes = 0;
            var initialContinuousDriving = tacho is null ? 0 : tacho.BreakMinutes >= 45 ? tacho.DriveMinutes % 270 : Math.Min(tacho.DriveMinutes, 270);
            foreach (var stop in load.Stops.OrderBy(stop => stop.Sequence))
            {
                orders.TryGetValue(stop.OrderId ?? Guid.Empty, out var order);
                var eta = stop.PlannedArrivalUtc;
                var source = eta is null ? "Unavailable" : "Planned";
                if (current is not null && stop.Longitude is not null && stop.Latitude is not null && now - live!.LastEventTimeUtc <= TimeSpan.FromMinutes(30))
                {
                    try
                    {
                        var travelTime = await maps.TravelTime(current.Value, (stop.Longitude.Value, stop.Latitude.Value), ct);
                        cumulativeDrivingMinutes += travelTime.TotalMinutes;
                        var requiredBreaks = tacho is null ? 0 : Math.Max(0, (int)Math.Floor((initialContinuousDriving + cumulativeDrivingMinutes - 0.01) / 270d));
                        if (requiredBreaks * 45 > breakDelayMinutes)
                        {
                            var extraBreakMinutes = requiredBreaks * 45 - breakDelayMinutes;
                            currentEta += TimeSpan.FromMinutes(extraBreakMinutes);
                            breakDelayMinutes += extraBreakMinutes;
                        }
                        currentEta += travelTime;
                        eta = currentEta; source = "Live"; current = (stop.Longitude.Value, stop.Latitude.Value);
                    }
                    catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or Azure.Identity.AuthenticationFailedException)
                    {
                        source = eta is null ? "Unavailable" : "Planned";
                    }
                }
                var windowStart = order?.DeliveryWindowStartUtc;
                var windowEnd = order?.DeliveryWindowEndUtc ?? (IsDeliveryStop(stop) ? stop.PlannedArrivalUtc : null);
                var tachoAssessment = source == "Live"
                    ? TachoAssessment(tacho, cumulativeDrivingMinutes, breakDelayMinutes)
                    : (Status: "RouteUnavailable", Explanation: tacho is null
                        ? "Live route and current TachoMaster duty are unavailable; this ETA must be verified before export."
                        : "TachoMaster matched the driver, but no fresh live route could be calculated; the planned ETA has not been adjusted for a break.");
                records.Add(new DeliveryEtaResponse(load.Id, load.Reference, load.Status.ToString(), stop.Id, stop.Sequence, stop.Name,
                    order?.Reference, order?.CustomerCode, vehicle?.Registration, eta, source, windowStart, windowEnd,
                    Risk(eta, windowStart, windowEnd), live?.LastEventTimeUtc,
                    tacho?.DriverName, tacho?.DriveAvailableTodayMinutes, (int)Math.Ceiling(cumulativeDrivingMinutes), breakDelayMinutes,
                    tachoAssessment.Status, tachoAssessment.Explanation));
            }
        }
        return Ok(new { planningDate, calculatedAtUtc = now, records });
    }

    [HttpGet("forecast")]
    public async Task<IActionResult> Forecast([FromQuery] DateOnly? from, CancellationToken ct)
    {
        var firstDate = from ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var lastDate = firstDate.AddDays(6);
        List<Load> loads;
        try
        {
            loads = await db.Loads.AsNoTracking().Include(load => load.Stops)
                .Where(load => load.PlanningDate >= firstDate && load.PlanningDate <= lastDate && load.Status != LoadStatus.Cancelled)
                .OrderBy(load => load.PlanningDate).ThenBy(load => load.Reference).Take(2000).ToListAsync(ct);
        }
        catch (Exception exception) when (IsSchemaUnavailable(exception))
        {
            db.ChangeTracker.Clear();
            loads = (await PlanningRegisterStore.ReadLoadsAsync(db, null, ct)).Where(load => load.PlanningDate >= firstDate && load.PlanningDate <= lastDate && load.Status != LoadStatus.Cancelled).ToList();
        }
        await LoadCommercialStore.EnrichAsync(db, loads, ct);
        var orderIds = loads.SelectMany(load => load.Stops).Where(stop => stop.OrderId != null).Select(stop => stop.OrderId!.Value).Distinct().ToList();
        var orders = await SafeDictionary(db.TransportOrders.AsNoTracking().Where(order => orderIds.Contains(order.Id)), order => order.Id, ct);
        if (orders.Count == 0 && orderIds.Count > 0) orders = (await PlanningRegisterStore.ReadOrdersAsync(db, null, null, ct)).Where(order => orderIds.Contains(order.Id)).ToDictionary(order => order.Id);
        var trailers = await SafeDictionary(db.Trailers.AsNoTracking().Where(trailer => trailer.Active), trailer => trailer.Id, ct);
        var activeDrivers = await db.Drivers.AsNoTracking().CountAsync(driver => driver.Active, ct);
        var activeVehicles = await db.Vehicles.AsNoTracking().CountAsync(vehicle => vehicle.Active, ct);
        var activeTrailerCapacity = trailers.Values.Sum(trailer => trailer.StandardCapacity ?? 0);
        var days = Enumerable.Range(0, 7).Select(offset =>
        {
            var date = firstDate.AddDays(offset);
            var dayLoads = loads.Where(load => load.PlanningDate == date).ToList();
            var dayOrderIds = dayLoads.SelectMany(load => load.Stops).Where(stop => stop.OrderId != null).Select(stop => stop.OrderId!.Value).Distinct();
            var pallets = (int)Math.Ceiling(dayLoads.Sum(load => load.PalletSpacesUsed ?? 0));
            if (pallets == 0) pallets = dayOrderIds.Sum(id => orders.TryGetValue(id, out var order) ? order.Pallets ?? 0 : 0);
            var plannedCapacity = (int)Math.Ceiling(dayLoads.Sum(load => load.TotalPalletSpaces ?? 0));
            var utilisation = plannedCapacity > 0 ? Math.Round((decimal)pallets / plannedCapacity * 100, 1) : (decimal?)null;
            var overCapacityLoads = dayLoads.Count(load => load.TotalPalletSpaces > 0 && load.PalletSpacesUsed > load.TotalPalletSpaces);
            var revenue = dayLoads.Sum(load => (load.RevenueAmount ?? 0) + (load.FuelSurchargeAmount ?? 0));
            var cost = dayLoads.Sum(load => load.ActualCostAmount ?? load.EstimatedCostAmount ?? 0);
            var distance = dayLoads.Sum(load => load.EstimatedDistanceMiles ?? 0);
            var emptyMiles = dayLoads.Sum(load => load.EmptyMiles ?? 0);
            var assignedDrivers = dayLoads.Where(load => load.DriverId != null).Select(load => load.DriverId).Distinct().Count();
            var assignedVehicles = dayLoads.Where(load => load.VehicleId != null).Select(load => load.VehicleId).Distinct().Count();
            var exceptions = dayLoads.Count(load => load.DriverId is null || load.VehicleId is null || load.Stops.Any(stop => stop.Latitude is null || stop.Longitude is null) || load.RevenueAmount is null
                || load.TotalPalletSpaces > 0 && load.PalletSpacesUsed > load.TotalPalletSpaces);
            return new ForecastDay(date, dayLoads.Count, assignedDrivers, activeDrivers, assignedVehicles, activeVehicles, pallets, plannedCapacity > 0 ? plannedCapacity : activeTrailerCapacity,
                revenue, cost, revenue - cost, revenue > 0 ? Math.Round((revenue - cost) / revenue * 100, 1) : null,
                distance, emptyMiles, distance > 0 ? Math.Round(emptyMiles / distance * 100, 1) : null,
                dayLoads.Count(load => load.RevenueAmount is null), dayLoads.Count(load => string.IsNullOrWhiteSpace(load.InvoiceStatus)), exceptions,
                utilisation, overCapacityLoads);
        }).ToList();
        return Ok(new
        {
            from = firstDate,
            to = lastDate,
            generatedAtUtc = DateTimeOffset.UtcNow,
            activeDrivers,
            activeVehicles,
            days,
            totals = new
            {
                loads = days.Sum(day => day.Loads),
                revenue = days.Sum(day => day.Revenue),
                cost = days.Sum(day => day.Cost),
                margin = days.Sum(day => day.Margin),
                emptyMiles = days.Sum(day => day.EmptyMiles),
                exceptions = days.Sum(day => day.Exceptions),
                plannedPallets = days.Sum(day => day.PlannedPallets),
                availableTrailerPallets = days.Sum(day => day.AvailableTrailerPallets),
                utilisationPercent = days.Sum(day => day.AvailableTrailerPallets) > 0
                    ? Math.Round((decimal)days.Sum(day => day.PlannedPallets) / days.Sum(day => day.AvailableTrailerPallets) * 100, 1) : (decimal?)null,
                overCapacityLoads = days.Sum(day => day.OverCapacityLoads)
            }
        });
    }

    private static async Task<Dictionary<TKey, T>> SafeDictionary<T, TKey>(IQueryable<T> query, Func<T, TKey> keySelector, CancellationToken ct) where TKey : notnull
    {
        try { return await query.ToDictionaryAsync(keySelector, ct); }
        catch (Exception exception) when (IsSchemaUnavailable(exception)) { return []; }
    }

    private static async Task<List<T>> SafeList<T>(IQueryable<T> query, CancellationToken ct)
    {
        try { return await query.ToListAsync(ct); }
        catch (Exception exception) when (IsSchemaUnavailable(exception)) { return []; }
    }

    private static bool IsSchemaUnavailable(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        return exception is InvalidOperationException or DbUpdateException || message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase) || message.Contains("Cannot find the object", StringComparison.OrdinalIgnoreCase) || message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase);
    }

    private static VehicleLiveStatus? MatchLive(Vehicle vehicle, List<VehicleLiveStatus> statuses)
    {
        var keys = new[] { vehicle.Registration, vehicle.FleetNumber, vehicle.Abbreviation }.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => Normalise(value!)).ToList();
        return statuses.Where(status => keys.Contains(Normalise(status.VehicleIdentifier))).OrderByDescending(status => status.LastEventTimeUtc).FirstOrDefault();
    }
    private static TachoVehicleDriverStatus? MatchTacho(Vehicle vehicle, IReadOnlyDictionary<string, TachoVehicleDriverStatus> statuses)
    {
        var aliases = new[] { vehicle.Registration, vehicle.FleetNumber, vehicle.Abbreviation }.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => Normalise(value!));
        foreach (var alias in aliases) if (statuses.TryGetValue(alias, out var status)) return status;
        return null;
    }
    internal static (string Status, string Explanation) TachoAssessment(TachoVehicleDriverStatus? tacho, double routeDrivingMinutes, int breakMinutes)
    {
        if (tacho is null) return ("Unavailable", "No current TachoMaster duty was matched; verify the driver before promising this ETA.");
        if (tacho.DriveAvailableTodayMinutes is int remaining && routeDrivingMinutes > remaining)
            return ("InsufficientDriveTime", $"The route needs about {Math.Ceiling(routeDrivingMinutes)} driving minutes but TachoMaster shows {remaining} minutes available today. Re-plan or confirm legal availability.");
        if (breakMinutes > 0) return ("BreakIncluded", $"ETA includes {breakMinutes} minutes for a statutory driving break based on current duty and route time.");
        return ("WithinDriveTime", "Current TachoMaster availability covers the calculated route without an additional driving break.");
    }
    private static string Normalise(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static bool IsDeliveryStop(LoadStop stop) => stop.Name.StartsWith("Deliver", StringComparison.OrdinalIgnoreCase);
    private static string Risk(DateTimeOffset? eta, DateTimeOffset? start, DateTimeOffset? end)
    {
        if (eta is null || end is null) return "Pending";
        if (eta > end) return "Late";
        if (end - eta <= TimeSpan.FromMinutes(30) || start is not null && eta < start.Value - TimeSpan.FromMinutes(30)) return "AtRisk";
        return "OnTrack";
    }
}

public sealed record DeliveryEtaResponse(Guid LoadId, string LoadReference, string LoadStatus, Guid StopId, int Sequence, string StopName, string? OrderReference, string? CustomerCode, string? VehicleRegistration, DateTimeOffset? EtaUtc, string Source, DateTimeOffset? DeliveryWindowStartUtc, DateTimeOffset? DeliveryWindowEndUtc, string Risk, DateTimeOffset? TrackingUpdatedAtUtc, string? TachoDriverName, int? DriveAvailableTodayMinutes, int RouteDrivingMinutes, int BreakMinutesIncluded, string TachoStatus, string TachoExplanation);
public sealed record ForecastDay(DateOnly Date, int Loads, int AssignedDrivers, int AvailableDrivers, int AssignedVehicles, int AvailableVehicles, int PlannedPallets, int AvailableTrailerPallets, decimal Revenue, decimal Cost, decimal Margin, decimal? MarginPercent, decimal DistanceMiles, decimal EmptyMiles, decimal? EmptyMilePercent, int UnpricedLoads, int UninvoicedLoads, int Exceptions, decimal? UtilisationPercent, int OverCapacityLoads);
