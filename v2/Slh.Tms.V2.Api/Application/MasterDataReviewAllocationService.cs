using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.V2.Api.Data;
using Slh.Tms.V2.Api.Domain;

namespace Slh.Tms.V2.Api.Application;

public sealed record SiteAllocationRequest(string Kind, Guid Id, Guid SiteId);

public sealed class MasterDataReviewAllocationService(MasterDataDbContext db)
{
    public async Task AllocateToSiteAsync(
        string kind,
        Guid id,
        Guid siteId,
        CancellationToken ct)
    {
        if (!await db.Sites.AnyAsync(x => x.Id == siteId && x.Active, ct))
            throw new InvalidOperationException("The selected Site does not exist or is inactive.");

        switch (kind.Trim().ToLowerInvariant())
        {
            case "alias":
                await AllocateAliasAsync(id, siteId, ct);
                break;
            case "routetiming":
                await AllocateRouteTimingAsync(id, siteId, ct);
                break;
            case "customercontact":
                await AllocateCustomerContactAsync(id, siteId, ct);
                break;
            case "review":
                await AllocateReviewItemAsync(id, siteId, ct);
                break;
            default:
                throw new InvalidOperationException($"Review allocation kind '{kind}' is not supported.");
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task AllocateAliasAsync(Guid id, Guid siteId, CancellationToken ct)
    {
        var candidate = await db.SiteAliasCandidates.SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new InvalidOperationException("The alias review item no longer exists.");

        var exists = await db.SiteAliases.AnyAsync(
            x => x.SiteId == siteId && x.Alias == candidate.Alias,
            ct);

        if (!exists)
        {
            db.SiteAliases.Add(new SiteAlias
            {
                SiteId = siteId,
                Alias = candidate.Alias,
                Source = candidate.Source ?? "Master Data Review",
                Approved = true
            });
        }

        candidate.SiteId = siteId;
        candidate.Approved = true;
        candidate.Active = true;
    }

    private async Task AllocateRouteTimingAsync(Guid id, Guid siteId, CancellationToken ct)
    {
        var timing = await db.RouteTimings.SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new InvalidOperationException("The Planner Knowledge record no longer exists.");

        timing.SiteId = siteId;
    }

    private async Task AllocateCustomerContactAsync(Guid id, Guid siteId, CancellationToken ct)
    {
        var contact = await db.CustomerContacts.SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new InvalidOperationException("The customer contact no longer exists.");

        contact.SiteId = siteId;
    }

    private async Task AllocateReviewItemAsync(Guid id, Guid siteId, CancellationToken ct)
    {
        var review = await db.MasterDataReviewItems.SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new InvalidOperationException("The review item no longer exists.");

        if (!review.EntityType.Equals("SiteCutoff", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This review item cannot currently be allocated to a Site.");

        if (string.IsNullOrWhiteSpace(review.PayloadJson))
            throw new InvalidOperationException("The deadline review item has no source payload.");

        using var payload = JsonDocument.Parse(review.PayloadJson);
        var root = payload.RootElement;

        string? Get(string name)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.ToString();
            }

            return null;
        }

        static TimeOnly? Time(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            return TimeOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                || TimeOnly.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.None, out parsed)
                ? parsed
                : null;
        }

        var code = Get("SiteCutoffID") ?? review.SourceReference;
        if (string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException("The deadline review item has no cut-off code.");

        var cutoff = await db.SiteCutoffs.SingleOrDefaultAsync(x => x.Code == code, ct);
        if (cutoff is null)
        {
            cutoff = new SiteCutoff
            {
                Code = code,
                SiteId = siteId
            };
            db.SiteCutoffs.Add(cutoff);
        }

        cutoff.SiteId = siteId;
        cutoff.Plan = Get("Plan");
        cutoff.StandardCutoff = Time(Get("Standard Cutoff"));
        cutoff.ExtendedCutoff = Time(Get("Extended Cutoff"));
        cutoff.Contact = Get("Contact");
        cutoff.Notes = Get("Notes");
        cutoff.Temperature = Get("Temperature");
        cutoff.PalletType = Get("Pallet Type");
        cutoff.LastDespatchTime = Time(Get("Last Despatch Time"));
        cutoff.PlannedCollectFrom = Time(Get("Planned Collect From"));
        cutoff.PlannedCollectTo = Time(Get("Planned Collect To"));
        cutoff.DepotDeliveryDeadline = Time(Get("Depot Delivery Deadline"));
        cutoff.Active = true;

        review.Resolved = true;
        review.ResolutionNotes = $"Allocated to Site {siteId}.";
        review.UpdatedAtUtc = DateTimeOffset.UtcNow;
    }
}
