using Microsoft.EntityFrameworkCore;
using Slh.Tms.V2.Api.Data;
using Slh.Tms.V2.Api.Domain;

namespace Slh.Tms.V2.Api.Application;

public sealed class MasterResolver(MasterDataDbContext db)
{
    public async Task<MasterResolution> ResolveAsync(ExtractedOrderDraft draft, CancellationToken ct)
    {
        var issues = new List<string>();

        var customerId = await ResolveCustomerAsync(draft.CustomerCode, ct);
        if (customerId is null)
            issues.Add("Customer could not be resolved exactly against Master Data.");

        var collectionSiteId = await ResolveSiteAsync(draft.CollectionSiteText, customerId, ct);
        if (collectionSiteId is null)
            issues.Add("Collection site could not be resolved exactly against Master Data.");

        var deliverySiteId = await ResolveSiteAsync(draft.DeliverySiteText, customerId: null, ct);
        if (deliverySiteId is null)
            issues.Add("Delivery site could not be resolved exactly against Master Data.");

        var marketId = await ResolveMarketAsync(draft.MarketName, ct);
        if (!string.IsNullOrWhiteSpace(draft.MarketName) && marketId is null)
            issues.Add("Market could not be resolved exactly against Master Data.");

        var resolvedRequired = new[] { customerId, collectionSiteId, deliverySiteId }.Count(x => x is not null);
        var confidence = resolvedRequired / 3m;

        return new MasterResolution(
            customerId,
            collectionSiteId,
            deliverySiteId,
            marketId,
            confidence,
            issues);
    }

    private async Task<Guid?> ResolveCustomerAsync(string? value, CancellationToken ct)
    {
        var key = Normalise(value);
        if (key.Length == 0) return null;

        var candidates = await db.Customers.AsNoTracking()
            .Where(x => x.Active)
            .Select(x => new { x.Id, x.Code, x.Name })
            .ToListAsync(ct);

        var matches = candidates
            .Where(x => Normalise(x.Code) == key || Normalise(x.Name) == key)
            .Select(x => x.Id)
            .Distinct()
            .Take(2)
            .ToList();

        return matches.Count == 1 ? matches[0] : null;
    }

    private async Task<Guid?> ResolveSiteAsync(string? value, Guid? customerId, CancellationToken ct)
    {
        var key = Normalise(value);
        if (key.Length == 0) return null;

        var sites = await db.Sites.AsNoTracking()
            .Where(x => x.Active && (customerId == null || x.CustomerId == customerId))
            .Select(x => new { x.Id, x.Code, x.Name, x.Postcode })
            .ToListAsync(ct);

        var directMatches = sites
            .Where(x =>
                Normalise(x.Code) == key ||
                Normalise(x.Name) == key ||
                Normalise(x.Postcode) == key)
            .Select(x => x.Id)
            .Distinct()
            .Take(2)
            .ToList();

        if (directMatches.Count == 1)
            return directMatches[0];

        if (directMatches.Count > 1)
            return null;

        var aliasRows = await db.SiteAliases.AsNoTracking()
            .Where(x => x.Approved)
            .Select(x => new { x.SiteId, x.Alias })
            .ToListAsync(ct);

        var aliasMatches = aliasRows
            .Where(x => Normalise(x.Alias) == key)
            .Select(x => x.SiteId)
            .Where(id => sites.Any(site => site.Id == id))
            .Distinct()
            .Take(2)
            .ToList();

        return aliasMatches.Count == 1 ? aliasMatches[0] : null;
    }

    private async Task<Guid?> ResolveMarketAsync(string? value, CancellationToken ct)
    {
        var key = Normalise(value);
        if (key.Length == 0) return null;

        var candidates = await db.Markets.AsNoTracking()
            .Where(x => x.Active)
            .Select(x => new { x.Id, x.Code, x.Name })
            .ToListAsync(ct);

        var matches = candidates
            .Where(x => Normalise(x.Code) == key || Normalise(x.Name) == key)
            .Select(x => x.Id)
            .Distinct()
            .Take(2)
            .ToList();

        return matches.Count == 1 ? matches[0] : null;
    }

    private static string Normalise(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        return new string(value
            .Trim()
            .ToUpperInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());
    }
}
