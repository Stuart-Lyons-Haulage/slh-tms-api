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
/// Builds an independent read-only plan directly from order work. Quantified pallet runs are
/// capacity-planned exactly as before. Operational movements whose quantity is not stated
/// (common for Southbound trays, crates, trollies and market work) remain visible and routable
/// as standalone movements without inventing a pallet count or capacity utilisation.
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
        var quantified = ExpandOversizeOrders(orders.Where(order => order.Pallets > 0).ToList())
            .OrderBy(order => PeriodRank(order.Period))
            .ThenBy(order => order.CollectionTimeFrom ?? TimeOnly.MaxValue)
            .ThenBy(order => order.Reference, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var unquantified = orders
            .Where(order => order.Pallets <= 0)
            .OrderBy(order => PeriodRank(order.Period))
            .ThenBy(order => order.CollectionTimeFrom ?? TimeOnly.MaxValue)
            .ThenBy(order => order.Reference, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new List<BetaDayBuiltRun>();
        var sequence = 0;

        foreach (var group in quantified.GroupBy(order => (Period: NormalisePeriod(order.Period), Family: PalletFamily(order.PalletType))))
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

                IReadOnlyList<BetaRoutePoint> stops = BuildStops(selected);
                BetaHgvRouteCost? route = null;
                if (selected.All(order => order.RoutingMapped))
                {
                    var collectionCount = DistinctPoints(selected
                        .OrderBy(order => order.CollectionTimeFrom ?? TimeOnly.MaxValue)
                        .ThenBy(order => order.Reference, StringComparer.OrdinalIgnoreCase)
                        .Select(order => order.Collection)).Count;
                    (stops, route) = await OptimiseWithinCollectionAndDeliveryPhasesAsync(stops, collectionCount, ct);
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

        // Unknown quantity is not zero work. Route it as a standalone operational movement so
        // it participates in coverage and geography, but never use it to claim pallet capacity.
        foreach (var order in unquantified)
        {
            ct.ThrowIfCancellationRequested();
            sequence++;
            var warnings = new List<string>
            {
                "Quantity was not stated for this movement. It is routed and reconciled, but excluded from pallet-capacity utilisation."
            };
            if (!order.RoutingMapped || !string.IsNullOrWhiteSpace(order.MappingWarning))
                warnings.Add(order.MappingWarning ?? $"{order.Reference}: collection or delivery is not mapped to Site Master coordinates.");

            var stops = BuildStops([order]);
            BetaHgvRouteCost? route = null;
            if (order.RoutingMapped && stops.Count >= 2)
                route = await routeProvider.GetRouteAsync(stops, ct);
            if (route is null)
                warnings.Add("Live Azure Maps HGV evidence is unavailable for this movement. No approximate mileage was substituted.");

            result.Add(new BetaDayBuiltRun(
                $"BETA-{planningDate:yyyyMMdd}-{NormalisePeriod(order.Period)}-{sequence:00}",
                NormalisePeriod(order.Period),
                "Unquantified",
                0,
                0,
                0m,
                route is not null,
                route?.Miles,
                route?.DriveMinutes,
                [order],
                stops,
                warnings));
        }

        return result
            .OrderBy(run => PeriodRank(run.Period))
            .ThenBy(run => run.Reference, StringComparer.OrdinalIgnoreCase)
            .ToList();
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

    internal static List<BetaRoutePoint> BuildStops(IReadOnlyList<BetaDayOrderInput> orders)
    {
        var collections = DistinctPoints(orders
            .OrderBy(order => order.CollectionTimeFrom ?? TimeOnly.MaxValue)
            .ThenBy(order => order.Reference, StringComparer.OrdinalIgnoreCase)
            .Select(order => order.Collection));
        var deliveries = DistinctPoints(orders
            .OrderBy(order => order.CollectionTimeFrom ?? TimeOnly.MaxValue)
            .ThenBy(order => order.Reference, StringComparer.OrdinalIgnoreCase)
            .Select(order => order.Delivery));
        return collections.Concat(deliveries).ToList();
    }

    private static List<BetaRoutePoint> DistinctPoints(IEnumerable<BetaRoutePoint> points)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<BetaRoutePoint>();
        foreach (var point in points)
        {
            var key = $"{point.Latitude:0.000000}|{point.Longitude:0.000000}|{NormalisePointName(point.Name)}";
            if (!seen.Add(key)) continue;
            result.Add(point);
        }
        return result;
    }

    private static string NormalisePointName(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

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
    private static string NormalisePeriod(string? value)
    {
        if (string.Equals(value, "W3", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "Wave 3", StringComparison.OrdinalIgnoreCase)) return "W3";
        return string.Equals(value, "PM", StringComparison.OrdinalIgnoreCase) ? "PM" : "AM";
    }
    private static int PeriodRank(string? value) => NormalisePeriod(value) switch { "AM" => 0, "PM" => 1, "W3" => 2, _ => 3 };
}
