using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.V2.Api.Data;
using Slh.Tms.V2.Api.Domain;

namespace Slh.Tms.V2.Api.Application;

public sealed class OrderPromotionService(
    IntakeDbContext intakeDb,
    OperationsDbContext operationsDb)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<TransportOrder> PromoteAsync(Guid intakeRecordId, CancellationToken ct)
    {
        var intake = await intakeDb.IntakeRecords
            .SingleOrDefaultAsync(x => x.Id == intakeRecordId, ct)
            ?? throw new InvalidOperationException("Intake record was not found.");

        if (intake.State == IntakeReviewState.Promoted)
        {
            var existingLink = await operationsDb.OrderSourceLinks.AsNoTracking()
                .FirstOrDefaultAsync(x => x.EvidenceId == intake.EvidenceId, ct);

            if (existingLink is not null)
            {
                var existingOrder = await operationsDb.Orders.AsNoTracking()
                    .SingleAsync(x => x.Id == existingLink.OrderId, ct);
                return existingOrder;
            }
        }

        if (intake.State != IntakeReviewState.Approved)
            throw new InvalidOperationException("Only approved intake records can be promoted.");

        var draft = JsonSerializer.Deserialize<ExtractedOrderDraft>(intake.ExtractedJson, JsonOptions)
            ?? throw new InvalidOperationException("The extracted order payload is invalid.");

        var resolution = string.IsNullOrWhiteSpace(intake.ResolutionJson)
            ? null
            : JsonSerializer.Deserialize<MasterResolution>(intake.ResolutionJson, JsonOptions);

        if (resolution is null || resolution.RequiresReview)
            throw new InvalidOperationException("Master Data resolution is incomplete.");

        if (draft.CollectionDate is null)
            throw new InvalidOperationException("Collection date is required before promotion.");

        var stableKey = BuildStableKey(draft, resolution, intake.EvidenceId);

        var existing = await operationsDb.Orders
            .SingleOrDefaultAsync(x => x.StableKey == stableKey, ct);

        if (existing is not null)
            throw new InvalidOperationException(
                "A canonical order already exists for this stable movement key. " +
                "Amendments must use the revision workflow rather than silently merging.");

        var order = new TransportOrder
        {
            StableKey = stableKey,
            CustomerId = resolution.CustomerId!.Value,
            CollectionSiteId = resolution.CollectionSiteId!.Value,
            DeliverySiteId = resolution.DeliverySiteId!.Value,
            MarketId = resolution.MarketId,
            PurchaseOrder = Clean(draft.PurchaseOrder),
            CustomerOrderReference = Clean(draft.OrderReference),
            SourceOrderReference = Clean(draft.OrderReference),
            CollectionDate = draft.CollectionDate.Value,
            CollectionTime = draft.CollectionTime,
            DeliveryDate = draft.DeliveryDate,
            DeliveryTime = draft.DeliveryTime,
            Pallets = Math.Max(0, draft.Pallets ?? 0),
            Cases = Math.Max(0, draft.Cases ?? 0),
            Crates = Math.Max(0, draft.Crates ?? 0),
            Trays = Math.Max(0, draft.Trays ?? 0),
            TemperatureRequirement = Clean(draft.TemperatureRequirement),
            TrailerRequirement = Clean(draft.TrailerRequirement),
            StallNumber = Clean(draft.StallNumber),
            Notes = Clean(draft.Notes),
            State = OrderState.ReadyToPlan,
            RevisionNumber = 1
        };

        operationsDb.Orders.Add(order);
        operationsDb.OrderSourceLinks.Add(new OrderSourceLink
        {
            OrderId = order.Id,
            EvidenceId = intake.EvidenceId,
            RevisionNumber = 1
        });

        intake.State = IntakeReviewState.Promoted;
        intake.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await operationsDb.SaveChangesAsync(ct);
        await intakeDb.SaveChangesAsync(ct);

        return order;
    }

    private static string BuildStableKey(
        ExtractedOrderDraft draft,
        MasterResolution resolution,
        Guid evidenceId)
    {
        var strongReference = Clean(draft.OrderReference);

        // PO alone is deliberately not treated as a unique movement identifier:
        // one PO can legitimately contain several collection/delivery movements.
        var sourceIdentity = !string.IsNullOrWhiteSpace(strongReference)
            ? $"REF:{strongReference}"
            : $"EVIDENCE:{evidenceId:N}";

        var raw = string.Join("|",
            resolution.CustomerId,
            resolution.CollectionSiteId,
            resolution.DeliverySiteId,
            draft.CollectionDate?.ToString("yyyy-MM-dd"),
            sourceIdentity);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash);
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
