using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;

namespace Slh.Tms.Api.Services;

public static class EmbeddedGeofenceEngine
{
    private const int DefaultConfirmDwellMinutes = 10;
    private static readonly Lazy<IReadOnlyList<EmbeddedFence>> Fences = new(ParseFences);
    private static readonly HashSet<string> IgnoredNameTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "THE", "LTD", "LIMITED", "PLC", "RDC", "SITE", "DEPOT", "DELIVERY", "COLLECTION", "CUSTOMER"
    };

    public static IReadOnlyList<EmbeddedFence> ApprovedFences => Fences.Value;

    public static async Task<EmbeddedGeofenceSnapshot> BuildAsync(TmsDbContext db, DateOnly planningDate, IReadOnlyCollection<Load> loads, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var fences = Fences.Value;
        var vehicleIds = loads.Where(x => x.VehicleId is not null).Select(x => x.VehicleId!.Value).Distinct().ToList();
        List<Vehicle> vehicles = vehicleIds.Count == 0
            ? new List<Vehicle>()
            : await db.Vehicles.AsNoTracking()
                .Where(x => vehicleIds.Contains(x.Id))
                .Select(x => new Vehicle { Id = x.Id, Registration = x.Registration, FleetNumber = x.FleetNumber, Abbreviation = x.Abbreviation, Active = x.Active })
                .ToListAsync(ct);

        // Use the same canonical vehicle aliases as Fleet Status and customer ETA
        // evidence, including explicit DotTracking/TachoMaster mappings and the same
        // registration/fleet suffix forms used by the live DOT screen.
        var aliasesByVehicle = await ExecutionIdentityResolver.VehicleAliasesAsync(db, vehicles, ct);

        var (startUtc, endUtc) = OperatingWindow(planningDate);
        var windowStartUtc = startUtc.AddHours(-2);
        var windowEndUtc = endUtc.AddHours(2);
        List<VehicleTrackingEvent> events;
        if (vehicles.Count == 0)
        {
            events = [];
        }
        else
        {
            // Never truncate the operating day before vehicle matching. The previous
            // implementation took the first 20,000 fleet-wide events and only then
            // matched them to planned vehicles. As the day accumulated telemetry,
            // later events (including departures/final deliveries) disappeared from
            // reconstruction, making completed runs regress to Upcoming.
            //
            // Do not rediscover provider identifiers with a fleet-wide DISTINCT scan.
            // Production can hold a full day's telemetry for every vehicle; scanning
            // that set before filtering to today's planned vehicles can time out and
            // force run-progress into its safe fallback. Query the indexed identifier
            // values derived from the planned vehicle aliases instead.
            var plannedIdentifiers = aliasesByVehicle.Values
                .SelectMany(aliases => aliases)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            try
            {
                events = plannedIdentifiers.Count == 0
                    ? []
                    : await db.VehicleTrackingEvents.AsNoTracking()
                        .Where(x => x.EventTimeUtc >= windowStartUtc &&
                                    x.EventTimeUtc < windowEndUtc &&
                                    plannedIdentifiers.Contains(x.VehicleIdentifier))
                        .OrderBy(x => x.EventTimeUtc)
                        .ToListAsync(ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                db.ChangeTracker.Clear();
                events = [];
            }
        }

        var matchedEvents = events
            .Where(item => aliasesByVehicle.Values.Any(aliases =>
                ExecutionIdentityResolver.MatchesVehicleIdentifier(aliases, item.VehicleIdentifier)))
            .ToList();

        // Falcon's current-telemetry endpoint can legitimately return the same provider
        // event while a vehicle is stationary. The event is correctly deduplicated in
        // VehicleTrackingEvents, but VehicleLiveStatus.LastReceivedAtUtc still proves that
        // the same position was freshly observed. Resolve those live observations using
        // the identical alias rules as the historic tracking event stream.
        var freshLiveByVehicle = new Dictionary<Guid, VehicleLiveStatus>();
        if (vehicles.Count > 0 && planningDate == UkOperatingDate(now))
        {
            var freshnessFloor = now.AddMinutes(-5);
            List<VehicleLiveStatus> liveStatuses;
            try
            {
                liveStatuses = await db.VehicleLiveStatuses.AsNoTracking()
                    .Where(x => x.LastReceivedAtUtc >= freshnessFloor)
                    .ToListAsync(ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                db.ChangeTracker.Clear();
                liveStatuses = [];
            }
            foreach (var live in liveStatuses)
            {
                foreach (var vehicle in vehicles)
                {
                    if (!aliasesByVehicle.TryGetValue(vehicle.Id, out var aliases) ||
                        !ExecutionIdentityResolver.MatchesVehicleIdentifier(aliases, live.VehicleIdentifier))
                        continue;

                    if (!freshLiveByVehicle.TryGetValue(vehicle.Id, out var existing) ||
                        live.LastReceivedAtUtc > existing.LastReceivedAtUtc)
                    {
                        freshLiveByVehicle[vehicle.Id] = live;
                    }
                }
            }
        }

        var visits = new List<DerivedVisit>();
        var observationCount = matchedEvents.Count;
        foreach (var vehicle in vehicles)
        {
            var aliases = aliasesByVehicle.TryGetValue(vehicle.Id, out var known) ? known : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var vehicleEvents = matchedEvents
                .Where(item => ExecutionIdentityResolver.MatchesVehicleIdentifier(aliases, item.VehicleIdentifier))
                .OrderBy(x => x.EventTimeUtc)
                .ToList();

            var live = freshLiveByVehicle.GetValueOrDefault(vehicle.Id);
            if (live is not null && (vehicleEvents.Count == 0 || live.LastReceivedAtUtc > vehicleEvents[^1].EventTimeUtc))
            {
                vehicleEvents.Add(new VehicleTrackingEvent
                {
                    ProviderName = "RoadTech Falcon live observation",
                    ProviderEventId = $"live-{live.Id}-{live.LastReceivedAtUtc:O}",
                    VehicleIdentifier = live.VehicleIdentifier,
                    EventTimeUtc = live.LastReceivedAtUtc,
                    Latitude = live.Latitude,
                    Longitude = live.Longitude,
                    SpeedKph = live.SpeedKph,
                    IgnitionOn = live.IgnitionOn,
                    IsMoving = live.IsMoving,
                    RawPayload = "{}",
                    MatchStatus = "FreshLiveObservation"
                });
                observationCount++;
            }

            visits.AddRange(DeriveVisits(vehicle.Id, vehicle.Registration, vehicleEvents.OrderBy(x => x.EventTimeUtc).ToList(), fences));
        }

        LinkVisitsToRuns(visits, loads);
        var activeVisits = visits.Where(x => x.ExitedAtUtc is null && now - x.LastInsideAtUtc <= TimeSpan.FromMinutes(15)).ToList();
        var confirmed = visits.Where(x => x.ConfirmedAtUtc is not null).ToList();
        var latestTracking = new[]
            {
                matchedEvents.OrderByDescending(x => x.EventTimeUtc).FirstOrDefault()?.EventTimeUtc,
                freshLiveByVehicle.Values.OrderByDescending(x => x.LastReceivedAtUtc).FirstOrDefault()?.LastReceivedAtUtc
            }
            .Where(x => x is not null)
            .Select(x => x!.Value)
            .DefaultIfEmpty()
            .Max();
        return new EmbeddedGeofenceSnapshot(fences, visits, activeVisits, confirmed, observationCount, latestTracking == default ? null : latestTracking);
    }

    public static async Task<IReadOnlyList<EmbeddedFenceStatus>> FenceStatusesAsync(TmsDbContext db, CancellationToken ct)
    {
        List<Site> sites;
        try { sites = await GeofenceSiteResolver.LoadActiveSitesAsync(db, ct); }
        catch { sites = new List<Site>(); db.ChangeTracker.Clear(); }

        return Fences.Value.Select(fence =>
        {
            var site = MatchSite(fence, sites);
            return new EmbeddedFenceStatus(fence, site?.Id, site?.Name);
        }).ToList();
    }

    private static IReadOnlyList<EmbeddedFence> ParseFences()
    {
        using var document = JsonDocument.Parse(GeofenceSeedPayload.Json);
        var result = new List<EmbeddedFence>();
        foreach (var record in document.RootElement.EnumerateArray())
        {
            var name = Text(record, "name")?.Trim();
            if (string.IsNullOrWhiteSpace(name) || !record.TryGetProperty("points", out var points) || points.ValueKind != JsonValueKind.Array) continue;
            var parsedPoints = points.EnumerateArray().Select(ReadPoint).Where(x => x is not null).Select(x => x!).ToList();
            if (parsedPoints.Count < 3) continue;
            result.Add(new EmbeddedFence(StableId(name), name, Text(record, "category"), Int(record, "category_max_wait_time"), Int(record, "max_wait_time"), Int(record, "pending_entry_minutes") ?? 0, Int(record, "pending_exit_minutes") ?? 0, Text(record, "site_no"), parsedPoints));
        }
        return result;
    }

    private static IEnumerable<DerivedVisit> DeriveVisits(Guid vehicleId, string registration, IReadOnlyList<VehicleTrackingEvent> events, IReadOnlyList<EmbeddedFence> fences)
    {
        var visits = new List<DerivedVisit>();
        EmbeddedFence? currentFence = null;
        DerivedVisit? current = null;

        foreach (var evt in events)
        {
            var fence = fences.FirstOrDefault(x => Contains(x.Points, evt.Longitude, evt.Latitude));
            if (fence?.Id == currentFence?.Id)
            {
                if (current is not null) UpdateVisit(current, evt.EventTimeUtc);
                continue;
            }

            if (current is not null)
            {
                var enteringDifferentFence = fence is not null && fence.Id != current.Fence.Id;
                var exitMinutes = Math.Max(0, current.Fence.PendingExitMinutes);
                var exitConfirmed = enteringDifferentFence || evt.EventTimeUtc - current.LastInsideAtUtc >= TimeSpan.FromMinutes(exitMinutes);
                if (exitConfirmed)
                {
                    current.ExitedAtUtc = evt.EventTimeUtc;
                    current.DwellMinutes = Math.Max(0, (int)Math.Floor((current.LastInsideAtUtc - current.EnteredAtUtc).TotalMinutes));
                    visits.Add(current);
                    current = null;
                    currentFence = null;
                }
                else
                {
                    continue;
                }
            }

            if (fence is not null)
            {
                currentFence = fence;
                current = new DerivedVisit
                {
                    Id = StableId($"{registration}|{fence.Name}|{evt.EventTimeUtc:O}"),
                    VehicleId = vehicleId,
                    VehicleIdentifier = registration,
                    Fence = fence,
                    EnteredAtUtc = evt.EventTimeUtc,
                    LastInsideAtUtc = evt.EventTimeUtc
                };
            }
        }

        if (current is not null)
        {
            UpdateVisit(current, current.LastInsideAtUtc);
            visits.Add(current);
        }
        return visits;
    }

    private static void UpdateVisit(DerivedVisit visit, DateTimeOffset at)
    {
        visit.LastInsideAtUtc = at;
        visit.DwellMinutes = Math.Max(0, (int)Math.Floor((visit.LastInsideAtUtc - visit.EnteredAtUtc).TotalMinutes));
        var confirmMinutes = Math.Max(DefaultConfirmDwellMinutes, visit.Fence.PendingEntryMinutes);
        if (visit.ConfirmedAtUtc is null && visit.DwellMinutes >= confirmMinutes) visit.ConfirmedAtUtc = at;
    }

    private static void LinkVisitsToRuns(List<DerivedVisit> visits, IReadOnlyCollection<Load> loads)
    {
        var usedStops = new HashSet<Guid>();
        foreach (var visit in visits.OrderBy(x => x.EnteredAtUtc))
        {
            var candidate = loads
                .Where(load => load.VehicleId == visit.VehicleId && load.Status != LoadStatus.Cancelled)
                .SelectMany(load => (load.Stops ?? []).Where(stop => !usedStops.Contains(stop.Id)).Select(stop => new { load, stop }))
                .Where(x => NamesOverlap(x.stop.Name, visit.Fence.Name) || NamesOverlap(x.stop.Address, visit.Fence.Name))
                .Select(x => new { x.load, x.stop, delta = x.stop.PlannedArrivalUtc is null ? double.MaxValue : Math.Abs((x.stop.PlannedArrivalUtc.Value - visit.EnteredAtUtc).TotalMinutes) })
                .OrderBy(x => x.delta)
                .ThenBy(x => x.stop.Sequence)
                .FirstOrDefault();
            if (candidate is null) continue;
            visit.LoadId = candidate.load.Id;
            visit.LoadStopId = candidate.stop.Id;
            usedStops.Add(candidate.stop.Id);
        }
    }

    private static Site? MatchSite(EmbeddedFence fence, IReadOnlyCollection<Site> sites)
    {
        var siteNumber = Normalize(fence.SiteNumber);
        var fenceName = Normalize(fence.Name);
        if (siteNumber.Length > 0)
        {
            var byCode = sites.FirstOrDefault(site => Normalize(site.ExternalCode) == siteNumber);
            if (byCode is not null) return byCode;
        }
        var exact = sites.FirstOrDefault(site => Normalize(site.Name) == fenceName || Normalize(site.DriverTextName) == fenceName);
        return exact ?? sites.FirstOrDefault(site => NamesOverlap(site.Name, fence.Name) || NamesOverlap(site.DriverTextName, fence.Name));
    }

    private static (DateTimeOffset StartUtc, DateTimeOffset EndUtc) OperatingWindow(DateOnly date)
    {
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
            var localStart = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
            var start = TimeZoneInfo.ConvertTimeToUtc(localStart, zone);
            var end = TimeZoneInfo.ConvertTimeToUtc(localStart.AddDays(1), zone);
            return (new DateTimeOffset(start), new DateTimeOffset(end));
        }
        catch (TimeZoneNotFoundException)
        {
            var utc = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            return (utc, utc.AddDays(1));
        }
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

    private static bool Contains(IReadOnlyList<GeoPoint> points, decimal longitude, decimal latitude)
    {
        var x = (double)longitude;
        var y = (double)latitude;
        var inside = false;
        for (int i = 0, j = points.Count - 1; i < points.Count; j = i++)
        {
            var pi = points[i];
            var pj = points[j];
            if (((pi.Latitude > y) != (pj.Latitude > y)) && x < (pj.Longitude - pi.Longitude) * (y - pi.Latitude) / (pj.Latitude - pi.Latitude) + pi.Longitude) inside = !inside;
        }
        return inside;
    }

    private static GeoPoint? ReadPoint(JsonElement point)
    {
        if (point.ValueKind == JsonValueKind.Array && point.GetArrayLength() >= 2 && point[0].ValueKind == JsonValueKind.Number && point[1].ValueKind == JsonValueKind.Number && point[0].TryGetDouble(out var x) && point[1].TryGetDouble(out var y)) return new GeoPoint(x, y);
        if (point.ValueKind != JsonValueKind.Object) return null;
        var longitude = Double(point, "longitude") ?? Double(point, "lng") ?? Double(point, "lon") ?? Double(point, "x");
        var latitude = Double(point, "latitude") ?? Double(point, "lat") ?? Double(point, "y");
        return longitude is not null && latitude is not null ? new GeoPoint(longitude.Value, latitude.Value) : null;
    }

    private static bool NamesOverlap(string? left, string? right)
    {
        var a = Normalize(left);
        var b = Normalize(right);
        if (a.Length >= 4 && b.Length >= 4 && (a == b || a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal)))
            return true;

        var leftTokens = NameTokens(left);
        var rightTokens = NameTokens(right);
        if (leftTokens.Count == 0 || rightTokens.Count == 0) return false;
        var common = leftTokens.Intersect(rightTokens, StringComparer.OrdinalIgnoreCase).ToList();
        if (common.Count >= 2) return true;

        // One-token names are only considered safe when the token itself is specific.
        // This avoids linking every generic "Aldi" or "Morrisons" stop to the wrong RDC.
        var smaller = leftTokens.Count <= rightTokens.Count ? leftTokens : rightTokens;
        return smaller.Count == 1 && common.Count == 1 && common[0].Length >= 7;
    }

    private static List<string> NameTokens(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        var spaced = new string(value.Select(character => char.IsLetterOrDigit(character) ? char.ToUpperInvariant(character) : ' ').ToArray());
        return spaced.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length >= 2 && !IgnoredNameTokens.Contains(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static Guid StableId(string value) => new(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
    private static string Normalize(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static string? Text(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined ? value.ToString() : null;
    private static int? Int(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
    private static double? Double(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : null;
}

public sealed record GeoPoint(double Longitude, double Latitude);
public sealed record EmbeddedFence(Guid Id, string Name, string? Category, int? CategoryMaxWaitMinutes, int? MaxWaitMinutes, int PendingEntryMinutes, int PendingExitMinutes, string? SiteNumber, IReadOnlyList<GeoPoint> Points);
public sealed record EmbeddedFenceStatus(EmbeddedFence Fence, Guid? SiteId, string? SiteName);
public sealed record EmbeddedGeofenceSnapshot(IReadOnlyList<EmbeddedFence> Fences, IReadOnlyList<DerivedVisit> Visits, IReadOnlyList<DerivedVisit> ActiveVisits, IReadOnlyList<DerivedVisit> ConfirmedVisits, int TrackingEventCount, DateTimeOffset? LatestTrackingUtc);
public sealed class DerivedVisit
{
    public Guid Id { get; set; }
    public Guid VehicleId { get; set; }
    public string VehicleIdentifier { get; set; } = string.Empty;
    public required EmbeddedFence Fence { get; set; }
    public Guid? LoadId { get; set; }
    public Guid? LoadStopId { get; set; }
    public DateTimeOffset EnteredAtUtc { get; set; }
    public DateTimeOffset? ConfirmedAtUtc { get; set; }
    public DateTimeOffset? ExitedAtUtc { get; set; }
    public DateTimeOffset LastInsideAtUtc { get; set; }
    public int DwellMinutes { get; set; }
}
