using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/orders")]
[Authorize]
public sealed class OrdersMaintenanceController(TmsDbContext db) : ControllerBase
{
    [HttpPut("{id:guid}"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Update(Guid id, [FromBody] OrderUpdateRequest request, CancellationToken ct)
    {
        var order = await db.TransportOrders.SingleOrDefaultAsync(item => item.Id == id, ct);
        if (order is null) return NotFound(new { message = "Order was not found." });
        if (order.Status == OrderStatus.Cancelled) return BadRequest(new { message = "A cancelled order cannot be amended." });

        var reference = Clip(request.Reference, 80);
        var customerCode = Clip(request.CustomerCode, 40);
        if (string.IsNullOrWhiteSpace(reference) || string.IsNullOrWhiteSpace(customerCode))
            return BadRequest(new { message = "Order reference and customer are required." });
        if (request.Pallets is < 0) return BadRequest(new { message = "Pallet quantity cannot be negative." });

        var previousAddress = ExtractTagged(order.DriverInstructions, "Delivery address");
        var deliveryAddress = Clip(request.DeliveryAddress, 500);
        var collectionSite = Clip(request.CollectionSite, 200);
        var depotId = Clip(request.DepotId, 80);
        var destination = Clip(request.Destination, 200);
        var customerRef = Clip(request.CustomerRef, 200);
        var poRef = Clip(request.PoRef, 200);
        var palletName = Clip(request.PalletName, 200);
        var notes = Clip(request.Notes, 1000);

        order.Reference = reference!;
        order.CustomerCode = customerCode!;
        order.CollectionDate = request.CollectionDate;
        order.DeliveryDate = request.DeliveryDate;
        order.DeliveryWindowStartUtc = request.DeliveryWindowStartUtc;
        order.DeliveryWindowEndUtc = request.DeliveryWindowEndUtc;
        order.Pallets = request.Pallets;
        order.SellerName = collectionSite;
        order.MarketName = depotId;
        order.StallNumber = destination;
        order.DriverInstructions = BuildNotes(collectionSite, depotId, destination, deliveryAddress, customerRef, poRef, palletName, notes);
        order.MapLink = string.IsNullOrWhiteSpace(deliveryAddress)
            ? Clip(request.MapLink, 1000)
            : $"https://www.google.com/maps/search/?api=1&query={Uri.EscapeDataString(deliveryAddress)}";

        var linkedStops = await db.LoadStops.Where(stop => stop.OrderId == id).ToListAsync(ct);
        foreach (var stop in linkedStops)
        {
            stop.Name = Clip($"{order.CustomerCode} · {destination ?? depotId ?? order.Reference}", 200)!;
            if (!string.Equals(previousAddress, deliveryAddress, StringComparison.OrdinalIgnoreCase))
            {
                stop.Address = deliveryAddress;
                stop.Latitude = null;
                stop.Longitude = null;
            }
        }

        await db.SaveChangesAsync(ct);
        return Ok(order);
    }

    [HttpDelete("{id:guid}"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        var order = await db.TransportOrders.SingleOrDefaultAsync(item => item.Id == id, ct);
        if (order is null) return NotFound(new { message = "Order was not found." });
        if (order.Status == OrderStatus.Delivered) return BadRequest(new { message = "A delivered order cannot be deleted." });

        var linkedStops = await db.LoadStops.Where(stop => stop.OrderId == id).ToListAsync(ct);
        if (linkedStops.Count > 0) db.LoadStops.RemoveRange(linkedStops);
        order.Status = OrderStatus.Cancelled;
        await db.SaveChangesAsync(ct);
        return Ok(new { order.Id, order.Reference, status = order.Status.ToString(), removedStops = linkedStops.Count });
    }

    private static string BuildNotes(string? collectionSite, string? depotId, string? destination, string? deliveryAddress, string? customerRef, string? poRef, string? palletName, string? notes)
        => string.Join(" · ", new[]
        {
            Tag("Collection site", collectionSite),
            Tag("Depot ID", depotId),
            Tag("Depot", destination),
            Tag("Delivery address", deliveryAddress),
            Tag("Customer ref", customerRef),
            Tag("PO ref", poRef),
            Tag("Pallet", palletName),
            notes
        }.Where(value => !string.IsNullOrWhiteSpace(value))).Length <= 1000
            ? string.Join(" · ", new[] { Tag("Collection site", collectionSite), Tag("Depot ID", depotId), Tag("Depot", destination), Tag("Delivery address", deliveryAddress), Tag("Customer ref", customerRef), Tag("PO ref", poRef), Tag("Pallet", palletName), notes }.Where(value => !string.IsNullOrWhiteSpace(value)))
            : string.Join(" · ", new[] { Tag("Collection site", collectionSite), Tag("Depot ID", depotId), Tag("Depot", destination), Tag("Delivery address", deliveryAddress), Tag("Customer ref", customerRef), Tag("PO ref", poRef), Tag("Pallet", palletName), notes }.Where(value => !string.IsNullOrWhiteSpace(value)))[..1000];

    private static string? ExtractTagged(string? notes, string label)
    {
        if (string.IsNullOrWhiteSpace(notes)) return null;
        var prefix = $"{label}:";
        return notes.Split('·', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(part => part.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..].Trim();
    }

    private static string? Tag(string label, string? value) => string.IsNullOrWhiteSpace(value) ? null : $"{label}: {value.Trim()}";
    private static string? Clip(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= max ? value.Trim() : value.Trim()[..max];
}

public sealed record OrderUpdateRequest(
    string Reference,
    string CustomerCode,
    DateOnly CollectionDate,
    DateOnly? DeliveryDate,
    DateTimeOffset? DeliveryWindowStartUtc,
    DateTimeOffset? DeliveryWindowEndUtc,
    int? Pallets,
    string? CollectionSite,
    string? DepotId,
    string? Destination,
    string? DeliveryAddress,
    string? CustomerRef,
    string? PoRef,
    string? PalletName,
    string? Notes,
    string? MapLink);