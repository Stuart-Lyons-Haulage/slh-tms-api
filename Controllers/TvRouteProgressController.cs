using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Read-only route progression for the office TV. The progression is derived
/// from the embedded approved SLH geofences, RoadTech tracking and the planning register.
/// No geofence-specific SQL tables are required.
/// </summary>
[ApiController, Route("api/v1/tv-display/route-progress")]
public sealed class TvRouteProgressController(TmsDbContext db, IConfiguration configuration) : ControllerBase
{
    [HttpGet, AllowAnonymous]
    public async Task<IActionResult> Get(
        [FromHeader(Name = "X-TV-Display-Key")] string? displayKey,
        [FromQuery] DateOnly? date,
        CancellationToken ct)
    {
        var pairedKeyAllowed = await TvDisplayKeyStore.ValidateAsync(db, displayKey, ct);
        var legacyKeyAllowed = TvWallboardAccess.IsAllowed(HttpContext, configuration);
        if (!pairedKeyAllowed && !legacyKeyAllowed)
            return Unauthorized(new { message = "This TV display is not authorised." });

        var day = date ?? UkOperatingDate(DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow;
        var loads = (await PlanningResilience.ReadLoadsAsync(db, day, ct))
            .Where(load => load.Status != LoadStatus.Cancelled)
            .OrderBy(load => load.Reference)
            .ToList();

        var snapshot = await EmbeddedGeofenceEngine.BuildAsync(db, day, loads, ct);
        var vehicleIds = loads.Where(x => x.VehicleId is not null).Select(x => x.VehicleId!.Value).Distinct().ToList();
        var vehicles = vehicleIds.Count == 0
            ? new List<Vehicle>()
            : await SafeList(db.Vehicles.AsNoTracking().Where(x => vehicleIds.Contains(x.Id)), ct);
        var liveStatuses = await SafeList(db.VehicleLiveStatuses.AsNoTracking(), ct);
        var vehicleById = vehicles.ToDictionary(x => x.Id);

        var rows = new List<object>();
        foreach (var load in loads)
        {
            var stops = load.Stops.OrderBy(x => x.Sequence).ToList();
            var visits = snapshot.Visits.Where(x => x.LoadId == load.Id).OrderBy(x => x.EnteredAtUtc).ToList();
            var completedStopIds = visits
                .Where(x => x.LoadStopId is not null && x.ConfirmedAtUtc is not null && x.ExitedAtUtc is not null)
                .Select(x => x.LoadStopId!.Value)
                .ToHashSet();
            var activeVisit = snapshot.ActiveVisits
                .Where(x => x.LoadId == load.Id)
                .OrderByDescending(x => x.EnteredAtUtc)
                .FirstOrDefault();

            var currentStop = activeVisit?.LoadStopId is Guid currentId
                ? stops.FirstOrDefault(x => x.Id == currentId)
                : null;
            var nextStop = stops.FirstOrDefault(x => !completedStopIds.Contains(x.Id) && x.Id != currentStop?.Id);
            if (currentStop is null)
                nextStop = stops.FirstOrDefault(x => !completedStopIds.Contains(x.Id));

            VehicleLiveStatus? live = null;
            if (load.VehicleId is Guid vehicleId && vehicleById.TryGetValue(vehicleId, out var vehicle))
                live = MatchLive(vehicle, liveStatuses);

            var truckPosition = TruckPositionPercent(stops, completedStopIds, activeVisit, live);
            var complete = stops.Count > 0 && completedStopIds.Count >= stops.Count;
            var phase = complete
                ? "Complete"
                : currentStop is not null
                    ? "On site"
                    : completedStopIds.Count > 0 || load.Status is LoadStatus.InProgress or LoadStatus.Dispatched
                        ? "Heading to"
                        : live is not null && (live.IsMoving == true || (live.SpeedKph ?? 0) > 2)
                            ? "Heading to"
                            : "Next job";
            var focusStop = currentStop ?? nextStop;

            var stopRows = stops.Select(stop =>
            {
                var state = completedStopIds.Contains(stop.Id)
                    ? "completed"
                    : currentStop?.Id == stop.Id
                        ? "onsite"
                        : nextStop?.Id == stop.Id
                            ? "heading"
                            : "upcoming";
                return new
                {
                    stop.Id,
                    stop.Sequence,
                    stop.Name,
                    stop.PlannedArrivalUtc,
                    state
                };
            }).ToList();

            var freshnessAtUtc = live?.LastReceivedAtUtc;
            rows.Add(new
            {
                loadId = load.Id,
                reference = load.Reference,
                totalStops = stops.Count,
                completedStops = completedStopIds.Count,
                currentStopId = currentStop?.Id,
                nextStopId = nextStop?.Id,
                focusStop = focusStop?.Name,
                phase,
                truckPositionPercent = truckPosition,
                geofenceOnSite = currentStop is not null,
                trackingFresh = freshnessAtUtc is not null && now - freshnessAtUtc <= TimeSpan.FromMinutes(15),
                trackingMoving = live is not null && (live.IsMoving == true || (live.SpeedKph ?? 0) > 2),
                speedKph = live?.SpeedKph,
                stops = stopRows
            });
        }

        return Ok(new
        {
            planningDate = day,
            calculatedAtUtc = now,
            geofenceAvailable = snapshot.Fences.Count > 0,
            geofenceCount = snapshot.Fences.Count,
            geofenceVisitCount = snapshot.Visits.Count,
            geofenceLinkedRuns = snapshot.Visits.Where(x => x.LoadId is not null).Select(x => x.LoadId!.Value).Distinct().Count(),
            runs = rows
        });
    }

    private static decimal TruckPositionPercent(
        IReadOnlyList<LoadStop> stops,
        IReadOnlySet<Guid> completedStopIds,
        DerivedVisit? activeVisit,
        VehicleLiveStatus? live)
    {
        if (stops.Count <= 1) return 0m;

        if (activeVisit?.LoadStopId is Guid activeStopId)
        {
            var activeIndex = IndexOf(stops, activeStopId);
            if (activeIndex >= 0) return PercentAtIndex(activeIndex, stops.Count);
        }

        var lastCompletedIndex = -1;
        for (var i = 0; i < stops.Count; i++)
            if (completedStopIds.Contains(stops[i].Id)) lastCompletedIndex = i;

        if (lastCompletedIndex >= stops.Count - 1) return 100m;
        if (lastCompletedIndex < 0)
        {
            return live is not null && (live.IsMoving == true || (live.SpeedKph ?? 0) > 2) ? 5m : 0m;
        }

        var nextIndex = lastCompletedIndex + 1;
        while (nextIndex < stops.Count && completedStopIds.Contains(stops[nextIndex].Id)) nextIndex++;
        if (nextIndex >= stops.Count) return 100m;

        var legFraction = 0.42m;
        var previous = stops[lastCompletedIndex];
        var next = stops[nextIndex];
        if (live is not null && previous.Latitude is not null && previous.Longitude is not null && next.Latitude is not null && next.Longitude is not null)
        {
            var fromPrevious = DistanceKm(previous.Latitude.Value, previous.Longitude.Value, live.Latitude, live.Longitude);
            var toNext = DistanceKm(live.Latitude, live.Longitude, next.Latitude.Value, next.Longitude.Value);
            var total = fromPrevious + toNext;
            if (total > 0.05)
                legFraction = Math.Clamp((decimal)(fromPrevious / total), 0.05m, 0.95m);
        }

        var indexPosition = lastCompletedIndex + (nextIndex - lastCompletedIndex) * legFraction;
        return Math.Round(indexPosition / (stops.Count - 1) * 100m, 1);
    }

    private static int IndexOf(IReadOnlyList<LoadStop> stops, Guid id)
    {
        for (var i = 0; i < stops.Count; i++) if (stops[i].Id == id) return i;
        return -1;
    }

    private static decimal PercentAtIndex(int index, int count) => count <= 1 ? 0m : Math.Round((decimal)index / (count - 1) * 100m, 1);

    private static double DistanceKm(decimal latitude1, decimal longitude1, decimal latitude2, decimal longitude2)
    {
        const double earthRadiusKm = 6371.0;
        var lat1 = DegreesToRadians((double)latitude1);
        var lat2 = DegreesToRadians((double)latitude2);
        var dLat = lat2 - lat1;
        var dLon = DegreesToRadians((double)longitude2 - (double)longitude1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return earthRadiusKm * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;

    private static VehicleLiveStatus? MatchLive(Vehicle vehicle, IReadOnlyCollection<VehicleLiveStatus> statuses)
    {
        var aliases = new[] { vehicle.Registration, vehicle.FleetNumber, vehicle.Abbreviation }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Normalise(value!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return statuses
            .Where(status => aliases.Contains(Normalise(status.VehicleIdentifier)))
            .OrderByDescending(status => status.LastReceivedAtUtc)
            .FirstOrDefault();
    }

    private static string Normalise(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static async Task<List<T>> SafeList<T>(IQueryable<T> query, CancellationToken ct)
    {
        try { return await query.ToListAsync(ct); }
        catch { dbSafeNoop(); return new List<T>(); }

        static void dbSafeNoop() { }
    }

    private static DateOnly UkOperatingDate(DateTimeOffset value)
    {
        try
        {
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, TimeZoneInfo.FindSystemTimeZoneById("Europe/London")).DateTime);
        }
        catch (TimeZoneNotFoundException)
        {
            return DateOnly.FromDateTime(value.UtcDateTime);
        }
    }
}
