using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/staging")]
[Authorize]
public sealed class StagingAmendmentController(TmsDbContext db) : ControllerBase
{
    [HttpPut("{id:guid}/payload"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> Amend(
        Guid id,
        [FromBody] StagedPayloadAmendment request,
        CancellationToken ct)
    {
        var item = await db.StagedImports.SingleOrDefaultAsync(row => row.Id == id, ct);
        if (item is null) return NotFound();
        if (item.Status != StagingStatus.PendingReview)
            return BadRequest(new { message = "Only pending staged records can be amended." });
        if (!string.Equals(item.EntityType, "order", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "This amendment endpoint is currently limited to staged orders." });
        if (request.Payload.ValueKind != JsonValueKind.Object)
            return BadRequest(new { message = "Order payload must be a JSON object." });

        var po = Text(request.Payload, "poNumber");
        var customer = Text(request.Payload, "customerCode");
        var date = Text(request.Payload, "collectionDate");
        if (string.IsNullOrWhiteSpace(po) || string.IsNullOrWhiteSpace(customer) || !DateOnly.TryParse(date, out _))
            return BadRequest(new { message = "Order requires poNumber/reference, customerCode and a valid collectionDate before it can be saved." });

        var previousStatus = item.Status;
        item.PayloadJson = request.Payload.GetRawText();
        item.ReviewNote = string.Join(" | ", new[]
        {
            item.ReviewNote,
            request.Note,
            $"Pending payload amended by {User.Identity?.Name ?? "authorised user"} at {DateTimeOffset.UtcNow:O}."
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
        db.StagedImportEvents.Add(StagingAudit.Create(
            item,
            "Amended",
            previousStatus,
            request.Note,
            User.Identity?.Name ?? User.FindFirst("oid")?.Value));
        await db.SaveChangesAsync(ct);
        return Ok(item);
    }

    private static string? Text(JsonElement payload, string name)
    {
        foreach (var property in payload.EnumerateObject())
        {
            var key = new string(property.Name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
            var wanted = new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
            if (key != wanted) continue;
            return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()?.Trim() : property.Value.GetRawText();
        }
        return null;
    }
}

public sealed record StagedPayloadAmendment(JsonElement Payload, string? Note);
