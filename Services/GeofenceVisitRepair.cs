using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public static class GeofenceVisitRepair
{
    public static async Task<int> RepairRecentAsync(TmsDbContext db, CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-36);
        var visits = await db.GeofenceVisits
            .Where(x => x.LoadId == null && x.EnteredAtUtc >= cutoff)
            .OrderBy(x => x.EnteredAtUtc)
            .Take(1000)
            .ToListAsync(ct);
        if (visits.Count == 0) return 0;

        var geofenceIds = visits.Select(x => x.GeofenceId).Distinct().ToList();
        var geofences = await db.SiteGeofences.AsNoTracking()
            .Where(x => geofenceIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, ct);
        var vehicles = await db.Vehicles.AsNoTracking().Where(x => x.Active).ToListAsync(ct);
        var dates = visits.Select(x => UkOperatingDate(x.EnteredAtUtc)).Distinct().ToList();
        var loads = new List<Load>();

        foreach (var date in dates)
        {
            try
            {
                loads.AddRange(await db.Loads.AsNoTracking().Include(x => x.Stops)
                    .Where(x => x.PlanningDate == date && x.VehicleId != null && x.Status != LoadStatus.Completed && x.Status != LoadStatus.Cancelled)
                    .ToListAsync(ct));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Dedicated planning tables are optional during the planning-register rollout.
            }

            try
            {
                var registered = (await PlanningRegisterStore.ReadLoadsAsync(db, date, ct))
                    .Where(x => x.VehicleId != null && x.Status != LoadStatus.Cancelled);
                foreach (var load in registered)
                    if (loads.All(existing => existing.Id != load.Id)) loads.Add(load);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Leave the orphan untouched if neither planning source is readable.
            }
        }

        var repaired = 0;
        foreach (var visit in visits)
        {
            var vehicle = vehicles.FirstOrDefault(x => VehicleMatches(x, visit.VehicleIdentifier));
            if (vehicle is null) continue;
            geofences.TryGetValue(visit.GeofenceId, out var fence);
            var date = UkOperatingDate(visit.EnteredAtUtc);
            var candidates = loads.Where(x => x.PlanningDate == date && x.VehicleId == vehicle.Id).ToList();
            if (candidates.Count == 0) continue;

            var load = candidates
                .Select(candidate =>
                {
                    var namedStops = (candidate.Stops ?? []).Where(stop => fence is not null && NamesOverlap(stop.Name, fence.Name)).ToList();
                    var planned = namedStops.Select(stop => stop.PlannedArrivalUtc).FirstOrDefault(value => value != null)
                        ?? (candidate.Stops ?? []).OrderBy(stop => stop.Sequence).Select(stop => stop.PlannedArrivalUtc).FirstOrDefault(value => value != null);
                    var distance = planned is null ? double.MaxValue : Math.Abs((planned.Value - visit.EnteredAtUtc).TotalMinutes);
                    return new { candidate, hasSiteMatch = namedStops.Count > 0, priority = LoadPriority(candidate.Status), distance };
                })
                .OrderByDescending(x => x.hasSiteMatch)
                .ThenByDescending(x => x.priority)
                .ThenBy(x => x.distance)
                .ThenBy(x => x.candidate.Reference)
                .Select(x => x.candidate)
                .First();

            var matchingStops = fence is null
                ? []
                : (load.Stops ?? []).Where(stop => NamesOverlap(stop.Name, fence.Name)).OrderBy(stop => stop.Sequence).ToList();
            var safeStop = matchingStops.Count == 1
                ? matchingStops[0]
                : matchingStops.Count == 0 && (load.Stops?.Count ?? 0) == 1
                    ? load.Stops![0]
                    : null;

            visit.LoadId = load.Id;
            visit.VehicleId ??= vehicle.Id;
            visit.LoadStopId ??= safeStop?.Id;
            visit.StatusReason = string.IsNullOrWhiteSpace(visit.StatusReason)
                ? $"Re-linked to run {load.Reference} from RoadTech/geofence identity."
                : $"{visit.StatusReason} Re-linked to run {load.Reference}.";
            visit.UpdatedAtUtc = DateTimeOffset.UtcNow;
            repaired++;
        }

        if (repaired > 0) await db.SaveChangesAsync(ct);
        return repaired;
    }

    private static bool VehicleMatches(Vehicle vehicle, string identifier)
    {
        var key = Normalize(identifier);
        return new[] { vehicle.Registration, vehicle.FleetNumber, vehicle.Abbreviation }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Any(x => Normalize(x) == key);
    }

    private static bool NamesOverlap(string a, string b)
    {
        var left = Normalize(a);
        var right = Normalize(b);
        return left == right || left.Contains(right, StringComparison.Ordinal) || right.Contains(left, StringComparison.Ordinal);
    }

    private static int LoadPriority(LoadStatus status) => status switch
    {
        LoadStatus.InProgress => 4,
        LoadStatus.Dispatched => 3,
        LoadStatus.Planned => 2,
        LoadStatus.Draft => 1,
        _ => 0
    };

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

    private static string Normalize(string? value) => new((value ?? string.Empty)
        .Where(char.IsLetterOrDigit)
        .Select(char.ToUpperInvariant)
        .ToArray());
}
