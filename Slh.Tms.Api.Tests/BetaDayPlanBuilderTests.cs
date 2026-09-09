using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class BetaDayPlanBuilderTests
{
    [Fact]
    public async Task BuildAsync_BuildsWholeDayByPeriodAndKeepsEveryCollectionBeforeDeliveries()
    {
        var provider = new FakeRouteProvider();
        var builder = new BetaDayPlanBuilder(provider);
        var date = new DateOnly(2026, 9, 9);
        var orders = new[]
        {
            Order("PO-A", "AM", 10, "Collect A", "Deliver X", new TimeOnly(4, 0)),
            Order("PO-B", "AM", 12, "Collect B", "Deliver Y", new TimeOnly(5, 0)),
            Order("PO-C", "PM", 8, "Collect C", "Deliver Z", new TimeOnly(18, 0)),
        };

        var runs = await builder.BuildAsync(date, orders, CancellationToken.None);

        Assert.Equal(2, runs.Count);
        var am = Assert.Single(runs.Where(run => run.Period == "AM"));
        Assert.Equal(22, am.PlannedPallets);
        Assert.True(am.RoutingAvailable);
        Assert.Equal(2, am.Orders.Count);
        AssertCollectionsBeforeDeliveries(am, orders.Where(order => order.Period == "AM").ToList());

        var pm = Assert.Single(runs.Where(run => run.Period == "PM"));
        Assert.Equal(8, pm.PlannedPallets);
        AssertCollectionsBeforeDeliveries(pm, orders.Where(order => order.Period == "PM").ToList());
    }

    [Fact]
    public void BuildStops_ConsolidatesRepeatedPhysicalSitesWithoutLosingOrderLines()
    {
        var sharedCollection = new BetaRoutePoint("NWF-Selsey", 50.74m, -0.78m);
        var sharedDelivery = new BetaRoutePoint("Aldi-Darlington", 54.52m, -1.56m);
        var orders = new[]
        {
            new BetaDayOrderInput(Guid.NewGuid(), Guid.NewGuid(), "SO-1", "NWF", "AM", "Euro", 16, new TimeOnly(5, 0), sharedCollection, sharedDelivery),
            new BetaDayOrderInput(Guid.NewGuid(), Guid.NewGuid(), "SO-2", "NWF", "AM", "Euro", 6, new TimeOnly(5, 0), sharedCollection, sharedDelivery),
            new BetaDayOrderInput(Guid.NewGuid(), Guid.NewGuid(), "SO-3", "NWF", "AM", "Euro", 4, new TimeOnly(6, 0), new BetaRoutePoint("NWF-Runcton", 50.84m, -0.72m), sharedDelivery),
        };

        var stops = BetaDayPlanBuilder.BuildStops(orders);

        Assert.Equal(new[] { "NWF-Selsey", "NWF-Runcton", "Aldi-Darlington" }, stops.Select(stop => stop.Name));
        Assert.Equal(3, orders.Length);
        Assert.Equal(26, orders.Sum(order => order.Pallets));
    }

    private static void AssertCollectionsBeforeDeliveries(BetaDayBuiltRun run, IReadOnlyList<BetaDayOrderInput> orders)
    {
        var positions = run.Stops.Select((stop, index) => (stop.Name, index)).ToDictionary(item => item.Name, item => item.index);
        var latestCollection = orders.Max(order => positions[order.Collection.Name]);
        var earliestDelivery = orders.Min(order => positions[order.Delivery.Name]);
        Assert.True(latestCollection < earliestDelivery);
    }

    private static BetaDayOrderInput Order(string reference, string period, int pallets, string collection, string delivery, TimeOnly time) =>
        new(Guid.NewGuid(), Guid.NewGuid(), reference, "TEST", period, "Standard", pallets, time,
            new BetaRoutePoint(collection, 50m, -1m), new BetaRoutePoint(delivery, 51m, -1m));

    private sealed class FakeRouteProvider : IBetaHgvRouteProvider
    {
        public Task<BetaHgvRouteCost?> GetRouteAsync(IReadOnlyList<BetaRoutePoint> points, CancellationToken ct)
        {
            var miles = Math.Max(points.Count - 1, 0) * 10m;
            return Task.FromResult<BetaHgvRouteCost?>(new BetaHgvRouteCost(miles, (int)miles, "AzureMapsHgv"));
        }
    }
}
