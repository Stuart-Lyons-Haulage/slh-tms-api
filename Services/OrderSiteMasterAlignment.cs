using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Resolves raw collection/delivery site names from an email-parsed order payload
/// against the live Site master data and MarketContact master data.
///
/// Changes vs original:
///  - Site.Aliases is now a real persisted column — no enrichment round-trip required.
///  - After site matching, falls through to MarketContact to populate SellerName /
///    StallNumber for market orders where the email parser could not supply them.
///  - The MasterDetailStore.EnrichSitesAsync call is removed; Aliases are always present.
/// </summary>
public static class OrderSiteMasterAlignment
{
    public sealed record Alignment(
        string? CollectionName,
        string? CollectionAddress,
        string? DeliveryName,
        string? DeliveryAddress,
        string? DeliveryMapLink,
        string? DriverInstructions,
        // New: filled from MarketContact when site match is a market destination
        string? MarketSellerName,
        string? MarketStallNumber,
        string? MarketSalesman);

    public static async Task<Alignment> ResolveAsync(TmsDbContext db, JsonElement payload, CancellationToken ct)
    {
        var rawCollection = Text(payload, "collectionSite") ?? Text(payload, "collectionLocation") ?? Text(payload, "sellerName");
        var rawDelivery   = Text(payload, "deliverySite")   ?? Text(payload, "deliveryLocation")   ?? Text(payload, "stallNumber") ?? Text(payload, "destination");
        var rawSeller     = Text(payload, "sellerName");
        var marketName    = Text(payload, "marketName");

        return await ResolveNamesAsync(
            db,
            rawCollection,
            rawDelivery,
            Text(payload, "collectionAddress"),
            Text(payload, "deliveryAddress"),
            Text(payload, "mapLink"),
            Text(payload, "driverInstructions"),
            rawSeller,
            marketName,
            ct);
    }

    public static async Task<Alignment> ResolveNamesAsync(
        TmsDbContext db,
        string? rawCollection,
        string? rawDelivery,
        string? rawCollectionAddress,
        string? rawDeliveryAddress,
        string? rawMapLink,
        string? rawDriverInstructions,
        string? rawSellerName   = null,
        string? marketName      = null,
        CancellationToken ct    = default)
    {
        // ── 1. Load site master data ─────────────────────────────────────────
        // Aliases is now a real persisted column — no separate enrichment call.
        List<Site> sites;
        try
        {
            sites = await db.Sites.AsNoTracking().Where(x => x.Active).ToListAsync(ct);
        }
        catch (Exception ex) when (SchemaUnavailable(ex))
        {
            db.ChangeTracker.Clear();
            return Fallback(rawCollection, rawCollectionAddress, rawDelivery,
                            rawDeliveryAddress, rawMapLink, rawDriverInstructions);
        }

        // ── 2. Match collection and delivery against Sites ───────────────────
        var collection = Match(sites, rawCollection);
        var delivery   = Match(sites, rawDelivery);

        var collectionName    = DisplayName(collection) ?? rawCollection;
        var deliveryName      = DisplayName(delivery)   ?? rawDelivery;
        var collectionAddress = collection?.CollectionAddress ?? rawCollectionAddress;
        var deliveryAddress   = delivery?.CollectionAddress   ?? rawDeliveryAddress;
        var deliveryMapLink   = delivery?.MapLink             ?? rawMapLink;

        // ── 3. MarketContact enrichment for market orders ─────────────────────
        // When the email parser gives us a seller name but no stall/stand number,
        // look up the MarketContact by seller name to fill in StallNumber and
        // Salesman from master data. This is the fix for market orders arriving
        // with missing SellerName / StallNumber.
        string? marketSellerName = null;
        string? marketStallNumber = null;
        string? marketSalesman    = null;

        if (!string.IsNullOrWhiteSpace(rawSellerName) || !string.IsNullOrWhiteSpace(rawCollection))
        {
            var searchName = rawSellerName ?? rawCollection;
            try
            {
                var contacts = await db.MarketContacts.AsNoTracking()
                    .Where(c => c.Active)
                    .ToListAsync(ct);

                // Match by seller name (normalised) — market name used as secondary filter
                var contact = contacts.FirstOrDefault(c =>
                    NormalisedEquals(c.Name, searchName) &&
                    (string.IsNullOrWhiteSpace(marketName) ||
                     NormalisedEquals(c.Market, marketName)));

                // Looser match if tight match fails: name contains
                contact ??= contacts.FirstOrDefault(c =>
                    !string.IsNullOrWhiteSpace(searchName) &&
                    (Normalise(c.Name).Contains(Normalise(searchName), StringComparison.Ordinal) ||
                     Normalise(searchName!).Contains(Normalise(c.Name), StringComparison.Ordinal)));

                if (contact is not null)
                {
                    marketSellerName  = contact.Name;
                    marketStallNumber = contact.StandOrLocation;
                    marketSalesman    = contact.Salesman;
                }
            }
            catch (Exception ex) when (SchemaUnavailable(ex))
            {
                db.ChangeTracker.Clear();
            }
        }

        // ── 4. Build enriched driver instructions ─────────────────────────────
        var instructions = rawDriverInstructions;
        instructions = UpsertTag(instructions, "Collection site",    collectionName);
        instructions = UpsertTag(instructions, "Collection address", collectionAddress);
        instructions = UpsertTag(instructions, "Depot",              deliveryName);
        instructions = UpsertTag(instructions, "Delivery address",   deliveryAddress);
        if (!string.IsNullOrWhiteSpace(marketStallNumber))
            instructions = UpsertTag(instructions, "Stand/Stall", marketStallNumber);
        if (!string.IsNullOrWhiteSpace(marketSalesman))
            instructions = UpsertTag(instructions, "Salesman", marketSalesman);

        return new Alignment(
            collectionName, collectionAddress,
            deliveryName,   deliveryAddress,
            deliveryMapLink, instructions,
            marketSellerName, marketStallNumber, marketSalesman);
    }

    // ── Site matching ─────────────────────────────────────────────────────────

    private static Site? Match(IEnumerable<Site> sites, string? value)
    {
        var key = Normalise(value);
        if (string.IsNullOrWhiteSpace(key)) return null;

        // Exact match first (ExternalCode, Name, DriverTextName, Aliases)
        return sites.FirstOrDefault(site => Candidates(site).Any(c => Normalise(c) == key))
            // Substring / containment match as fallback (min 5 chars to avoid false positives)
            ?? sites.FirstOrDefault(site => Candidates(site).Any(c =>
            {
                var ck = Normalise(c);
                return ck.Length >= 5 && (key.Contains(ck, StringComparison.Ordinal) || ck.Contains(key, StringComparison.Ordinal));
            }));
    }

    private static IEnumerable<string?> Candidates(Site site)
    {
        yield return site.ExternalCode;
        yield return site.Name;
        yield return site.DriverTextName;
        // Aliases is now a real persisted column — always available
        foreach (var alias in (site.Aliases ?? string.Empty)
            .Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return alias;
    }

    private static string? DisplayName(Site? site) => site is null ? null :
        !string.IsNullOrWhiteSpace(site.DriverTextName) ? site.DriverTextName.Trim() : site.Name.Trim();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string? UpsertTag(string? notes, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return notes;
        var parts  = (notes ?? string.Empty).Split('·', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var prefix = $"{label}:";
        var index  = parts.FindIndex(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        var tagged = $"{label}: {value.Trim()}";
        if (index >= 0) parts[index] = tagged;
        else            parts.Add(tagged);
        return string.Join(" · ", parts);
    }

    private static Alignment Fallback(
        string? collection, string? collectionAddress,
        string? delivery,   string? deliveryAddress,
        string? mapLink,    string? instructions) =>
        new(collection, collectionAddress, delivery, deliveryAddress, mapLink, instructions,
            null, null, null);

    private static bool NormalisedEquals(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) &&
        Normalise(a) == Normalise(b);

    private static string Normalise(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static string? Text(JsonElement payload, string name)
    {
        foreach (var property in payload.EnumerateObject())
        {
            if (Normalise(property.Name) != Normalise(name)) continue;
            return property.Value.ValueKind switch
            {
                JsonValueKind.String => string.IsNullOrWhiteSpace(property.Value.GetString()) ? null : property.Value.GetString()!.Trim(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => property.Value.ToString(),
                _ => null
            };
        }
        return null;
    }

    private static bool SchemaUnavailable(Exception ex)
    {
        var msg = ex.GetBaseException().Message;
        return msg.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("Cannot find the object", StringComparison.OrdinalIgnoreCase);
    }
}
