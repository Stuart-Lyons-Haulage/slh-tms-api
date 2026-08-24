using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/planning-control")]
[Authorize]
public sealed class PalletPlanningControlController(TmsDbContext db) : ControllerBase
{
    private const string AllocationType = "planningpalletallocation";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    [HttpGet("pallets")]
    public async Task<IActionResult> Get([FromQuery] DateOnly date, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        var orders = await ReadOrders(date, ct);
        var loads = await ReadLoads(date, ct);
        var details = await ReadOrderDetails(date, ct);
        var explicitAllocations = await ReadLatestAllocations(date, ct);
        var sourceLines = await ReadCurrentSourceLines(orders, ct);
        var loadById = loads.ToDictionary(x => x.Id);
        var firstRunCreated = loads.Count == 0 ? (DateTimeOffset?)null : loads.Min(x => x.CreatedAtUtc);

        var orderRows = new List<object>();
        var matrixRows = new Dictionary<string, Dictionary<string, CellAccumulator>>(StringComparer.OrdinalIgnoreCase);
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var totalOrdered = 0;
        var totalPlanned = 0;
        var totalOutstanding = 0;
        var totalOverplanned = 0;
        var lateCount = 0;

        foreach (var order in orders.Where(x => x.Status != OrderStatus.Cancelled))
        {
            details.TryGetValue(Normalise(order.Reference), out var detail);
            sourceLines.TryGetValue(order.Id, out var orderSourceLines);
            orderSourceLines ??= [];
            var ordered = orderSourceLines.Count > 0 ? orderSourceLines.Sum(x => Math.Max(x.Pallets ?? 0, 0)) : EffectiveOrderedPallets(order, detail);
            if (ordered <= 0) continue;

            var hasExplicitAllocations = explicitAllocations.Keys.Any(key => key.OrderId == order.Id);
            var allocations = explicitAllocations.Values
                .Where(x => x.OrderId == order.Id && x.Pallets > 0)
                .OrderBy(x => loadById.TryGetValue(x.LoadId, out var load) ? load.Reference : x.LoadId.ToString())
                .ToList();

            // Runs created before quantity allocation existed remain valid. Once any explicit allocation
            // exists for the order, including a zero, the explicit quantities become authoritative.
            if (!hasExplicitAllocations && allocations.Count == 0)
            {
                var linkedLoad = loads.FirstOrDefault(load => load.Status != LoadStatus.Cancelled && load.Stops.Any(stop => stop.OrderId == order.Id));
                if (linkedLoad is not null && ordered > 0)
                    allocations.Add(new AllocationState(order.Id, linkedLoad.Id, ordered, date, linkedLoad.CreatedAtUtc, "Existing run allocation"));
            }

            var planned = allocations.Sum(x => x.Pallets);
            var outstanding = Math.Max(ordered - planned, 0);
            var overplanned = Math.Max(planned - ordered, 0);
            var collection = Collection(detail, order);
            var group = collection;
            var destination = Destination(detail, order);
            var temperature = detail?.Temperature;
            var palletType = NormalisePalletType(detail?.PalletType);
            var late = firstRunCreated is not null && order.CreatedAtUtc > firstRunCreated.Value.AddMinutes(15);
            if (late) lateCount++;

            destinations.Add(destination);
            if (!matrixRows.TryGetValue(group, out var byDestination))
                matrixRows[group] = byDestination = new Dictionary<string, CellAccumulator>(StringComparer.OrdinalIgnoreCase);
            if (!byDestination.TryGetValue(destination, out var cell))
                byDestination[destination] = cell = new CellAccumulator(group, destination);
            cell.Ordered += ordered;
            cell.Planned += planned;
            cell.OrderIds.Add(order.Id);

            totalOrdered += ordered;
            totalPlanned += planned;
            totalOutstanding += outstanding;
            totalOverplanned += overplanned;
            orderRows.Add(new
            {
                order.Id,
                order.Reference,
                order.CustomerCode,
                order.CollectionDate,
                order.DeliveryDate,
                order.DeliveryWindowStartUtc,
                order.DeliveryWindowEndUtc,
                orderedPallets = ordered,
                plannedPallets = planned,
                outstandingPallets = outstanding,
                overplannedPallets = overplanned,
                collection,
                destination,
                planningGroup = group,
                temperature,
                palletType,
                source = detail?.Source,
                receivedAtUtc = detail?.UpdatedAtUtc ?? order.CreatedAtUtc,
                lateAddition = late,
                sourceMovementId = order.SourceMovementId,
                sourceLines = orderSourceLines.Select(line =>
                {
                    var lineAllocations = explicitAllocations.Values.Where(x => x.OrderId == order.Id && x.SourceLineId == line.Id && x.Pallets > 0).ToList();
                    var lineOrdered = Math.Max(line.Pallets ?? 0, 0);
                    var linePlanned = lineAllocations.Sum(x => x.Pallets);
                    return new
                    {
                        sourceLineId = line.Id, line.SourceRowKey, line.CollectionSite, line.DeliverySite, line.CollectionDate, line.DeliveryDate,
                        line.CollectionTimeFrom, line.CollectionTimeTo, line.PalletType, orderedPallets = lineOrdered,
                        plannedPallets = linePlanned, outstandingPallets = Math.Max(lineOrdered - linePlanned, 0),
                        overplannedPallets = Math.Max(linePlanned - lineOrdered, 0), line.TemperatureRequirement, line.LoadReference
                    };
                }).ToList(),
                allocations = allocations.Select(x => new
                {
                    x.SourceLineId,
                    x.LoadId,
                    loadReference = loadById.TryGetValue(x.LoadId, out var linked) ? linked.Reference : null,
                    pallets = x.Pallets,
                    x.UpdatedAtUtc,
                    x.UpdatedBy
                }).ToList()
            });
        }

        var orderedDestinations = destinations.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var cells = matrixRows.Values.SelectMany(x => x.Values).Select(cell => new
        {
            planningGroup = cell.Group,
            destination = cell.Destination,
            ordered = cell.Ordered,
            planned = cell.Planned,
            outstanding = Math.Max(cell.Ordered - cell.Planned, 0),
            overplanned = Math.Max(cell.Planned - cell.Ordered, 0),
            orderIds = cell.OrderIds
        }).ToList();

        return Ok(new
        {
            date,
            generatedAtUtc = DateTimeOffset.UtcNow,
            summary = new
            {
                ordered = totalOrdered,
                planned = totalPlanned,
                outstanding = totalOutstanding,
                overplanned = totalOverplanned,
                lateAdditions = lateCount,
                orders = orderRows.Count,
                runs = loads.Count(x => x.Status != LoadStatus.Cancelled)
            },
            planningGroups = matrixRows.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
            destinations = orderedDestinations,
            cells,
            orders = orderRows,
            runs = loads.Where(x => x.Status != LoadStatus.Cancelled).OrderBy(x => x.Reference).Select(x => new
            {
                x.Id,
                x.Reference,
                x.Status,
                x.PalletSpacesUsed,
                x.TotalPalletSpaces,
                x.CapacityType,
                stopCount = x.Stops.Count
            }).ToList()
        });
    }

    [HttpPost("allocations")]
    [Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Allocate([FromBody] PalletAllocationRequest request, CancellationToken ct)
    {
        if (request.Pallets < 0) return BadRequest(new { message = "Allocated pallets cannot be negative." });
        var orders = await ReadOrders(request.Date, ct);
        var order = orders.SingleOrDefault(x => x.Id == request.OrderId);
        if (order is null) return NotFound(new { message = "The order could not be found for this planning date." });

        var loads = await ReadLoads(request.Date, ct);
        var load = loads.SingleOrDefault(x => x.Id == request.LoadId);
        if (load is null) return NotFound(new { message = "The selected run could not be found for this planning date." });
        if (load.Status == LoadStatus.Cancelled) return BadRequest(new { message = "Pallets cannot be allocated to a cancelled run." });

        var latest = await ReadLatestAllocations(request.Date, ct);
        latest.TryGetValue((request.OrderId, request.LoadId, request.SourceLineId), out var previous);
        var previousPallets = previous?.Pallets ?? 0;
        var detailMap = await ReadOrderDetails(request.Date, ct);
        detailMap.TryGetValue(Normalise(order.Reference), out var detail);
        var sourceLineMap = await ReadCurrentSourceLines([order], ct);
        sourceLineMap.TryGetValue(order.Id, out var sourceLines);
        sourceLines ??= [];
        var permitted = request.SourceLineId is Guid sourceLineId
            ? sourceLines.SingleOrDefault(x => x.Id == sourceLineId)?.Pallets
            : sourceLines.Count > 0 ? sourceLines.Sum(x => Math.Max(x.Pallets ?? 0, 0)) : EffectiveOrderedPallets(order, detail);
        if (permitted is null) return BadRequest(new { message = "The selected source line could not be found for this order." });
        var allocatedElsewhere = latest.Values.Where(x => x.OrderId == order.Id && x.LoadId != request.LoadId &&
            (request.SourceLineId is null || x.SourceLineId is null || x.SourceLineId == request.SourceLineId)).Sum(x => Math.Max(x.Pallets, 0));
        if (allocatedElsewhere + request.Pallets > permitted.Value)
            return Conflict(new { message = "Allocation exceeds the approved pallet quantity.", approvedPallets = permitted, allocatedElsewhere, requestedPallets = request.Pallets });
        var now = DateTimeOffset.UtcNow;
        var payload = new AllocationState(request.OrderId, request.LoadId, request.Pallets, request.Date, now, User.Identity?.Name, request.SourceLineId);
        var allocationKey = $"palletallocation:{request.OrderId:N}:{request.LoadId:N}:{request.SourceLineId?.ToString("N") ?? "order"}:{now:yyyyMMddHHmmssfff}:{Guid.NewGuid():N}";
        if (allocationKey.Length > 200) allocationKey = allocationKey[..200];
        db.StagedImports.Add(new StagedImport
        {
            EntityType = AllocationType,
            IdempotencyKey = allocationKey,
            PayloadJson = JsonSerializer.Serialize(payload, JsonOptions),
            Source = "Pallet planning control",
            Status = StagingStatus.Promoted,
            ReviewedAtUtc = now,
            ReviewedBy = User.Identity?.Name,
            ReviewNote = $"Run pallet allocation changed from {previousPallets} to {request.Pallets}. {request.Note}".Trim()
        });
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();

        await EnsureOrderOnRun(load, order, detail, ct);
        var capacity = await UpdateRunPalletTotal(load.Id, request.Date, ct);

        var allLatest = await ReadLatestAllocations(request.Date, ct);
        var totalPlanned = allLatest.Values.Where(x => x.OrderId == order.Id).Sum(x => Math.Max(x.Pallets, 0));
        var ordered = sourceLines.Count > 0 ? sourceLines.Sum(x => Math.Max(x.Pallets ?? 0, 0)) : EffectiveOrderedPallets(order, detail);
        return Ok(new
        {
            orderId = order.Id,
            orderReference = order.Reference,
            sourceLineId = request.SourceLineId,
            loadId = load.Id,
            loadReference = load.Reference,
            allocatedToRun = request.Pallets,
            plannedPallets = totalPlanned,
            orderedPallets = ordered,
            outstandingPallets = Math.Max(ordered - totalPlanned, 0),
            overplannedPallets = Math.Max(totalPlanned - ordered, 0),
            runCapacityStatus = capacity?.Status,
            runUtilisationPercent = capacity?.UtilisationPercent,
            updatedAtUtc = now
        });
    }

    private async Task<List<TransportOrder>> ReadOrders(DateOnly date, CancellationToken ct)
    {
        var result = new Dictionary<string, TransportOrder>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var primary = await db.TransportOrders.AsNoTracking().Where(x => x.CollectionDate == date).OrderBy(x => x.Reference).Take(2000).ToListAsync(ct);
            foreach (var order in primary) result[order.Reference] = order;
        }
        catch (Exception ex) when (SchemaUnavailable(ex)) { db.ChangeTracker.Clear(); }

        var registered = await PlanningRegisterStore.ReadOrdersAsync(db, date, date, ct);
        foreach (var order in registered)
            if (!result.ContainsKey(order.Reference)) result[order.Reference] = order;
        return result.Values.OrderBy(x => x.Reference, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<List<Load>> ReadLoads(DateOnly date, CancellationToken ct)
    {
        try
        {
            var primary = await db.Loads.AsNoTracking().Include(x => x.Stops).Where(x => x.PlanningDate == date).OrderBy(x => x.Reference).Take(1000).ToListAsync(ct);
            await LoadCommercialStore.EnrichAsync(db, primary, ct);
            var registered = await PlanningRegisterStore.ReadLoadsAsync(db, date, ct);
            foreach (var row in registered.Where(x => primary.All(p => p.Id != x.Id))) primary.Add(row);
            return primary;
        }
        catch (Exception ex) when (SchemaUnavailable(ex))
        {
            db.ChangeTracker.Clear();
            return await PlanningRegisterStore.ReadLoadsAsync(db, date, ct);
        }
    }

    private async Task<Dictionary<string, OrderDetail>> ReadOrderDetails(DateOnly date, CancellationToken ct)
    {
        var result = new Dictionary<string, OrderDetail>(StringComparer.OrdinalIgnoreCase);
        var rows = await db.StagedImports.AsNoTracking()
            .Where(x => (x.EntityType == "order" || x.EntityType == "register:order") &&
                (x.Status == StagingStatus.Approved || x.Status == StagingStatus.Promoted))
            .OrderByDescending(x => x.ReviewedAtUtc ?? x.ReceivedAtUtc).ThenByDescending(x => x.ReceivedAtUtc).Take(8000).ToListAsync(ct);
        foreach (var row in rows)
        {
            try
            {
                using var document = JsonDocument.Parse(row.PayloadJson);
                var root = document.RootElement;
                var reference = Text(root, "poNumber", "reference", "orderReference", "orderRef");
                if (string.IsNullOrWhiteSpace(reference) || result.ContainsKey(Normalise(reference))) continue;
                if (DateOnly.TryParse(Text(root, "collectionDate"), out var collectionDate) && collectionDate != date) continue;
                var collection = Text(root, "collectionLocation", "collectionSite", "collection", "sellerName", "pickupLocation", "pickupSite");
                var destination = Text(root, "deliveryLocation", "deliverySite", "delivery", "destination", "depot", "stallNumber");
                var group = Text(root, "planningGroup", "palletOrderGroup", "collectionGroup");
                var temperature = Text(root, "temperature", "temperatureC", "temp", "temperatureRequirement") ?? Tagged(Text(root, "driverInstructions", "notes"), "Temperature");
                var palletType = Text(root, "unitType", "capacityType", "palletType", "palletName", "palletFormat", "pallet");
                var pallets = Int(root, "pallets", "palletQty", "palletQuantity", "quantity");
                var amended = row.ReviewNote?.Contains("Amended from Manage Jobs", StringComparison.OrdinalIgnoreCase) == true;
                result[Normalise(reference)] = new OrderDetail(reference, collection, destination, group, temperature, palletType, pallets, row.Source, row.ReviewedAtUtc ?? row.ReceivedAtUtc, amended);
            }
            catch (JsonException) { }
        }
        return result;
    }

    private async Task<Dictionary<(Guid OrderId, Guid LoadId, Guid? SourceLineId), AllocationState>> ReadLatestAllocations(DateOnly date, CancellationToken ct)
    {
        var rows = await db.StagedImports.AsNoTracking().Where(x => x.EntityType == AllocationType && x.Status == StagingStatus.Promoted)
            .OrderByDescending(x => x.ReceivedAtUtc).Take(20000).ToListAsync(ct);
        var result = new Dictionary<(Guid OrderId, Guid LoadId, Guid? SourceLineId), AllocationState>();
        foreach (var row in rows)
        {
            try
            {
                var state = JsonSerializer.Deserialize<AllocationState>(row.PayloadJson, JsonOptions);
                if (state is null || state.Date != date) continue;
                var key = (state.OrderId, state.LoadId, state.SourceLineId);
                if (!result.ContainsKey(key)) result[key] = state;
            }
            catch (JsonException) { }
        }
        return result;
    }

    private async Task EnsureOrderOnRun(Load load, TransportOrder order, OrderDetail? detail, CancellationToken ct)
    {
        if (load.Stops.Any(x => x.OrderId == order.Id)) return;
        var collection = Collection(detail, order);
        var destination = Destination(detail, order);

        try
        {
            if (await db.Loads.AsNoTracking().AnyAsync(x => x.Id == load.Id, ct))
            {
                var existingStops = await db.LoadStops.AsNoTracking().Where(x => x.LoadId == load.Id).OrderBy(x => x.Sequence).ToListAsync(ct);
                var sequence = existingStops.Count == 0 ? 1 : existingStops.Max(x => x.Sequence) + 1;
                if (!string.IsNullOrWhiteSpace(collection) && collection != "Collection not mapped" && !existingStops.Any(x => x.Name.Contains(collection, StringComparison.OrdinalIgnoreCase)))
                    db.LoadStops.Add(new LoadStop { LoadId = load.Id, Sequence = sequence++, Name = $"Collect · {collection}" });
                db.LoadStops.Add(new LoadStop { LoadId = load.Id, OrderId = order.Id, Sequence = sequence, Name = $"Deliver · {order.CustomerCode} · {destination}" });
                await db.SaveChangesAsync(ct);
                return;
            }
        }
        catch (Exception ex) when (SchemaUnavailable(ex)) { db.ChangeTracker.Clear(); }

        var registered = await PlanningRegisterStore.GetLoadAsync(db, load.Id, ct);
        if (registered is null) return;
        if (!string.IsNullOrWhiteSpace(collection) && collection != "Collection not mapped" && !registered.Stops.Any(x => x.Name.Contains(collection, StringComparison.OrdinalIgnoreCase)))
            registered.Stops.Add(new LoadStop { LoadId = registered.Id, Sequence = registered.Stops.Count + 1, Name = $"Collect · {collection}" });
        registered.Stops.Add(new LoadStop { LoadId = registered.Id, OrderId = order.Id, Sequence = registered.Stops.Count + 1, Name = $"Deliver · {order.CustomerCode} · {destination}" });
        await PlanningRegisterStore.SaveLoadAsync(db, registered, User.Identity?.Name, ct);
    }

    private async Task<PalletCapacityResult?> UpdateRunPalletTotal(Guid loadId, DateOnly date, CancellationToken ct)
    {
        var loads = await ReadLoads(date, ct);
        var target = loads.SingleOrDefault(x => x.Id == loadId);
        if (target is null) return null;
        var latest = await ReadLatestAllocations(date, ct);
        var orders = await ReadOrders(date, ct);
        var details = await ReadOrderDetails(date, ct);
        decimal standard = 0;
        decimal euro = 0;
        decimal unknown = 0;

        foreach (var order in orders.Where(x => x.Status != OrderStatus.Cancelled))
        {
            details.TryGetValue(Normalise(order.Reference), out var detail);
            int pallets;
            var hasExplicit = latest.Keys.Any(key => key.OrderId == order.Id);
            if (hasExplicit)
            {
                pallets = latest.Values.Where(x => x.OrderId == order.Id && x.LoadId == loadId).Sum(x => Math.Max(x.Pallets, 0));
            }
            else
            {
                if (!target.Stops.Any(stop => stop.OrderId == order.Id)) continue;
                pallets = EffectiveOrderedPallets(order, detail);
            }
            if (pallets <= 0) continue;
            switch (NormalisePalletType(detail?.PalletType))
            {
                case "Standard": standard += pallets; break;
                case "Euro": euro += pallets; break;
                default: unknown += pallets; break;
            }
        }

        decimal standardCapacity = PalletCapacityCalculator.DefaultStandardCapacity;
        decimal euroCapacity = PalletCapacityCalculator.DefaultEuroCapacity;
        if (target.TrailerId is not null)
        {
            try
            {
                var trailer = await db.Trailers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == target.TrailerId.Value, ct);
                if (trailer?.StandardCapacity is > 0) standardCapacity = trailer.StandardCapacity.Value;
                if (trailer?.EuroCapacity is > 0) euroCapacity = trailer.EuroCapacity.Value;
            }
            catch (Exception ex) when (SchemaUnavailable(ex)) { db.ChangeTracker.Clear(); }
        }

        var capacity = PalletCapacityCalculator.Calculate(standard, euro, unknown, standardCapacity, euroCapacity);
        var capacityLabel = $"Standard/Euro · {capacity.Status} · {capacity.UtilisationPercent:0.0}%";

        try
        {
            var load = await db.Loads.SingleOrDefaultAsync(x => x.Id == loadId, ct);
            if (load is not null)
            {
                await LoadCommercialStore.EnrichAsync(db, new[] { load }, ct);
                await LoadCommercialStore.SaveAsync(db, load, new LoadCommercialValues(load.RevenueAmount, load.FuelSurchargeAmount, load.EstimatedCostAmount,
                    load.ActualCostAmount, load.EstimatedDistanceMiles, load.EmptyMiles, load.InvoiceStatus, load.CommercialNotes,
                    capacity.StandardEquivalentUsed, capacity.StandardEquivalentCapacity, capacityLabel, load.DepotSplits, load.TemperatureC, load.PlannerNotes), User.Identity?.Name, ct);
                return capacity;
            }
        }
        catch (Exception ex) when (SchemaUnavailable(ex)) { db.ChangeTracker.Clear(); }
        var registered = await PlanningRegisterStore.GetLoadAsync(db, loadId, ct);
        if (registered is null) return capacity;
        registered.PalletSpacesUsed = capacity.StandardEquivalentUsed;
        registered.TotalPalletSpaces = capacity.StandardEquivalentCapacity;
        registered.CapacityType = capacityLabel;
        await PlanningRegisterStore.SaveLoadAsync(db, registered, User.Identity?.Name, ct);
        return capacity;
    }

    private static int EffectiveOrderedPallets(TransportOrder order, OrderDetail? detail)
    {
        // A register-backed order amended in Manage Jobs updates its audited staged payload rather than
        // an older duplicate TransportOrders row. In that case the amended payload must be authoritative.
        var value = detail?.Amended == true ? detail.Pallets ?? order.Pallets : order.Pallets ?? detail?.Pallets;
        return Math.Max(value ?? 0, 0);
    }

    private static string PlanningGroup(OrderDetail? detail, TransportOrder order)
    {
        if (!string.IsNullOrWhiteSpace(detail?.Group)) return detail.Group!;
        var collection = Collection(detail, order);
        var temperature = detail?.Temperature;
        if (string.IsNullOrWhiteSpace(temperature) || collection.Contains("°", StringComparison.OrdinalIgnoreCase) || collection.Contains("temp", StringComparison.OrdinalIgnoreCase)) return collection;
        var clean = temperature.Trim().Replace("degrees", "°", StringComparison.OrdinalIgnoreCase);
        if (!clean.Contains("°") && decimal.TryParse(new string(clean.Where(c => char.IsDigit(c) || c is '-' or '.').ToArray()), out var number)) clean = $"{number:0.#}°C";
        return $"{collection} ({clean})";
    }

    private static string Collection(OrderDetail? detail, TransportOrder order) =>
        !string.IsNullOrWhiteSpace(detail?.Collection) ? detail.Collection! : !string.IsNullOrWhiteSpace(order.SellerName) ? order.SellerName! : "Collection not mapped";

    private static string Destination(OrderDetail? detail, TransportOrder order)
    {
        if (!string.IsNullOrWhiteSpace(detail?.Destination)) return detail.Destination!;
        if (!string.IsNullOrWhiteSpace(order.StallNumber)) return order.StallNumber!;
        var tagged = Tagged(order.DriverInstructions, "Depot") ?? Tagged(order.DriverInstructions, "Delivery site") ?? Tagged(order.DriverInstructions, "Destination");
        return string.IsNullOrWhiteSpace(tagged) ? "Destination not mapped" : tagged;
    }

    private static string? NormalisePalletType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var clean = value.Trim();
        if (clean.Contains("euro", StringComparison.OrdinalIgnoreCase)) return "Euro";
        if (clean.Contains("std", StringComparison.OrdinalIgnoreCase) || clean.Contains("standard", StringComparison.OrdinalIgnoreCase)) return "Standard";
        return clean;
    }

    private static string? Tagged(string? notes, string label)
    {
        if (string.IsNullOrWhiteSpace(notes)) return null;
        var prefix = $"{label}:";
        return notes.Split('·', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim())
            .FirstOrDefault(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..].Trim();
    }

    private static string? Text(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (Normalise(property.Name) != Normalise(name)) continue;
                return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()?.Trim() : property.Value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False ? property.Value.ToString() : null;
            }
        }
        return null;
    }

    private static int? Int(JsonElement root, params string[] names) => int.TryParse(Text(root, names), out var value) ? value : null;
    private static string Normalise(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static bool SchemaUnavailable(Exception ex) => ex.GetBaseException().Message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase) || ex.GetBaseException().Message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase);

    private sealed record OrderDetail(string Reference, string? Collection, string? Destination, string? Group, string? Temperature, string? PalletType, int? Pallets, string? Source, DateTimeOffset UpdatedAtUtc, bool Amended);
    private async Task<Dictionary<Guid, List<OrderSourceLine>>> ReadCurrentSourceLines(IReadOnlyCollection<TransportOrder> orders, CancellationToken ct)
    {
        var movementByOrder = orders.Where(x => x.SourceMovementId is not null).ToDictionary(x => x.SourceMovementId!.Value, x => x.Id);
        if (movementByOrder.Count == 0) return [];
        var movements = await db.OrderMovements.AsNoTracking().Where(x => movementByOrder.Keys.Contains(x.Id) && x.CurrentRevisionId != null).ToListAsync(ct);
        var revisionToOrder = movements.ToDictionary(x => x.CurrentRevisionId!.Value, x => movementByOrder[x.Id]);
        var lines = await db.OrderSourceLines.AsNoTracking().Where(x => revisionToOrder.Keys.Contains(x.RevisionId)).ToListAsync(ct);
        return lines.GroupBy(x => revisionToOrder[x.RevisionId]).ToDictionary(x => x.Key, x => x.OrderBy(line => line.SourceRowKey).ToList());
    }

    private sealed record AllocationState(Guid OrderId, Guid LoadId, int Pallets, DateOnly Date, DateTimeOffset UpdatedAtUtc, string? UpdatedBy, Guid? SourceLineId = null);
    public sealed record PalletAllocationRequest(Guid OrderId, Guid LoadId, DateOnly Date, int Pallets, string? Note, Guid? SourceLineId = null);

    private sealed class CellAccumulator(string group, string destination)
    {
        public string Group { get; } = group;
        public string Destination { get; } = destination;
        public int Ordered { get; set; }
        public int Planned { get; set; }
        public List<Guid> OrderIds { get; } = [];
    }
}
