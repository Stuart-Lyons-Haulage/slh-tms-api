using Microsoft.EntityFrameworkCore;
using Slh.Tms.V2.Api.Data;
using Slh.Tms.V2.Api.Domain;

namespace Slh.Tms.V2.Api.Application;

public sealed record PlanningMovementView(
    string MovementKey,
    PlanningPeriod Period,
    Guid CollectionSiteId,
    string CollectionSite,
    Guid DeliverySiteId,
    string DeliverySite,
    int OrderCount,
    int TotalStandardPallets,
    int PlannedStandardPallets,
    int RemainingStandardPallets,
    int TotalEuroPallets,
    int PlannedEuroPallets,
    int RemainingEuroPallets,
    int TotalTrolleys,
    int PlannedTrolleys,
    int RemainingTrolleys,
    string? TemperatureRequirement,
    string? TrailerRequirement,
    TimeOnly? LatestCollectionTime,
    TimeOnly? LatestDeliveryTime);

public sealed record RunMovementView(
    string MovementKey,
    Guid CollectionSiteId,
    string CollectionSite,
    Guid DeliverySiteId,
    string DeliverySite,
    int StandardPallets,
    int EuroPallets,
    int Trolleys);

public sealed record CapacityView(
    string Status,
    decimal? UtilisationPercent,
    int StandardPallets,
    int EuroPallets,
    int Trolleys,
    int? StandardCapacity,
    int? EuroCapacity,
    int? TrolleyCapacity,
    decimal? EquivalentUsed,
    decimal? EquivalentCapacity,
    string Message);

public sealed record PlanningRunView(
    Guid Id,
    string RunNumber,
    DateOnly PlanDate,
    PlanningPeriod Period,
    Guid? DriverId,
    string? Driver,
    Guid? VehicleId,
    string? Vehicle,
    Guid? TrailerId,
    string? Trailer,
    TimeOnly? StartTime,
    bool NightOut,
    string? TrailerSwapNotes,
    string? Notes,
    string? CapacityOverrideReason,
    PlanningRunState State,
    CapacityView Capacity,
    IReadOnlyList<RunMovementView> Movements);

public sealed record PalletMatrixCellView(
    Guid CollectionSiteId,
    string CollectionSite,
    Guid DeliverySiteId,
    string DeliverySite,
    int TotalStandardPallets,
    int PlannedStandardPallets,
    int OutstandingStandardPallets,
    int TotalEuroPallets,
    int PlannedEuroPallets,
    int OutstandingEuroPallets,
    int TotalTrolleys,
    int PlannedTrolleys,
    int OutstandingTrolleys);

public sealed record PlanningSnapshot(
    DateOnly PlanDate,
    IReadOnlyList<PlanningMovementView> Movements,
    IReadOnlyList<PlanningRunView> Runs,
    IReadOnlyList<PalletMatrixCellView> PalletMatrix);

public sealed class PlanningService(
    OperationsDbContext ops,
    MasterDataDbContext master)
{
    private sealed record OrderWorking(
        TransportOrder Order,
        PlanningPeriod Period,
        Site CollectionSite,
        Site DeliverySite,
        int PlannedStandard,
        int PlannedEuro,
        int PlannedTrolleys);

    public async Task<PlanningSnapshot> GetSnapshotAsync(DateOnly date, CancellationToken ct)
    {
        var working = await LoadWorkingOrdersAsync(date, ct);
        var movements = BuildMovements(working);
        var runs = await BuildRunsAsync(date, working, ct);
        var matrix = BuildPalletMatrix(working);
        return new(date, movements, runs, matrix);
    }

    public async Task<TransportOrder> CreateQuickOrderAsync(
        QuickOrderBookingRequest request,
        CancellationToken ct)
    {
        if (request.StandardPallets < 0 || request.EuroPallets < 0 || request.Trolleys < 0)
            throw new InvalidOperationException("Order quantities cannot be negative.");

        if (request.StandardPallets == 0 && request.EuroPallets == 0 && request.Trolleys == 0)
            throw new InvalidOperationException("Enter at least one pallet or trolley.");

        var collection = await master.Sites.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == request.CollectionSiteId && x.Active, ct)
            ?? throw new InvalidOperationException("Collection Site was not found.");

        var delivery = await master.Sites.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == request.DeliverySiteId && x.Active, ct)
            ?? throw new InvalidOperationException("Delivery Site was not found.");

        var customerId =
            request.CustomerId
            ?? delivery.CustomerId
            ?? collection.CustomerId
            ?? throw new InvalidOperationException("Select a customer because neither Site has a linked customer.");

        var customerExists = await master.Customers.AsNoTracking()
            .AnyAsync(x => x.Id == customerId && x.Active, ct);
        if (!customerExists)
            throw new InvalidOperationException("The selected customer was not found.");

        var orderId = Guid.NewGuid();
        var periodTime = request.Period == PlanningPeriod.PM
            ? new TimeOnly(15, 0)
            : new TimeOnly(8, 0);

        var order = new TransportOrder
        {
            Id = orderId,
            StableKey = $"MANUAL:{orderId:N}",
            CustomerId = customerId,
            CollectionSiteId = request.CollectionSiteId,
            DeliverySiteId = request.DeliverySiteId,
            PurchaseOrder = string.IsNullOrWhiteSpace(request.PurchaseOrder) ? null : request.PurchaseOrder.Trim(),
            CustomerOrderReference = string.IsNullOrWhiteSpace(request.OrderReference) ? null : request.OrderReference.Trim(),
            SourceOrderReference = string.IsNullOrWhiteSpace(request.OrderReference) ? $"MANUAL-{orderId:N}" : request.OrderReference.Trim(),
            CollectionDate = request.PlanDate,
            CollectionTime = periodTime,
            DeliveryDate = request.PlanDate,
            Pallets = request.StandardPallets,
            EuroPallets = request.EuroPallets,
            Trolleys = request.Trolleys,
            Cases = 0,
            Crates = 0,
            Trays = 0,
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? "Manual planner booking" : request.Notes.Trim(),
            State = OrderState.ReadyToPlan,
            RevisionNumber = 1,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

        ops.Orders.Add(order);
        await ops.SaveChangesAsync(ct);
        return order;
    }

    public async Task<PlanningRun> CreateRunAsync(
        CreatePlanningRunRequest request,
        CancellationToken ct)
    {
        var runNumber = string.IsNullOrWhiteSpace(request.RunNumber)
            ? await NextRunNumberAsync(request.PlanDate, request.Period, ct)
            : request.RunNumber.Trim();

        var run = new PlanningRun
        {
            RunNumber = runNumber,
            PlanDate = request.PlanDate,
            Period = request.Period
        };

        ops.PlanningRuns.Add(run);
        await ops.SaveChangesAsync(ct);
        return run;
    }

    public async Task<PlanningRun?> UpdateRunAsync(
        Guid runId,
        PlanningRunUpdateRequest request,
        CancellationToken ct)
    {
        var run = await ops.PlanningRuns.SingleOrDefaultAsync(x => x.Id == runId, ct);
        if (run is null) return null;

        run.DriverId = request.DriverId;
        run.VehicleId = request.VehicleId;
        run.TrailerId = request.TrailerId;
        run.StartTime = request.StartTime;
        run.NightOut = request.NightOut ?? run.NightOut;
        run.TrailerSwapNotes = request.TrailerSwapNotes;
        run.Notes = request.Notes;
        run.CapacityOverrideReason = request.CapacityOverrideReason;
        run.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await ops.SaveChangesAsync(ct);
        return run;
    }

    public async Task SetMovementQuantityAsync(
        Guid runId,
        SetMovementQuantityRequest request,
        CancellationToken ct)
    {
        if (request.StandardPallets < 0 || request.EuroPallets < 0 || request.Trolleys < 0)
            throw new InvalidOperationException("Run quantities cannot be negative.");

        var run = await ops.PlanningRuns.SingleOrDefaultAsync(x => x.Id == runId, ct)
            ?? throw new InvalidOperationException("The selected run no longer exists.");

        var working = await LoadWorkingOrdersAsync(run.PlanDate, ct);
        var group = working
            .Where(x => MovementKey(x) == request.MovementKey)
            .Where(x => x.Period == run.Period)
            .OrderBy(x => x.Order.CreatedAtUtc)
            .ThenBy(x => x.Order.Id)
            .ToList();

        if (group.Count == 0)
            throw new InvalidOperationException("The selected order movement is no longer available for this run.");

        var orderIds = group.Select(x => x.Order.Id).ToArray();

        var otherAllocations = await ops.RunOrderAllocations
            .Where(x => orderIds.Contains(x.OrderId) && x.RunId != runId)
            .ToListAsync(ct);

        var otherByOrder = otherAllocations
            .GroupBy(x => x.OrderId)
            .ToDictionary(
                g => g.Key,
                g => new
                {
                    Standard = g.Sum(x => x.StandardPallets),
                    Euro = g.Sum(x => x.EuroPallets),
                    Trolleys = g.Sum(x => x.Trolleys)
                });

        var availableStandard = group.Sum(x =>
            Math.Max(0, x.Order.Pallets - (otherByOrder.TryGetValue(x.Order.Id, out var a) ? a.Standard : 0)));
        var availableEuro = group.Sum(x =>
            Math.Max(0, x.Order.EuroPallets - (otherByOrder.TryGetValue(x.Order.Id, out var a) ? a.Euro : 0)));
        var availableTrolleys = group.Sum(x =>
            Math.Max(0, x.Order.Trolleys - (otherByOrder.TryGetValue(x.Order.Id, out var a) ? a.Trolleys : 0)));

        if (request.StandardPallets > availableStandard)
            throw new InvalidOperationException($"Only {availableStandard} standard pallets remain available for this movement.");
        if (request.EuroPallets > availableEuro)
            throw new InvalidOperationException($"Only {availableEuro} Euro pallets remain available for this movement.");
        if (request.Trolleys > availableTrolleys)
            throw new InvalidOperationException($"Only {availableTrolleys} trolleys remain available for this movement.");

        var existing = await ops.RunOrderAllocations
            .Where(x => x.RunId == runId && orderIds.Contains(x.OrderId))
            .ToListAsync(ct);

        var existingByOrder = existing.ToDictionary(x => x.OrderId);

        var standardLeft = request.StandardPallets;
        var euroLeft = request.EuroPallets;
        var trolleyLeft = request.Trolleys;
        var sequence = existing.Count == 0 ? 10 : existing.Min(x => x.Sequence);

        foreach (var item in group)
        {
            var other = otherByOrder.TryGetValue(item.Order.Id, out var otherRow)
                ? otherRow
                : null;

            var maxStandard = Math.Max(0, item.Order.Pallets - (other?.Standard ?? 0));
            var maxEuro = Math.Max(0, item.Order.EuroPallets - (other?.Euro ?? 0));
            var maxTrolley = Math.Max(0, item.Order.Trolleys - (other?.Trolleys ?? 0));

            var takeStandard = Math.Min(standardLeft, maxStandard);
            var takeEuro = Math.Min(euroLeft, maxEuro);
            var takeTrolley = Math.Min(trolleyLeft, maxTrolley);

            if (takeStandard == 0 && takeEuro == 0 && takeTrolley == 0)
            {
                if (existingByOrder.TryGetValue(item.Order.Id, out var emptyAllocation))
                    ops.RunOrderAllocations.Remove(emptyAllocation);
            }
            else
            {
                if (!existingByOrder.TryGetValue(item.Order.Id, out var allocation))
                {
                    allocation = new RunOrderAllocation
                    {
                        RunId = runId,
                        OrderId = item.Order.Id
                    };
                    ops.RunOrderAllocations.Add(allocation);
                }

                allocation.StandardPallets = takeStandard;
                allocation.EuroPallets = takeEuro;
                allocation.Trolleys = takeTrolley;
                allocation.Sequence = sequence;
                allocation.UpdatedAtUtc = DateTimeOffset.UtcNow;
                sequence += 10;
            }

            standardLeft -= takeStandard;
            euroLeft -= takeEuro;
            trolleyLeft -= takeTrolley;
        }

        await ops.SaveChangesAsync(ct);
        await RefreshOrderStatesAsync(orderIds, ct);
    }

    private async Task<List<OrderWorking>> LoadWorkingOrdersAsync(DateOnly date, CancellationToken ct)
    {
        var orders = await ops.Orders.AsNoTracking()
            .Where(x => x.CollectionDate == date)
            .Where(x => x.State != OrderState.Cancelled && x.State != OrderState.Delivered)
            .OrderBy(x => x.CollectionTime)
            .ThenBy(x => x.CreatedAtUtc)
            .ToListAsync(ct);

        if (orders.Count == 0)
            return [];

        var siteIds = orders
            .SelectMany(x => new[] { x.CollectionSiteId, x.DeliverySiteId })
            .Distinct()
            .ToArray();

        var sites = await master.Sites.AsNoTracking()
            .Where(x => siteIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, ct);

        var orderIds = orders.Select(x => x.Id).ToArray();
        var allocations = await ops.RunOrderAllocations.AsNoTracking()
            .Where(x => orderIds.Contains(x.OrderId))
            .ToListAsync(ct);

        var planned = allocations
            .GroupBy(x => x.OrderId)
            .ToDictionary(
                g => g.Key,
                g => new
                {
                    Standard = g.Sum(x => x.StandardPallets),
                    Euro = g.Sum(x => x.EuroPallets),
                    Trolleys = g.Sum(x => x.Trolleys)
                });

        var result = new List<OrderWorking>();
        foreach (var order in orders)
        {
            if (!sites.TryGetValue(order.CollectionSiteId, out var collection)
                || !sites.TryGetValue(order.DeliverySiteId, out var delivery))
                continue;

            planned.TryGetValue(order.Id, out var p);
            result.Add(new(
                order,
                PeriodFor(order.CollectionTime),
                collection,
                delivery,
                p?.Standard ?? 0,
                p?.Euro ?? 0,
                p?.Trolleys ?? 0));
        }

        return result;
    }

    private static List<PlanningMovementView> BuildMovements(List<OrderWorking> working) =>
        working
            .GroupBy(MovementKey)
            .Select(group =>
            {
                var first = group.First();
                var totalStandard = group.Sum(x => x.Order.Pallets);
                var plannedStandard = group.Sum(x => x.PlannedStandard);
                var totalEuro = group.Sum(x => x.Order.EuroPallets);
                var plannedEuro = group.Sum(x => x.PlannedEuro);
                var totalTrolleys = group.Sum(x => x.Order.Trolleys);
                var plannedTrolleys = group.Sum(x => x.PlannedTrolleys);

                return new PlanningMovementView(
                    group.Key,
                    first.Period,
                    first.CollectionSite.Id,
                    first.CollectionSite.Name,
                    first.DeliverySite.Id,
                    first.DeliverySite.Name,
                    group.Count(),
                    totalStandard,
                    plannedStandard,
                    Math.Max(0, totalStandard - plannedStandard),
                    totalEuro,
                    plannedEuro,
                    Math.Max(0, totalEuro - plannedEuro),
                    totalTrolleys,
                    plannedTrolleys,
                    Math.Max(0, totalTrolleys - plannedTrolleys),
                    first.Order.TemperatureRequirement,
                    first.Order.TrailerRequirement,
                    first.CollectionSite.LatestCollectionTime,
                    first.DeliverySite.LatestDeliveryTime);
            })
            .Where(x =>
                x.RemainingStandardPallets > 0
                || x.RemainingEuroPallets > 0
                || x.RemainingTrolleys > 0)
            .OrderBy(x => x.Period)
            .ThenBy(x => x.LatestCollectionTime)
            .ThenBy(x => x.CollectionSite)
            .ThenBy(x => x.DeliverySite)
            .ToList();

    private async Task<List<PlanningRunView>> BuildRunsAsync(
        DateOnly date,
        List<OrderWorking> working,
        CancellationToken ct)
    {
        var runs = await ops.PlanningRuns.AsNoTracking()
            .Where(x => x.PlanDate == date && x.State != PlanningRunState.Cancelled)
            .OrderBy(x => x.Period)
            .ThenBy(x => x.StartTime)
            .ThenBy(x => x.RunNumber)
            .ToListAsync(ct);

        if (runs.Count == 0)
            return [];

        var runIds = runs.Select(x => x.Id).ToArray();
        var allocations = await ops.RunOrderAllocations.AsNoTracking()
            .Where(x => runIds.Contains(x.RunId))
            .OrderBy(x => x.Sequence)
            .ToListAsync(ct);

        var drivers = await master.Drivers.AsNoTracking()
            .Where(x => runs.Where(r => r.DriverId != null).Select(r => r.DriverId!.Value).Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.DisplayName, ct);

        var vehicles = await master.Vehicles.AsNoTracking()
            .Where(x => runs.Where(r => r.VehicleId != null).Select(r => r.VehicleId!.Value).Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Registration, ct);

        var trailerIds = runs.Where(r => r.TrailerId != null).Select(r => r.TrailerId!.Value).Distinct().ToArray();
        var trailers = await master.Trailers.AsNoTracking()
            .Where(x => trailerIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, ct);

        var workingByOrder = working.ToDictionary(x => x.Order.Id);

        var result = new List<PlanningRunView>();
        foreach (var run in runs)
        {
            var runAllocations = allocations.Where(x => x.RunId == run.Id).ToList();
            var movementRows = runAllocations
                .Where(x => workingByOrder.ContainsKey(x.OrderId))
                .GroupBy(x => MovementKey(workingByOrder[x.OrderId]))
                .Select(group =>
                {
                    var firstWorking = workingByOrder[group.First().OrderId];
                    return new RunMovementView(
                        group.Key,
                        firstWorking.CollectionSite.Id,
                        firstWorking.CollectionSite.Name,
                        firstWorking.DeliverySite.Id,
                        firstWorking.DeliverySite.Name,
                        group.Sum(x => x.StandardPallets),
                        group.Sum(x => x.EuroPallets),
                        group.Sum(x => x.Trolleys));
                })
                .ToList();

            Trailer? trailer = null;
            if (run.TrailerId is Guid trailerId)
                trailers.TryGetValue(trailerId, out trailer);

            var standard = runAllocations.Sum(x => x.StandardPallets);
            var euro = runAllocations.Sum(x => x.EuroPallets);
            var trolley = runAllocations.Sum(x => x.Trolleys);
            var capacity = CalculateCapacity(trailer, standard, euro, trolley);

            result.Add(new(
                run.Id,
                run.RunNumber,
                run.PlanDate,
                run.Period,
                run.DriverId,
                run.DriverId is Guid driverId && drivers.TryGetValue(driverId, out var driver) ? driver : null,
                run.VehicleId,
                run.VehicleId is Guid vehicleId && vehicles.TryGetValue(vehicleId, out var vehicle) ? vehicle : null,
                run.TrailerId,
                trailer?.TrailerNumber,
                run.StartTime,
                run.NightOut,
                run.TrailerSwapNotes,
                run.Notes,
                run.CapacityOverrideReason,
                run.State,
                capacity,
                movementRows));
        }

        return result;
    }

    private static List<PalletMatrixCellView> BuildPalletMatrix(List<OrderWorking> working) =>
        working
            .GroupBy(x => new
            {
                CollectionId = x.CollectionSite.Id,
                Collection = x.CollectionSite.Name,
                DeliveryId = x.DeliverySite.Id,
                Delivery = x.DeliverySite.Name
            })
            .Select(group =>
            {
                var standardTotal = group.Sum(x => x.Order.Pallets);
                var standardPlanned = group.Sum(x => x.PlannedStandard);
                var euroTotal = group.Sum(x => x.Order.EuroPallets);
                var euroPlanned = group.Sum(x => x.PlannedEuro);
                var trolleyTotal = group.Sum(x => x.Order.Trolleys);
                var trolleyPlanned = group.Sum(x => x.PlannedTrolleys);

                return new PalletMatrixCellView(
                    group.Key.CollectionId,
                    group.Key.Collection,
                    group.Key.DeliveryId,
                    group.Key.Delivery,
                    standardTotal,
                    standardPlanned,
                    Math.Max(0, standardTotal - standardPlanned),
                    euroTotal,
                    euroPlanned,
                    Math.Max(0, euroTotal - euroPlanned),
                    trolleyTotal,
                    trolleyPlanned,
                    Math.Max(0, trolleyTotal - trolleyPlanned));
            })
            .OrderBy(x => x.CollectionSite)
            .ThenBy(x => x.DeliverySite)
            .ToList();

    private static CapacityView CalculateCapacity(
        Trailer? trailer,
        int standard,
        int euro,
        int trolley)
    {
        if (trailer is null)
        {
            return new(
                "no-trailer",
                null,
                standard,
                euro,
                trolley,
                null,
                null,
                null,
                null,
                null,
                "Select a trailer to calculate capacity.");
        }

        var activeTypes =
            (standard > 0 ? 1 : 0)
            + (euro > 0 ? 1 : 0)
            + (trolley > 0 ? 1 : 0);

        decimal? utilisation = null;
        decimal? equivalentUsed = null;
        decimal? equivalentCapacity = null;
        var missingRules = false;

        if (activeTypes <= 1)
        {
            if (standard > 0 && trailer.PalletCapacity is int stdCapacity && stdCapacity > 0)
            {
                utilisation = Math.Round((decimal)standard / stdCapacity * 100m, 1);
                equivalentUsed = standard;
                equivalentCapacity = stdCapacity;
            }
            else if (euro > 0 && trailer.EuroPalletCapacity is int euroCapacity && euroCapacity > 0)
            {
                utilisation = Math.Round((decimal)euro / euroCapacity * 100m, 1);
                equivalentUsed = euro;
                equivalentCapacity = euroCapacity;
            }
            else if (trolley > 0 && trailer.TrolleyCapacity is int trolleyCapacity && trolleyCapacity > 0)
            {
                utilisation = Math.Round((decimal)trolley / trolleyCapacity * 100m, 1);
                equivalentUsed = trolley;
                equivalentCapacity = trolleyCapacity;
            }
            else if (activeTypes == 0)
            {
                utilisation = 0m;
            }
        }
        else
        {
            if (trailer.PalletCapacity is not int baseCapacity || baseCapacity <= 0)
            {
                missingRules = true;
            }
            else
            {
                decimal used = standard;

                if (euro > 0)
                {
                    var euroFactor =
                        trailer.EuroToStandardEquivalent is decimal configuredEuroFactor && configuredEuroFactor > 0
                            ? configuredEuroFactor
                            : trailer.EuroPalletCapacity is int euroCapacity && euroCapacity > 0
                                ? (decimal)baseCapacity / euroCapacity
                                : (decimal?)null;

                    if (euroFactor is decimal factor)
                        used += euro * factor;
                    else
                        missingRules = true;
                }

                if (trolley > 0)
                {
                    var trolleyFactor =
                        trailer.TrolleyToStandardEquivalent is decimal configuredTrolleyFactor && configuredTrolleyFactor > 0
                            ? configuredTrolleyFactor
                            : trailer.TrolleyCapacity is int trolleyCapacity && trolleyCapacity > 0
                                ? (decimal)baseCapacity / trolleyCapacity
                                : (decimal?)null;

                    if (trolleyFactor is decimal factor)
                        used += trolley * factor;
                    else
                        missingRules = true;
                }

                if (!missingRules)
                {
                    equivalentUsed = used;
                    equivalentCapacity = baseCapacity;
                    utilisation = Math.Round(used / baseCapacity * 100m, 1);
                }
            }
        }

        var directOver =
            (trailer.PalletCapacity is int standardCapacity && standard > standardCapacity)
            || (trailer.EuroPalletCapacity is int euroCapacity2 && euro > euroCapacity2)
            || (trailer.TrolleyCapacity is int trolleyCapacity2 && trolley > trolleyCapacity2);

        if (directOver || utilisation > 100m)
        {
            return new(
                "red",
                utilisation,
                standard,
                euro,
                trolley,
                trailer.PalletCapacity,
                trailer.EuroPalletCapacity,
                trailer.TrolleyCapacity,
                equivalentUsed,
                equivalentCapacity,
                "OVER CAPACITY — amend the load or record an override reason.");
        }

        if (missingRules)
        {
            return new(
                "rules-missing",
                utilisation,
                standard,
                euro,
                trolley,
                trailer.PalletCapacity,
                trailer.EuroPalletCapacity,
                trailer.TrolleyCapacity,
                equivalentUsed,
                equivalentCapacity,
                "Mixed load detected. Complete the Euro/trolley conversion rules in Trailer Master.");
        }

        if (utilisation is null)
        {
            return new(
                "unknown",
                null,
                standard,
                euro,
                trolley,
                trailer.PalletCapacity,
                trailer.EuroPalletCapacity,
                trailer.TrolleyCapacity,
                equivalentUsed,
                equivalentCapacity,
                "Trailer capacity is incomplete in Master Data.");
        }

        var status = utilisation >= 90m ? "amber" : "green";
        return new(
            status,
            utilisation,
            standard,
            euro,
            trolley,
            trailer.PalletCapacity,
            trailer.EuroPalletCapacity,
            trailer.TrolleyCapacity,
            equivalentUsed,
            equivalentCapacity,
            $"{utilisation:0.#}% of configured trailer capacity.");
    }

    private async Task RefreshOrderStatesAsync(Guid[] orderIds, CancellationToken ct)
    {
        var orders = await ops.Orders.Where(x => orderIds.Contains(x.Id)).ToListAsync(ct);
        var allocations = await ops.RunOrderAllocations
            .Where(x => orderIds.Contains(x.OrderId))
            .ToListAsync(ct);

        var byOrder = allocations
            .GroupBy(x => x.OrderId)
            .ToDictionary(
                g => g.Key,
                g => new
                {
                    Standard = g.Sum(x => x.StandardPallets),
                    Euro = g.Sum(x => x.EuroPallets),
                    Trolleys = g.Sum(x => x.Trolleys)
                });

        foreach (var order in orders)
        {
            byOrder.TryGetValue(order.Id, out var planned);
            var fullyPlanned =
                (planned?.Standard ?? 0) >= order.Pallets
                && (planned?.Euro ?? 0) >= order.EuroPallets
                && (planned?.Trolleys ?? 0) >= order.Trolleys;

            order.State = fullyPlanned ? OrderState.Planned : OrderState.ReadyToPlan;
            order.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }

        await ops.SaveChangesAsync(ct);
    }

    private async Task<string> NextRunNumberAsync(
        DateOnly date,
        PlanningPeriod period,
        CancellationToken ct)
    {
        var count = await ops.PlanningRuns.CountAsync(
            x => x.PlanDate == date && x.Period == period && x.State != PlanningRunState.Cancelled,
            ct);

        return $"{period} {count + 1:00}";
    }

    private static PlanningPeriod PeriodFor(TimeOnly? collectionTime) =>
        collectionTime is TimeOnly time && time >= new TimeOnly(15, 0)
            ? PlanningPeriod.PM
            : PlanningPeriod.AM;

    private static string MovementKey(OrderWorking item) =>
        $"{item.Order.CollectionDate:yyyyMMdd}|{item.Period}|{item.CollectionSite.Id:N}|{item.DeliverySite.Id:N}|{Normalize(item.Order.TemperatureRequirement)}|{Normalize(item.Order.TrailerRequirement)}";

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? "-"
            : value.Trim().ToUpperInvariant();
}
