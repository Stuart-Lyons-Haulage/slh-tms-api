using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Contracts;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/order-intake")]
[Authorize]
public sealed class OrderIntakeController(TmsDbContext db, StagingService stagingService, ILogger<OrderIntakeController> logger) : ControllerBase
{
    private readonly EmailOrderIntakeService emailParser = new();
    private readonly SpecialistMailboxOrderParser specialistParser = new();
    private readonly KnownCustomerMailboxOrderParser knownCustomerParser = new();
    private readonly GenericCsvOrderParser genericCsvParser = new();
    private readonly SainsburyHaulierPlanParser sainsburyParser = new();
    private readonly NwfDailyTrackerParser nwfParser = new();
    private readonly NwfWorkbookSnapshotParser nwfWorkbookParser = new();
    private readonly NwfPalletOrderCsvParser nwfCsvParser = new();

    [HttpPost("email/preview"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Preview([FromBody] MailboxEmailIntakeEnvelope request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.MessageId))
            return BadRequest(new ErrorResponse("missing_message_id", "Mailbox message ID is required.", HttpContext.TraceIdentifier));

        var parserRequest = request.ToParserRequest();
        var parsed = ParseEmail(parserRequest);
        var enrichment = new MailboxOrderIntakeEnrichmentService(db);
        var orders = new List<ParsedEmailOrder>();
        foreach (var order in parsed.Orders)
            orders.Add(await enrichment.EnrichAsync(request, order, ct));
        if (orders.Count == 0 && parsed.IgnoredReason is not null && enrichment.ShouldRetainForReview(request, parsed.IgnoredReason))
            orders.Add(enrichment.BuildReviewException(request, parsed.IgnoredReason, parsed.Warnings));

        return Ok(new
        {
            ignored = orders.Count == 0,
            ignoredReason = orders.Count == 0 ? parsed.IgnoredReason : null,
            warnings = parsed.Warnings,
            orderCount = orders.Count,
            orders = orders.Select(order => new { order.SourceKey, order.NaturalKey, payload = order.Payload, warnings = order.Warnings })
        });
    }

    [HttpPost("email"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Intake([FromBody] MailboxEmailIntakeEnvelope request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.MessageId))
            return BadRequest(new ErrorResponse("missing_message_id", "Mailbox message ID is required so repeated flow runs remain idempotent.", HttpContext.TraceIdentifier));

        var parserRequest = request.ToParserRequest();
        var parsed = ParseEmail(parserRequest);
        var enrichment = new MailboxOrderIntakeEnrichmentService(db);
        var enrichedOrders = new List<ParsedEmailOrder>();
        foreach (var order in parsed.Orders)
            enrichedOrders.Add(await enrichment.EnrichAsync(request, order, ct));

        if (enrichedOrders.Count == 0 && parsed.IgnoredReason is not null)
        {
            if (!enrichment.ShouldRetainForReview(request, parsed.IgnoredReason))
                return Ok(new { ignored = true, reason = parsed.IgnoredReason, staged = 0, existing = 0, superseded = 0, failed = 0, warnings = parsed.Warnings });
            enrichedOrders.Add(enrichment.BuildReviewException(request, parsed.IgnoredReason, parsed.Warnings));
        }

        var staged = 0;
        var existing = 0;
        var superseded = 0;
        var failed = 0;
        var exactDuplicates = 0;
        var records = new List<object>();

        foreach (var rawOrder in enrichedOrders)
        {
            try
            {
                var idempotencyKey = $"email:{CompactKey(request.MessageId)}:{rawOrder.SourceKey}";
                if (idempotencyKey.Length > 200) idempotencyKey = idempotencyKey[..200];

                var already = await db.StagedImports.AsNoTracking().SingleOrDefaultAsync(item => item.IdempotencyKey == idempotencyKey, ct);
                if (already is not null)
                {
                    existing++;
                    records.Add(new { stagingId = already.Id, status = already.Status.ToString(), existing = true, duplicateClassification = "Same message retry", reviewUrl = ReviewUrl(already.Id) });
                    continue;
                }

                var order = rawOrder;
                var matchKeys = ReadMatchKeys(order.Payload);
                var related = await FindRelated(matchKeys, request.MessageId, ct);
                var fingerprint = ReadText(order.Payload, "businessFingerprint");
                var exact = related.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(fingerprint) && string.Equals(candidate.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));
                var strongRelated = related.Where(candidate => candidate.SharedKeys.Any(IsStrongMatchKey)).ToList();

                string duplicateClassification;
                if (exact is not null)
                {
                    duplicateClassification = "Exact duplicate";
                    order = WithField(order, "duplicateClassification", duplicateClassification);
                    var duplicateItem = stagingService.Create(new StageImportRequest("order", idempotencyKey, order.Payload, SourceLabel(request)));
                    duplicateItem.Status = StagingStatus.Rejected;
                    duplicateItem.ReviewedAtUtc = DateTimeOffset.UtcNow;
                    duplicateItem.ReviewedBy = "Mailbox duplicate protection";
                    duplicateItem.ReviewNote = $"Exact duplicate of staging record {exact.Id}. New source email evidence retained; no second live order created.";
                    db.StagedImports.Add(duplicateItem);
                    await db.SaveChangesAsync(ct);
                    exactDuplicates++;
                    records.Add(new { stagingId = duplicateItem.Id, status = duplicateItem.Status.ToString(), existing = false, duplicateClassification, duplicateOf = exact.Id, warnings = order.Warnings, reviewUrl = ReviewUrl(duplicateItem.Id) });
                    continue;
                }

                if (strongRelated.Count > 0)
                {
                    duplicateClassification = "Amendment/update";
                    superseded += await SupersedeOlderPendingByMatchKeys(matchKeys, request.MessageId, ct);
                }
                else if (related.Count > 0)
                {
                    duplicateClassification = "Possible duplicate";
                }
                else
                {
                    duplicateClassification = "New order";
                }

                order = WithField(order, "duplicateClassification", duplicateClassification);
                if (duplicateClassification == "New order" && matchKeys.Count == 0)
                    superseded += await SupersedeOlderPending(order.NaturalKey, request.MessageId, ct);

                var item = stagingService.Create(new StageImportRequest("order", idempotencyKey, order.Payload, SourceLabel(request)));
                db.StagedImports.Add(item);
                await db.SaveChangesAsync(ct);
                staged++;
                records.Add(new
                {
                    stagingId = item.Id,
                    status = item.Status.ToString(),
                    existing = false,
                    duplicateClassification,
                    plannerReady = ReadBool(order.Payload, "plannerReady"),
                    intakeStatus = ReadText(order.Payload, "intakeStatus"),
                    validationStatus = ReadText(order.Payload, "validationStatus"),
                    pallets = ReadNumber(order.Payload, "pallets"),
                    warnings = order.Warnings,
                    reviewUrl = ReviewUrl(item.Id)
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                logger.LogError(ex, "Info mailbox order row {SourceKey} failed for message {MessageId}; continuing with remaining rows.", rawOrder.SourceKey, request.MessageId);
                db.ChangeTracker.Clear();
                records.Add(new { sourceKey = rawOrder.SourceKey, status = "Failed", error = ex.GetBaseException().Message, correlationId = HttpContext.TraceIdentifier });
            }
        }

        logger.LogInformation(
            "Info mailbox intake {MessageId}: staged {Staged}, existing {Existing}, exact duplicates {ExactDuplicates}, superseded {Superseded}, failed {Failed}.",
            request.MessageId, staged, existing, exactDuplicates, superseded, failed);

        var response = new { ignored = false, staged, existing, exactDuplicates, superseded, failed, warnings = parsed.Warnings, records };
        return failed > 0 && staged == 0 && existing == 0 && exactDuplicates == 0 ? StatusCode(StatusCodes.Status207MultiStatus, response) : Accepted(response);
    }

    private EmailIntakeParseResult ParseEmail(MailboxEmailIntakeRequest request) =>
        nwfCsvParser.TryParse(request)
        ?? nwfWorkbookParser.TryParse(request)
        ?? nwfParser.TryParse(request)
        ?? knownCustomerParser.TryParse(request)
        ?? sainsburyParser.TryParse(request)
        ?? specialistParser.TryParse(request)
        ?? genericCsvParser.TryParse(request)
        ?? emailParser.Parse(request);

    private async Task<List<RelatedCandidate>> FindRelated(IReadOnlyCollection<string> currentKeys, string currentMessageId, CancellationToken ct)
    {
        if (currentKeys.Count == 0) return [];
        var keySet = currentKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = await db.StagedImports.AsNoTracking()
            .Where(item => item.EntityType == "order")
            .OrderByDescending(item => item.ReceivedAtUtc)
            .Take(10000)
            .ToListAsync(ct);
        var result = new List<RelatedCandidate>();
        foreach (var candidate in candidates)
        {
            try
            {
                using var document = JsonDocument.Parse(candidate.PayloadJson);
                var root = document.RootElement;
                if (string.Equals(ReadText(root, "sourceMessageId"), currentMessageId, StringComparison.Ordinal)) continue;
                var shared = ReadMatchKeys(root).Where(keySet.Contains).ToList();
                if (shared.Count == 0) continue;
                result.Add(new RelatedCandidate(candidate.Id, ReadText(root, "businessFingerprint"), shared));
            }
            catch (JsonException) { }
        }
        return result;
    }

    private async Task<int> SupersedeOlderPendingByMatchKeys(IReadOnlyCollection<string> currentKeys, string currentMessageId, CancellationToken ct)
    {
        if (currentKeys.Count == 0) return 0;
        var keySet = currentKeys.Where(IsStrongMatchKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (keySet.Count == 0) return 0;
        var candidates = await db.StagedImports.Where(item => item.EntityType == "order" && item.Status == StagingStatus.PendingReview).ToListAsync(ct);
        var matching = new List<StagedImport>();
        foreach (var candidate in candidates)
        {
            try
            {
                using var document = JsonDocument.Parse(candidate.PayloadJson);
                var root = document.RootElement;
                if (string.Equals(ReadText(root, "sourceMessageId"), currentMessageId, StringComparison.Ordinal)) continue;
                if (ReadMatchKeys(root).Any(keySet.Contains)) matching.Add(candidate);
            }
            catch (JsonException) { }
        }
        if (matching.Count == 0) return 0;
        var now = DateTimeOffset.UtcNow;
        foreach (var candidate in matching)
        {
            candidate.Status = StagingStatus.Rejected;
            candidate.ReviewedAtUtc = now;
            candidate.ReviewedBy = "Mailbox amendment supersession";
            candidate.ReviewNote = $"Superseded by a newer order/amendment ({currentMessageId}). Original source evidence retained.";
        }
        await db.SaveChangesAsync(ct);
        return matching.Count;
    }

    private async Task<int> SupersedeOlderPending(string naturalKey, string currentMessageId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(naturalKey)) return 0;
        var marker = $"\"intakeNaturalKey\":\"{EscapeForContains(naturalKey)}\"";
        var candidates = await db.StagedImports.Where(item => item.EntityType == "order" && item.Status == StagingStatus.PendingReview && item.PayloadJson.Contains(marker)).ToListAsync(ct);
        var count = 0;
        foreach (var candidate in candidates)
        {
            try
            {
                using var document = JsonDocument.Parse(candidate.PayloadJson);
                if (string.Equals(ReadText(document.RootElement, "sourceMessageId"), currentMessageId, StringComparison.Ordinal)) continue;
            }
            catch (JsonException) { }
            candidate.Status = StagingStatus.Rejected;
            candidate.ReviewedAtUtc = DateTimeOffset.UtcNow;
            candidate.ReviewedBy = "Mailbox supersession";
            candidate.ReviewNote = $"Superseded automatically by a newer Info mailbox message ({currentMessageId}). Original evidence retained.";
            count++;
        }
        if (count > 0) await db.SaveChangesAsync(ct);
        return count;
    }

    private ParsedEmailOrder WithField(ParsedEmailOrder order, string name, string value)
    {
        var node = JsonNode.Parse(order.Payload.GetRawText()) as JsonObject ?? new JsonObject();
        node[name] = value;
        using var document = JsonDocument.Parse(node.ToJsonString());
        return new ParsedEmailOrder(order.SourceKey, order.NaturalKey, document.RootElement.Clone(), order.Warnings);
    }

    private string ReviewUrl(Guid id) => $"{Request.Scheme}://{Request.Host}/api/v1/staging/{id}";
    private static string SourceLabel(MailboxEmailIntakeEnvelope request) => $"Info mailbox / {(request.SenderAddress ?? "unknown sender").Trim()}";

    private static IReadOnlyList<string> ReadMatchKeys(JsonElement payload)
    {
        if (!TryGetProperty(payload, "intakeMatchKeys", out var value) || value.ValueKind != JsonValueKind.Array) return [];
        return value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
            .Select(item => CanonicalMatchKey(item.GetString()!.Trim())).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsStrongMatchKey(string key) => key.StartsWith("PO|", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("REF|", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("NWF|PRODUCT:", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("NWF|TRANSPORT:", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("NWF|LOAD:", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("NWF|CRATEREF:", StringComparison.OrdinalIgnoreCase);

    private static string CanonicalMatchKey(string key)
    {
        var parts = key.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3 && string.Equals(parts[0], "NWF", StringComparison.OrdinalIgnoreCase) && DateOnly.TryParse(parts[1], out _) &&
            (parts[2].StartsWith("PRODUCT:", StringComparison.OrdinalIgnoreCase) || parts[2].StartsWith("TRANSPORT:", StringComparison.OrdinalIgnoreCase) || parts[2].StartsWith("LOAD:", StringComparison.OrdinalIgnoreCase) || parts[2].StartsWith("CRATEREF:", StringComparison.OrdinalIgnoreCase)))
            return $"NWF|{parts[2].ToUpperInvariant()}";
        return key.ToUpperInvariant();
    }

    private static string? ReadText(JsonElement payload, string name)
    {
        if (!TryGetProperty(payload, name, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;
    }
    private static decimal? ReadNumber(JsonElement payload, string name)
    {
        if (!TryGetProperty(payload, name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), out number) ? number : null;
    }
    private static bool? ReadBool(JsonElement payload, string name)
    {
        if (!TryGetProperty(payload, name, out var value)) return null;
        return value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
    }
    private static bool TryGetProperty(JsonElement payload, string name, out JsonElement value)
    {
        if (payload.TryGetProperty(name, out value)) return true;
        foreach (var property in payload.EnumerateObject()) if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        value = default; return false;
    }
    private static string CompactKey(string value)
    {
        var compact = new string(value.Where(char.IsLetterOrDigit).ToArray());
        return compact.Length <= 96 ? compact : compact[^96..];
    }
    private static string EscapeForContains(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    private sealed record RelatedCandidate(Guid Id, string? Fingerprint, IReadOnlyList<string> SharedKeys);
}
