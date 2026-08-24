using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
    private readonly NwfPalletOrderCsvParser nwfCsvParser = new();
    private static readonly Regex DateRegex = new(
        @"\b(?<day>0?[1-9]|[12]\d|3[01])[./-](?<month>0?[1-9]|1[0-2])(?:[./-](?<year>20\d{2}|\d{2}))?\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
        {
            if (ShouldStageMappingException(request, parsed))
                return await StageMappingException(request, parsed, ct);
            return Ok(new { ignored = true, reason = parsed.IgnoredReason, staged = 0, existing = 0, superseded = 0, warnings = parsed.Warnings });
        }

        var staged = 0;
        var existing = 0;
        var superseded = 0;
        var records = new List<object>();

        // NWF tracker workbooks and pallet-order CSVs are versioned snapshots.
        // Supersede older pending versions by any stable alias before inserting
        // the new snapshot rows. Strong NWF references are canonicalised without
        // the planning date so corrected customer snapshots replace earlier rows.
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
            var stagedPayload = EnrichSourceEvidence(order.Payload, request);
            var item = stagingService.Create(new StageImportRequest(
                "order",
                idempotencyKey,
                stagedPayload,
                $"Info mailbox / {(request.SenderAddress ?? "unknown sender").Trim()}"));
            db.StagedImports.Add(item);
            db.StagedImportEvents.Add(StagingAudit.Create(item, "Received"));
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
        nwfCsvParser.TryParse(request)
        ?? nwfWorkbookParser.TryParse(request)
        ?? nwfParser.TryParse(request)
        ?? sainsburyParser.TryParse(request)
        ?? specialistParser.TryParse(request)
        ?? emailParser.Parse(request);

    private async Task<IActionResult> StageMappingException(MailboxEmailIntakeRequest request, EmailIntakeParseResult parsed, CancellationToken ct)
    {
        var idempotencyKey = $"email:{CompactKey(request.MessageId)}:mapping-exception";
        if (idempotencyKey.Length > 200) idempotencyKey = idempotencyKey[..200];
        var already = await db.StagedImports.AsNoTracking().SingleOrDefaultAsync(item => item.IdempotencyKey == idempotencyKey, ct);
        if (already is not null)
            return Accepted(new
            {
                ignored = false,
                staged = 0,
                existing = 1,
                superseded = 0,
                warnings = parsed.Warnings,
                records = new[]
                {
                    new
                    {
                        stagingId = already.Id,
                        status = already.Status.ToString(),
                        existing = true,
                        plannerReady = false,
                        intakeStatus = "MappingException",
                        warnings = parsed.Warnings.Append(parsed.IgnoredReason).Where(value => !string.IsNullOrWhiteSpace(value)).ToList(),
                        reviewUrl = $"{Request.Scheme}://{Request.Host}/api/v1/staging/{already.Id}"
                    }
                }
            });

        var warnings = parsed.Warnings.Append(parsed.IgnoredReason).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var date = ExtractDate(request) ?? DateOnly.FromDateTime((request.ReceivedAtUtc ?? DateTimeOffset.UtcNow).DateTime);
        var payload = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["poNumber"] = $"MAPPING-{CompactKey(request.MessageId)[..Math.Min(CompactKey(request.MessageId).Length, 24)]}",
            ["customerCode"] = InferMappingCustomer(request),
            ["collectionDate"] = date.ToString("yyyy-MM-dd"),
            ["deliveryDate"] = date.ToString("yyyy-MM-dd"),
            ["pallets"] = null,
            ["sellerName"] = null,
            ["marketName"] = InferMappingCustomer(request),
            ["stallNumber"] = null,
            ["jobType"] = "Mapping Exception",
            ["driverInstructions"] = "Mailbox order needs manual mapping before approval.",
            ["plannerReady"] = false,
            ["intakeStatus"] = "MappingException",
            ["intakeConfidence"] = "Low",
            ["intakeWarnings"] = warnings,
            ["intakeParser"] = "Mapping Exception",
            ["sourceMessageId"] = request.MessageId,
            ["sourceInternetMessageId"] = request.InternetMessageId,
            ["sourceSender"] = request.SenderAddress,
            ["sourceSenderName"] = request.SenderName,
            ["sourceSubject"] = request.Subject,
            ["sourceReceivedAtUtc"] = request.ReceivedAtUtc,
            ["sourceWebLink"] = request.WebLink,
            ["sourceAttachmentNames"] = (request.Attachments ?? []).Where(item => item.IsInline != true).Select(item => item.Name).Where(name => !string.IsNullOrWhiteSpace(name)).ToList()
        });
        var stagedPayload = EnrichSourceEvidence(payload, request);
        var item = stagingService.Create(new StageImportRequest(
            "order",
            idempotencyKey,
            stagedPayload,
            $"Info mailbox mapping exception / {(request.SenderAddress ?? "unknown sender").Trim()}"));
        db.StagedImports.Add(item);
        db.StagedImportEvents.Add(StagingAudit.Create(item, "MappingException", item.Status, "Plausible mailbox order could not be parsed automatically; source evidence retained for review.", "Info mailbox intake"));
        await db.SaveChangesAsync(ct);

        logger.LogWarning(
            "Info mailbox intake {MessageId}: staged mapping exception for {Sender} / {Subject}. Reason: {Reason}",
            request.MessageId,
            request.SenderAddress,
            request.Subject,
            parsed.IgnoredReason);

        return Accepted(new
        {
            ignored = false,
            staged = 1,
            existing = 0,
            superseded = 0,
            warnings,
            records = new[]
            {
                new
                {
                    stagingId = item.Id,
                    status = item.Status.ToString(),
                    existing = false,
                    plannerReady = false,
                    intakeStatus = "MappingException",
                    warnings,
                    reviewUrl = $"{Request.Scheme}://{Request.Host}/api/v1/staging/{item.Id}"
                }
            }
        });
    }

    private static bool ShouldStageMappingException(MailboxEmailIntakeRequest request, EmailIntakeParseResult parsed)
    {
        if (string.IsNullOrWhiteSpace(request.MessageId)) return false;
        var reason = parsed.IgnoredReason ?? string.Empty;
        if (reason.Contains("Internal TMS", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("Operational request", StringComparison.OrdinalIgnoreCase)) return false;
        var sender = request.SenderAddress ?? string.Empty;
        var subject = request.Subject ?? string.Empty;
        var body = $"{request.BodyText} {request.BodyHtml}";
        var attachments = string.Join(" ", (request.Attachments ?? []).Select(item => item.Name));
        var value = $"{sender} {subject} {body} {attachments}";
        return sender.EndsWith("@nwfltd.co.uk", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("NWAY", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("NWF", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("IFCO", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("crate", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("tray", StringComparison.OrdinalIgnoreCase) ||
               (value.Contains("pallet", StringComparison.OrdinalIgnoreCase) && value.Contains("order", StringComparison.OrdinalIgnoreCase)) ||
               value.Contains("transport", StringComparison.OrdinalIgnoreCase);
    }

    private static string InferMappingCustomer(MailboxEmailIntakeRequest request)
    {
        var value = $"{request.SenderAddress} {request.Subject}";
        if (value.Contains("IFCO", StringComparison.OrdinalIgnoreCase))
            return "IFCO";
        if (value.Contains("NWAY", StringComparison.OrdinalIgnoreCase) || value.Contains("NWF", StringComparison.OrdinalIgnoreCase) ||
            (request.SenderAddress ?? string.Empty).EndsWith("@nwfltd.co.uk", StringComparison.OrdinalIgnoreCase))
            return "NWF";
        return "EMAIL";
    }

    private static DateOnly? ExtractDate(MailboxEmailIntakeRequest request)
    {
        var value = $"{request.Subject}\n{request.BodyText}\n{string.Join("\n", (request.Attachments ?? []).Select(item => item.Name))}";
        var match = DateRegex.Match(value);
        if (!match.Success) return null;
        var day = int.Parse(match.Groups["day"].Value);
        var month = int.Parse(match.Groups["month"].Value);
        var yearText = match.Groups["year"].Value;
        var year = string.IsNullOrWhiteSpace(yearText)
            ? (request.ReceivedAtUtc ?? DateTimeOffset.UtcNow).Year
            : yearText.Length == 2
                ? 2000 + int.Parse(yearText)
                : int.Parse(yearText);
        try { return new DateOnly(year, month, day); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

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
            var previous = candidate.Status;
            candidate.Status = StagingStatus.Rejected;
            candidate.ReviewedAtUtc = now;
            candidate.ReviewedBy = "Mailbox snapshot supersession";
            candidate.ReviewNote = $"Superseded by a newer NWF/Info mailbox snapshot ({currentMessageId}). Original evidence retained.";
            db.StagedImportEvents.Add(StagingAudit.Create(candidate, "Superseded", previous, candidate.ReviewNote, candidate.ReviewedBy));
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

            var previous = candidate.Status;
            candidate.Status = StagingStatus.Rejected;
            candidate.ReviewedAtUtc = now;
            candidate.ReviewedBy = "Mailbox supersession";
            candidate.ReviewNote = $"Superseded automatically by a newer Info mailbox message ({currentMessageId}). Original evidence retained.";
            db.StagedImportEvents.Add(StagingAudit.Create(candidate, "Superseded", previous, candidate.ReviewNote, candidate.ReviewedBy));
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
            .Select(item => CanonicalMatchKey(item.GetString()!.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string CanonicalMatchKey(string key)
    {
        var parts = key.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3 &&
            string.Equals(parts[0], "NWF", StringComparison.OrdinalIgnoreCase) &&
            DateOnly.TryParse(parts[1], out _) &&
            (parts[2].StartsWith("PRODUCT:", StringComparison.OrdinalIgnoreCase) ||
             parts[2].StartsWith("TRANSPORT:", StringComparison.OrdinalIgnoreCase) ||
             parts[2].StartsWith("LOAD:", StringComparison.OrdinalIgnoreCase) ||
             parts[2].StartsWith("CRATEREF:", StringComparison.OrdinalIgnoreCase)))
        {
            return $"NWF|{parts[2].ToUpperInvariant()}";
        }

        if (parts.Length >= 3 &&
            string.Equals(parts[0], "IFCO", StringComparison.OrdinalIgnoreCase) &&
            DateOnly.TryParse(parts[1], out _) &&
            (parts[2].StartsWith("TRANSPORT:", StringComparison.OrdinalIgnoreCase) ||
             parts[2].StartsWith("CRATEPO:", StringComparison.OrdinalIgnoreCase) ||
             parts[2].StartsWith("LOAD:", StringComparison.OrdinalIgnoreCase)))
        {
            return $"IFCO|{parts[2].ToUpperInvariant()}";
        }

        // Route/loading fallback identities remain date-scoped because they are
        // not unique enough to link across planning dates safely.
        return key.ToUpperInvariant();
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

    internal static JsonElement EnrichSourceEvidence(JsonElement payload, MailboxEmailIntakeRequest request)
    {
        var root = JsonNode.Parse(payload.GetRawText())?.AsObject() ?? new JsonObject();
        root["sourceMailbox"] = request.Mailbox;
        root["sourceSender"] = request.SenderAddress;
        root["sourceSenderName"] = request.SenderName;
        root["sourceEmailSubject"] = request.Subject;
        root["sourceEmailReceivedAt"] = request.ReceivedAtUtc;
        root["sourceEmailMessageId"] = request.MessageId;
        root["sourceInternetMessageId"] = request.InternetMessageId;
        root["sourceConversationId"] = request.ConversationId;
        root["sourceEmailWebLink"] = request.WebLink;
        root["sourceBodyFormat"] = request.BodyFormat;
        root["sourceImportance"] = request.Importance;
        root["importCorrelationId"] = request.CorrelationId;
        root["sourceToRecipients"] = request.ToRecipients is { } to ? JsonNode.Parse(to.GetRawText()) : null;
        root["sourceCcRecipients"] = request.CcRecipients is { } cc ? JsonNode.Parse(cc.GetRawText()) : null;

        var attachments = new JsonArray();
        foreach (var attachment in request.Attachments ?? [])
        {
            attachments.Add(new JsonObject
            {
                ["name"] = attachment.Name,
                ["contentType"] = attachment.ContentType,
                ["contentId"] = attachment.ContentId,
                ["size"] = attachment.Size,
                ["isInline"] = attachment.IsInline
            });
        }
        root["sourceAttachments"] = attachments;
        root["importSource"] = "PowerAutomate/InfoMailbox";
        root["reviewStatus"] = "Pending Review";
        return JsonSerializer.SerializeToElement(root);
    }
}
