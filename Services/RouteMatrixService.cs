using System.Text.Json;
using Slh.Tms.Api.Models.Planning;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Builds a travel-time and distance matrix between a set of geocoded stops by
/// calling AzureMapsRouteClient for each leg.  Falls back to the Haversine
/// approximation already embedded in AzureMapsRouteClient when the live API is
/// unavailable — the fallback is flagged in RoutingSource so the optimiser can
/// adjust confidence accordingly.
/// </summary>
public sealed class RouteMatrixService(
    AzureMapsRouteClient mapsClient,
    ILogger<RouteMatrixService> logger)
{
    // Road-factor applied on top of straight-line Haversine when Azure Maps is down.
    private const double RoadFactor = 1.18;

    public sealed record LegEstimate(
        decimal Miles,
        int DriveMinutes,
        string RoutingSource);   // "AzureMaps" | "ResilientEstimate"

    public sealed record RouteMatrix(
        IReadOnlyList<StopPoint> Stops,
        IReadOnlyList<IReadOnlyList<LegEstimate>> Legs,  // [from][to]
        string RoutingSource);

    public sealed record StopPoint(
        Guid? SiteId,
        string Name,
        decimal Latitude,
        decimal Longitude);

    /// <summary>
    /// Build a full NxN matrix for the given stops.  Only the upper-right triangle
    /// is populated (from → to); reverse legs are populated symmetrically.
    /// Uses a sequential fan-out rather than a bulk matrix call so that individual
    /// leg failures do not block the whole matrix.
    /// </summary>
    public async Task<RouteMatrix> BuildAsync(
        IReadOnlyList<StopPoint> stops,
        CancellationToken ct)
    {
        if (stops.Count < 2)
            return new RouteMatrix(stops, [], "None");

        var n = stops.Count;
        var legs = new LegEstimate[n][];
        for (var i = 0; i < n; i++) legs[i] = new LegEstimate[n];

        var anyLive = false;
        var anyEstimated = false;

        for (var from = 0; from < n; from++)
        {
            for (var to = from + 1; to < n; to++)
            {
                var leg = await EstimateLegAsync(stops[from], stops[to], ct);
                legs[from][to] = leg;
                legs[to][from] = leg;   // symmetric assumption for road network
                if (leg.RoutingSource == "AzureMaps") anyLive = true;
                else anyEstimated = true;
            }
            // self-leg is zero
            legs[from][from] = new LegEstimate(0m, 0, "Self");
        }

        var matrixSource = anyLive && !anyEstimated ? "AzureMaps"
            : anyLive ? "Mixed"
            : "ResilientEstimate";

        return new RouteMatrix(stops, legs.Select(row => (IReadOnlyList<LegEstimate>)row).ToList(), matrixSource);
    }

    /// <summary>
    /// Calculate the total road cost of visiting stops in the given sequence.
    /// Returns (miles, driveMinutes, source).
    /// </summary>
    public static (decimal Miles, int DriveMinutes, string Source) SequenceCost(
        RouteMatrix matrix,
        IReadOnlyList<int> sequence)
    {
        if (sequence.Count < 2) return (0m, 0, matrix.RoutingSource);
        var miles = 0m;
        var minutes = 0;
        for (var i = 0; i < sequence.Count - 1; i++)
        {
            var leg = matrix.Legs[sequence[i]][sequence[i + 1]];
            miles += leg.Miles;
            minutes += leg.DriveMinutes;
        }
        return (miles, minutes, matrix.RoutingSource);
    }

    /// <summary>
    /// 2-opt improvement on a delivery-only sub-sequence.
    /// Hard constraints (collection-before-delivery ordering) are checked via the
    /// provided predicate — the swap is only kept when the predicate returns true.
    /// </summary>
    public static IReadOnlyList<int> TwoOpt(
        RouteMatrix matrix,
        IReadOnlyList<int> sequence,
        Func<IReadOnlyList<int>, bool> isValid)
    {
        var best = sequence.ToList();
        var improved = true;
        while (improved)
        {
            improved = false;
            for (var i = 1; i < best.Count - 1; i++)
            {
                for (var k = i + 1; k < best.Count; k++)
                {
                    var candidate = TwoOptSwap(best, i, k);
                    if (!isValid(candidate)) continue;
                    var (cMiles, _, _) = SequenceCost(matrix, candidate);
                    var (bMiles, _, _) = SequenceCost(matrix, best);
                    if (cMiles < bMiles)
                    {
                        best = candidate;
                        improved = true;
                    }
                }
            }
        }
        return best;
    }

    private static List<int> TwoOptSwap(List<int> route, int i, int k)
    {
        var next = new List<int>(route.Count);
        for (var x = 0; x < i; x++) next.Add(route[x]);
        for (var x = k; x >= i; x--) next.Add(route[x]);
        for (var x = k + 1; x < route.Count; x++) next.Add(route[x]);
        return next;
    }

    private async Task<LegEstimate> EstimateLegAsync(
        StopPoint from,
        StopPoint to,
        CancellationToken ct)
    {
        try
        {
            var estimate = await mapsClient.TravelTimeEstimate(
                (from.Longitude, from.Latitude),
                (to.Longitude, to.Latitude),
                ct);

            var haversineKm = HaversineKm(from, to);
            var roadMiles = (decimal)(haversineKm * RoadFactor * 0.621371);
            var driveMinutes = (int)Math.Ceiling(estimate.TravelTime.TotalMinutes);

            return new LegEstimate(
                Math.Round(roadMiles, 2),
                driveMinutes,
                estimate.IsApproximate ? "ResilientEstimate" : "AzureMaps");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Route leg estimate failed for {From}→{To}; using Haversine fallback.", from.Name, to.Name);
            return HaversineLeg(from, to);
        }
    }

    private static LegEstimate HaversineLeg(StopPoint from, StopPoint to)
    {
        var km = HaversineKm(from, to);
        var roadMiles = (decimal)(km * RoadFactor * 0.621371);
        var driveMinutes = (int)Math.Ceiling((double)roadMiles / 45.0 * 60.0);
        return new LegEstimate(Math.Round(roadMiles, 2), driveMinutes, "ResilientEstimate");
    }

    private static double HaversineKm(StopPoint a, StopPoint b)
    {
        const double R = 6371.0;
        static double Rad(double v) => v * Math.PI / 180.0;
        var lat1 = Rad((double)a.Latitude);  var lat2 = Rad((double)b.Latitude);
        var dLat = lat2 - lat1;              var dLon = Rad((double)(b.Longitude - a.Longitude));
        var h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
              + Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2.0 * R * Math.Asin(Math.Min(1.0, Math.Sqrt(h)));
    }
}
