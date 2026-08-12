using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Contracts;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;
public sealed class StagingService(TmsDbContext db)
{
    private static readonly HashSet<string> Types = new(StringComparer.OrdinalIgnoreCase) { "customer", "customercontact", "vehicle", "driver", "trailer", "site", "marketcontact", "order" };
    public StagedImport Create(StageImportRequest r)
    {
        if (!Types.Contains(r.EntityType)) throw new ArgumentException("Unsupported entityType");
        return new StagedImport { EntityType = r.EntityType.ToLowerInvariant(), IdempotencyKey = r.IdempotencyKey, PayloadJson = r.Payload.GetRawText(), Source = r.Source };
    }
    public StageImportResponse ToResponse(StagedImport x, HttpRequest request) => new(x.Id, x.Status.ToString(), x.ReceivedAtUtc, $"{request.Scheme}://{request.Host}/api/v1/staging/{x.Id}");
    public async Task<StagedImport> ReviewAndPromote(Guid id, bool approve, string? note, ClaimsPrincipal user, CancellationToken ct)
    {
        var item = await db.StagedImports.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("Staged item not found");
        if (item.Status != StagingStatus.PendingReview) throw new InvalidOperationException("Only PendingReview items can be reviewed");
        item.ReviewedAtUtc = DateTimeOffset.UtcNow; item.ReviewedBy = user.Identity?.Name ?? user.FindFirstValue("oid"); item.ReviewNote = note;
        if (!approve) item.Status = StagingStatus.Rejected;
        else
        {
            item.Status = StagingStatus.Approved;
            try
            {
                await Promote(item, ct);
                item.Status = StagingStatus.Promoted;
            }
            catch (Exception ex) when (ex is JsonException or DbUpdateException or InvalidOperationException)
            {
                item.Status = StagingStatus.Failed;
                item.ReviewNote = string.Join(" | ", new[] { note, $"Promotion failed: {ex.GetBaseException().Message}" }.Where(value => !string.IsNullOrWhiteSpace(value)));
                await db.SaveChangesAsync(ct);
                throw new InvalidOperationException($"Staged {item.EntityType} record could not be promoted: {ex.GetBaseException().Message}", ex);
            }
        }
        await db.SaveChangesAsync(ct); return item;
    }
    private async Task Promote(StagedImport item, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(item.PayloadJson);
        var payload = document.RootElement;
        switch (item.EntityType)
        {
            case "customer": await PromoteCustomer(payload, ct); break;
            case "customercontact": await PromoteCustomerContact(payload, ct); break;
            case "vehicle": await PromoteVehicle(payload, ct); break;
            case "driver": await PromoteDriver(payload, ct); break;
            case "trailer": await PromoteTrailer(payload, ct); break;
            case "site": await PromoteSite(payload, ct); break;
            case "marketcontact": await PromoteMarketContact(payload, ct); break;
            case "order": await PromoteOrder(payload, ct); break;
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task PromoteCustomer(JsonElement payload, CancellationToken ct)
    {
        var code = Required(payload, "code"); var name = Required(payload, "name");
        var customer = await db.Customers.SingleOrDefaultAsync(item => item.Code == code, ct);
        if (customer is null) db.Customers.Add(new Customer { Code = code, Name = name, Active = Bool(payload, "active", true) });
        else { customer.Name = name; customer.Active = Bool(payload, "active", true); }
    }

    private async Task PromoteCustomerContact(JsonElement payload, CancellationToken ct)
    {
        var customerCode = Required(payload, "customerCode"); var name = Required(payload, "name");
        var contact = await db.CustomerContacts.SingleOrDefaultAsync(item => item.CustomerCode == customerCode && item.Name == name, ct);
        if (contact is null) db.CustomerContacts.Add(new CustomerContact { CustomerCode = customerCode, Name = name, Email = Text(payload, "email"), MobileNumber = Text(payload, "mobileNumber"), ReceivesEtaUpdates = Bool(payload, "receivesEtaUpdates", true), Active = Bool(payload, "active", true) });
        else { contact.Email = Text(payload, "email"); contact.MobileNumber = Text(payload, "mobileNumber"); contact.ReceivesEtaUpdates = Bool(payload, "receivesEtaUpdates", true); contact.Active = Bool(payload, "active", true); }
    }

    private async Task PromoteVehicle(JsonElement payload, CancellationToken ct)
    {
        var registration = Required(payload, "registration").Replace(" ", "").ToUpperInvariant();
        var vehicle = await db.Vehicles.SingleOrDefaultAsync(item => item.Registration == registration, ct);
        if (vehicle is null) db.Vehicles.Add(new Vehicle { Registration = registration, FleetNumber = Text(payload, "fleetNumber"), Abbreviation = Text(payload, "abbreviation"), Transmission = Text(payload, "transmission"), DvsCompliant = BoolOrNull(payload, "dvsCompliant"), FuelProvider = Text(payload, "fuelProvider"), FuelPinSecretName = Text(payload, "fuelPinSecretName"), FuelCardLastFour = Text(payload, "fuelCardLastFour"), Active = Bool(payload, "active", true) });
        else { vehicle.FleetNumber = Text(payload, "fleetNumber"); vehicle.Abbreviation = Text(payload, "abbreviation"); vehicle.Transmission = Text(payload, "transmission"); vehicle.DvsCompliant = BoolOrNull(payload, "dvsCompliant"); vehicle.FuelProvider = Text(payload, "fuelProvider"); vehicle.FuelPinSecretName = Text(payload, "fuelPinSecretName"); vehicle.FuelCardLastFour = Text(payload, "fuelCardLastFour"); vehicle.Active = Bool(payload, "active", true); }
    }

    private async Task PromoteDriver(JsonElement payload, CancellationToken ct)
    {
        var employeeNumber = Text(payload, "employeeNumber") ?? Text(payload, "driverId") ?? Text(payload, "driverID") ?? Text(payload, "DriverID");
        var displayName = Text(payload, "displayName") ?? Text(payload, "driver") ?? Text(payload, "Driver") ?? Text(payload, "name");
        if (string.IsNullOrWhiteSpace(employeeNumber) && !string.IsNullOrWhiteSpace(displayName)) employeeNumber = displayName.Trim().ToUpperInvariant().Replace(" ", "-");
        if (string.IsNullOrWhiteSpace(employeeNumber) || string.IsNullOrWhiteSpace(displayName)) throw new JsonException("Driver payload requires employeeNumber and displayName.");
        var driver = await db.Drivers.SingleOrDefaultAsync(item => item.EmployeeNumber == employeeNumber, ct);
        if (driver is null) db.Drivers.Add(new Driver { EmployeeNumber = employeeNumber, DisplayName = displayName, TachoName = Text(payload, "tachoName"), MobileNumber = Text(payload, "mobileNumber"), DriverType = Text(payload, "driverType"), DriverGroup = Text(payload, "driverGroup"), Skills = Text(payload, "skills"), Active = Bool(payload, "active", true) });
        else { driver.DisplayName = displayName; driver.TachoName = Text(payload, "tachoName"); driver.MobileNumber = Text(payload, "mobileNumber"); driver.DriverType = Text(payload, "driverType"); driver.DriverGroup = Text(payload, "driverGroup"); driver.Skills = Text(payload, "skills"); driver.Active = Bool(payload, "active", true); }
    }

    private async Task PromoteTrailer(JsonElement payload, CancellationToken ct)
    {
        var trailerNumber = Required(payload, "trailerNumber");
        var trailer = await db.Trailers.SingleOrDefaultAsync(item => item.TrailerNumber == trailerNumber, ct);
        if (trailer is null) db.Trailers.Add(new Trailer { TrailerNumber = trailerNumber, Type = Text(payload, "type"), StandardCapacity = IntOrNull(payload, "standardCapacity"), EuroCapacity = IntOrNull(payload, "euroCapacity"), Active = Bool(payload, "active", true) });
        else { trailer.Type = Text(payload, "type"); trailer.StandardCapacity = IntOrNull(payload, "standardCapacity"); trailer.EuroCapacity = IntOrNull(payload, "euroCapacity"); trailer.Active = Bool(payload, "active", true); }
    }

    private async Task PromoteSite(JsonElement payload, CancellationToken ct)
    {
        var externalCode = Required(payload, "externalCode"); var name = Required(payload, "name");
        var site = await db.Sites.SingleOrDefaultAsync(item => item.ExternalCode == externalCode, ct);
        if (site is null) db.Sites.Add(new Site { ExternalCode = externalCode, Name = name, DriverTextName = Text(payload, "driverTextName"), CollectionAddress = Text(payload, "collectionAddress"), CollectionInstructions = Text(payload, "collectionInstructions"), MapLink = Text(payload, "mapLink"), Active = Bool(payload, "active", true) });
        else { site.Name = name; site.DriverTextName = Text(payload, "driverTextName"); site.CollectionAddress = Text(payload, "collectionAddress"); site.CollectionInstructions = Text(payload, "collectionInstructions"); site.MapLink = Text(payload, "mapLink"); site.Active = Bool(payload, "active", true); }
    }

    private async Task PromoteMarketContact(JsonElement payload, CancellationToken ct)
    {
        var market = Text(payload, "market") ?? Text(payload, "marketName") ?? "General";
        var name = Text(payload, "name") ?? Text(payload, "contactName") ?? Text(payload, "sellerName");
        if (string.IsNullOrWhiteSpace(name)) throw new JsonException("Market contact payload requires name.");
        var contact = await db.MarketContacts.SingleOrDefaultAsync(item => item.Market == market && item.Name == name, ct);
        if (contact is null) db.MarketContacts.Add(new MarketContact { Market = market, Name = name, StandOrLocation = Text(payload, "standOrLocation") ?? Text(payload, "stallNumber"), Active = Bool(payload, "active", true) });
        else { contact.StandOrLocation = Text(payload, "standOrLocation") ?? Text(payload, "stallNumber"); contact.Active = Bool(payload, "active", true); }
    }

    private async Task PromoteOrder(JsonElement payload, CancellationToken ct)
    {
        var reference = Required(payload, "poNumber"); var customerCode = Required(payload, "customerCode"); var collectionDateText = Required(payload, "collectionDate");
        if (!DateOnly.TryParse(collectionDateText, out var collectionDate)) throw new JsonException("Order payload requires a valid collectionDate.");
        if (!await db.TransportOrders.AnyAsync(order => order.Reference == reference, ct))
        {
            DateOnly? deliveryDate = null;
            if (DateOnly.TryParse(Text(payload, "deliveryDate"), out var parsedDelivery)) deliveryDate = parsedDelivery;
            DateTimeOffset? deliveryWindowStartUtc = null;
            if (DateTimeOffset.TryParse(Text(payload, "deliveryWindowStartUtc"), out var parsedWindowStart)) deliveryWindowStartUtc = parsedWindowStart;
            DateTimeOffset? deliveryWindowEndUtc = null;
            if (DateTimeOffset.TryParse(Text(payload, "deliveryWindowEndUtc"), out var parsedWindowEnd)) deliveryWindowEndUtc = parsedWindowEnd;
            db.TransportOrders.Add(new TransportOrder { Reference = reference, CustomerCode = customerCode, CollectionDate = collectionDate, DeliveryDate = deliveryDate, DeliveryWindowStartUtc = deliveryWindowStartUtc, DeliveryWindowEndUtc = deliveryWindowEndUtc, Pallets = IntOrNull(payload, "pallets"), SellerName = Text(payload, "sellerName"), MarketName = Text(payload, "marketName"), StallNumber = Text(payload, "stallNumber"), DriverInstructions = Text(payload, "driverInstructions"), MapLink = Text(payload, "mapLink") });
        }
    }

    private static string Required(JsonElement payload, string name) => Text(payload, name) ?? throw new JsonException($"Payload requires {name}.");
    private static string? Text(JsonElement payload, string name)
    {
        if (!TryGetProperty(payload, name, out var value)) return null;
        return value.ValueKind switch { JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString()!.Trim(), JsonValueKind.Number => value.GetRawText(), JsonValueKind.True => "true", JsonValueKind.False => "false", _ => null };
    }
    private static bool TryGetProperty(JsonElement payload, string name, out JsonElement value)
    {
        if (payload.TryGetProperty(name, out value)) return true;
        foreach (var property in payload.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) || NormaliseKey(property.Name) == NormaliseKey(name))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }
    private static string NormaliseKey(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private static int? IntOrNull(JsonElement payload, string name) => int.TryParse(Text(payload, name), out var value) ? value : null;
    private static bool Bool(JsonElement payload, string name, bool fallback) => bool.TryParse(Text(payload, name), out var value) ? value : fallback;
    private static bool? BoolOrNull(JsonElement payload, string name) => bool.TryParse(Text(payload, name), out var value) ? value : null;
}
