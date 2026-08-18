using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/customer-communications")]
[Authorize]
public sealed class CustomerCommunicationsController(TmsDbContext db) : ControllerBase
{
    private const string SentMarker = "[CUSTOMER-ACK:SENT:";

    [HttpGet("pending"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Pending([FromQuery] int take = 50, CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 200);
        var staged = await db.StagedImports
            .AsNoTracking()
            .Where(item => item.EntityType == "order" &&
                           item.Source.StartsWith("Info mailbox") &&
                           (item.Status == StagingStatus.PendingReview || item.Status == StagingStatus.Promoted))
            .OrderByDescending(item => item.ReceivedAtUtc)
            .Take(2000)
            .ToListAsync(ct);

        var parsed = staged
            .Select(TryParse)
            .Where(item => item is not null)
            .Cast<CommunicationSource>()
            .Where(item => !string.IsNullOrWhiteSpace(item.SourceMessageId) &&
                           !string.IsNullOrWhiteSpace(item.SourceSender) &&
                           !item.SourceSender.EndsWith("@lyonshaulage.com", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var pending = new List<object>();
        foreach (var group in parsed.GroupBy(item => item.SourceMessageId!, StringComparer.Ordinal))
        {
            if (group.Any(item => item.ReviewNote?.Contains(SentMarker, StringComparison.Ordinal) == true))
                continue;

            var promoted = group.Where(item => item.Status == StagingStatus.Promoted).ToList();
            if (promoted.Count == 0)
                continue;

            var accepted = promoted.Where(item => item.PlannerReady != false &&
                                                   !string.Equals(item.IntakeStatus, "PreOrder", StringComparison.OrdinalIgnoreCase))
                                   .ToList();
            if (accepted.Count == 0)
                continue;

            var awaitingInstruction = group.Count(item => item.Status == StagingStatus.PendingReview &&
                                                           (item.PlannerReady == false ||
                                                            string.Equals(item.IntakeStatus, "PreOrder", StringComparison.OrdinalIgnoreCase)));
            var first = accepted[0];
            var key = CommunicationKey(group.Key);
            var trackerSummary = accepted.Any(item => string.Equals(item.CustomerCode, "NWF", StringComparison.OrdinalIgnoreCase)) || accepted.Count > 1;
            pending.Add(new
            {
                communicationKey = key,
                kind = trackerSummary ? "OrderSummaryAccepted" : "OrderAccepted",
                sourceMessageId = first.SourceMessageId,
                sourceInternetMessageId = first.SourceInternetMessageId,
                sourceSubject = first.SourceSubject,
                replyTo = first.SourceSender,
                acceptedCount = accepted.Count,
                awaitingInstructionCount = awaitingInstruction,
                references = accepted.Select(item => item.Reference).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToList(),
                bodyHtml = BuildAcceptedHtml(accepted, awaitingInstruction, trackerSummary),
                idempotencyKey = $"customer-ack:{key}"
            });

            if (pending.Count >= take)
                break;
        }

        return Ok(new { count = pending.Count, communications = pending });
    }

    [HttpPost("{communicationKey}/sent"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> MarkSent(string communicationKey, [FromBody] CommunicationSentRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(communicationKey)) return BadRequest(new { code = "communication_key_required" });
        var staged = await db.StagedImports
            .Where(item => item.EntityType == "order" && item.Source.StartsWith("Info mailbox"))
            .OrderByDescending(item => item.ReceivedAtUtc)
            .Take(2000)
            .ToListAsync(ct);

        var matching = staged
            .Select(item => new { Item = item, Parsed = TryParse(item) })
            .Where(pair => pair.Parsed is not null && CommunicationKey(pair.Parsed.SourceMessageId!) == communicationKey)
            .ToList();
        if (matching.Count == 0) return NotFound();

        if (matching.Any(pair => pair.Item.ReviewNote?.Contains(SentMarker, StringComparison.Ordinal) == true))
            return Ok(new { communicationKey, alreadySent = true });

        var timestamp = DateTimeOffset.UtcNow;
        var marker = $"{SentMarker}{communicationKey}:{timestamp:O}]";
        foreach (var pair in matching)
        {
            var detail = string.IsNullOrWhiteSpace(request.ProviderMessageId)
                ? marker
                : $"{marker} ProviderMessageId={request.ProviderMessageId}";
            pair.Item.ReviewNote = string.Join(" | ", new[] { pair.Item.ReviewNote, detail }.Where(value => !string.IsNullOrWhiteSpace(value)));
        }
        await db.SaveChangesAsync(ct);
        return Ok(new { communicationKey, sentAtUtc = timestamp, marked = matching.Count });
    }

    private static CommunicationSource? TryParse(StagedImport item)
    {
        try
        {
            using var document = JsonDocument.Parse(item.PayloadJson);
            var payload = document.RootElement;
            return new CommunicationSource(
                item.Status,
                item.ReviewNote,
                Text(payload, "sourceMessageId"),
                Text(payload, "sourceInternetMessageId"),
                Text(payload, "sourceSender"),
                Text(payload, "sourceSubject"),
                Text(payload, "poNumber") ?? Text(payload, "customerPo") ?? Text(payload, "productPo"),
                Text(payload, "customerCode"),
                Text(payload, "collectionDate"),
                Text(payload, "deliveryDate"),
                Text(payload, "sellerName"),
                Text(payload, "stallNumber"),
                Int(payload, "pallets"),
                Bool(payload, "plannerReady"),
                Text(payload, "intakeStatus"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string BuildAcceptedHtml(IReadOnlyList<CommunicationSource> accepted, int awaitingInstruction, bool summary)
    {
        var encoder = HtmlEncoder.Default;
        var html = new StringBuilder();
        html.Append("<p>Thank you for your transport instruction.</p>");
        html.Append(summary
            ? $"<p>We have reviewed the latest instruction and <strong>{accepted.Count}</strong> movement(s) have been accepted and passed to the Stuart Lyons Haulage Planning Team.</p>"
            : "<p>Your order has been received, reviewed and passed to the Stuart Lyons Haulage Planning Team for the requested collection/delivery.</p>");
        html.Append("<table style=\"border-collapse:collapse;border:1px solid #d1d5db\"><thead><tr>");
        foreach (var heading in new[] { "Reference", "Collection", "Collection date", "Delivery", "Delivery date", "Pallets" })
            html.Append($"<th style=\"padding:6px;border:1px solid #d1d5db;text-align:left\">{heading}</th>");
        html.Append("</tr></thead><tbody>");
        foreach (var order in accepted)
        {
            html.Append("<tr>");
            foreach (var value in new[] { order.Reference, order.CollectionSite, order.CollectionDate, order.DeliverySite, order.DeliveryDate, order.Pallets?.ToString() })
                html.Append($"<td style=\"padding:6px;border:1px solid #d1d5db\">{encoder.Encode(value ?? "—")}</td>");
            html.Append("</tr>");
        }
        html.Append("</tbody></table>");
        if (awaitingInstruction > 0)
            html.Append($"<p><strong>{awaitingInstruction}</strong> pre-order item(s) remain recorded as awaiting further instruction and have not yet been committed to Planning.</p>");
        html.Append("<p>If any of the above information changes, please reply to this email quoting the relevant reference. Amendments and cancellations will be reviewed against the existing order.</p>");
        html.Append("<p>Kind regards,<br/>Stuart Lyons Haulage<br/>Transport Planning</p>");
        return html.ToString();
    }

    private static string CommunicationKey(string sourceMessageId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sourceMessageId));
        return Convert.ToHexString(bytes)[..24].ToLowerInvariant();
    }

    private static string? Text(JsonElement payload, string name)
    {
        if (!TryGet(payload, name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString()!.Trim(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static int? Int(JsonElement payload, string name) => int.TryParse(Text(payload, name), out var value) ? value : null;
    private static bool? Bool(JsonElement payload, string name)
    {
        if (!TryGet(payload, name, out var value)) return null;
        return value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
    }

    private static bool TryGet(JsonElement payload, string name, out JsonElement value)
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

    private sealed record CommunicationSource(
        StagingStatus Status,
        string? ReviewNote,
        string? SourceMessageId,
        string? SourceInternetMessageId,
        string? SourceSender,
        string? SourceSubject,
        string? Reference,
        string? CustomerCode,
        string? CollectionDate,
        string? DeliveryDate,
        string? CollectionSite,
        string? DeliverySite,
        int? Pallets,
        bool? PlannerReady,
        string? IntakeStatus);
}

public sealed record CommunicationSentRequest(string? ProviderMessageId);
