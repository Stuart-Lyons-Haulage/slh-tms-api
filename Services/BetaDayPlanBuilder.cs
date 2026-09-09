namespace Slh.Tms.Api.Services;

public sealed record BetaDayOrderInput(
    Guid OrderId,
    Guid SourceLineId,
    string Reference,
    string CustomerCode,
    string Period,
    string? PalletType,
    int Pallets,
    TimeOnly? CollectionTimeFrom,
    BetaRoutePoint Collection,
    BetaRoutePoint Delivery,
    bool RoutingMapped = true,
    string? MappingWarning = null);

public sealed record BetaDayBuiltRun(
    string Reference,
    string Period,
    string PalletFamily,
    int CapacityPallets,
    int PlannedPallets,
    decimal UtilisationPercent,
    bool RoutingAvailable,
    decimal? Miles,
    int? DriveMinutes,
    IReadOnlyList<BetaDayOrderInput> Orders,
    IReadOnlyList<BetaRoutePoint> Stops,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Builds an independent read-only plan directly from order work. Runs are separated by
/// AM/PM and pallet family, never exceed the family capacity, and always visit every
/// collection before the first delivery. Live Azure Maps HGV cost is used when deciding
/// which compatible order should fill the remaining run capacity. Approximate routing is
/// never supplied by IBetaHgvRouteProvider.
/// </summary>
public sealed class BetaDayPlanBuilder(IBetaHgvRouteProvider routeProvider)
{
    private const int StandardCapacity = 26;
    private const int EuroCapacity = 33;
    private const int MaxCandidateRouteChecksPerFill = 16;
    private const int MaxPhaseSwapChecks = 12;

    public async Task<IReadOnlyList<BetaDayBuiltRun>> BuildAsync(
        DateOnly planningDate,
        IReadOnlyList<BetaDayOrderInput> orders,
        CancellationToken ct)
    {
        var expanded = ExpandOversizeOrders(orders)
            .Where(order => order.Pallets > 0)
            .OrderBy(order => PeriodRank(order.Period))
            .ThenBy(order => order.CollectionTimeFrom ?? TimeOnly.MaxValue)
            .ThenBy(order => order.Reference, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new List<BetaDayBuiltRun>();
        var sequence = 0;

        foreach (var group in expanded.GroupBy(order => (Period: NormalisePeriod(order.Period), Family: PalletFamily(order.PalletType))))
        {
            var remaining = group.ToList();
            while (remaining.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var capacity = Capacity(group.Key.Family);
                var selected = new List<BetaDayOrderInput> { remaining[0] };
                remaining.RemoveAt(0);
                var planned = selected[0].Pallets;

                while (remaining.Count > 0 && planned < capacity)
                {
                    var fits = remaining.Where(order => planned + order.Pallets <= capacity).Take(MaxCandidateRouteChecksPerFill).ToList();
                    if (fits.Count == 0) break;

                    BetaDayOrderInput? best = null;
                    BetaHgvRouteCost? bestCost = null;
                    foreach (var candidate in fits)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!selected.All(order => order.RoutingMapped) || !candidate.RoutingMapped) continue;
                        var cost = await routeProvider.GetRouteAsync(BuildStops(selected.Append(candidate).ToList()), ct);
                        if (cost is null) continue;
                        if (bestCost is null || Better(cost, bestCost))
                        {
                            best = candidate;
                            bestCost = cost;
                        }
                    }

                    // If no candidate has live HGV evidence, still keep all work visible and
                    // capacity-safe. The run will be explicitly marked routing-unavailable.
                    best ??= fits
                        .OrderBy(order => order.CollectionTimeFrom ?? TimeOnly.MaxValue)
                        .ThenBy(order => order.Reference, StringComparer.OrdinalIgnoreCase)
                        .First();

                    selected.Add(best);
                    remaining.Remove(best);
                    planned += best.Pallets;
                }

                sequence++;
                var warnings = selected
                    .Where(order => !order.RoutingMapped || !string.IsNullOrWhiteSpace(order.MappingWarning))
                    .Select(order => order.MappingWarning ?? $"{order.Reference}: collection or delivery is not mapped to Site Master coordinates.")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var stops = BuildStops(selected);
                BetaHgvRouteCost? route = null;
                if (selected.All(order => order.RoutingMapped))
                {
                    (stops, route) = await OptimiseWithinCollectionAndDeliveryPhasesAsync(stops, selected.Count, ct);
                    route ??= await routeProvider.GetRouteAsync(stops, ct);
                }

                if (route is null)
                    warnings.Add("Live Azure Maps HGV evidence is unavailable for this run. It remains in the Beta day plan but no estimated mileage is substituted.");

                result.Add(new BetaDayBuiltRun(
                    $"BETA-{planningDate:yyyyMMdd}-{group.Key.Period}-{sequence:00}",
                    group.Key.Period,
                    group.Key.Family,
                    capacity,
                    planned,
                    capacity == 0 ? 0m : Math.Round((decimal)planned / capacity * 100m, 1),
                    route is not null,
                    route?.Miles,
                    route?.DriveMinutes,
                    selected,
                    stops,
                    warnings));
            }
        }

        return result;
    }

    private async Task<(IReadOnlyList<BetaRoutePoint> Stops, BetaHgvRouteCost? Cost)> OptimiseWithinCollectionAndDeliveryPhasesAsync(
        IReadOnlyList<BetaRoutePoint> original,
        int collectionCount,
        CancellationToken ct)
    {
        var best = original.ToList();
        var bestCost = await routeProvider.GetRouteAsync(best, ct);
        if (bestCost is null) return (best, null);

        var checks = 0;
        // Adjacent swaps are deliberately restricted to within each phase. The final collection
        // can never cross the first delivery, preserving the SLH operating rule by construction.
        foreach (var (start, endExclusive) in new[] { (0, collectionCount), (collectionCount, best.Count) })
        {
            var changed = true;
            while (changed && checks < MaxPhaseSwapChecks)
            {
                changed = false;
                for (var index = start; index + 1 < endExclusive && checks < MaxPhaseSwapChecks; index++)
                {
                    var candidate = best.ToList();
                    (candidate[index], candidate[index + 1]) = (candidate[index + 1], candidate[index]);
                    checks++;
                    var cost = await routeProvider.GetRouteAsync(candidate, ct);
                    if (cost is null || !Better(cost, bestCost)) continue;
                    best = candidate;
                    bestCost = cost;
                    changed = true;
                }
            }
        }

        return (best, bestCost);
    }

    private static List<BetaRoutePoint> BuildStops(IReadOnlyList<BetaDayOrderInput> orders)
    {
        var collections = orders
            .OrderBy(order => order.CollectionTimeFrom ?? TimeOnly.MaxValue)
            .ThenBy(order => order.Reference, StringComparer.OrdinalIgnoreCase)
            .Select(order => order.Collection);
        var deliveries = orders
            .OrderBy(order => order.CollectionTimeFrom ?? TimeOnly.MaxValue)
            .ThenBy(order => order.Reference, StringComparer.OrdinalIgnoreCase)
            .Select(order => order.Delivery);
        return collections.Concat(deliveries).ToList();
    }

    private static IReadOnlyList<BetaDayOrderInput> ExpandOversizeOrders(IReadOnlyList<BetaDayOrderInput> orders)
    {
        var result = new List<BetaDayOrderInput>();
        foreach (var order in orders)
        {
            var capacity = Capacity(PalletFamily(order.PalletType));
            var remaining = Math.Max(order.Pallets, 0);
            while (remaining > capacity)
            {
                result.Add(order with { Pallets = capacity });
                remaining -= capacity;
            }
            if (remaining > 0) result.Add(order with { Pallets = remaining });
        }
        return result;
    }

    private static bool Better(BetaHgvRouteCost candidate, BetaHgvRouteCost current) =>
        candidate.Miles < current.Miles - 0.1m ||
        (Math.Abs(candidate.Miles - current.Miles) <= 0.1m && candidate.DriveMinutes < current.DriveMinutes);

    private static int Capacity(string family) => family == "Euro" ? EuroCapacity : StandardCapacity;
    private static string PalletFamily(string? value) => value?.Contains("euro", StringComparison.OrdinalIgnoreCase) == true ? "Euro" : "Standard";
    private static string NormalisePeriod(string? value) => string.Equals(value, "PM", StringComparison.OrdinalIgnoreCase) ? "PM" : "AM";
    private static int PeriodRank(string? value) => NormalisePeriod(value) == "AM" ? 0 : 1;
}
