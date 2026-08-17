using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/planning"), Authorize]
public sealed class PlannerSuggestionsController(TmsDbContext db, ILogger<PlannerSuggestionsController> logger) : ControllerBase
{
    [HttpGet("day-suggestions")]
    public async Task<IActionResult> DaySuggestions([FromQuery] DateOnly? date, CancellationToken ct)
    {
        var planningDate = date ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var previousDate = planningDate.AddDays(-1);

        var orders = await ReadOrders(planningDate, ct);
        var loads = await ReadLoads(planningDate, ct);
        var plannedOrderIds = loads.SelectMany(load => load.Stops ?? [])
            .Where(stop => stop.OrderId is not null)
            .Select(stop => stop.OrderId!.Value)
            .ToHashSet();
        var unplanned = orders
            .Where(order => order.Status != OrderStatus.Cancelled && order.Status != OrderStatus.Delivered && !plannedOrderIds.Contains(order.Id))
            .ToList();

        var previousLoads = await ReadLoads(previousDate, ct);
        previousLoads = previousLoads
            .Where(load => load.Status != LoadStatus.Cancelled && load.DriverId is not null)
            .ToList();

        var driverIds = previousLoads.Select(load => load.DriverId!.Value).Distinct().ToList();
        var drivers = await SafeList(db.Drivers.AsNoTracking().Where(driver => driverIds.Contains(driver.Id) && driver.Active), ct);
        await MasterDetailStore.EnrichDriversAsync(db, drivers, ct);
        var driverById = drivers.ToDictionary(driver => driver.Id);

        var sites = await SafeList(db.Sites.AsNoTracking().Where(site => site.Active), ct);
        await MasterDetailStore.EnrichSitesAsync(db, sites, ct);

        var vehicles = await SafeList(db.Vehicles.AsNoTracking().Where(vehicle => vehicle.Active), ct);
        var vehicleById = vehicles.ToDictionary(vehicle => vehicle.Id);
        var liveStatuses = await SafeList(db.VehicleLiveStatuses.AsNoTracking(), ct);

        var candidates = new List<PlannerDaySuggestion>();
        foreach (var driverLoads in previousLoads.GroupBy(load => load.DriverId!.Value))
        {
            if (!driverById.TryGetValue(driverLoads.Key, out var driver)) continue;
            var latestLoad = driverLoads.OrderByDescending(load => load.CreatedAtUtc).First();
            var finalStop = (latestLoad.Stops ?? []).OrderBy(stop => stop.Sequence).LastOrDefault();

            decimal? latitude = finalStop?.Latitude;
            decimal? longitude = finalStop?.Longitude;
            var lastLocation = finalStop?.Name ?? "Previous final stop";

            if (latestLoad.VehicleId is Guid vehicleId && vehicleById.TryGetValue(vehicleId, out var vehicle))
            {
                var keys = new[] { vehicle.Registration, vehicle.FleetNumber, vehicle.Abbreviation }
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(Normalise)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var live = liveStatuses
                    .Where(status => keys.Contains(Normalise(status.VehicleIdentifier)))
                    .OrderByDescending(status => status.LastEventTimeUtc)
                    .FirstOrDefault();
                if (live is not null && DateTimeOffset.UtcNow - live.LastEventTimeUtc <= TimeSpan.FromHours(18))
                {
                    latitude = live.Latitude;
                    longitude = live.Longitude;
                    lastLocation = $"Live position · {vehicle.Registration}";
                }
            }

            if (latitude is null || longitude is null) continue;

            var scored = unplanned.Select(order =>
            {
                var collectionName = CollectionName(order);
                var site = FindSite(sites, collectionName);
                var distance = site?.Latitude is not null && site.Longitude is not null
                    ? HaversineMiles((double)latitude.Value, (double)longitude.Value, (double)site.Latitude.Value, (double)site.Longitude.Value)
                    : (double?)null;
                var orderType = OrderType(order);
                var crateBonus = string.Equals(orderType, "Crates", StringComparison.OrdinalIgnoreCase) && latitude >= 52.0m ? 30 : 0;
                var score = distance is null ? -1000 : 100 - Math.Min(distance.Value, 250) + crateBonus;
                return new { order, collectionName, site, distance, orderType, score };
            })
            .Where(item => item.distance is not null)
            .OrderByDescending(item => item.score)
            .ThenBy(item => item.distance)
            .Take(2)
            .ToList();

            foreach (var item in scored)
            {
                var destination = Destination(item.order);
                var crateText = item.orderType == "Crates" ? " Potential southbound crate/backhaul work." : string.Empty;
                var tachoText = driver.TachoDriveAvailableTodayMinutes is int minutes
                    ? $" Tacho availability currently recorded: {minutes / 60}h {minutes % 60:00}."
                    : " Tacho hours are not currently available, so legal availability must still be confirmed.";
                candidates.Add(new PlannerDaySuggestion(
                    driver.Id,
                    driver.DisplayName,
                    driver.EmployeeNumber,
                    latestLoad.Id,
                    latestLoad.Reference,
                    previousDate,
                    lastLocation,
                    latitude,
                    longitude,
                    item.order.Id,
                    item.order.Reference,
                    item.order.CustomerCode,
                    item.collectionName,
                    destination,
                    item.orderType,
                    item.order.Pallets,
                    item.distance is null ? null : Math.Round(item.distance.Value, 1),
                    driver.TachoDriveAvailableTodayMinutes,
                    item.score,
                    $"{driver.DisplayName} finished yesterday at {lastLocation}. Today's collection at {item.collectionName} is about {item.distance:0.0} miles from that position.{crateText}{tachoText}"));
            }
        }

        return Ok(new
        {
            planningDate,
            previousDate,
            generatedAtUtc = DateTimeOffset.UtcNow,
            unplannedOrders = unplanned.Count,
            previousDayDrivers = previousLoads.Select(load => load.DriverId).Where(id => id is not null).Distinct().Count(),
            suggestions = candidates.OrderByDescending(item => item.Score).ThenBy(item => item.RepositionMiles).Take(12)
        });
    }

    private async Task<List<TransportOrder>> ReadOrders(DateOnly date, CancellationToken ct)
    {
        try
        {
            var result = await db.TransportOrders.AsNoTracking()
                .Where(order => order.CollectionDate == date && order.Status != OrderStatus.Cancelled)
                .OrderBy(order => order.Reference).Take(1500).ToListAsync(ct);
            if (result.Count > 0) return result;
        }
        catch (Exception ex) when (SchemaUnavailable(ex))
        {
            logger.LogWarning(ex, "Planner suggestions are using the fallback order register.");
            db.ChangeTracker.Clear();
        }
        return await PlanningRegisterStore.ReadOrdersAsync(db, date, date, ct);
    }

    private async Task<List<Load>> ReadLoads(DateOnly date, CancellationToken ct)
    {
        try
        {
            var result = await db.Loads.AsNoTracking().Include(load => load.Stops)
                .Where(load => load.PlanningDate == date && load.Status != LoadStatus.Cancelled)
                .OrderBy(load => load.Reference).Take(1000).ToListAsync(ct);
            if (result.Count > 0) return result;
        }
        catch (Exception ex) when (SchemaUnavailable(ex))
        {
            logger.LogWarning(ex, "Planner suggestions are using the fallback planning register.");
            db.ChangeTracker.Clear();
        }
        return await PlanningRegisterStore.ReadLoadsAsync(db, date, ct);
    }

    private static async Task<List<T>> SafeList<T>(IQueryable<T> query, CancellationToken ct)
    {
        try { return await query.ToListAsync(ct); }
        catch { return []; }
    }

    private static Site? FindSite(IEnumerable<Site> sites, string? value)
    {
        var key = Normalise(value);
        if (key.Length == 0) return null;
        return sites.FirstOrDefault(site =>
            new[] { site.ExternalCode, site.Name, site.DriverTextName }
                .Concat((site.Aliases ?? string.Empty).Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Any(candidate => Normalise(candidate) == key));
    }

    private static string CollectionName(TransportOrder order) =>
        order.SellerName ?? Tag(order.DriverInstructions, "Collection site") ?? "Collection not mapped";

    private static string Destination(TransportOrder order) =>
        order.StallNumber ?? Tag(order.DriverInstructions, "Depot") ?? order.MarketName ?? order.Reference;

    private static string OrderType(TransportOrder order)
    {
        var tagged = Tag(order.DriverInstructions, "Order type");
        if (!string.IsNullOrWhiteSpace(tagged)) return tagged;
        var combined = $"{order.DriverInstructions} {order.StallNumber} {order.MarketName}";
        return combined.Contains("crate", StringComparison.OrdinalIgnoreCase) ? "Crates" : "Pallets";
    }

    private static string? Tag(string? notes, string label)
    {
        if (string.IsNullOrWhiteSpace(notes)) return null;
        var prefix = $"{label}:";
        return notes.Split('·', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(part => part.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..].Trim();
    }

    private static double HaversineMiles(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthMiles = 3958.7613;
        static double Radians(double degrees) => degrees * Math.PI / 180d;
        var dLat = Radians(lat2 - lat1);
        var dLon = Radians(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(Radians(lat1)) * Math.Cos(Radians(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return earthMiles * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static string Normalise(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static bool SchemaUnavailable(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        return exception is InvalidOperationException or DbUpdateException ||
               message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Cannot find the object", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record PlannerDaySuggestion(
    Guid DriverId,
    string DriverName,
    string EmployeeNumber,
    Guid PreviousLoadId,
    string PreviousLoadReference,
    DateOnly PreviousPlanningDate,
    string LastLocation,
    decimal? LastLatitude,
    decimal? LastLongitude,
    Guid OrderId,
    string OrderReference,
    string CustomerCode,
    string CollectionSite,
    string Destination,
    string OrderType,
    int? Quantity,
    double? RepositionMiles,
    int? DriveAvailableTodayMinutes,
    double Score,
    string Reason);
