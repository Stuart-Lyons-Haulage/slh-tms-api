using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/order-intake/email/heartbeat")]
[Authorize(Policy = "TmsWrite")]
public sealed class InfoMailboxHeartbeatController(TmsDbContext db, ILogger<InfoMailboxHeartbeatController> logger) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Record([FromBody] InfoMailboxHeartbeatRequest request, CancellationToken ct)
    {
        var mailbox = (request.Mailbox ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(mailbox))
            return BadRequest(new { message = "Mailbox is required." });

        var now = DateTimeOffset.UtcNow;
        var idempotencyKey = $"infomailboxheartbeat:{mailbox}";
        if (idempotencyKey.Length > 200) idempotencyKey = idempotencyKey[..200];
        var payload = JsonSerializer.Serialize(new
        {
            mailbox,
            flowName = request.FlowName,
            flowRunId = request.FlowRunId,
            checkedAtUtc = request.CheckedAtUtc,
            latestInboxReceivedAtUtc = request.LatestInboxReceivedAtUtc,
            recordedAtUtc = now,
            probe = "shared Outlook mailbox read + TMS API write"
        });

        var marker = await db.StagedImports.SingleOrDefaultAsync(item => item.IdempotencyKey == idempotencyKey, ct);
        if (marker is null)
        {
            marker = new StagedImport
            {
                EntityType = "infomailboxheartbeat",
                IdempotencyKey = idempotencyKey,
                PayloadJson = payload,
                Source = "Info mailbox scheduled heartbeat",
                Status = StagingStatus.Promoted,
                ReceivedAtUtc = now,
                ReviewedAtUtc = now,
                ReviewedBy = "system:power-automate-heartbeat",
                ReviewNote = "Scheduled shared-mailbox connectivity probe. This is runtime health evidence, not an order import."
            };
            db.StagedImports.Add(marker);
        }
        else
        {
            marker.PayloadJson = payload;
            marker.Source = "Info mailbox scheduled heartbeat";
            marker.Status = StagingStatus.Promoted;
            marker.ReceivedAtUtc = now;
            marker.ReviewedAtUtc = now;
            marker.ReviewedBy = "system:power-automate-heartbeat";
            marker.ReviewNote = "Scheduled shared-mailbox connectivity probe. This is runtime health evidence, not an order import.";
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Info mailbox heartbeat recorded for {Mailbox}; flow {FlowName}, run {FlowRunId}, latest inbox message {LatestInboxReceivedAtUtc}.",
            mailbox,
            request.FlowName,
            request.FlowRunId,
            request.LatestInboxReceivedAtUtc);

        return Accepted(new
        {
            heartbeatAccepted = true,
            mailbox,
            recordedAtUtc = now,
            latestInboxReceivedAtUtc = request.LatestInboxReceivedAtUtc
        });
    }
}

public sealed record InfoMailboxHeartbeatRequest(
    string? Mailbox,
    string? FlowName,
    string? FlowRunId,
    DateTimeOffset? CheckedAtUtc,
    DateTimeOffset? LatestInboxReceivedAtUtc);
