using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.V2.Api.Data;
using Slh.Tms.V2.Api.Domain;

namespace Slh.Tms.V2.Api.Application;

public sealed class MasterDataCrudService(
    MasterDataDbContext master,
    OperationsDbContext operations)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<object?> UpdateAsync(
        string entity,
        Guid id,
        JsonElement patch,
        CancellationToken ct)
    {
        var record = await FindAsync(entity, id, ct);
        if (record is null)
            return null;

        ApplyPatch(record, patch);

        try
        {
            await master.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            throw new InvalidOperationException(
                "The change could not be saved because it conflicts with another Master Data record or relationship.",
                ex);
        }

        return record;
    }

    public async Task<bool> DeleteAsync(
        string entity,
        Guid id,
        CancellationToken ct)
    {
        var record = await FindAsync(entity, id, ct);
        if (record is null)
            return false;

        var blocker = await GetDeleteBlockerAsync(entity, id, ct);
        if (!string.IsNullOrWhiteSpace(blocker))
            throw new InvalidOperationException(blocker);

        if (record is Site)
        {
            var aliases = await master.SiteAliases.Where(x => x.SiteId == id).ToListAsync(ct);
            var identities = await master.ExternalIdentities
                .Where(x => x.EntityType == "Site" && x.EntityId == id)
                .ToListAsync(ct);
            master.SiteAliases.RemoveRange(aliases);
            master.ExternalIdentities.RemoveRange(identities);
        }
        else
        {
            var entityType = record.GetType().Name;
            var identities = await master.ExternalIdentities
                .Where(x => x.EntityType == entityType && x.EntityId == id)
                .ToListAsync(ct);
            if (identities.Count > 0)
                master.ExternalIdentities.RemoveRange(identities);
        }

        master.Remove(record);

        try
        {
            await master.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            throw new InvalidOperationException(
                "This record is still referenced elsewhere and cannot be permanently deleted. Correct or remove the linked record first.",
                ex);
        }

        return true;
    }

    private async Task<object?> FindAsync(string entity, Guid id, CancellationToken ct) =>
        CanonicalEntity(entity) switch
        {
            "customers" => await master.Customers.SingleOrDefaultAsync(x => x.Id == id, ct),
            "sites" => await master.Sites.SingleOrDefaultAsync(x => x.Id == id, ct),
            "drivers" => await master.Drivers.SingleOrDefaultAsync(x => x.Id == id, ct),
            "vehicles" => await master.Vehicles.SingleOrDefaultAsync(x => x.Id == id, ct),
            "trailers" => await master.Trailers.SingleOrDefaultAsync(x => x.Id == id, ct),
            "markets" => await master.Markets.SingleOrDefaultAsync(x => x.Id == id, ct),
            "customer-contacts" => await master.CustomerContacts.SingleOrDefaultAsync(x => x.Id == id, ct),
            "market-contacts" => await master.MarketContacts.SingleOrDefaultAsync(x => x.Id == id, ct),
            "site-cutoffs" => await master.SiteCutoffs.SingleOrDefaultAsync(x => x.Id == id, ct),
            "route-times" => await master.RouteTimings.SingleOrDefaultAsync(x => x.Id == id, ct),
            "fuel-prices" => await master.FuelPrices.SingleOrDefaultAsync(x => x.Id == id, ct),
            _ => throw new InvalidOperationException($"Master Data entity '{entity}' is not editable.")
        };

    private async Task<string?> GetDeleteBlockerAsync(string entity, Guid id, CancellationToken ct)
    {
        switch (CanonicalEntity(entity))
        {
            case "customers":
                if (await master.Sites.AnyAsync(x => x.CustomerId == id, ct))
                    return "This customer is linked to one or more Sites. Reassign those Sites before deleting the customer.";
                if (await operations.Orders.AnyAsync(x => x.CustomerId == id, ct))
                    return "This customer is referenced by transport orders and cannot be permanently deleted.";
                return null;

            case "sites":
                if (await master.Markets.AnyAsync(x => x.SiteId == id, ct))
                    return "This Site is linked to a Market. Reassign or remove the Market link before deleting the Site.";
                if (await master.SiteCutoffs.AnyAsync(x => x.SiteId == id, ct))
                    return "This Site has Site Cut-offs. Reassign or remove them before deleting the Site.";
                if (await operations.Orders.AnyAsync(x => x.CollectionSiteId == id || x.DeliverySiteId == id, ct))
                    return "This Site is referenced by transport orders and cannot be permanently deleted.";
                return null;

            case "markets":
                if (await operations.Orders.AnyAsync(x => x.MarketId == id, ct))
                    return "This Market is referenced by transport orders and cannot be permanently deleted.";
                return null;

            default:
                return null;
        }
    }

    private static string CanonicalEntity(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "customercontacts" => "customer-contacts",
            "marketcontacts" => "market-contacts",
            "sitecutoffs" => "site-cutoffs",
            "routetimes" => "route-times",
            "fuelprices" => "fuel-prices",
            var normalized => normalized
        };

    private static void ApplyPatch(object record, JsonElement patch)
    {
        if (patch.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Master Data updates must be a JSON object.");

        var properties = record.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanWrite && !string.Equals(property.Name, "Id", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(property => property.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var item in patch.EnumerateObject())
        {
            if (!properties.TryGetValue(item.Name, out var property))
                continue;

            var value = ConvertValue(item.Value, property.PropertyType, property.Name);
            property.SetValue(record, value);
        }
    }

    private static object? ConvertValue(JsonElement json, Type propertyType, string propertyName)
    {
        var nullableType = Nullable.GetUnderlyingType(propertyType);
        var targetType = nullableType ?? propertyType;

        if (json.ValueKind == JsonValueKind.Null)
        {
            if (nullableType is not null || !propertyType.IsValueType)
                return null;

            throw new InvalidOperationException($"{propertyName} cannot be blank.");
        }

        if (targetType == typeof(string))
            return json.ValueKind == JsonValueKind.String ? json.GetString() : json.ToString();

        if (targetType == typeof(DateOnly))
        {
            var value = json.GetString();
            if (DateOnly.TryParse(value, out var parsed))
                return parsed;
            throw new InvalidOperationException($"{propertyName} must be a valid date.");
        }

        if (targetType == typeof(TimeOnly))
        {
            var value = json.GetString();
            if (TimeOnly.TryParse(value, out var parsed))
                return parsed;
            throw new InvalidOperationException($"{propertyName} must be a valid time.");
        }

        if (targetType == typeof(DateTimeOffset))
        {
            var value = json.GetString();
            if (DateTimeOffset.TryParse(value, out var parsed))
                return parsed;
            throw new InvalidOperationException($"{propertyName} must be a valid date/time.");
        }

        try
        {
            return JsonSerializer.Deserialize(json.GetRawText(), targetType, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{propertyName} contains an invalid value.", ex);
        }
    }
}
