using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public sealed class MasterDataService(TmsDbContext db, IMemoryCache cache)
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(2);

    public Task<IReadOnlyList<MasterDriver>> GetActiveDriversAsync(CancellationToken ct = default) =>
        GetAsync("master:drivers", () => db.MasterDrivers.AsNoTracking().OrderBy(x => x.FullName).ToListAsync(ct));

    public Task<IReadOnlyList<MasterVehicle>> GetActiveVehiclesAsync(CancellationToken ct = default) =>
        GetAsync("master:vehicles", () => db.MasterVehicles.AsNoTracking().OrderBy(x => x.Registration).ToListAsync(ct));

    public Task<IReadOnlyList<MasterTrailer>> GetActiveTrailersAsync(CancellationToken ct = default) =>
        GetAsync("master:trailers", () => db.MasterTrailers.AsNoTracking().OrderBy(x => x.Registration).ToListAsync(ct));

    public Task<IReadOnlyList<MasterDepot>> GetActiveDepotsAsync(CancellationToken ct = default) =>
        GetAsync("master:depots", () => db.MasterDepots.AsNoTracking().OrderBy(x => x.DepotName).ToListAsync(ct));

    public Task<IReadOnlyList<MasterCustomer>> GetActiveCustomersAsync(CancellationToken ct = default) =>
        GetAsync("master:customers", () => db.MasterCustomers.AsNoTracking().OrderBy(x => x.CustomerName).ToListAsync(ct));

    public Task<IReadOnlyList<MasterSite>> GetActiveSitesAsync(CancellationToken ct = default) =>
        GetAsync("master:sites", () => db.MasterSites.AsNoTracking().OrderBy(x => x.SiteName).ToListAsync(ct));

    public Task<IReadOnlyList<MasterSubcontractor>> GetActiveSubcontractorsAsync(CancellationToken ct = default) =>
        GetAsync("master:subcontractors", () => db.MasterSubcontractors.AsNoTracking().OrderBy(x => x.CompanyName).ToListAsync(ct));

    public Task<IReadOnlyList<MasterMarket>> GetActiveMarketsAsync(CancellationToken ct = default) =>
        GetAsync("master:markets", () => db.MasterMarkets.AsNoTracking().OrderBy(x => x.Market).ThenBy(x => x.Name).ToListAsync(ct));

    public Task<IReadOnlyList<MasterFuelCard>> GetActiveFuelCardsAsync(CancellationToken ct = default) =>
        GetAsync("master:fuel-cards", () => db.MasterFuelCards.AsNoTracking().OrderBy(x => x.Registration).ToListAsync(ct));

    public Task<IReadOnlyList<MasterFuelPrice>> GetActiveFuelPricesAsync(CancellationToken ct = default) =>
        GetAsync("master:fuel-prices", () => db.MasterFuelPrices.AsNoTracking().OrderByDescending(x => x.WeekCommencing).ThenBy(x => x.Provider).ToListAsync(ct));

    public Task<MasterSite?> GetSiteByIdAsync(string siteId, CancellationToken ct = default) =>
        cache.GetOrCreateAsync($"master:site:{siteId}", entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
            return db.MasterSites.AsNoTracking().SingleOrDefaultAsync(x => x.SiteId == siteId, ct);
        });

    public Task<MasterDriver?> GetDriverByIdAsync(string driverId, CancellationToken ct = default) =>
        cache.GetOrCreateAsync($"master:driver:{driverId}", entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
            return db.MasterDrivers.AsNoTracking().SingleOrDefaultAsync(x => x.DriverId == driverId, ct);
        });

    public void Invalidate()
    {
        foreach (var key in CacheKeys)
            cache.Remove(key);
        // Single-record lookups use the same two-minute policy. Removing by prefix
        // keeps invalidation deterministic without requiring a distributed cache.
        foreach (var key in cacheKeysForIndividualRows)
            cache.Remove(key);
    }

    private static readonly string[] cacheKeysForIndividualRows = [];

    private static readonly string[] CacheKeys =
    [
        "master:drivers", "master:vehicles", "master:trailers", "master:depots",
        "master:customers", "master:sites", "master:subcontractors", "master:markets",
        "master:fuel-cards", "master:fuel-prices"
    ];

    private async Task<IReadOnlyList<T>> GetAsync<T>(string key, Func<Task<List<T>>> factory)
    {
        if (cache.TryGetValue(key, out IReadOnlyList<T>? cached) && cached is not null)
            return cached;

        var rows = await factory();
        cache.Set(key, (IReadOnlyList<T>)rows, CacheDuration);
        return rows;
    }
}
