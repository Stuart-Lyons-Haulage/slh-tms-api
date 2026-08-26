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
        var detailKey = DetailKey(entityType, payload);
        if (!string.IsNullOrWhiteSpace(detailKey))
            await MasterDetailStore.SaveAsync(db, entityType, detailKey, payload.GetRawText(), "SLH master workbook", null, ct);
    }

    public async Task RegisterFallback(string entityType, JsonElement payload, string? source, CancellationToken ct)
    {
        var registerType = $"register:{entityType.ToLowerInvariant()}";
        db.StagedImports.Add(new StagedImport
        {
            EntityType = registerType,
            IdempotencyKey = $"{registerType}:{Guid.NewGuid():N}",
            PayloadJson = payload.GetRawText(),
            Source = source ?? "Master register fallback",
            Status = StagingStatus.Promoted,
            ReviewedAtUtc = DateTimeOffset.UtcNow,
            ReviewNote = "Stored in the section register because the live SQL table was unavailable."
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<int> LinkRegistered(int batchSize, CancellationToken ct)
    {
        var items = await db.StagedImports
            .AsNoTracking()
            .Where(x => x.EntityType.StartsWith("register:") && x.Status == StagingStatus.Promoted)
            .OrderBy(x => x.ReceivedAtUtc)
            .Take(Math.Clamp(batchSize, 1, 200))
            .ToListAsync(ct);
        var linked = 0;
        foreach (var item in items)
        {
            db.ChangeTracker.Clear();
            try
            {
                var entityType = item.EntityType["register:".Length..];
                await Promote(new StagedImport { EntityType = entityType, IdempotencyKey = item.IdempotencyKey, PayloadJson = item.PayloadJson }, ct);
                db.ChangeTracker.Clear();
                var linkedItem = await db.StagedImports.SingleAsync(x => x.Id == item.Id, ct);
                linkedItem.Status = StagingStatus.Approved;
                linkedItem.ReviewNote = "Linked into the live master table.";
                linkedItem.ReviewedAtUtc = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                linked++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                db.ChangeTracker.Clear();
                var waitingItem = await db.StagedImports.SingleAsync(x => x.Id == item.Id, ct);
                waitingItem.ReviewNote = $"Waiting to link: {ex.GetBaseException().Message}";
                waitingItem.ReviewedAtUtc = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
            }
        }
        return linked;
    }

    public void ClearTrackedChanges() => db.ChangeTracker.Clear();

    public async Task<StagedImport> ReviewAndPromote(Guid id, bool approve, string? note, ClaimsPrincipal user, CancellationToken ct)
    {
        var item = await db.StagedImports.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("Staged item not found");
        if (item.Status != StagingStatus.PendingReview) throw new InvalidOperationException("Only PendingReview items can be reviewed");
        var actor = user.Identity?.Name ?? user.FindFirstValue("oid");
        item.ReviewedAtUtc = DateTimeOffset.UtcNow; item.ReviewedBy = actor; item.ReviewNote = note;
        if (!approve)
        {
            var previous = item.Status;
            item.Status = StagingStatus.Rejected;
            db.StagedImportEvents.Add(StagingAudit.Create(item, "Rejected", previous, note, actor));
        }
        else
        {
            var previous = item.Status;
            item.Status = StagingStatus.Approved;
            db.StagedImportEvents.Add(StagingAudit.Create(item, "Approved", previous, note, actor));
            try
            {
                await Promote(item, ct);
                using var document = JsonDocument.Parse(item.PayloadJson);
                var detailKey = DetailKey(item.EntityType, document.RootElement);
                if (!string.IsNullOrWhiteSpace(detailKey))
                    await MasterDetailStore.SaveAsync(db, item.EntityType, detailKey, item.PayloadJson, "Approved SLH master-data edit", user.Identity?.Name, ct);
                previous = item.Status;
                item.Status = StagingStatus.Promoted;
                db.StagedImportEvents.Add(StagingAudit.Create(item, "Promoted", previous, note, actor));
            }
            catch (Exception ex) when (ex is JsonException or DbUpdateException or InvalidOperationException)
            {
                previous = item.Status;
                item.Status = StagingStatus.Failed;
                item.ReviewNote = string.Join(" | ", new[] { note, $"Promotion failed: {ex.GetBaseException().Message}" }.Where(value => !string.IsNullOrWhiteSpace(value)));
                db.StagedImportEvents.Add(StagingAudit.Create(item, "Failed", previous, item.ReviewNote, actor));
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
            case "order": await PromoteOrder(item, payload, ct); break;
            default: throw new JsonException($"Unsupported registered entity type '{item.EntityType}'.");
        }
        await db.SaveChangesAsync(ct);
    }

    private static string? DetailKey(string entityType, JsonElement payload) => entityType.ToLowerInvariant() switch
    {
        "driver" => Text(payload, "employeeNumber") ?? Text(payload, "driverId") ?? Text(payload, "payrollNumber"),
        "vehicle" => Text(payload, "registration"),
        "trailer" => Text(payload, "trailerNumber"),
        "site" => Text(payload, "externalCode"),
        _ => null
    };

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
        vehicle.FleetioId = Clip(Text(payload, "fleetioId") ?? Text(payload, "fleetioID") ?? Text(payload, "fleetioVehicleId"), 80);
        vehicle.FleetioName = Clip(Text(payload, "fleetioName"), 160);
        vehicle.FleetioStatus = Clip(Text(payload, "fleetioStatus"), 80);
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
        var coding = Clip(Text(payload, "coding"), 80);
        var agencyName = Clip(Text(payload, "agencyName"), 160);
        var northEligible = BoolOrNull(payload, "northEligible");
        var preloadEligible = BoolOrNull(payload, "preloadEligible");
        var notes = Clip(Text(payload, "notes"), 500);
        var tachoMasterDriverId = Clip(Text(payload, "tachoMasterDriverId") ?? Text(payload, "tachomasterDriverId"), 80);
        var drivingLicenceNumber = Clip(Text(payload, "drivingLicenceNumber") ?? Text(payload, "licenceNumber"), 80);
        var licenceExpiry = DateOnlyOrNull(payload, "licenceExpiry");
        var licenceStatus = Clip(Text(payload, "licenceStatus"), 40);
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
                Coding = coding, AgencyName = agencyName, NorthEligible = northEligible, PreloadEligible = preloadEligible, Notes = notes,
                TachoMasterDriverId = tachoMasterDriverId, DrivingLicenceNumber = drivingLicenceNumber, LicenceExpiry = licenceExpiry, LicenceStatus = licenceStatus,
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
            driver.Coding = coding; driver.AgencyName = agencyName; driver.NorthEligible = northEligible; driver.PreloadEligible = preloadEligible; driver.Notes = notes;
            driver.TachoMasterDriverId = tachoMasterDriverId; driver.DrivingLicenceNumber = drivingLicenceNumber; driver.LicenceExpiry = licenceExpiry; driver.LicenceStatus = licenceStatus;
            driver.Active = active;
        }
    }

    private async Task PromoteTrailer(JsonElement payload, CancellationToken ct)
    {
        var trailerNumber = ClipRequired(Required(payload, "trailerNumber"), 40);
        var trailer = await db.Trailers.SingleOrDefaultAsync(item => item.TrailerNumber == trailerNumber, ct);
        if (trailer is null) db.Trailers.Add(new Trailer { TrailerNumber = trailerNumber, Type = Clip(Text(payload, "type"), 80), StandardCapacity = IntOrNull(payload, "standardCapacity"), EuroCapacity = IntOrNull(payload, "euroCapacity"), Notes = Clip(Text(payload, "notes"), 500), Active = Bool(payload, "active", true) });
        else { trailer.Type = Clip(Text(payload, "type"), 80); trailer.StandardCapacity = IntOrNull(payload, "standardCapacity"); trailer.EuroCapacity = IntOrNull(payload, "euroCapacity"); trailer.Notes = Clip(Text(payload, "notes"), 500); trailer.Active = Bool(payload, "active", true); }
    }

    private async Task PromoteSite(JsonElement payload, CancellationToken ct)
    {
        var externalCode = ClipRequired(Required(payload, "externalCode"), 40); var name = ClipRequired(Required(payload, "name"), 200);
        var site = await db.Sites.SingleOrDefaultAsync(item => item.ExternalCode == externalCode, ct);
        if (site is null) db.Sites.Add(new Site { ExternalCode = externalCode, Name = name, DriverTextName = Clip(Text(payload, "driverTextName"), 200), CollectionAddress = Clip(Text(payload, "collectionAddress"), 500), CollectionInstructions = Clip(Text(payload, "collectionInstructions"), 1000), MapLink = Clip(Text(payload, "mapLink"), 1000), Aliases = Clip(Text(payload, "aliases"), 500), CustomField1 = Clip(Text(payload, "customField1"), 200), CustomField2 = Clip(Text(payload, "customField2"), 200), CustomField3 = Clip(Text(payload, "customField3"), 200), OperationalRegion = Clip(Text(payload, "operationalRegion") ?? Text(payload, "region"), 80), Active = Bool(payload, "active", true) });
        else { site.Name = name; site.DriverTextName = Clip(Text(payload, "driverTextName"), 200); site.CollectionAddress = Clip(Text(payload, "collectionAddress"), 500); site.CollectionInstructions = Clip(Text(payload, "collectionInstructions"), 1000); site.MapLink = Clip(Text(payload, "mapLink"), 1000); site.Aliases = Clip(Text(payload, "aliases"), 500); site.CustomField1 = Clip(Text(payload, "customField1"), 200); site.CustomField2 = Clip(Text(payload, "customField2"), 200); site.CustomField3 = Clip(Text(payload, "customField3"), 200); site.OperationalRegion = Clip(Text(payload, "operationalRegion") ?? Text(payload, "region"), 80); site.Active = Bool(payload, "active", true); }
    }

    private async Task PromoteMarketContact(JsonElement payload, CancellationToken ct)
    {
        var market = CanonicalMarket(ClipRequired(Text(payload, "market") ?? Text(payload, "marketName") ?? "General", 80));
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

    private async Task PromoteOrder(StagedImport item, JsonElement payload, CancellationToken ct)
    {
        var reference = Required(payload, "poNumber"); var customerCode = Required(payload, "customerCode"); var collectionDateText = Required(payload, "collectionDate");
        if (!DateOnly.TryParse(collectionDateText, out var collectionDate)) throw new JsonException("Order payload requires a valid collectionDate.");
        var (movement, plannerReady) = await RecordOrderRevision(item, payload, reference, customerCode, ct);
        if (!plannerReady) return;
        var siteAlignment = await OrderSiteMasterAlignment.ResolveAsync(db, payload, ct);
        TransportOrder? existing;
        try { existing = await db.TransportOrders.SingleOrDefaultAsync(order => order.Reference == reference, ct); }
        catch (Exception ex) when (ex.GetBaseException().Message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase))
        {
            // The approved staged row is itself the durable order register on
            // legacy production databases where DDL permissions are unavailable.
            return;
        }
        if (existing is null)
        {
            DateOnly? deliveryDate = null;
            if (DateOnly.TryParse(Text(payload, "deliveryDate"), out var parsedDelivery)) deliveryDate = parsedDelivery;
            DateTimeOffset? deliveryWindowStartUtc = null;
            if (DateTimeOffset.TryParse(Text(payload, "deliveryWindowStartUtc"), out var parsedWindowStart)) deliveryWindowStartUtc = parsedWindowStart;
            DateTimeOffset? deliveryWindowEndUtc = null;
            if (DateTimeOffset.TryParse(Text(payload, "deliveryWindowEndUtc"), out var parsedWindowEnd)) deliveryWindowEndUtc = parsedWindowEnd;
            Guid? sourceStagedImportId = db.Entry(item).State == EntityState.Detached ? null : item.Id;
            db.TransportOrders.Add(new TransportOrder { SourceStagedImportId = sourceStagedImportId, SourceMovementId = movement.Id, Reference = ClipRequired(reference, 80), CustomerCode = ClipRequired(customerCode, 40), CollectionDate = collectionDate, DeliveryDate = deliveryDate, DeliveryWindowStartUtc = deliveryWindowStartUtc, DeliveryWindowEndUtc = deliveryWindowEndUtc, Pallets = IntOrNull(payload, "pallets"), SellerName = Clip(siteAlignment.CollectionName ?? Text(payload, "sellerName"), 200), MarketName = Clip(Text(payload, "marketName"), 80), StallNumber = Clip(siteAlignment.DeliveryName ?? Text(payload, "stallNumber"), 200), DriverInstructions = Clip(siteAlignment.DriverInstructions ?? Text(payload, "driverInstructions"), 1000), MapLink = Clip(siteAlignment.DeliveryMapLink ?? Text(payload, "mapLink"), 1000) });
        }
        else
        {
            existing.SourceStagedImportId = item.Id;
            existing.SourceMovementId = movement.Id;
            existing.CollectionDate = collectionDate;
            if (DateOnly.TryParse(Text(payload, "deliveryDate"), out var parsedDelivery)) existing.DeliveryDate = parsedDelivery;
            if (DateTimeOffset.TryParse(Text(payload, "deliveryWindowStartUtc"), out var parsedWindowStart)) existing.DeliveryWindowStartUtc = parsedWindowStart;
            if (DateTimeOffset.TryParse(Text(payload, "deliveryWindowEndUtc"), out var parsedWindowEnd)) existing.DeliveryWindowEndUtc = parsedWindowEnd;
            existing.Pallets = IntOrNull(payload, "pallets") ?? existing.Pallets;
            existing.SellerName = Clip(siteAlignment.CollectionName ?? Text(payload, "sellerName") ?? existing.SellerName, 200);
            existing.MarketName = Clip(Text(payload, "marketName") ?? existing.MarketName, 80);
            existing.StallNumber = Clip(siteAlignment.DeliveryName ?? Text(payload, "stallNumber") ?? existing.StallNumber, 200);
            existing.DriverInstructions = Clip(siteAlignment.DriverInstructions ?? Text(payload, "driverInstructions") ?? existing.DriverInstructions, 1000);
            existing.MapLink = Clip(siteAlignment.DeliveryMapLink ?? Text(payload, "mapLink") ?? existing.MapLink, 1000);
        }
    }

    private async Task<(OrderMovement Movement, bool PlannerReady)> RecordOrderRevision(
        StagedImport item, JsonElement payload, string reference, string customerCode, CancellationToken ct)
    {
        var normalCustomer = ClipRequired(customerCode.Trim().ToUpperInvariant(), 40);
        var normalReference = new string(reference.Trim().ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        var stableKey = ClipRequired($"{normalCustomer}:{normalReference}", 240);
        var movement = await db.OrderMovements.SingleOrDefaultAsync(
            x => x.CustomerCode == normalCustomer && x.StableMovementKey == stableKey, ct);
        if (movement is null)
        {
            movement = new OrderMovement { CustomerCode = normalCustomer, StableMovementKey = stableKey };
            db.OrderMovements.Add(movement);
        }

        var existingRevision = await db.OrderRevisions.SingleOrDefaultAsync(x => x.StagedImportId == item.Id, ct);
        if (existingRevision is not null)
        {
            movement.CurrentRevisionId = existingRevision.Id;
            return (movement, movement.LifecycleStatus == OrderMovementStatus.PlannerReady);
        }

        var previous = await db.OrderRevisions.Where(x => x.MovementId == movement.Id)
            .OrderByDescending(x => x.RevisionNumber).FirstOrDefaultAsync(ct);
        var revision = new OrderRevision
        {
            MovementId = movement.Id,
            StagedImportId = item.Id,
            RevisionNumber = (previous?.RevisionNumber ?? 0) + 1,
            MessageId = Clip(Text(payload, "messageId") ?? Text(payload, "internetMessageId"), 500),
            AttachmentIdentity = Clip(Text(payload, "attachmentIdentity") ?? Text(payload, "sourceAttachmentReference") ?? Text(payload, "sourceAttachmentName"), 500),
            ParserTemplate = Clip(Text(payload, "parserTemplate") ?? Text(payload, "mappingTemplate"), 120),
            ParserVersion = Clip(Text(payload, "parserVersion") ?? Text(payload, "sourceTemplateVersion"), 40),
            PayloadJson = payload.GetRawText(),
            ReceivedAtUtc = item.ReceivedAtUtc,
            SupersedesRevisionId = previous?.Id
        };
        db.OrderRevisions.Add(revision);

        var sourceLines = new List<JsonElement>();
        var hasExplicitSourceLines = TryGetProperty(payload, "sourceLines", out var sourceArray) && sourceArray.ValueKind == JsonValueKind.Array;
        if (hasExplicitSourceLines)
            sourceLines.AddRange(sourceArray.EnumerateArray().Select(x => x.Clone()));
        if (sourceLines.Count == 0) sourceLines.Add(payload.Clone());

        var plannerReady = false;
        for (var index = 0; index < sourceLines.Count; index++)
        {
            var line = sourceLines[index];
            var rawCollectionSite = Text(line, "collectionSite") ?? Text(line, "collectionLocation") ?? Text(payload, "collectionSite") ?? Text(payload, "collectionLocation") ?? Text(payload, "sellerName");
            var rawDeliverySite = Text(line, "deliverySite") ?? Text(line, "deliveryLocation") ?? Text(payload, "deliverySite") ?? Text(payload, "deliveryLocation") ?? Text(payload, "stallNumber");
            var lineAlignment = await OrderSiteMasterAlignment.ResolveNamesAsync(
                db, rawCollectionSite, rawDeliverySite,
                Text(line, "collectionAddress") ?? Text(payload, "collectionAddress"),
                Text(line, "deliveryAddress") ?? Text(payload, "deliveryAddress"),
                Text(line, "mapLink") ?? Text(payload, "mapLink"),
                Text(line, "driverInstructions") ?? Text(payload, "driverInstructions"), ct);
            var collectionSite = lineAlignment.CollectionName ?? rawCollectionSite;
            var deliverySite = lineAlignment.DeliveryName ?? rawDeliverySite;
            var lineCollectionDate = DateOnlyOrNull(line, "collectionDate") ?? DateOnlyOrNull(payload, "collectionDate");
            var lineDeliveryDate = DateOnlyOrNull(line, "deliveryDate") ?? DateOnlyOrNull(payload, "deliveryDate");
            var pallets = IntOrNull(line, "pallets") ?? IntOrNull(line, "palletQuantity") ?? (sourceLines.Count == 1 ? IntOrNull(payload, "pallets") : null);
            plannerReady |= !string.IsNullOrWhiteSpace(collectionSite) && !string.IsNullOrWhiteSpace(deliverySite)
                && lineCollectionDate is not null && lineDeliveryDate is not null && pallets is >= 0;
            db.OrderSourceLines.Add(new OrderSourceLine
            {
                RevisionId = revision.Id,
                SourceRowKey = ClipRequired(Text(line, "sourceRowKey") ?? Text(line, "sourceRowId") ?? $"row-{index + 1}", 160),
                CollectionSite = Clip(collectionSite, 200),
                DeliverySite = Clip(deliverySite, 200),
                CollectionDate = lineCollectionDate,
                DeliveryDate = lineDeliveryDate,
                CollectionTimeFrom = TimeOnlyOrNull(line, "collectionTimeFrom") ?? TimeOnlyOrNull(payload, "collectionTimeFrom"),
                CollectionTimeTo = TimeOnlyOrNull(line, "collectionTimeTo") ?? TimeOnlyOrNull(payload, "collectionTimeTo"),
                PalletType = Clip(Text(line, "palletType") ?? Text(payload, "palletType"), 40),
                Pallets = pallets,
                TemperatureRequirement = Clip(Text(line, "temperatureRequirement") ?? Text(line, "temperature") ?? Text(payload, "temperatureRequirement") ?? Text(payload, "temperature"), 80),
                LoadReference = Clip(Text(line, "loadReference") ?? Text(payload, "loadReference"), 120),
                PayloadJson = line.GetRawText()
            });
        }

        var declaredLifecycle = Text(payload, "lifecycleStatus") ?? Text(payload, "reviewStatus");
        if (!hasExplicitSourceLines && DateOnlyOrNull(payload, "collectionDate") is not null && IntOrNull(payload, "pallets") is > 0)
            plannerReady = true; // Existing single-row staging payloads remain promotable.
        if (declaredLifecycle?.Contains("awaiting", StringComparison.OrdinalIgnoreCase) == true)
            plannerReady = false;
        movement.CurrentRevisionId = revision.Id;
        movement.LifecycleStatus = plannerReady ? OrderMovementStatus.PlannerReady : OrderMovementStatus.AwaitingDetails;
        movement.UpdatedAtUtc = DateTimeOffset.UtcNow;
        return (movement, plannerReady);
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
    private static DateOnly? DateOnlyOrNull(JsonElement payload, string name) => DateOnly.TryParse(Text(payload, name), out var value) ? value : null;
    private static TimeOnly? TimeOnlyOrNull(JsonElement payload, string name) => TimeOnly.TryParse(Text(payload, name), out var value) ? value : null;
    private static string CanonicalMarket(string value)
    {
        var normal = NormaliseKey(value);
        if (normal.Contains("covent")) return "Covent";
        if (normal.Contains("spit")) return "Spit";
        if (normal.Contains("western")) return "Western";
        if (normal.Contains("sender")) return "Sender";
        return string.IsNullOrWhiteSpace(value) ? "General" : value.Trim();
    }
    private static string? Clip(string? value, int maxLength) => string.IsNullOrWhiteSpace(value) ? null : value.Length <= maxLength ? value : value[..maxLength];
    private static string ClipRequired(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];
}
