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
public sealed class OrderIntakeController(
    TmsDbContext db,
    StagingService stagingService,
    EmailOrderIntakeService emailParser,
    ILogger<OrderIntakeController> logger) : ControllerBase
{
    [HttpPost("email/preview"), Authorize(Policy = "TmsWrite")]
    public IActionResult Preview([FromBody] MailboxEmailIntakeRequest request)
    {
        var parsed = emailParser.Parse(request);
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
    public async Task<IActionResult> Intake(
        [FromBody] MailboxEmailIntakeRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.MessageId))
            return BadRequest(new ErrorResponse(
                "missing_message_id",
                "Mailbox message ID is required so repeated flow runs remain idempotent.",
                HttpContext.TraceIdentifier));

        var parsed = emailParser.Parse(request);
        if (parsed.IgnoredReason is not null)
        {
            return Ok(new
            {
                ignored = true,
                reason = parsed.IgnoredReason,
                staged = 0,
                existing = 0,
                superseded = 0,
                warnings = parsed.Warnings
            });
        }

        var staged = 0;
        var existing = 0;
        var superseded = 0;
        var records = new List<object>();

        foreach (var order in parsed.Orders)
        {
            var idempotencyKey = $"email:{CompactKey(request.MessageId)}:{order.SourceKey}";
            if (idempotencyKey.Length > 200)
                idempotencyKey = idempotencyKey[..200];

            var already = await db.StagedImports
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.IdempotencyKey == idempotencyKey, ct);

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

            var stagedRequest = new StageImportRequest(
                "order",
                idempotencyKey,
                order.Payload,
                $"Info mailbox / {(request.SenderAddress ?? "unknown sender").Trim()}");

            var item = stagingService.Create(stagedRequest);
            db.StagedImports.Add(item);
            await db.SaveChangesAsync(ct);
            staged++;

            records.Add(new
            {
                stagingId = item.Id,
                status = item.Status.ToString(),
                existing = false,
                warnings = order.Warnings,
                reviewUrl = $"{Request.Scheme}://{Request.Host}/api/v1/staging/{item.Id}"
            });
        }

        logger.LogInformation(
            "Info mailbox intake {MessageId}: staged {Staged}, existing {Existing}, superseded {Superseded}, parser warnings {Warnings}.",
            request.MessageId,
            staged,
            existing,
            superseded,
            parsed.Warnings.Count);

        return Accepted(new
        {
            ignored = false,
            staged,
            existing,
            superseded,
            warnings = parsed.Warnings,
            records
        });
    }

    private async Task<int> SupersedeOlderPending(
        string naturalKey,
        string currentMessageId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(naturalKey)) return 0;

        var marker = $"\"intakeNaturalKey\":\"{EscapeForContains(naturalKey)}\"";
        var candidates = await db.StagedImports
            .Where(item =>
                item.EntityType == "order" &&
                item.Status == StagingStatus.PendingReview &&
                item.PayloadJson.Contains(marker))
            .ToListAsync(ct);

        if (candidates.Count == 0) return 0;

        var now = DateTimeOffset.UtcNow;
        foreach (var candidate in candidates)
        {
            candidate.Status = StagingStatus.Rejected;
            candidate.ReviewedAtUtc = now;
            candidate.ReviewedBy = "Mailbox supersession";
            candidate.ReviewNote = $"Superseded automatically by a newer Info mailbox message ({currentMessageId}). Original evidence retained.";
        }
        await db.SaveChangesAsync(ct);
        return candidates.Count;
    }

    private static string CompactKey(string value)
    {
        var compact = new string(value.Where(char.IsLetterOrDigit).ToArray());
        if (compact.Length <= 96) return compact;
        return compact[^96..];
    }

    private static string EscapeForContains(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("\"", "\\\"", StringComparison.Ordinal);
}