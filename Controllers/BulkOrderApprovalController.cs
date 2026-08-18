using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/staging/orders")]
[Authorize]
public sealed class BulkOrderApprovalController(TmsDbContext db, StagingService stagingService) : ControllerBase
{
    [HttpPost("bulk-approve"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> BulkApprove([FromBody] BulkApproveOrdersRequest request, CancellationToken ct)
    {
        if (request.Ids is null || request.Ids.Count == 0)
            return BadRequest(new { message = "Select at least one staged order to approve." });
        if (request.Ids.Count > 500)
            return BadRequest(new { message = "A maximum of 500 orders can be mass-approved at once." });
        if (request.Ids.Distinct().Count() != request.Ids.Count)
            return BadRequest(new { message = "The approval request contains duplicate staging IDs." });

        var items = await db.StagedImports
            .Where(x => request.Ids.Contains(x.Id))
            .OrderBy(x => x.ReceivedAtUtc)
            .ToListAsync(ct);

        var approved = 0;
        var skipped = new List<object>();
        var failed = new List<object>();

        foreach (var item in items)
        {
            var eligibility = CheckEligibility(item, request.Date);
            if (eligibility is not null)
            {
                skipped.Add(new { id = item.Id, reason = eligibility });
                continue;
            }

            try
            {
                await stagingService.ReviewAndPromote(
                    item.Id,
                    true,
                    $"Mass approved from Order Review for {request.Date:yyyy-MM-dd}. Clean, planner-ready order.",
                    User,
                    ct);
                approved++;
            }
            catch (Exception ex) when (ex is InvalidOperationException or DbUpdateException or JsonException)
            {
                failed.Add(new { id = item.Id, reason = ex.GetBaseException().Message });
            }
        }

        var missing = request.Ids.Count - items.Count;
        return Ok(new
        {
            date = request.Date,
            requested = request.Ids.Count,
            approved,
            skipped = skipped.Count,
            failed = failed.Count,
            missing,
            skippedItems = skipped.Take(100).ToList(),
            failedItems = failed.Take(100).ToList(),
            message = approved == 0
                ? "No orders were mass-approved. Anything uncertain remains in Order Review."
                : $"{approved} clean order{(approved == 1 ? "" : "s")} approved into live Orders. Anything uncertain remains in Order Review."
        });
    }

    private static string? CheckEligibility(StagedImport item, DateOnly requestedDate)
    {
        if (item.EntityType != "order") return "Not an order staging record.";
        if (item.Status != StagingStatus.PendingReview) return $"Status is {item.Status}, not PendingReview.";

        try
        {
            using var document = JsonDocument.Parse(item.PayloadJson);
            var payload = document.RootElement;

            var poNumber = Text(payload, "poNumber");
            var customerCode = Text(payload, "customerCode");
            var collectionDateText = Text(payload, "collectionDate");
            if (string.IsNullOrWhiteSpace(poNumber)) return "PO/order reference is missing.";
            if (string.IsNullOrWhiteSpace(customerCode)) return "Customer code is missing.";
            if (!DateOnly.TryParse(collectionDateText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var collectionDate))
                return "Collection date is missing or invalid.";
            if (collectionDate != requestedDate) return $"Order belongs to {collectionDate:yyyy-MM-dd}, not the selected date.";

            var pallets = Int(payload, "pallets", "palletQty", "palletQuantity", "quantity");
            if (pallets is null or <= 0) return "Zero or missing pallet quantity.";

            if (Bool(payload, "plannerReady") == false) return "Order is not planner-ready.";
            if (string.Equals(Text(payload, "intakeStatus"), "PreOrder", StringComparison.OrdinalIgnoreCase))
                return "Pre-order awaiting customer instruction.";

            var confidence = Text(payload, "intakeConfidence");
            if (!string.Equals(confidence, "High", StringComparison.OrdinalIgnoreCase))
                return $"Intake confidence is {confidence ?? "not set"}; individual review required.";

            if (HasWarnings(payload)) return "Source/intake warnings require individual review.";
            return null;
        }
        catch (JsonException)
        {
            return "Staged payload is not valid JSON.";
        }
    }

    private static bool HasWarnings(JsonElement payload)
    {
        if (!TryGetProperty(payload, "intakeWarnings", out var value)) return false;
        return value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0;
    }

    private static bool? Bool(JsonElement payload, string name)
    {
        if (!TryGetProperty(payload, name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static int? Int(JsonElement payload, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(payload, name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
            if (value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
                return (int)Math.Round(parsed, MidpointRounding.AwayFromZero);
        }
        return null;
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

public sealed record BulkApproveOrdersRequest(DateOnly Date, List<Guid> Ids);
