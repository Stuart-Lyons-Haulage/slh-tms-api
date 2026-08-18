using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Contracts;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/planning")]
[Authorize]
public sealed class PlannerPlanImportController(TmsDbContext db) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    [HttpPost("import-plan"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> ImportPlan(PlannerPlanImportRequest request, CancellationToken ct)
    {
        if (request.Runs is null || request.Runs.Count == 0)
            return BadRequest(new ErrorResponse("planner_plan_empty", "At least one planner run is required.", HttpContext.TraceIdentifier));
        if (request.Runs.Any(run => run.PlanningDate != request.PlanningDate))
            return BadRequest(new ErrorResponse("planning_date_mismatch", "Every run must use the root planningDate.", HttpContext.TraceIdentifier));
        if (request.Runs.Any(run => string.IsNullOrWhiteSpace(run.RunRef)))
            return BadRequest(new ErrorResponse("run_ref_required", "Every run must have a RunRef.", HttpContext.TraceIdentifier));
        if (request.Runs.GroupBy(run => run.RunRef, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            return BadRequest(new ErrorResponse("duplicate_run_ref", "RunRef values must be unique within the import.", HttpContext.TraceIdentifier));

        var activeDrivers = await db.Drivers.AsNoTracking().Where(x => x.Active).ToListAsync(ct);
        var activeVehicles = await db.Vehicles.AsNoTracking().Where(x => x.Active).ToListAsync(ct);
        var activeTrailers = await db.Trailers.AsNoTracking().Where(x => x.Active).ToListAsync(ct);
        List<TransportOrder> orders;
        try { orders = await db.TransportOrders.AsNoTracking().ToListAsync(ct); }
        catch (Exception ex) when (IsSchemaUnavailable(ex)) { db.ChangeTracker.Clear(); orders = await PlanningRegisterStore.ReadOrdersAsync(db, request.PlanningDate, request.PlanningDate.AddDays(2), ct); }

        var warnings = new List<string>();
        var unresolvedDrivers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unresolvedVehicles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unresolvedTrailers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<PlannerPlanRunResult>();
        var created = 0;
        var updated = 0;
        var unchanged = 0;
        var held = 0;

        foreach (var run in request.Runs)
        {
            var tmsReference = PlannerPlanImportRules.TmsReference(request.PlanningDate, run.RunRef);
            var capacity = PlannerPlanImportRules.Capacity(run);

            if (!run.IncludeInImport)
            {
                held++;
                results.Add(new PlannerPlanRunResult(run.RunRef, tmsReference, "Held", capacity.Status, capacity.UtilisationPercent, run.ReconciliationStatus));
                continue;
            }

            if (run.Stops is null || run.Stops.Count == 0)
            {
                held++;
                warnings.Add($"{run.RunRef}: no stops supplied; run held.");
                results.Add(new PlannerPlanRunResult(run.RunRef, tmsReference, "Held", capacity.Status, capacity.UtilisationPercent, "No stops supplied"));
                continue;
            }

            var driver = ResolveDriver(activeDrivers, run.Driver);
            var vehicle = ResolveVehicle(activeVehicles, run.Vehicle);
            var trailer = ResolveTrailer(activeTrailers, run.Trailer);
            if (!string.IsNullOrWhiteSpace(run.Driver) && driver is null && !IsPlaceholder(run.Driver)) unresolvedDrivers.Add(run.Driver.Trim());
            if (!string.IsNullOrWhiteSpace(run.Vehicle) && vehicle is null) unresolvedVehicles.Add(run.Vehicle.Trim());
            if (!string.IsNullOrWhiteSpace(run.Trailer) && trailer is null) unresolvedTrailers.Add(run.Trailer.Trim());
            if (capacity.Status == "Red") warnings.Add($"{run.RunRef}: trailer footprint is {capacity.UtilisationPercent:0.0}% and requires planner review.");
            if (capacity.Status == "Amber") warnings.Add($"{run.RunRef}: pallet type is incomplete so trailer capacity cannot be fully confirmed.");

            Load? load;
            var registerFallback = false;
            try { load = await db.Loads.Include(x => x.Stops).SingleOrDefaultAsync(x => x.Reference == tmsReference, ct); }
            catch (Exception ex) when (IsSchemaUnavailable(ex))
            {
                db.ChangeTracker.Clear();
                load = (await PlanningRegisterStore.ReadLoadsAsync(db, request.PlanningDate, ct)).SingleOrDefault(x => string.Equals(x.Reference, tmsReference, StringComparison.OrdinalIgnoreCase));
                registerFallback = true;
            }

            var auditKey = $"planimport:{request.PlanningDate:yyyyMMdd}:{run.RunRef.ToUpperInvariant()}";
            var runJson = JsonSerializer.Serialize(run, JsonOptions);
            var audit = await db.StagedImports.SingleOrDefaultAsync(x => x.IdempotencyKey == auditKey, ct);
            var samePayload = audit is not null && string.Equals(audit.PayloadJson, runJson, StringComparison.Ordinal);

            if (load is null)
            {
                load = new Load { Id = Guid.NewGuid(), Reference = tmsReference, PlanningDate = request.PlanningDate };
                created++;
            }
            else if (samePayload)
            {
                unchanged++;
                results.Add(new PlannerPlanRunResult(run.RunRef, tmsReference, "Unchanged", capacity.Status, capacity.UtilisationPercent, capacity.Message));
                continue;
            }
            else updated++;

            load.DriverId = driver?.Id;
            load.VehicleId = vehicle?.Id;
            load.TrailerId = trailer?.Id;
            load.Status = driver is not null && vehicle is not null ? LoadStatus.Planned : LoadStatus.Draft;
            load.PalletSpacesUsed = capacity.StandardEquivalentUsed;
            load.TotalPalletSpaces = capacity.StandardEquivalentCapacity;
            load.CapacityType = "Mixed Standard/Euro";
            load.PlannerNotes = PlannerPlanImportRules.BuildPlannerNotes(run, capacity);

            if (registerFallback)
            {
                load.Stops = BuildStops(load.Id, run, orders);
                await PlanningRegisterStore.SaveLoadAsync(db, load, User.Identity?.Name, ct);
            }
            else
            {
                if (db.Entry(load).State == EntityState.Detached) db.Loads.Add(load);
                else
                {
                    db.LoadStops.RemoveRange(load.Stops);
                    load.Stops.Clear();
                }
                load.Stops = BuildStops(load.Id, run, orders);
                await db.SaveChangesAsync(ct);
                await LoadCommercialStore.SaveAsync(db, load, new LoadCommercialValues(null, null, null, null, null, null, null, null,
                    capacity.StandardEquivalentUsed, capacity.StandardEquivalentCapacity, "Mixed Standard/Euro", null, null, load.PlannerNotes), User.Identity?.Name, ct);
            }

            audit ??= new StagedImport { EntityType = "plannerplanrun", IdempotencyKey = auditKey, PayloadJson = runJson, Source = "Planner plan import" };
            if (db.Entry(audit).State == EntityState.Detached) db.StagedImports.Add(audit);
            audit.PayloadJson = runJson;
            audit.Status = StagingStatus.Promoted;
            audit.ReviewedAtUtc = DateTimeOffset.UtcNow;
            audit.ReviewedBy = User.Identity?.Name;
            audit.ReviewNote = $"Imported as {tmsReference}.";
            await db.SaveChangesAsync(ct);

            results.Add(new PlannerPlanRunResult(run.RunRef, tmsReference, load.Status == LoadStatus.Planned ? "ImportedPlanned" : "ImportedDraft", capacity.Status, capacity.UtilisationPercent, capacity.Message));
        }

        return Ok(new PlannerPlanImportSummary(
            request.PlanningDate,
            request.Runs.Count,
            created,
            updated,
            unchanged,
            held,
            warnings,
            unresolvedDrivers.OrderBy(x => x).ToList(),
            unresolvedVehicles.OrderBy(x => x).ToList(),
            unresolvedTrailers.OrderBy(x => x).ToList(),
            results));
    }

    private static List<LoadStop> BuildStops(Guid loadId, PlannerPlanRunRequest run, IReadOnlyCollection<TransportOrder> orders)
    {
        var sourceRows = run.Stops.OrderBy(stop => stop.Sequence).ToList();
        var stops = new List<LoadStop>();

        foreach (var group in GroupBySite(sourceRows, stop => stop.CollectionSite))
        {
            stops.Add(new LoadStop
            {
                Id = Guid.NewGuid(),
                LoadId = loadId,
                Sequence = stops.Count + 1,
                Name = Clip($"Collect · {group.Site}", 200)!,
                Address = Clip(BuildGroupedStopDetail(group.Rows, includeDelivery: true), 500),
                PlannedArrivalUtc = EarliestPlannerTime(run.PlanningDate, group.Rows.Select(stop => stop.CollectFrom ?? stop.CollectTo))
            });
        }

        foreach (var group in GroupBySite(sourceRows, stop => stop.DeliverySite))
        {
            var order = FirstMatchingOrder(group.Rows, orders);
            stops.Add(new LoadStop
            {
                Id = Guid.NewGuid(),
                LoadId = loadId,
                OrderId = order?.Id,
                Sequence = stops.Count + 1,
                Name = Clip($"Deliver · {group.Site}", 200)!,
                Address = Clip(BuildGroupedStopDetail(group.Rows, includeDelivery: false), 500),
                PlannedArrivalUtc = EarliestPlannerTime(run.PlanningDate, group.Rows.Select(stop => stop.Deadline))
            });
        }

        return stops.Count > 0
            ? stops
            : sourceRows.Select((stop, index) => new LoadStop
            {
                Id = Guid.NewGuid(),
                LoadId = loadId,
                Sequence = index + 1,
                Name = Clip(PlannerPlanImportRules.StopName(stop), 200)!,
                Address = Clip(BuildStopDetail(stop), 500),
                PlannedArrivalUtc = ParsePlannerTime(run.PlanningDate, stop.CollectFrom ?? stop.Deadline)
            }).ToList();
    }

    private static IEnumerable<(string Site, List<PlannerPlanStopRequest> Rows)> GroupBySite(
        IReadOnlyCollection<PlannerPlanStopRequest> rows,
        Func<PlannerPlanStopRequest, string?> siteSelector)
    {
        var groups = new List<(string Site, List<PlannerPlanStopRequest> Rows)>();
        foreach (var row in rows)
        {
            var site = siteSelector(row)?.Trim();
            if (string.IsNullOrWhiteSpace(site)) continue;
            var existingIndex = groups.FindIndex(group => string.Equals(Normalize(group.Site), Normalize(site), StringComparison.Ordinal));
            if (existingIndex >= 0) groups[existingIndex].Rows.Add(row);
            else groups.Add((site, [row]));
        }
        return groups;
    }

    private static TransportOrder? FirstMatchingOrder(IEnumerable<PlannerPlanStopRequest> rows, IReadOnlyCollection<TransportOrder> orders)
    {
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Reference)) continue;
            var order = orders.FirstOrDefault(item => string.Equals(item.Reference, row.Reference, StringComparison.OrdinalIgnoreCase));
            if (order is not null) return order;
        }
        return null;
    }

    private static string BuildGroupedStopDetail(IEnumerable<PlannerPlanStopRequest> rows, bool includeDelivery)
    {
        var details = rows.OrderBy(row => row.Sequence).Select(row =>
        {
            var routePart = includeDelivery && !string.IsNullOrWhiteSpace(row.DeliverySite) ? $"to {row.DeliverySite}" :
                !includeDelivery && !string.IsNullOrWhiteSpace(row.CollectionSite) ? $"from {row.CollectionSite}" : null;
            var parts = new[]
            {
                routePart,
                row.Pallets is null ? null : $"{row.Pallets:0.##} pallets",
                string.IsNullOrWhiteSpace(row.Reference) ? null : $"Ref {row.Reference}",
                string.IsNullOrWhiteSpace(row.CollectFrom) ? null : $"collect {row.CollectFrom}",
                string.IsNullOrWhiteSpace(row.CollectTo) ? null : $"to {row.CollectTo}",
                string.IsNullOrWhiteSpace(row.Deadline) ? null : $"deadline {row.Deadline}"
            }.Where(part => !string.IsNullOrWhiteSpace(part));
            return string.Join(" · ", parts);
        }).Where(detail => !string.IsNullOrWhiteSpace(detail));

        return string.Join(" | ", details);
    }

    private static Driver? ResolveDriver(IEnumerable<Driver> drivers, string? value) => string.IsNullOrWhiteSpace(value) || IsPlaceholder(value)
        ? null
        : drivers.FirstOrDefault(x => string.Equals(x.DisplayName.Trim(), value.Trim(), StringComparison.OrdinalIgnoreCase) || string.Equals(x.TachoName?.Trim(), value.Trim(), StringComparison.OrdinalIgnoreCase));

    private static Vehicle? ResolveVehicle(IEnumerable<Vehicle> vehicles, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var needle = Normalize(value);
        var exact = vehicles.Where(x => Normalize(x.Registration) == needle || Normalize(x.Abbreviation) == needle || Normalize(x.FleetNumber) == needle).ToList();
        if (exact.Count == 1) return exact[0];
        var suffix = vehicles.Where(x => Normalize(x.Registration).EndsWith(needle, StringComparison.OrdinalIgnoreCase)).ToList();
        return suffix.Count == 1 ? suffix[0] : null;
    }

    private static Trailer? ResolveTrailer(IEnumerable<Trailer> trailers, string? value) => string.IsNullOrWhiteSpace(value)
        ? null
        : trailers.FirstOrDefault(x => string.Equals(x.TrailerNumber.Trim(), value.Trim(), StringComparison.OrdinalIgnoreCase));

    private static bool IsPlaceholder(string? value) => string.Equals(value?.Trim(), "c/o", StringComparison.OrdinalIgnoreCase) || string.Equals(value?.Trim(), "tbc", StringComparison.OrdinalIgnoreCase);
    private static string Normalize(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static string? Clip(string? value, int length) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, length)];
    private static string? BuildStopDetail(PlannerPlanStopRequest stop) => string.Join(" | ", new[]
    {
        string.IsNullOrWhiteSpace(stop.Reference) ? null : $"Ref {stop.Reference}",
        stop.Pallets is null ? null : $"{stop.Pallets:0.##} pallets",
        string.IsNullOrWhiteSpace(stop.PalletType) ? null : stop.PalletType,
        string.IsNullOrWhiteSpace(stop.CollectFrom) ? null : $"Collect from {stop.CollectFrom}",
        string.IsNullOrWhiteSpace(stop.CollectTo) ? null : $"Collect to {stop.CollectTo}",
        string.IsNullOrWhiteSpace(stop.Deadline) ? null : $"Deadline {stop.Deadline}"
    }.Where(x => x is not null));

    private static DateTimeOffset? EarliestPlannerTime(DateOnly date, IEnumerable<string?> values)
    {
        return values
            .Select(value => ParsePlannerTime(date, value))
            .Where(value => value is not null)
            .OrderBy(value => value)
            .FirstOrDefault();
    }

    private static DateTimeOffset? ParsePlannerTime(DateOnly date, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !TimeOnly.TryParse(value, out var time)) return null;
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "GMT Standard Time" : "Europe/London");
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    private static bool IsSchemaUnavailable(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        return exception is InvalidOperationException or DbUpdateException || message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase) || message.Contains("Cannot find the object", StringComparison.OrdinalIgnoreCase) || message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase);
    }
}
