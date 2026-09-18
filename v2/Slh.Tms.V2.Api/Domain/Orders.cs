namespace Slh.Tms.V2.Api.Domain;

public enum OrderState
{
    Draft,
    ReadyToPlan,
    Planned,
    InTransit,
    Delivered,
    Cancelled
}

public sealed class TransportOrder
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string StableKey { get; set; }
    public Guid CustomerId { get; set; }
    public Guid CollectionSiteId { get; set; }
    public Guid DeliverySiteId { get; set; }
    public Guid? MarketId { get; set; }
    public string? PurchaseOrder { get; set; }
    public string? CustomerOrderReference { get; set; }
    public string? SourceOrderReference { get; set; }
    public DateOnly CollectionDate { get; set; }
    public TimeOnly? CollectionTime { get; set; }
    public DateOnly? DeliveryDate { get; set; }
    public TimeOnly? DeliveryTime { get; set; }
    public int Pallets { get; set; }
    public int Cases { get; set; }
    public int Crates { get; set; }
    public int Trays { get; set; }
    public string? TemperatureRequirement { get; set; }
    public string? TrailerRequirement { get; set; }
    public string? StallNumber { get; set; }
    public string? Notes { get; set; }
    public OrderState State { get; set; } = OrderState.Draft;
    public int RevisionNumber { get; set; } = 1;
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class OrderSourceLink
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public Guid EvidenceId { get; set; }
    public int RevisionNumber { get; set; }
    public DateTimeOffset LinkedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}
