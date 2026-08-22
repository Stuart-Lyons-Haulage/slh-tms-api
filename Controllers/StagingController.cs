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
    public async Task<IActionResult> List([FromQuery] StagingStatus? status, [FromQuery] string? entityType, [FromQuery] int take = 100, CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 2000);
        var query = db.StagedImports.AsNoTracking().AsQueryable().Where(x => x.Status == (status ?? StagingStatus.PendingReview));
        if (!string.IsNullOrWhiteSpace(entityType)) query = query.Where(x => x.EntityType == entityType.Trim().ToLowerInvariant());
        return Ok(await query.OrderByDescending(x => x.ReceivedAtUtc).Take(take).ToListAsync(ct));
    }

    [HttpPost, Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Stage(StageImportRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)) return BadRequest(new ErrorResponse("invalid_idempotency_key", "IdempotencyKey is required", HttpContext.TraceIdentifier));
        if (request.IdempotencyKey.Length > 200) return BadRequest(new ErrorResponse("invalid_idempotency_key", "IdempotencyKey must be 200 characters or fewer.", HttpContext.TraceIdentifier));
        if (IsExplicitZeroPalletOrder(request)) return Ok(new { ignored = true, reason = "zero_pallet_order", message = "The source row has zero pallets and was retained as source evidence rather than staged as a transport order." });
        var existing = await db.StagedImports.AsNoTracking().SingleOrDefaultAsync(x => x.IdempotencyKey == request.IdempotencyKey, ct);
        if (existing is not null) return Ok(service.ToResponse(existing, Request));
        try
        {
            var item = service.Create(request); db.StagedImports.Add(item); await db.SaveChangesAsync(ct);
            return Accepted(service.ToResponse(item, Request));
        }
        catch (ArgumentException ex) { return BadRequest(new ErrorResponse("invalid_staging_record", ex.Message, HttpContext.TraceIdentifier)); }
    }

    [HttpPost("batch"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> StageBatch(List<StageImportRequest> requests, CancellationToken ct)
    {
        if (requests.Count == 0 || requests.Count > 500) return BadRequest(new ErrorResponse("invalid_batch", "Submit between 1 and 500 records.", HttpContext.TraceIdentifier));
        if (requests.Any(request => string.IsNullOrWhiteSpace(request.IdempotencyKey))) return BadRequest(new ErrorResponse("invalid_idempotency_key", "Every record needs an IdempotencyKey.", HttpContext.TraceIdentifier));
        if (requests.Any(request => request.IdempotencyKey.Length > 200)) return BadRequest(new ErrorResponse("invalid_idempotency_key", "Every IdempotencyKey must be 200 characters or fewer.", HttpContext.TraceIdentifier));
        var filteredRequests = requests.Where(request => !IsExplicitZeroPalletOrder(request)).ToList();
        var skippedZeroPallets = requests.Count - filteredRequests.Count;
        if (filteredRequests.Count == 0) return Accepted(new { received = requests.Count, existing = 0, created = 0, skippedZeroPallets, records = Array.Empty<StageImportResponse>() });
        var keys = filteredRequests.Select(request => request.IdempotencyKey).ToList();
        if (keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != keys.Count) return BadRequest(new ErrorResponse("duplicate_batch_key", "Idempotency keys must be unique within the batch.", HttpContext.TraceIdentifier));
        var existing = await db.StagedImports.AsNoTracking().Where(item => keys.Contains(item.IdempotencyKey)).ToDictionaryAsync(item => item.IdempotencyKey, ct);
        var existingCount = existing.Count;
        var responses = new List<StageImportResponse>();
        try
        {
            foreach (var request in filteredRequests)
            {
                if (existing.TryGetValue(request.IdempotencyKey, out var item)) responses.Add(service.ToResponse(item, Request));
                else { var created = service.Create(request); db.StagedImports.Add(created); responses.Add(service.ToResponse(created, Request)); }
            }
            await db.SaveChangesAsync(ct);
            return Accepted(new { received = requests.Count, existing = existingCount, created = responses.Count - existingCount, skippedZeroPallets, records = responses });
        }
        catch (ArgumentException ex) { return BadRequest(new ErrorResponse("invalid_staging_record", ex.Message, HttpContext.TraceIdentifier)); }
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => (await db.StagedImports.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)) is { } x ? Ok(x) : NotFound();

    [HttpDelete("pending"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> ClearPending([FromQuery] string confirm, CancellationToken ct)
    {
        if (!string.Equals(confirm, "CLEAR-PENDING", StringComparison.Ordinal)) return BadRequest(new ErrorResponse("confirmation_required", "Add confirm=CLEAR-PENDING to clear pending staging records.", HttpContext.TraceIdentifier));
        var count = await db.StagedImports.Where(item => item.Status == StagingStatus.PendingReview).ExecuteDeleteAsync(ct);
        return Ok(new { deleted = count });
    }

    [HttpPost("{id:guid}/approve"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> Approve(Guid id, ReviewRequest request, CancellationToken ct)
    {
        var staged = await db.StagedImports.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, ct);
        if (staged is null) return NotFound();
        if (staged.EntityType == "order" && IsPlannerBlocked(staged.PayloadJson))
            return BadRequest(new ErrorResponse("order_not_ready", "This staged order contains critical/unresolved intake information and cannot be promoted until the planner corrects it. The source evidence remains attached to the staged record.", HttpContext.TraceIdentifier));

        try
        {
            var result = await service.ReviewAndPromote(id, true, request.Note, User, ct);
            if (string.Equals(staged.EntityType, "order", StringComparison.OrdinalIgnoreCase))
                await ApplyApprovedOrderPayload(staged.PayloadJson, ct);
            return Ok(result);
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (JsonException ex) { return BadRequest(new ErrorResponse("staging_promotion_failed", ex.Message, HttpContext.TraceIdentifier)); }
        catch (InvalidOperationException ex) { return BadRequest(new ErrorResponse("staging_promotion_failed", ex.Message, HttpContext.TraceIdentifier)); }
        catch (DbUpdateException ex) { return BadRequest(new ErrorResponse("staging_promotion_failed", $"The order could not be approved because the planning schema is incomplete: {ex.GetBaseException().Message}", HttpContext.TraceIdentifier)); }
    }

    [HttpPost("{id:guid}/reject"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> Reject(Guid id, ReviewRequest request, CancellationToken ct)
    {
        try { return Ok(await service.ReviewAndPromote(id, false, request.Note, User, ct)); }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    private async Task ApplyApprovedOrderPayload(string payloadJson, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(payloadJson);
        var payload = document.RootElement;
        var reference = Text(payload, "poNumber") ?? throw new JsonException("Approved order requires poNumber/reference.");
        var customerCode = Text(payload, "customerCode") ?? throw new JsonException("Approved order requires customerCode.");
        if (!DateOnly.TryParse(Text(payload, "collectionDate"), out var collectionDate)) throw new JsonException("Approved order requires a valid collectionDate.");

        try
        {
            var order = await db.TransportOrders.SingleOrDefaultAsync(x => x.Reference == reference, ct);
            if (order is null) return; // StagingService has just created it on modern schemas.
            order.CustomerCode = Clip(customerCode, 40)!;
            order.CollectionDate = collectionDate;
            order.DeliveryDate = DateOnly.TryParse(Text(payload, "deliveryDate"), out var deliveryDate) ? deliveryDate : null;
            order.DeliveryWindowStartUtc = DateTimeOffset.TryParse(Text(payload, "deliveryWindowStartUtc"), out var start) ? start : null;
            order.DeliveryWindowEndUtc = DateTimeOffset.TryParse(Text(payload, "deliveryWindowEndUtc"), out var end) ? end : null;
            order.Pallets = Int(payload, "pallets");
            order.SellerName = Clip(Text(payload, "sellerName") ?? Text(payload, "collectionSite") ?? Text(payload, "collectionLocation"), 200);
            order.MarketName = Clip(Text(payload, "marketName"), 80);
            order.StallNumber = Clip(Text(payload, "stallNumber") ?? Text(payload, "deliverySite") ?? Text(payload, "deliveryLocation"), 200);
            order.DriverInstructions = Clip(Text(payload, "driverInstructions") ?? Text(payload, "specialInstructions") ?? Text(payload, "loadNotes"), 1000);
            order.MapLink = Clip(Text(payload, "mapLink"), 1000);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex.GetBaseException().Message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase))
        {
            // On legacy databases the approved staged row is the durable live register.
            db.ChangeTracker.Clear();
        }
    }

    private static bool IsExplicitZeroPalletOrder(StageImportRequest request)
    {
        if (!string.Equals(request.EntityType, "order", StringComparison.OrdinalIgnoreCase)) return false;
        if (!TryGetProperty(request.Payload, "pallets", out var pallets) && !TryGetProperty(request.Payload, "palletQty", out pallets) && !TryGetProperty(request.Payload, "palletQuantity", out pallets)) return false;
        return pallets.ValueKind switch { JsonValueKind.Number => pallets.TryGetDecimal(out var number) && number <= 0, JsonValueKind.String => decimal.TryParse(pallets.GetString(), out var number) && number <= 0, _ => false };
    }

    private static bool IsPlannerBlocked(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            if (TryGetProperty(root, "plannerReady", out var ready) && ready.ValueKind == JsonValueKind.False) return true;
            if (TryGetProperty(root, "validationStatus", out var validation) && validation.ValueKind == JsonValueKind.String && string.Equals(validation.GetString(), "Critical", StringComparison.OrdinalIgnoreCase)) return true;
            if (TryGetProperty(root, "intakeStatus", out var status) && status.ValueKind == JsonValueKind.String && (string.Equals(status.GetString(), "PreOrder", StringComparison.OrdinalIgnoreCase) || string.Equals(status.GetString(), "Exception", StringComparison.OrdinalIgnoreCase))) return true;
            return false;
        }
        catch (JsonException) { return true; }
    }

    private static string? Text(JsonElement payload, string name)
    {
        if (!TryGetProperty(payload, name, out var value)) return null;
        return value.ValueKind switch { JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString()!.Trim(), JsonValueKind.Number => value.GetRawText(), _ => null };
    }
    private static int? Int(JsonElement payload, string name) => int.TryParse(Text(payload, name), out var value) ? value : null;
    private static string? Clip(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Length <= max ? value : value[..max];
    private static bool TryGetProperty(JsonElement payload, string name, out JsonElement value)
    {
        if (payload.TryGetProperty(name, out value)) return true;
        foreach (var property in payload.EnumerateObject()) if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        value = default; return false;
    }
}
