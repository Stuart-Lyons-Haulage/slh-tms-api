using System.Text.Json;
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
    private readonly SainsburyHaulierPlanParser sainsburyParser = new();
    private readonly NwfDailyTrackerParser nwfParser = new();
    private readonly NwfWorkbookSnapshotParser nwfWorkbookParser = new();

    [HttpPost("email/preview"), Authorize(Policy = "TmsWrite")]
    public IActionResult Preview([FromBody] MailboxEmailIntakeRequest request)
    {
        var parsed = ParseEmail(request);
        return Ok(new
        {
            ignored = parsed.IgnoredReason is not null,
            ignoredReason = parsed.IgnoredReason,
            warnings = parsed.Warnings,
            orderCount = parsed.Orders.Count,
            orders = parsed.Orders.Select(order => new
            {
                order.SourceKey,
                order.NaturalKey,
                payload = order.Payload,
                warnings = order.Warnings
            })
        });
    }

    [HttpPost("email"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Intake([FromBody] MailboxEmailIntakeRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.MessageId))
            return BadRequest(new ErrorResponse("missing_message_id", "Mailbox message ID is required so repeated flow runs remain idempotent.", HttpContext.TraceIdentifier));

        var parsed = ParseEmail(request);
        if (parsed.IgnoredReason is not null)
            return Ok(new { ignored = true, reason = parsed.IgnoredReason, staged = 0, existing = 0, superseded = 0, warnings = parsed.Warnings });

        var staged = 0;
        var existing = 0;
        var superseded = 0;
        var records = new List<object>();

        // NWF tracker workbooks are versioned snapshots. Supersede older pending
        // versions by any stable alias (Product PO, Transport PO, Load Ref or
        // route identity) before inserting the new snapshot rows. This allows a
        // blank pre-order to become enriched without creating a duplicate.
        var matchKeys = parsed.Orders
            .SelectMany(order => ReadMatchKeys(order.Payload))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (matchKeys.Count > 0)
            superseded += await SupersedeOlderPendingByMatchKeys(matchKeys, request.MessageId, ct);

        foreach (var order in parsed.Orders)
        {
            var idempotencyKey = $"email:{CompactKey(request.MessageId)}:{order.SourceKey}";
            if (idempotencyKey.Length > 200) idempotencyKey = idempotencyKey[..200];

            var already = await db.StagedImports.AsNoTracking().SingleOrDefaultAsync(item => item.IdempotencyKey == idempotencyKey, ct);
            if (already is not null)
            {
                existing++;
                records.Add(new
                {
                    stagingId = already.Id,
                    status = already.Status.ToString(),
                    existing = true,
                    reviewUrl = $"{Request.Scheme}://{Request.Host}/api/v1/staging/{already.Id}"
                });
                continue;
            }

            superseded += await SupersedeOlderPending(order.NaturalKey, request.MessageId, ct);
            var item = stagingService.Create(new StageImportRequest(
                "order",
                idempotencyKey,
                order.Payload,
                $"Info mailbox / {(request.SenderAddress ?? "unknown sender").Trim()}"));
            db.StagedImports.Add(item);
            await db.SaveChangesAsync(ct);
            staged++;

            records.Add(new
            {
                stagingId = item.Id,
                status = item.Status.ToString(),
                existing = false,
                plannerReady = ReadBool(order.Payload, "plannerReady"),
                intakeStatus = ReadText(order.Payload, "intakeStatus"),
                warnings = order.Warnings,
                reviewUrl = $"{Request.Scheme}://{Request.Host}/api/v1/staging/{item.Id}"
            });
        }

        logger.LogInformation(
            "Info mailbox intake {MessageId}: staged {Staged}, existing {Existing}, superseded {Superseded}, parser warnings {Warnings}.",
            request.MessageId, staged, existing, superseded, parsed.Warnings.Count);

        return Accepted(new { ignored = false, staged, existing, superseded, warnings = parsed.Warnings, records });
    }

    private EmailIntakeParseResult ParseEmail(MailboxEmailIntakeRequest request) =>
        nwfWorkbookParser.TryParse(request)
        ?? nwfParser.TryParse(request)
        ?? sainsburyParser.TryParse(request)
        ?? specialistParser.TryParse(request)
        ?? emailParser.Parse(request);

    private async Task<int> SupersedeOlderPendingByMatchKeys(IReadOnlyCollection<string> currentKeys, string currentMessageId, CancellationToken ct)
    {
        if (currentKeys.Count == 0) return 0;
        var keySet = currentKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = await db.StagedImports
            .Where(item => item.EntityType == "order" && item.Status == StagingStatus.PendingReview)
            .ToListAsync(ct);

        var matching = new List<StagedImport>();
        foreach (var candidate in candidates)
        {
            try
            {
                using var document = JsonDocument.Parse(candidate.PayloadJson);
                var root = document.RootElement;
                if (string.Equals(ReadText(root, "sourceMessageId"), currentMessageId, StringComparison.Ordinal))
                    continue;
                if (ReadMatchKeys(root).Any(keySet.Contains))
                    matching.Add(candidate);
            }
            catch (JsonException)
            {
                // A malformed legacy staging payload should not block the new
                // mailbox snapshot; it remains visible for manual review.
            }
        }

        if (matching.Count == 0) return 0;
        var now = DateTimeOffset.UtcNow;
        foreach (var candidate in matching)
        {
            candidate.Status = StagingStatus.Rejected;
            candidate.ReviewedAtUtc = now;
            candidate.ReviewedBy = "Mailbox snapshot supersession";
            candidate.ReviewNote = $"Superseded by a newer NWF/Info mailbox snapshot ({currentMessageId}). Original evidence retained.";
        }
        await db.SaveChangesAsync(ct);
        return matching.Count;
    }

    private async Task<int> SupersedeOlderPending(string naturalKey, string currentMessageId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(naturalKey)) return 0;
        var marker = $"\"intakeNaturalKey\":\"{EscapeForContains(naturalKey)}\"";
        var candidates = await db.StagedImports
            .Where(item => item.EntityType == "order" && item.Status == StagingStatus.PendingReview && item.PayloadJson.Contains(marker))
            .ToListAsync(ct);
        if (candidates.Count == 0) return 0;

        var now = DateTimeOffset.UtcNow;
        var count = 0;
        foreach (var candidate in candidates)
        {
            try
            {
                using var document = JsonDocument.Parse(candidate.PayloadJson);
                if (string.Equals(ReadText(document.RootElement, "sourceMessageId"), currentMessageId, StringComparison.Ordinal))
                    continue;
            }
            catch (JsonException) { }

            candidate.Status = StagingStatus.Rejected;
            candidate.ReviewedAtUtc = now;
            candidate.ReviewedBy = "Mailbox supersession";
            candidate.ReviewNote = $"Superseded automatically by a newer Info mailbox message ({currentMessageId}). Original evidence retained.";
            count++;
        }
        if (count > 0) await db.SaveChangesAsync(ct);
        return count;
    }

    private static IReadOnlyList<string> ReadMatchKeys(JsonElement payload)
    {
        if (!TryGetProperty(payload, "intakeMatchKeys", out var value) || value.ValueKind != JsonValueKind.Array)
            return [];
        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
            .Select(item => item.GetString()!.Trim())
            .ToList();
    }

    private static string? ReadText(JsonElement payload, string name)
    {
        if (!TryGetProperty(payload, name, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;
    }

    private static bool? ReadBool(JsonElement payload, string name)
    {
        if (!TryGetProperty(payload, name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
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

    private static string CompactKey(string value)
    {
        var compact = new string(value.Where(char.IsLetterOrDigit).ToArray());
        return compact.Length <= 96 ? compact : compact[^96..];
    }

    private static string EscapeForContains(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
