using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public static class OrderSiteMasterAlignment
{
    public sealed record Alignment(
        string? CollectionName,
        string? CollectionAddress,
        string? DeliveryName,
        string? DeliveryAddress,
        string? DeliveryMapLink,
        string? DriverInstructions);

    public static async Task<Alignment> ResolveAsync(TmsDbContext db, JsonElement payload, CancellationToken ct)
    {
        var rawCollection = Text(payload, "collectionSite") ?? Text(payload, "collectionLocation") ?? Text(payload, "sellerName");
        var rawDelivery = Text(payload, "deliverySite") ?? Text(payload, "deliveryLocation") ?? Text(payload, "stallNumber") ?? Text(payload, "destination");
        return await ResolveNamesAsync(
            db,
            rawCollection,
            rawDelivery,
            Text(payload, "collectionAddress"),
            Text(payload, "deliveryAddress"),
            Text(payload, "mapLink"),
            Text(payload, "driverInstructions"),
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
        CancellationToken ct)
    {
        List<Site> sites;
        try
        {
            sites = await db.Sites.AsNoTracking().Where(x => x.Active).ToListAsync(ct);
            await MasterDetailStore.EnrichSitesAsync(db, sites, ct);
        }
        catch (Exception ex) when (SchemaUnavailable(ex))
        {
            db.ChangeTracker.Clear();
            return new Alignment(rawCollection, rawCollectionAddress, rawDelivery, rawDeliveryAddress, rawMapLink, rawDriverInstructions);
        }

        var collection = Match(sites, rawCollection);
        var delivery = Match(sites, rawDelivery);
        var collectionName = DisplayName(collection) ?? rawCollection;
        var deliveryName = DisplayName(delivery) ?? rawDelivery;
        var collectionAddress = collection?.CollectionAddress ?? rawCollectionAddress;
        var deliveryAddress = delivery?.CollectionAddress ?? rawDeliveryAddress;
        var deliveryMapLink = delivery?.MapLink ?? rawMapLink;

        var instructions = rawDriverInstructions;
        instructions = UpsertTag(instructions, "Collection site", collectionName);
        instructions = UpsertTag(instructions, "Collection address", collectionAddress);
        instructions = UpsertTag(instructions, "Depot", deliveryName);
        instructions = UpsertTag(instructions, "Delivery address", deliveryAddress);

        return new Alignment(collectionName, collectionAddress, deliveryName, deliveryAddress, deliveryMapLink, instructions);
    }

    private static Site? Match(IEnumerable<Site> sites, string? value)
    {
        var key = Normalise(value);
        if (string.IsNullOrWhiteSpace(key)) return null;
        return sites.FirstOrDefault(site => Candidates(site).Any(candidate => Normalise(candidate) == key))
            ?? sites.FirstOrDefault(site => Candidates(site).Any(candidate =>
            {
                var candidateKey = Normalise(candidate);
                return candidateKey.Length >= 5 && (key.Contains(candidateKey, StringComparison.Ordinal) || candidateKey.Contains(key, StringComparison.Ordinal));
            }));
    }

    private static IEnumerable<string?> Candidates(Site site)
    {
        yield return site.ExternalCode;
        yield return site.Name;
        yield return site.DriverTextName;
        foreach (var alias in (site.Aliases ?? string.Empty).Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return alias;
    }

    private static string? DisplayName(Site? site) => site is null ? null :
        !string.IsNullOrWhiteSpace(site.DriverTextName) ? site.DriverTextName.Trim() : site.Name.Trim();

    private static string? UpsertTag(string? notes, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return notes;
        var parts = (notes ?? string.Empty).Split('·', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var prefix = $"{label}:";
        var index = parts.FindIndex(part => part.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        var tagged = $"{label}: {value.Trim()}";
        if (index >= 0) parts[index] = tagged;
        else parts.Add(tagged);
        return string.Join(" · ", parts);
    }

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

    private static string Normalise(string? value) => new((value ?? string.Empty)
        .Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static bool SchemaUnavailable(Exception ex)
    {
        var message = ex.GetBaseException().Message;
        return message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Cannot find the object", StringComparison.OrdinalIgnoreCase);
    }
}
