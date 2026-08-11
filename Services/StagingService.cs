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
        else { item.Status = StagingStatus.Approved; await Promote(item, ct); item.Status = StagingStatus.Promoted; }
        await db.SaveChangesAsync(ct); return item;
    }
    private async Task Promote(StagedImport item, CancellationToken ct)
    {
        var o = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        switch (item.EntityType)
        {
            case "customer": db.Customers.Add(JsonSerializer.Deserialize<Customer>(item.PayloadJson, o) ?? throw new JsonException()); break;
            case "customercontact": db.CustomerContacts.Add(JsonSerializer.Deserialize<CustomerContact>(item.PayloadJson, o) ?? throw new JsonException()); break;
            case "vehicle": db.Vehicles.Add(JsonSerializer.Deserialize<Vehicle>(item.PayloadJson, o) ?? throw new JsonException()); break;
            case "driver": db.Drivers.Add(JsonSerializer.Deserialize<Driver>(item.PayloadJson, o) ?? throw new JsonException()); break;
            case "trailer": db.Trailers.Add(JsonSerializer.Deserialize<Trailer>(item.PayloadJson, o) ?? throw new JsonException()); break;
            case "site": db.Sites.Add(JsonSerializer.Deserialize<Site>(item.PayloadJson, o) ?? throw new JsonException()); break;
            case "marketcontact": db.MarketContacts.Add(JsonSerializer.Deserialize<MarketContact>(item.PayloadJson, o) ?? throw new JsonException()); break;
            case "order":
                using (var document = JsonDocument.Parse(item.PayloadJson))
                {
                    var payload = document.RootElement;
                    var reference = payload.GetProperty("poNumber").GetString();
                    var customerCode = payload.GetProperty("customerCode").GetString();
                    var collectionDateText = payload.GetProperty("collectionDate").GetString();
                    if (string.IsNullOrWhiteSpace(reference) || string.IsNullOrWhiteSpace(customerCode) || !DateOnly.TryParse(collectionDateText, out var collectionDate))
                        throw new JsonException("Order payload requires poNumber, customerCode and collectionDate.");
                    if (!await db.TransportOrders.AnyAsync(order => order.Reference == reference, ct))
                    {
                        DateOnly? deliveryDate = null;
                        if (payload.TryGetProperty("deliveryDate", out var delivery) && DateOnly.TryParse(delivery.GetString(), out var parsedDelivery)) deliveryDate = parsedDelivery;
                        DateTimeOffset? deliveryWindowStartUtc = null;
                        if (payload.TryGetProperty("deliveryWindowStartUtc", out var windowStart) && DateTimeOffset.TryParse(windowStart.GetString(), out var parsedWindowStart)) deliveryWindowStartUtc = parsedWindowStart;
                        DateTimeOffset? deliveryWindowEndUtc = null;
                        if (payload.TryGetProperty("deliveryWindowEndUtc", out var windowEnd) && DateTimeOffset.TryParse(windowEnd.GetString(), out var parsedWindowEnd)) deliveryWindowEndUtc = parsedWindowEnd;
                        int? pallets = null;
                        if (payload.TryGetProperty("pallets", out var palletValue) && int.TryParse(palletValue.GetString(), out var parsedPallets)) pallets = parsedPallets;
                        db.TransportOrders.Add(new TransportOrder { Reference = reference, CustomerCode = customerCode, CollectionDate = collectionDate, DeliveryDate = deliveryDate, DeliveryWindowStartUtc = deliveryWindowStartUtc, DeliveryWindowEndUtc = deliveryWindowEndUtc, Pallets = pallets,
                            SellerName = Read("sellerName"), MarketName = Read("marketName"), StallNumber = Read("stallNumber"), DriverInstructions = Read("driverInstructions"), MapLink = Read("mapLink") });
                        string? Read(string name) => payload.TryGetProperty(name, out var value) ? value.GetString() : null;
                    }
                }
                break;
        }
        await db.SaveChangesAsync(ct);
    }
}
