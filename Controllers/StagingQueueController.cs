using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/staging/queue")]
[Authorize]
public sealed class StagingQueueController(TmsDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] StagingStatus? status,
        [FromQuery] string? entityType,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 100,
        CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var targetStatus = status ?? StagingStatus.PendingReview;
        var query = db.StagedImports.AsNoTracking().Where(item => item.Status == targetStatus);
        if (!string.IsNullOrWhiteSpace(entityType))
            query = query.Where(item => item.EntityType == entityType.Trim().ToLowerInvariant());

        var total = await query.CountAsync(ct);
        var offset = (page - 1) * pageSize;
        var rows = await query
            .OrderByDescending(item => item.ReceivedAtUtc)
            .ThenByDescending(item => item.Id)
            .Skip(offset)
            .Take(pageSize)
            .Select(item => new StagingQueueRawRow(
                item.Id,
                item.EntityType,
                item.IdempotencyKey,
                item.PayloadJson,
                item.Status,
                item.Source,
                item.ReceivedAtUtc,
                item.ReviewedAtUtc,
                item.ReviewedBy,
                item.ReviewNote))
            .ToListAsync(ct);

        var records = rows.Select(StagingQueueProjection.ToSummary).ToList();
        return Ok(new
        {
            page,
            pageSize,
            total,
            hasMore = offset + rows.Count < total,
            records
        });
    }
}

internal sealed record StagingQueueRawRow(
    Guid Id,
    string EntityType,
    string IdempotencyKey,
    string PayloadJson,
    StagingStatus Status,
    string? Source,
    DateTimeOffset ReceivedAtUtc,
    DateTimeOffset? ReviewedAtUtc,
    string? ReviewedBy,
    string? ReviewNote);

internal static class StagingQueueProjection
{
    private static readonly string[] SummaryFields =
    [
        "poNumber", "customerPo", "customerRef", "poRef", "productPo", "cratePo", "transportPo",
        "customerCode", "collectionDate", "deliveryDate", "pallets", "sellerName", "stallNumber",
        "requestedTime", "overnightRoute", "wave", "routeTiming", "jobType", "driverInstructions",
        "plannerReady", "intakeStatus", "intakeConfidence", "intakeWarnings", "intakeParser",
        "emailRouteMatched", "emailRouteId", "emailRouteSender", "emailRouteIdentityOnly", "emailRouteRequiresReview",
        "orderIntakeRouteRuleId", "orderIntakeRouteConfidenceScore", "orderIntakeRouteMatchedDimensions",
        "orderIntakeRouteRequiresReview", "orderIntakeRouteExplanation", "orderIntakeRouteAlternatives",
        "sourceSubject", "sourceEmailSubject", "sourceMessageId", "sourceEmailMessageId",
        "sourceInternetMessageId", "sourceReceivedAtUtc", "sourceEmailReceivedAt", "sourceWebLink",
        "sourceEmailWebLink", "sourceAttachmentName"
    ];

    public static object ToSummary(StagingQueueRawRow row) => new
    {
        id = row.Id,
        entityType = row.EntityType,
        idempotencyKey = row.IdempotencyKey,
        payloadJson = BuildPayloadSummary(row.PayloadJson),
        status = row.Status.ToString(),
        source = row.Source,
        receivedAtUtc = row.ReceivedAtUtc,
        reviewedAtUtc = row.ReviewedAtUtc,
        reviewedBy = row.ReviewedBy,
        reviewNote = row.ReviewNote
    };

    internal static string BuildPayloadSummary(string payloadJson)
    {
        try
        {
            var source = JsonNode.Parse(payloadJson)?.AsObject();
            if (source is null) return "{}";

            var summary = new JsonObject();
            foreach (var field in SummaryFields)
            {
                var match = source.FirstOrDefault(property => string.Equals(property.Key, field, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(match.Key))
                    summary[field] = match.Value?.DeepClone();
            }

            if (!summary.ContainsKey("sourceAttachmentName") &&
                source.FirstOrDefault(property => string.Equals(property.Key, "sourceAttachments", StringComparison.OrdinalIgnoreCase)).Value is JsonArray attachments)
            {
                var attachmentName = attachments
                    .OfType<JsonObject>()
                    .FirstOrDefault(item => item["isInline"]?.GetValue<bool>() != true)?["name"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(attachmentName))
                    summary["sourceAttachmentName"] = attachmentName;
            }

            return summary.ToJsonString();
        }
        catch (JsonException)
        {
            return "{}";
        }
    }
}
