using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/planner-starts")]
[Authorize]
public sealed class PlannerCalculatedStartController(
    TmsDbContext db,
    TachoMasterClient tachoMaster,
    DotTrackingClient trackingClient,
    AzureMapsRouteClient maps,
    DriverWeeklyRestComplianceService weeklyRest,
    ILogger<PlannerCalculatedStartController> logger) : ControllerBase
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    private static readonly Regex Clock = new(@"(?<!\d)(?:[01]?\d|2[0-3]):[0-5]\d(?!\d)", RegexOptions.Compiled);
    private const int WalkaroundMinutes = 10;

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] DateOnly date, CancellationToken ct)
    {
        var loads = (await PlanningResilience.ReadLoadsAsync(db, date, ct))
            .Where(load => load.Status != LoadStatus.Cancelled)
            .OrderBy(load => load.Reference)
            .ToList();
        if (loads.Count == 0) return Ok(new { planningDate = date, walkaroundMinutes = WalkaroundMinutes, rows = Array.Empty<object>() });

        var driverIds = loads.Where(load => load.DriverId is not null).Select(load => load.DriverId!.Value).Distinct().ToList();
        var vehicleIds = loads.Where(load => load.VehicleId is not null).Select(load => load.VehicleId!.Value).Distinct().ToList();
        var drivers = driverIds.Count == 0 ? [] : await db.Drivers.AsNoTracking().Where(driver => driverIds.Contains(driver.Id)).ToListAsync(ct);
        var vehicles = vehicleIds.Count == 0 ? [] : await db.Vehicles.AsNoTracking().Where(vehicle => vehicleIds.Contains(vehicle.Id)).ToListAsync(ct);
        var driverById = drivers.ToDictionary(driver => driver.Id);
        var vehicleById = vehicles.ToDictionary(vehicle => vehicle.Id);
        var sites = await db.Sites.AsNoTracking().Where(site => site.Active).ToListAsync(ct);
        try { await MasterDetailStore.EnrichSitesAsync(db, sites, ct); } catch { /* core site fields remain usable */ }

        var dispatchState = await DriverDispatchStateStore.ReadAsync(db, loads.Select(load => load.Id), ct);
        var duties = await ReadDutiesAsync(date, ct);
        var live = await ReadLivePositionsAsync(ct);
        var history = driverIds.Count == 0 ? [] : await db.Loads.AsNoTracking().Include(load => load.Stops)
            .Where(load => load.DriverId != null && driverIds.Contains(load.DriverId.Value) && load.PlanningDate < date && load.Status != LoadStatus.Cancelled)
            .OrderByDescending(load => load.PlanningDate).ThenByDescending(load => load.CreatedAtUtc)
            .Take(500).ToListAsync(ct);
        var lyons = FindLyonsSite(sites);

        var rows = new List<PlannerStartSuggestion>();
        foreach (var load in loads)
        {
            var suggestion = await SuggestAsync(load, driverById, vehicleById, sites, lyons, duties, live, history, dispatchState.GetValueOrDefault(load.Id), ct);
            rows.Add(suggestion);
        }

        return Ok(new { planningDate = date, walkaroundMinutes = WalkaroundMinutes, rows });
    }

    [HttpPut("{loadId:guid}/apply"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Apply(Guid loadId, CancellationToken ct)
    {
        var load = await PlanningResilience.ReadLoadAsync(db, loadId, ct);
        if (load is null) return NotFound(new { message = "The run could not be found." });
        if (load.DriverId is null) return BadRequest(new { message = "Allocate a driver before calculating the start time." });

        var date = load.PlanningDate;
        var drivers = await db.Drivers.AsNoTracking().Where(driver => driver.Id == load.DriverId.Value).ToListAsync(ct);
        var vehicles = load.VehicleId is null ? [] : await db.Vehicles.AsNoTracking().Where(vehicle => vehicle.Id == load.VehicleId.Value).ToListAsync(ct);
        var sites = await db.Sites.AsNoTracking().Where(site => site.Active).ToListAsync(ct);
        try { await MasterDetailStore.EnrichSitesAsync(db, sites, ct); } catch { }
        var duties = await ReadDutiesAsync(date, ct);
        var live = await ReadLivePositionsAsync(ct);
        var history = await db.Loads.AsNoTracking().Include(item => item.Stops)
            .Where(item => item.DriverId == load.DriverId && item.PlanningDate < date && item.Status != LoadStatus.Cancelled)
            .OrderByDescending(item => item.PlanningDate).ThenByDescending(item => item.CreatedAtUtc).Take(30).ToListAsync(ct);
        var suggestion = await SuggestAsync(load, drivers.ToDictionary(x => x.Id), vehicles.ToDictionary(x => x.Id), sites, FindLyonsSite(sites), duties, live, history, null, ct);
        if (suggestion.SuggestedStartUtc is null) return BadRequest(new { message = suggestion.Explanation });

        var actor = User.Identity?.Name ?? "TMS planner";
        var state = await DriverDispatchStateStore.SetPlannedStartAsync(db, loadId, suggestion.SuggestedStartUtc, actor, ct, "Calculated");
        return Ok(new { state, suggestion });
    }

    private async Task<PlannerStartSuggestion> SuggestAsync(
        Load load,
        IReadOnlyDictionary<Guid, Driver> drivers,
        IReadOnlyDictionary<Guid, Vehicle> vehicles,
        IReadOnlyList<Site> sites,
        Site? lyons,
        IReadOnlyList<TachoDriverDutyStatus> duties,
        IReadOnlyDictionary<string, DotTelemetryRecord> live,
        IReadOnlyList<Load> history,
        DriverDispatchState? existing,
        CancellationToken ct)
    {
        var firstCollection = OperationalStopOrdering.Order(load.Stops).FirstOrDefault(stop => stop.Name.StartsWith("Collect", StringComparison.OrdinalIgnoreCase))
            ?? OperationalStopOrdering.Order(load.Stops).FirstOrDefault();
        var firstSite = firstCollection is null ? null : ResolveSite(sites, firstCollection.Name);
        var latestOnSite = SiteCutoff(firstSite);
        if (load.DriverId is not Guid driverId || !drivers.TryGetValue(driverId, out var driver))
            return PlannerStartSuggestion.Empty(load, firstCollection, existing, latestOnSite, "Allocate a driver to calculate the legal start.");

        var matched = duties.Where(duty => DriverDayCycleCalculator.MatchesDriver(driver, duty)).OrderBy(duty => duty.DutyStartUtc).ToList();
        var latestDuty = matched.LastOrDefault();
        var lastCompleted = matched.Where(duty => duty.DutyEndUtc is not null).OrderBy(duty => duty.DutyEndUtc).LastOrDefault();
        if (latestDuty is not null && latestDuty.DutyEndUtc is null)
            return await BuildFromActiveDuty(load, driver, vehicles, live, history, firstCollection, firstSite, existing, latestOnSite, ct);
        if (lastCompleted?.DutyEndUtc is not DateTimeOffset dutyEnd)
            return PlannerStartSuggestion.Empty(load, firstCollection, existing, latestOnSite, "No completed TachoMaster duty was found. Enter the start manually until Tacho history is available.");

        var shortRestsUsed = Math.Max(0, lastCompleted.ShortDailyRestTakenThisWeek ?? 0);
        var dailyRestHours = shortRestsUsed < 3 ? 9 : 11;
        var restComplete = dutyEnd.AddHours(dailyRestHours);
        var weekly = await weeklyRest.EvaluateAsync(driver, load.PlanningDate, restComplete, ct);
        if (string.Equals(weekly.Status, "Overdue", StringComparison.OrdinalIgnoreCase))
        {
            dailyRestHours = 45;
            restComplete = dutyEnd.AddHours(dailyRestHours);
        }

        var origin = OriginFromSite(lyons, "Stuart Lyons Haulage", "Fresh duty after legal rest");
        if (origin.Latitude is null || origin.Longitude is null)
        {
            var previous = PreviousFinal(history, driver.Id);
            if (previous is not null) origin = previous.Value;
        }

        var travel = await TravelAsync(origin, firstCollection, firstSite, ct);
        var firstEta = travel.Minutes is null ? null : restComplete.AddMinutes(WalkaroundMinutes + travel.Minutes.Value);
        var explanation = $"Tacho rest complete {Local(restComplete):HH:mm} · 10 min walkaround · {origin.Label} → {CleanStop(firstCollection?.Name) ?? "first collection"}{(travel.Minutes is null ? " · travel time unavailable" : $" {travel.Minutes} min")}.";

        return new PlannerStartSuggestion(load.Id, RunDisplayLabel.For(load), driver.DisplayName, existing?.PlannedStartUtc, existing?.Source,
            restComplete, restComplete, WalkaroundMinutes, origin.Label, travel.Minutes, firstEta, CleanStop(firstCollection?.Name), latestOnSite,
            dailyRestHours == 45 ? "Weekly rest" : dailyRestHours == 9 ? "Reduced daily rest" : "Regular daily rest", explanation);
    }

    private async Task<PlannerStartSuggestion> BuildFromActiveDuty(
        Load load, Driver driver, IReadOnlyDictionary<Guid, Vehicle> vehicles, IReadOnlyDictionary<string, DotTelemetryRecord> live,
        IReadOnlyList<Load> history, LoadStop? firstCollection, Site? firstSite, DriverDispatchState? existing, string? latestOnSite, CancellationToken ct)
    {
        var origin = default(Origin);
        if (load.VehicleId is Guid vehicleId && vehicles.TryGetValue(vehicleId, out var vehicle))
        {
            var key = Normalise(vehicle.Registration);
            if (live.TryGetValue(key, out var record) && record.Latitude is decimal lat && record.Longitude is decimal lon)
                origin = new Origin(vehicle.Registration, lat, lon, "DOT live vehicle location");
        }
        if (origin.Latitude is null)
        {
            var previous = PreviousFinal(history, driver.Id);
            if (previous is not null) origin = previous.Value;
        }
        var travel = await TravelAsync(origin, firstCollection, firstSite, ct);
        return new PlannerStartSuggestion(load.Id, RunDisplayLabel.For(load), driver.DisplayName, existing?.PlannedStartUtc, existing?.Source,
            null, null, WalkaroundMinutes, origin.Label, travel.Minutes, null, CleanStop(firstCollection?.Name), latestOnSite, "Active duty",
            "TachoMaster shows an open duty. DOT/previous-run location is shown, but the next legal rest completion cannot be calculated until that duty closes.");
    }

    private async Task<(int? Minutes, string Source)> TravelAsync(Origin origin, LoadStop? stop, Site? site, CancellationToken ct)
    {
        var destinationLat = stop?.Latitude ?? site?.Latitude;
        var destinationLon = stop?.Longitude ?? site?.Longitude;
        if (origin.Latitude is not decimal fromLat || origin.Longitude is not decimal fromLon || destinationLat is not decimal toLat || destinationLon is not decimal toLon)
            return (null, "Location missing");
        try
        {
            var estimate = await maps.TravelTimeEstimate((fromLon, fromLat), (toLon, toLat), ct);
            return ((int)Math.Ceiling(estimate.TravelTime.TotalMinutes), estimate.Provider);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Planner calculated-start routing failed for {Origin} to {Destination}.", origin.Label, stop?.Name);
            return (null, "Routing unavailable");
        }
    }

    private async Task<List<TachoDriverDutyStatus>> ReadDutiesAsync(DateOnly planningDate, CancellationToken ct)
    {
        if (!tachoMaster.IsConfigured) return [];
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, London).DateTime);
        var through = planningDate < today ? planningDate : today;
        var from = through.AddDays(-8);
        var duties = new List<TachoDriverDutyStatus>();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            for (var day = from; day <= through; day = day.AddDays(1)) duties.AddRange(await tachoMaster.GetDriverDutyStatusesAsync(day, timeout.Token));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Tacho duty history unavailable for Planner calculated start.");
        }
        return duties;
    }

    private async Task<IReadOnlyDictionary<string, DotTelemetryRecord>> ReadLivePositionsAsync(CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            return (await trackingClient.GetLatestVehicleEventsAsync(timeout.Token)).Select(DotTelemetryRecord.FromProvider)
                .Where(record => !string.IsNullOrWhiteSpace(record.VehicleIdentifier) && record.Latitude is not null && record.Longitude is not null)
                .GroupBy(record => Normalise(record.VehicleIdentifier), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.OrderByDescending(record => record.EventTimeUtc).First(), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "DOT live position unavailable for Planner calculated start.");
            return new Dictionary<string, DotTelemetryRecord>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static Site? FindLyonsSite(IEnumerable<Site> sites) => sites.FirstOrDefault(site =>
        site.Name.Contains("Stuart Lyons", StringComparison.OrdinalIgnoreCase) ||
        site.Name.Contains("Lyons Haulage", StringComparison.OrdinalIgnoreCase) ||
        site.DriverTextName?.Contains("Lyons Haulage", StringComparison.OrdinalIgnoreCase) == true);

    private static Site? ResolveSite(IEnumerable<Site> sites, string value)
    {
        var target = Normalise(CleanStop(value));
        return sites.FirstOrDefault(site => new[] { site.Name, site.DriverTextName, site.ExternalCode }
            .Concat((site.Aliases ?? string.Empty).Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries))
            .Any(candidate => Normalise(candidate) == target));
    }

    private static Origin OriginFromSite(Site? site, string fallback, string source) =>
        new(site?.Name ?? fallback, site?.Latitude, site?.Longitude, source);

    private static Origin? PreviousFinal(IEnumerable<Load> history, Guid driverId)
    {
        var previous = history.FirstOrDefault(load => load.DriverId == driverId);
        var stop = previous is null ? null : OperationalStopOrdering.Order(previous.Stops).LastOrDefault(item => item.Latitude is not null && item.Longitude is not null);
        return stop is null ? null : new Origin(CleanStop(stop.Name) ?? stop.Name, stop.Latitude, stop.Longitude, "Previous run final stop");
    }

    private static string? SiteCutoff(Site? site)
    {
        if (site is null) return null;
        foreach (var value in new[] { site.CustomField1, site.CustomField2, site.CustomField3, site.CollectionInstructions })
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            var match = Clock.Match(value);
            if (match.Success) return match.Value.PadLeft(5, '0');
        }
        return null;
    }

    private static string? CleanStop(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var marker = value.IndexOf('·');
        return (marker >= 0 ? value[(marker + 1)..] : value).Trim();
    }

    private static string Normalise(string? value) => Regex.Replace(value ?? string.Empty, "[^A-Za-z0-9]", string.Empty).ToUpperInvariant();
    private static DateTimeOffset Local(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, London);

    private readonly record struct Origin(string Label, decimal? Latitude, decimal? Longitude, string Source);
}

public sealed record PlannerStartSuggestion(
    Guid LoadId,
    string RunReference,
    string DriverName,
    DateTimeOffset? ExistingStartUtc,
    string? ExistingStartSource,
    DateTimeOffset? LegalRestCompleteUtc,
    DateTimeOffset? SuggestedStartUtc,
    int WalkaroundMinutes,
    string? Origin,
    int? TravelMinutes,
    DateTimeOffset? FirstCollectionEtaUtc,
    string? FirstCollection,
    string? LatestOnSite,
    string RestType,
    string Explanation)
{
    public static PlannerStartSuggestion Empty(Load load, LoadStop? firstCollection, DriverDispatchState? existing, string? latestOnSite, string explanation) =>
        new(load.Id, RunDisplayLabel.For(load), string.Empty, existing?.PlannedStartUtc, existing?.Source, null, null, 10, null, null, null,
            firstCollection?.Name, latestOnSite, "Unverified", explanation);
}
