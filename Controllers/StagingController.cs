using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Contracts;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;
[ApiController, Route("api/v1/staging")]
[Authorize]
public sealed class StagingController(TmsDbContext db, StagingService service) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] StagingStatus? status,
        [FromQuery] string? entityType,
        [FromQuery] int take = 100,
        CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 2000);
        var query = db.StagedImports.AsNoTracking().AsQueryable();
        query = query.Where(x => x.Status == (status ?? StagingStatus.PendingReview));
        if (!string.IsNullOrWhiteSpace(entityType))
            query = query.Where(x => x.EntityType == entityType.Trim().ToLowerInvariant());

        return Ok(await query.OrderByDescending(x => x.ReceivedAtUtc).Take(take).ToListAsync(ct));
    }

    [HttpPost, Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Stage(StageImportRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)) return BadRequest(new ErrorResponse("invalid_idempotency_key", "IdempotencyKey is required", HttpContext.TraceIdentifier));
        if (request.IdempotencyKey.Length > 200) return BadRequest(new ErrorResponse("invalid_idempotency_key", "IdempotencyKey must be 200 characters or fewer.", HttpContext.TraceIdentifier));
        if (IsNegativePalletOrder(request))
            return BadRequest(new ErrorResponse("negative_pallet_quantity", "Order pallet quantity cannot be negative.", HttpContext.TraceIdentifier));
        if (IsZeroPalletOrderWithoutCompleteRoute(request))
            return BadRequest(new ErrorResponse("zero_pallet_route_required", "A zero-pallet order is valid only when collection site, delivery site, collection date, and delivery date are all supplied.", HttpContext.TraceIdentifier));
        var existing = await db.StagedImports.AsNoTracking().SingleOrDefaultAsync(x => x.IdempotencyKey == request.IdempotencyKey, ct);
        if (existing is not null) return Ok(service.ToResponse(existing, Request));
        try
        {
            var item = service.Create(request);
            db.StagedImports.Add(item);
            db.StagedImportEvents.Add(StagingAudit.Create(item, "Received"));
            await db.SaveChangesAsync(ct);
            return Accepted(service.ToResponse(item, Request));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ErrorResponse("invalid_staging_record", ex.Message, HttpContext.TraceIdentifier));
        }
    }

    [HttpPost("batch"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> StageBatch(List<StageImportRequest> requests, CancellationToken ct)
    {
        if (requests.Count == 0 || requests.Count > 500) return BadRequest(new ErrorResponse("invalid_batch", "Submit between 1 and 500 records.", HttpContext.TraceIdentifier));
        if (requests.Any(request => string.IsNullOrWhiteSpace(request.IdempotencyKey))) return BadRequest(new ErrorResponse("invalid_idempotency_key", "Every record needs an IdempotencyKey.", HttpContext.TraceIdentifier));
        if (requests.Any(request => request.IdempotencyKey.Length > 200)) return BadRequest(new ErrorResponse("invalid_idempotency_key", "Every IdempotencyKey must be 200 characters or fewer.", HttpContext.TraceIdentifier));
        if (requests.Any(IsNegativePalletOrder))
            return BadRequest(new ErrorResponse("negative_pallet_quantity", "Order pallet quantity cannot be negative.", HttpContext.TraceIdentifier));
        if (requests.Any(IsZeroPalletOrderWithoutCompleteRoute))
            return BadRequest(new ErrorResponse("zero_pallet_route_required", "Every zero-pallet order must include collection site, delivery site, collection date, and delivery date.", HttpContext.TraceIdentifier));
        var keys = requests.Select(request => request.IdempotencyKey).ToList();
        if (keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != keys.Count) return BadRequest(new ErrorResponse("duplicate_batch_key", "Idempotency keys must be unique within the batch.", HttpContext.TraceIdentifier));
        var existing = await db.StagedImports.AsNoTracking().Where(item => keys.Contains(item.IdempotencyKey)).ToDictionaryAsync(item => item.IdempotencyKey, ct);
        var existingCount = existing.Count;
        var responses = new List<StageImportResponse>();
        try
        {
            foreach (var request in requests)
            {
                if (existing.TryGetValue(request.IdempotencyKey, out var item)) responses.Add(service.ToResponse(item, Request));
                else
                {
                    var created = service.Create(request);
                    db.StagedImports.Add(created);
                    db.StagedImportEvents.Add(StagingAudit.Create(created, "Received"));
                    responses.Add(service.ToResponse(created, Request));
                }
            }
            await db.SaveChangesAsync(ct);
            return Accepted(new { received = requests.Count, existing = existingCount, created = responses.Count - existingCount, skippedZeroPallets = 0, records = responses });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ErrorResponse("invalid_staging_record", ex.Message, HttpContext.TraceIdentifier));
        }
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        (await db.StagedImports.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)) is { } x ? Ok(x) : NotFound();

    [HttpGet("{id:guid}/history")]
    public async Task<IActionResult> History(Guid id, CancellationToken ct)
    {
        if (!await db.StagedImports.AsNoTracking().AnyAsync(x => x.Id == id, ct)) return NotFound();
        var events = await db.StagedImportEvents.AsNoTracking()
            .Where(x => x.StagedImportId == id)
            .OrderBy(x => x.OccurredAtUtc)
            .ThenBy(x => x.Id)
            .ToListAsync(ct);
        return Ok(events.Select(x => new
        {
            x.Id,
            x.StagedImportId,
            x.EventType,
            previousStatus = x.PreviousStatus?.ToString(),
            newStatus = x.NewStatus.ToString(),
            payload = JsonSerializer.Deserialize<JsonElement>(x.PayloadJson),
            x.Note,
            x.Actor,
            x.OccurredAtUtc
        }));
    }

    [HttpPost("pending/archive"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> ArchivePending(ArchivePendingRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
            return BadRequest(new ErrorResponse("archive_reason_required", "A reason is required to archive pending records.", HttpContext.TraceIdentifier));

        var pending = await db.StagedImports.Where(item => item.Status == StagingStatus.PendingReview).ToListAsync(ct);
        var actor = User.Identity?.Name ?? User.FindFirst("oid")?.Value ?? "authorised user";
        foreach (var item in pending)
        {
            var previous = item.Status;
            item.Status = StagingStatus.Archived;
            item.ReviewedAtUtc = DateTimeOffset.UtcNow;
            item.ReviewedBy = actor;
            item.ReviewNote = request.Reason.Trim();
            db.StagedImportEvents.Add(StagingAudit.Create(item, "Archived", previous, item.ReviewNote, actor));
        }
        await db.SaveChangesAsync(ct);
        return Ok(new { archived = pending.Count });
    }

    [HttpDelete("pending"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> ClearPending([FromQuery] string confirm, CancellationToken ct)
    {
        if (!string.Equals(confirm, "CLEAR-PENDING", StringComparison.Ordinal))
            return BadRequest(new ErrorResponse("confirmation_required", "Add confirm=CLEAR-PENDING to clear pending staging records.", HttpContext.TraceIdentifier));

        return await ArchivePending(new ArchivePendingRequest("Archived through the legacy clear-pending operation."), ct);
    }

    [HttpPost("{id:guid}/approve"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> Approve(Guid id, ReviewRequest request, CancellationToken ct)
    {
        var staged = await db.StagedImports.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, ct);
        if (staged is null) return NotFound();
        if (staged.EntityType == "order" && IsExplicitPreOrder(staged.PayloadJson))
        {
            return BadRequest(new ErrorResponse(
                "preorder_not_ready",
                "This NWF pre-order is awaiting customer instruction and cannot be accepted into live planning yet. A later NWF tracker snapshot will enrich/supersede it automatically.",
                HttpContext.TraceIdentifier));
        }

        try { return Ok(await service.ReviewAndPromote(id, true, request.Note, User, ct)); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return BadRequest(new ErrorResponse("staging_promotion_failed", ex.Message, HttpContext.TraceIdentifier)); }
        catch (DbUpdateException ex) { return BadRequest(new ErrorResponse("staging_promotion_failed", $"The order could not be approved because the planning schema is incomplete: {ex.GetBaseException().Message}", HttpContext.TraceIdentifier)); }
    }

    [HttpPost("{id:guid}/reject"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> Reject(Guid id, ReviewRequest request, CancellationToken ct)
    {
        try { return Ok(await service.ReviewAndPromote(id, false, request.Note, User, ct)); }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    private static bool IsNegativePalletOrder(StageImportRequest request) =>
        TryGetOrderPalletQuantity(request, out var quantity) && quantity < 0;

    private static bool IsZeroPalletOrderWithoutCompleteRoute(StageImportRequest request) =>
        TryGetOrderPalletQuantity(request, out var quantity) && quantity == 0 && !HasCompleteZeroPalletRoute(request.Payload);

    private static bool TryGetOrderPalletQuantity(StageImportRequest request, out decimal quantity)
    {
        quantity = 0;
        if (!string.Equals(request.EntityType, "order", StringComparison.OrdinalIgnoreCase)) return false;
        if (!TryGetProperty(request.Payload, "pallets", out var pallets)
            && !TryGetProperty(request.Payload, "palletQty", out pallets)
            && !TryGetProperty(request.Payload, "palletQuantity", out pallets)
            && !TryGetProperty(request.Payload, "quantity", out pallets))
            return false;

        return pallets.ValueKind switch
        {
            JsonValueKind.Number => pallets.TryGetDecimal(out quantity),
            JsonValueKind.String => decimal.TryParse(pallets.GetString(), out quantity),
            _ => false
        };
    }

    private static bool HasCompleteZeroPalletRoute(JsonElement payload)
    {
        var collectionSite = Text(payload, "collectionSite") ?? Text(payload, "collectionLocation") ?? Text(payload, "sellerName");
        var deliverySite = Text(payload, "deliverySite") ?? Text(payload, "deliveryLocation") ?? Text(payload, "stallNumber");
        return !string.IsNullOrWhiteSpace(collectionSite)
            && !string.IsNullOrWhiteSpace(deliverySite)
            && DateOnly.TryParse(Text(payload, "collectionDate"), out _)
            && DateOnly.TryParse(Text(payload, "deliveryDate"), out _);
    }

    private static string? Text(JsonElement payload, string name)
    {
        if (!TryGetProperty(payload, name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString()!.Trim(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static bool IsExplicitPreOrder(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            if (TryGetProperty(root, "plannerReady", out var plannerReady) && plannerReady.ValueKind == JsonValueKind.False)
                return true;
            if (TryGetProperty(root, "intakeStatus", out var status) && status.ValueKind == JsonValueKind.String &&
                string.Equals(status.GetString(), "PreOrder", StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGetProperty(JsonElement payload, string name, out JsonElement value)
    {
        if (payload.TryGetProperty(name, out value)) return true;
        foreach (var property in payload.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }
}

public sealed record ArchivePendingRequest(string Reason);
