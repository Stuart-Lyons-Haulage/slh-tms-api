using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public sealed class WarehouseMovementService(TmsDbContext db)
{
    private const string CanonicalSiteName = "SLH-Lyons Consolidation Centre FRV";

    public async Task<WarehouseDailyResult> BuildDailyAsync(DateOnly date, CancellationToken ct)
    {
        var sites = await db.Sites.AsNoTracking().Where(x => x.Active).ToListAsync(ct);
        var canonical = sites.FirstOrDefault(x => Normalize(x.Name) == Normalize(CanonicalSiteName) || Normalize(x.ExternalCode) == "slhfrv");
        if (canonical is null) return new(date, [], [], new(0, 0, 0, 0));

        var loads = await db.Loads.AsNoTracking().Include(x => x.Stops).Where(x => x.PlanningDate == date && x.Status != LoadStatus.Cancelled).ToListAsync(ct);
        var loadIds = loads.Select(x => x.Id).ToList();
        var allocations = await ReadLatestAllocations(loadIds, date, ct);
        var lineIds = allocations.Where(x => x.SourceLineId is not null).Select(x => x.SourceLineId!.Value).Distinct().ToList();
        var lines = await db.OrderSourceLines.AsNoTracking().Where(x => lineIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var revisionIds = lines.Values.Select(x => x.RevisionId).Distinct().ToList();
        var revisions = await db.OrderRevisions.AsNoTracking().Where(x => revisionIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var movementIds = revisions.Values.Select(x => x.MovementId).Distinct().ToList();
        var orders = await db.TransportOrders.AsNoTracking().Where(x => x.SourceMovementId != null && movementIds.Contains(x.SourceMovementId.Value)).ToListAsync(ct);
        var orderByMovement = orders.GroupBy(x => x.SourceMovementId!.Value).ToDictionary(x => x.Key, x => x.First());
        var vehicles = await db.Vehicles.AsNoTracking().Where(x => loads.Where(l => l.VehicleId != null).Select(l => l.VehicleId!.Value).Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var trailers = await db.Trailers.AsNoTracking().Where(x => loads.Where(l => l.TrailerId != null).Select(l => l.TrailerId!.Value).Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);

        var inbound = new List<WarehouseMovementRow>();
        var outbound = new List<WarehouseMovementRow>();
        foreach (var allocation in allocations.Where(x => x.Pallets > 0 && x.SourceLineId is not null))
        {
            if (!lines.TryGetValue(allocation.SourceLineId!.Value, out var line) || !revisions.TryGetValue(line.RevisionId, out var revision)) continue;
            orderByMovement.TryGetValue(revision.MovementId, out var order);
            var load = loads.Single(x => x.Id == allocation.LoadId);
            var hasCanonicalStop = load.Stops.Any(x => Normalize(x.Name).Contains(Normalize(canonical.Name), StringComparison.Ordinal));
            if (!hasCanonicalStop) continue;
            var direction = Normalize(line.DeliverySite) == Normalize(canonical.Name) ? "Inbound"
                : Normalize(line.CollectionSite) == Normalize(canonical.Name) ? "Outbound" : null;
            if (direction is null) continue;
            var row = new WarehouseMovementRow(direction, load.Id, load.Reference, Period(line.CollectionTimeFrom),
                load.VehicleId is Guid vehicleId && vehicles.TryGetValue(vehicleId, out var vehicle) ? vehicle.Registration : null,
                load.TrailerId is Guid trailerId && trailers.TryGetValue(trailerId, out var trailer) ? trailer.TrailerNumber : null,
                order?.CustomerCode ?? "Unknown", line.CollectionSite, line.DeliverySite, order?.Reference,
                line.LoadReference, line.PalletType, allocation.Pallets, line.TemperatureRequirement,
                direction == "Inbound" ? line.DeliveryDate : line.CollectionDate,
                direction == "Inbound" ? null : line.CollectionTimeFrom, null);
            (direction == "Inbound" ? inbound : outbound).Add(row);
        }
        inbound = inbound.OrderBy(x => x.RunReference).ThenBy(x => x.Customer).ToList();
        outbound = outbound.OrderBy(x => x.RunReference).ThenBy(x => x.Customer).ToList();
        return new(date, inbound, outbound, new(inbound.Count, outbound.Count, inbound.Sum(x => x.PlannedPallets), outbound.Sum(x => x.PlannedPallets)));
    }

    private async Task<List<WarehouseAllocation>> ReadLatestAllocations(List<Guid> loadIds, DateOnly date, CancellationToken ct)
    {
        var rows = await db.StagedImports.AsNoTracking().Where(x => x.EntityType == "planningpalletallocation" && x.Status == StagingStatus.Promoted).OrderByDescending(x => x.ReceivedAtUtc).Take(20000).ToListAsync(ct);
        var latest = new Dictionary<(Guid, Guid?), WarehouseAllocation>();
        foreach (var row in rows)
        {
            try
            {
                var allocation = JsonSerializer.Deserialize<WarehouseAllocation>(row.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true });
                if (allocation is null || allocation.Date != date || !loadIds.Contains(allocation.LoadId)) continue;
                var key = (allocation.LoadId, allocation.SourceLineId);
                if (!latest.ContainsKey(key)) latest[key] = allocation;
            }
            catch (JsonException) { }
        }
        return latest.Values.ToList();
    }

    private static string Period(TimeOnly? time) => time is null ? "Unallocated" : time.Value.Hour < 17 ? "AM" : "PM";
    private static string Normalize(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private sealed record WarehouseAllocation(Guid OrderId, Guid LoadId, int Pallets, DateOnly Date, DateTimeOffset UpdatedAtUtc, string? UpdatedBy, Guid? SourceLineId);
}

public sealed record WarehouseMovementRow(string Direction, Guid LoadId, string RunReference, string Period, string? Vehicle,
    string? Trailer, string Customer, string? From, string? To, string? PoReference, string? LoadReference,
    string? PalletType, int PlannedPallets, string? Temperature, DateOnly? DueDate, TimeOnly? DueTime, int? Difference);
public sealed record WarehouseDailyTotals(int InboundRows, int OutboundRows, int InboundPallets, int OutboundPallets);
public sealed record WarehouseDailyResult(DateOnly PlanningDate, IReadOnlyList<WarehouseMovementRow> Inbound,
    IReadOnlyList<WarehouseMovementRow> Outbound, WarehouseDailyTotals Totals);
