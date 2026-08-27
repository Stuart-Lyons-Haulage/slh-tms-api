using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Contracts;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Reconstructs the imported planner plan from its durable per-run audit rows when the
/// operational Loads row and planningload register row are no longer available. This is a
/// read-only recovery projection: real Loads/planning-register rows always take precedence.
/// </summary>
internal static class PlannerPlanAuditProjection
{
    private const string EntityType = "plannerplanrun";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    public static async Task<List<Load>> ReadLoadsAsync(TmsDbContext db, DateOnly? date, CancellationToken ct)
    {
        var query = db.StagedImports.AsNoTracking()
            .Where(row => row.EntityType == EntityType && row.Status == StagingStatus.Promoted);

        if (date is DateOnly day)
        {
            var prefix = $"planimport:{day:yyyyMMdd}:";
            query = query.Where(row => row.IdempotencyKey.StartsWith(prefix));
        }

        var rows = await query
            .OrderByDescending(row => row.ReviewedAtUtc ?? row.ReceivedAtUtc)
            .ThenByDescending(row => row.ReceivedAtUtc)
            .Take(5000)
            .ToListAsync(ct);

        if (rows.Count == 0) return [];

        var drivers = await db.Drivers.AsNoTracking().Where(item => item.Active).ToListAsync(ct);
        var vehicles = await db.Vehicles.AsNoTracking().Where(item => item.Active).ToListAsync(ct);
        var trailers = await db.Trailers.AsNoTracking().Where(item => item.Active).ToListAsync(ct);
        var result = new List<Load>();

        foreach (var row in rows)
        {
            PlannerPlanRunRequest? run;
            try { run = JsonSerializer.Deserialize<PlannerPlanRunRequest>(row.PayloadJson, JsonOptions); }
            catch (JsonException) { continue; }
            if (run is null || !run.IncludeInImport || run.Stops is null || run.Stops.Count == 0) continue;

            var planningDate = run.PlanningDate != default ? run.PlanningDate : DateFromAuditKey(row.IdempotencyKey);
            if (planningDate == default || date is not null && planningDate != date.Value) continue;

            var driver = ResolveDriver(drivers, run.Driver);
            var vehicle = ResolveVehicle(vehicles, run.Vehicle);
            var trailer = ResolveTrailer(trailers, run.Trailer);
            var capacity = PlannerPlanImportRules.Capacity(run);
            var loadId = row.Id;
            var stops = BuildStops(loadId, planningDate, run.Stops);

            result.Add(new Load
            {
                Id = loadId,
                Reference = PlannerPlanImportRules.TmsReference(planningDate, run.RunRef),
                PlanningDate = planningDate,
                DriverId = driver?.Id,
                VehicleId = vehicle?.Id,
                TrailerId = trailer?.Id,
                Status = driver is not null && vehicle is not null ? LoadStatus.Planned : LoadStatus.Draft,
                PalletSpacesUsed = capacity.StandardEquivalentUsed,
                TotalPalletSpaces = capacity.StandardEquivalentCapacity,
                CapacityType = "Mixed Standard/Euro",
                PlannerNotes = PlannerPlanImportRules.BuildPlannerNotes(run, capacity),
                Stops = stops,
                CreatedAtUtc = row.ReviewedAtUtc ?? row.ReceivedAtUtc
            });
        }

        return result
            .GroupBy(load => load.Reference, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(load => load.CreatedAtUtc).First())
            .OrderBy(load => load.PlanningDate)
            .ThenBy(load => load.Reference)
            .ToList();
    }

    private static List<LoadStop> BuildStops(Guid loadId, DateOnly planningDate, IReadOnlyCollection<PlannerPlanStopRequest> sourceRows)
    {
        var ordered = sourceRows.OrderBy(stop => stop.Sequence).ToList();
        var stops = new List<LoadStop>();

        foreach (var group in GroupBySiteAndWindow(ordered, row => row.CollectionSite, row => $"{Clean(row.CollectFrom)}-{Clean(row.CollectTo)}"))
        {
            stops.Add(new LoadStop
            {
                Id = StableGuid(loadId, $"collect:{stops.Count + 1}:{Normalize(group.Site)}:{group.WindowKey}"),
                LoadId = loadId,
                Sequence = stops.Count + 1,
                Name = Clip($"Collect · {group.Site}", 200)!,
                Address = Clip(GroupDetail(group.Rows), 500),
                PlannedArrivalUtc = EarliestPlannerTime(planningDate, group.Rows.Select(row => row.CollectFrom ?? row.CollectTo))
            });
        }

        foreach (var group in GroupBySiteAndWindow(ordered, row => row.DeliverySite, row => Clean(row.Deadline)))
        {
            stops.Add(new LoadStop
            {
                Id = StableGuid(loadId, $"deliver:{stops.Count + 1}:{Normalize(group.Site)}:{group.WindowKey}"),
                LoadId = loadId,
                Sequence = stops.Count + 1,
                Name = Clip($"Deliver · {group.Site}", 200)!,
                Address = Clip(GroupDetail(group.Rows), 500),
                PlannedArrivalUtc = EarliestPlannerTime(planningDate, group.Rows.Select(row => row.Deadline))
            });
        }

        if (stops.Count > 0) return stops;

        return ordered.Select((row, index) => new LoadStop
        {
            Id = StableGuid(loadId, $"stop:{index + 1}:{Normalize(PlannerPlanImportRules.StopName(row))}"),
            LoadId = loadId,
            Sequence = index + 1,
            Name = Clip(PlannerPlanImportRules.StopName(row), 200)!,
            Address = Clip(GroupDetail([row]), 500),
            PlannedArrivalUtc = ParsePlannerTime(planningDate, row.CollectFrom ?? row.Deadline)
        }).ToList();
    }

    private static IEnumerable<(string Site, string WindowKey, List<PlannerPlanStopRequest> Rows)> GroupBySiteAndWindow(
        IReadOnlyCollection<PlannerPlanStopRequest> rows,
        Func<PlannerPlanStopRequest, string?> siteSelector,
        Func<PlannerPlanStopRequest, string> windowSelector)
    {
        var groups = new List<(string Site, string WindowKey, List<PlannerPlanStopRequest> Rows)>();
        foreach (var row in rows)
        {
            var site = siteSelector(row)?.Trim();
            if (string.IsNullOrWhiteSpace(site)) continue;
            var window = windowSelector(row);
            var existing = groups.FindIndex(group => Normalize(group.Site) == Normalize(site) && group.WindowKey == window);
            if (existing >= 0) groups[existing].Rows.Add(row);
            else groups.Add((site, window, [row]));
        }
        return groups;
    }

    private static string GroupDetail(IEnumerable<PlannerPlanStopRequest> rows) => string.Join(" | ", rows
        .OrderBy(row => row.Sequence)
        .Select(row => string.Join(" · ", new[]
        {
            string.IsNullOrWhiteSpace(row.Reference) ? null : $"Ref {row.Reference}",
            row.Pallets is null ? null : $"{row.Pallets:0.##} pallets"
        }.Where(value => !string.IsNullOrWhiteSpace(value))))
        .Where(value => !string.IsNullOrWhiteSpace(value)));

    private static DateTimeOffset? EarliestPlannerTime(DateOnly date, IEnumerable<string?> values) => values
        .Select(value => ParsePlannerTime(date, value))
        .Where(value => value is not null)
        .OrderBy(value => value)
        .FirstOrDefault();

    private static DateTimeOffset? ParsePlannerTime(DateOnly date, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !TimeOnly.TryParse(value, out var time)) return null;
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "GMT Standard Time" : "Europe/London");
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    private static Driver? ResolveDriver(IEnumerable<Driver> drivers, string? value) => string.IsNullOrWhiteSpace(value) || IsPlaceholder(value)
        ? null
        : drivers.FirstOrDefault(driver =>
            string.Equals(driver.DisplayName?.Trim(), value.Trim(), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(driver.TachoName?.Trim(), value.Trim(), StringComparison.OrdinalIgnoreCase));

    private static Vehicle? ResolveVehicle(IEnumerable<Vehicle> vehicles, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var needle = Normalize(value);
        var exact = vehicles.Where(vehicle => Normalize(vehicle.Registration) == needle || Normalize(vehicle.Abbreviation) == needle || Normalize(vehicle.FleetNumber) == needle).ToList();
        if (exact.Count == 1) return exact[0];
        var suffix = vehicles.Where(vehicle => Normalize(vehicle.Registration).EndsWith(needle, StringComparison.OrdinalIgnoreCase)).ToList();
        return suffix.Count == 1 ? suffix[0] : null;
    }

    private static Trailer? ResolveTrailer(IEnumerable<Trailer> trailers, string? value) => string.IsNullOrWhiteSpace(value)
        ? null
        : trailers.FirstOrDefault(trailer => string.Equals(trailer.TrailerNumber?.Trim(), value.Trim(), StringComparison.OrdinalIgnoreCase));

    private static DateOnly DateFromAuditKey(string key)
    {
        var parts = key.Split(':');
        return parts.Length >= 3 && DateOnly.TryParseExact(parts[1], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : default;
    }

    private static Guid StableGuid(Guid seed, string discriminator)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{seed:N}:{discriminator}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static bool IsPlaceholder(string? value) => string.Equals(value?.Trim(), "c/o", StringComparison.OrdinalIgnoreCase) || string.Equals(value?.Trim(), "tbc", StringComparison.OrdinalIgnoreCase);
    private static string Normalize(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static string Clean(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    private static string? Clip(string? value, int length) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, length)];
}
