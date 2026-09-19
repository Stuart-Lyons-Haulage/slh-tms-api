namespace Slh.Tms.V2.Api.Domain;

public enum IntakeReviewState
{
    Received,
    Extracted,
    NeedsReview,
    Approved,
    Rejected,
    Promoted,
    Failed
}

public sealed class SourceEvidence
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string SourceSystem { get; set; }
    public string? Mailbox { get; set; }
    public string? MessageId { get; set; }
    public string? Subject { get; set; }
    public string? Sender { get; set; }
    public DateTimeOffset ReceivedAtUtc { get; set; }
    public required string EvidenceHash { get; set; }
    public string? RawBodyLocation { get; set; }
}

public sealed record ExtractedOrderDraft(
    string? CustomerCode,
    string? PurchaseOrder,
    string? OrderReference,
    string? CollectionSiteText,
    string? DeliverySiteText,
    DateOnly? CollectionDate,
    TimeOnly? CollectionTime,
    DateOnly? DeliveryDate,
    TimeOnly? DeliveryTime,
    int? Pallets,
    int? EuroPallets,
    int? Trolleys,
    int? Cases,
    int? Crates,
    int? Trays,
    string? TemperatureRequirement,
    string? TrailerRequirement,
    string? MarketName,
    string? StallNumber,
    string? Notes);

public sealed record MasterResolution(
    Guid? CustomerId,
    Guid? CollectionSiteId,
    Guid? DeliverySiteId,
    Guid? MarketId,
    decimal Confidence,
    IReadOnlyList<string> Issues)
{
    public bool RequiresReview =>
        CustomerId is null ||
        CollectionSiteId is null ||
        DeliverySiteId is null ||
        Issues.Count > 0;
}
