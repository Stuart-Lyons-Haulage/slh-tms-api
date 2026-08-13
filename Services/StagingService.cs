using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Contracts;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;
public sealed class StagingService(TmsDbContext db)
{
    private static readonly HashSet<string> Types = new(StringComparer.OrdinalIgnoreCase) { "customer", "customercontact", "vehicle", "driver", "trailer", "site", "marketcontact", "fuelprice", "order" };
    public StagedImport Create(StageImportRequest r)
    {
        if (!Types.Contains(r.EntityType)) throw new ArgumentException("Unsupported entityType");
        return new StagedImport { EntityType = r.EntityType.ToLowerInvariant(), IdempotencyKey = r.IdempotencyKey, PayloadJson = r.Payload.GetRawText(), Source = r.Source };
    }
    public StageImportResponse ToResponse(StagedImport x, HttpRequest request) => new(x.Id, x.Status.ToString(), x.ReceivedAtUtc, $"{request.Scheme}://{request.Host}/api/v1/staging/{x.Id}");
    public async Task PromoteDirect(string entityType, JsonElement payload, CancellationToken ct)
    {
        var item = new StagedImport { EntityType = entityType.ToLowerInvariant(), IdempotencyKey = $"direct:{Guid.NewGuid():N}", PayloadJson = payload.GetRawText(), Source = "Direct master-data apply" };
        await Promote(item, ct);
    }

    public void ClearTrackedChanges() => db.ChangeTracker.Clear();

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
            case "fuelprice": await PromoteFuelPrice(payload, ct); break;
            case "order": await PromoteOrder(payload, ct); break;
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task PromoteCustomer(JsonElement payload, CancellationToken ct)
    {
        var code = ClipRequired(Required(payload, "code"), 40); var name = ClipRequired(Required(payload, "name"), 200);
        var customer = await db.Customers.SingleOrDefaultAsync(item => item.Code == code, ct);
        if (customer is null) db.Customers.Add(new Customer { Code = code, Name = name, Active = Bool(payload, "active", true) });
        else { customer.Name = name; customer.Active = Bool(payload, "active", true); }
    }

    private async Task PromoteCustomerContact(JsonElement payload, CancellationToken ct)
    {
        var customerCode = ClipRequired(Required(payload, "customerCode"), 40); var name = ClipRequired(Required(payload, "name"), 200);
        var customerName = Clip(Text(payload, "customerName") ?? Text(payload, "customer") ?? customerCode, 200) ?? customerCode;
        var customer = await db.Customers.SingleOrDefaultAsync(item => item.Code == customerCode, ct);
        if (customer is null) db.Customers.Add(new Customer { Code = customerCode, Name = customerName, Active = true });
        else if (customer.Name == customer.Code && !string.Equals(customerName, customerCode, StringComparison.OrdinalIgnoreCase)) customer.Name = customerName;
        var contact = await db.CustomerContacts.SingleOrDefaultAsync(item => item.CustomerCode == customerCode && item.Name == name, ct);
        if (contact is null) db.CustomerContacts.Add(new CustomerContact { CustomerCode = customerCode, Name = name, Email = Clip(Text(payload, "email"), 320), MobileNumber = Clip(Text(payload, "mobileNumber"), 40), ReceivesEtaUpdates = Bool(payload, "receivesEtaUpdates", true), Active = Bool(payload, "active", true) });
        else { contact.Email = Clip(Text(payload, "email"), 320); contact.MobileNumber = Clip(Text(payload, "mobileNumber"), 40); contact.ReceivesEtaUpdates = Bool(payload, "receivesEtaUpdates", true); contact.Active = Bool(payload, "active", true); }
    }

    private async Task PromoteVehicle(JsonElement payload, CancellationToken ct)
    {
        var registration = ClipRequired(Required(payload, "registration").Replace(" ", "").ToUpperInvariant(), 20);
        var vehicle = await db.Vehicles.SingleOrDefaultAsync(item => item.Registration == registration, ct);
        if (vehicle is null)
        {
            vehicle = new Vehicle { Registration = registration };
            db.Vehicles.Add(vehicle);
        }
        vehicle.FleetNumber = Clip(Text(payload, "fleetNumber"), 40);
        vehicle.Abbreviation = Clip(Text(payload, "abbreviation"), 20);
        vehicle.Transmission = Clip(Text(payload, "transmission"), 20);
        vehicle.DvsCompliant = BoolOrNull(payload, "dvsCompliant");
        vehicle.CabMobile = Clip(Text(payload, "cabMobile") ?? Text(payload, "cabPhone") ?? Text(payload, "cabPhoneNumber"), 40);
        vehicle.FuelPin = Clip(Text(payload, "fuelPin"), 80);
        vehicle.ShellCard = Clip(Text(payload, "shellCard"), 80);
        vehicle.BpRedCard = Clip(Text(payload, "bpRedCard"), 80);
        vehicle.BpPlainCard = Clip(Text(payload, "bpPlainCard"), 80);
        vehicle.Notes = Clip(Text(payload, "notes"), 500);
        vehicle.FuelProvider = Clip(Text(payload, "fuelProvider"), 30);
        vehicle.FuelPinSecretName = Clip(Text(payload, "fuelPinSecretName"), 120);
        vehicle.FuelCardLastFour = Clip(Text(payload, "fuelCardLastFour"), 4);
        vehicle.Active = Bool(payload, "active", true);
    }

    private async Task PromoteDriver(JsonElement payload, CancellationToken ct)
    {
        var employeeNumber = Text(payload, "employeeNumber") ?? Text(payload, "driverId") ?? Text(payload, "driverID") ?? Text(payload, "DriverID") ?? Text(payload, "employeeNo") ?? Text(payload, "payrollNumber");
        var displayName = Text(payload, "displayName") ?? Text(payload, "driver") ?? Text(payload, "Driver") ?? Text(payload, "name") ?? Text(payload, "driverName");
        if (string.IsNullOrWhiteSpace(employeeNumber) && !string.IsNullOrWhiteSpace(displayName)) employeeNumber = displayName.Trim().ToUpperInvariant().Replace(" ", "-");
        if (string.IsNullOrWhiteSpace(employeeNumber) || string.IsNullOrWhiteSpace(displayName)) throw new JsonException("Driver payload requires employeeNumber and displayName.");
        employeeNumber = ClipRequired(employeeNumber, 40);
        displayName = ClipRequired(displayName, 160);
        var tachoName = Clip(Text(payload, "tachoName"), 160);
        var mobileNumber = Clip(Text(payload, "mobileNumber"), 40);
        var driverType = Clip(Text(payload, "driverType"), 80);
        var driverGroup = Clip(Text(payload, "driverGroup"), 80);
        var skills = Clip(Text(payload, "skills"), 160);
        var active = Bool(payload, "active", true);

        var driver = await db.Drivers.SingleOrDefaultAsync(item => item.EmployeeNumber == employeeNumber, ct);
        if (driver is null)
        {
            db.Drivers.Add(new Driver
            {
                EmployeeNumber = employeeNumber,
                DisplayName = displayName,
                TachoName = tachoName,
                MobileNumber = mobileNumber,
                DriverType = driverType,
                DriverGroup = driverGroup,
                Skills = skills,
                Active = active
            });
        }
        else
        {
            driver.DisplayName = displayName;
            driver.TachoName = tachoName;
            driver.MobileNumber = mobileNumber;
            driver.DriverType = driverType;
            driver.DriverGroup = driverGroup;
            driver.Skills = skills;
            driver.Active = active;
        }
    }

    private async Task PromoteTrailer(JsonElement payload, CancellationToken ct)
    {
        var trailerNumber = ClipRequired(Required(payload, "trailerNumber"), 40);
        var trailer = await db.Trailers.SingleOrDefaultAsync(item => item.TrailerNumber == trailerNumber, ct);
        if (trailer is null) db.Trailers.Add(new Trailer { TrailerNumber = trailerNumber, Type = Clip(Text(payload, "type"), 80), StandardCapacity = IntOrNull(payload, "standardCapacity"), EuroCapacity = IntOrNull(payload, "euroCapacity"), Active = Bool(payload, "active", true) });
        else { trailer.Type = Clip(Text(payload, "type"), 80); trailer.StandardCapacity = IntOrNull(payload, "standardCapacity"); trailer.EuroCapacity = IntOrNull(payload, "euroCapacity"); trailer.Active = Bool(payload, "active", true); }
    }

    private async Task PromoteSite(JsonElement payload, CancellationToken ct)
    {
        var externalCode = ClipRequired(Required(payload, "externalCode"), 40); var name = ClipRequired(Required(payload, "name"), 200);
        var site = await db.Sites.SingleOrDefaultAsync(item => item.ExternalCode == externalCode, ct);
        if (site is null) db.Sites.Add(new Site { ExternalCode = externalCode, Name = name, DriverTextName = Clip(Text(payload, "driverTextName"), 200), CollectionAddress = Clip(Text(payload, "collectionAddress"), 500), CollectionInstructions = Clip(Text(payload, "collectionInstructions"), 1000), MapLink = Clip(Text(payload, "mapLink"), 1000), Active = Bool(payload, "active", true) });
        else { site.Name = name; site.DriverTextName = Clip(Text(payload, "driverTextName"), 200); site.CollectionAddress = Clip(Text(payload, "collectionAddress"), 500); site.CollectionInstructions = Clip(Text(payload, "collectionInstructions"), 1000); site.MapLink = Clip(Text(payload, "mapLink"), 1000); site.Active = Bool(payload, "active", true); }
    }

    private async Task PromoteMarketContact(JsonElement payload, CancellationToken ct)
    {
        var market = ClipRequired(Text(payload, "market") ?? Text(payload, "marketName") ?? "General", 80);
        var name = Clip(Text(payload, "name") ?? Text(payload, "contactName") ?? Text(payload, "sellerName"), 200);
        if (string.IsNullOrWhiteSpace(name)) throw new JsonException("Market contact payload requires name.");
        var contact = await db.MarketContacts.SingleOrDefaultAsync(item => item.Market == market && item.Name == name, ct);
        var standOrLocation = Clip(Text(payload, "standOrLocation") ?? Text(payload, "stallNumber"), 200);
        var salesman = Clip(Text(payload, "salesman"), 200);
        var sender = Clip(Text(payload, "sender"), 200);
        if (contact is null) db.MarketContacts.Add(new MarketContact { Market = market, Name = name, StandOrLocation = standOrLocation, Salesman = salesman, Sender = sender, Active = Bool(payload, "active", true) });
        else { contact.StandOrLocation = standOrLocation; contact.Salesman = salesman; contact.Sender = sender; contact.Active = Bool(payload, "active", true); }
    }

    private async Task PromoteFuelPrice(JsonElement payload, CancellationToken ct)
    {
        var provider = ClipRequired(Required(payload, "provider"), 120);
        if (!DateOnly.TryParse(Required(payload, "weekCommencing"), out var weekCommencing)) throw new JsonException("Fuel price payload requires a valid weekCommencing.");
        if (!decimal.TryParse(Required(payload, "pricePencePerLitre"), out var pricePencePerLitre)) throw new JsonException("Fuel price payload requires a valid pricePencePerLitre.");
        var fuelPrice = await db.FuelPrices.SingleOrDefaultAsync(item => item.Provider == provider && item.WeekCommencing == weekCommencing, ct);
        if (fuelPrice is null) db.FuelPrices.Add(new FuelPrice { Provider = provider, WeekCommencing = weekCommencing, PricePencePerLitre = pricePencePerLitre, IsPricingMaximum = Bool(payload, "isPricingMaximum", false), Source = Clip(Text(payload, "source"), 200), Notes = Clip(Text(payload, "notes"), 500) });
        else { fuelPrice.PricePencePerLitre = pricePencePerLitre; fuelPrice.IsPricingMaximum = Bool(payload, "isPricingMaximum", false); fuelPrice.Source = Clip(Text(payload, "source"), 200); fuelPrice.Notes = Clip(Text(payload, "notes"), 500); }
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
            db.TransportOrders.Add(new TransportOrder { Reference = ClipRequired(reference, 80), CustomerCode = ClipRequired(customerCode, 40), CollectionDate = collectionDate, DeliveryDate = deliveryDate, DeliveryWindowStartUtc = deliveryWindowStartUtc, DeliveryWindowEndUtc = deliveryWindowEndUtc, Pallets = IntOrNull(payload, "pallets"), SellerName = Clip(Text(payload, "sellerName"), 200), MarketName = Clip(Text(payload, "marketName"), 80), StallNumber = Clip(Text(payload, "stallNumber"), 200), DriverInstructions = Clip(Text(payload, "driverInstructions"), 1000), MapLink = Clip(Text(payload, "mapLink"), 1000) });
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
    private static string? Clip(string? value, int maxLength) => string.IsNullOrWhiteSpace(value) ? null : value.Length <= maxLength ? value : value[..maxLength];
    private static string ClipRequired(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];
}
