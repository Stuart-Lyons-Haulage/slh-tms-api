using System.Text.Json;
using Slh.Tms.Api.Controllers;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class StagingQueueProjectionTests
{
    [Fact]
    public void BuildPayloadSummary_keeps_review_fields_and_drops_heavy_email_evidence()
    {
        var payload = JsonSerializer.Serialize(new
        {
            poNumber = "PO-123",
            customerPo = "CUST-77",
            customerCode = "NWF",
            collectionDate = "2026-09-15",
            deliveryDate = "2026-09-15",
            pallets = 12,
            sellerName = "Merston",
            stallNumber = "Aldi Darlington",
            plannerReady = true,
            intakeStatus = "Ready",
            intakeConfidence = "High",
            intakeWarnings = new[] { "Check booking time" },
            emailRouteMatched = true,
            emailRouteRequiresReview = false,
            orderIntakeRouteRuleId = "f558caaa-7f7b-4caa-9e95-cc4dc343cd4f",
            orderIntakeRouteConfidenceScore = 95,
            orderIntakeRouteMatchedDimensions = 3,
            orderIntakeRouteExplanation = new[] { "Customer NWF matched.", "retailer matched ALDI." },
            orderIntakeRouteAlternatives = new[] { new { score = 95, customerCode = "NWF" } },
            sourceMessageId = "message-123",
            sourceInternetMessageId = "<message-123@example.com>",
            sourceSubject = "NWAY pallet order",
            sourceWebLink = "https://outlook.office.com/mail/id/message-123",
            sourceBodyText = new string('x', 20000),
            sourceBodyHtml = "<p>large retained email body</p>",
            sourceToRecipients = new[] { new { address = "info@lyonshaulage.com" } },
            sourceCcRecipients = new[] { new { address = "planner@lyonshaulage.com" } },
            sourceAttachments = new[] { new { name = "orders.xlsx", size = 500000, isInline = false } }
        });

        var summaryJson = StagingQueueProjection.BuildPayloadSummary(payload);
        using var document = JsonDocument.Parse(summaryJson);
        var root = document.RootElement;

        Assert.Equal("PO-123", root.GetProperty("poNumber").GetString());
        Assert.Equal("NWF", root.GetProperty("customerCode").GetString());
        Assert.Equal(12, root.GetProperty("pallets").GetInt32());
        Assert.Equal("message-123", root.GetProperty("sourceMessageId").GetString());
        Assert.Equal("orders.xlsx", root.GetProperty("sourceAttachmentName").GetString());
        Assert.True(root.GetProperty("emailRouteMatched").GetBoolean());
        Assert.Equal(95, root.GetProperty("orderIntakeRouteConfidenceScore").GetInt32());
        Assert.Equal(3, root.GetProperty("orderIntakeRouteMatchedDimensions").GetInt32());
        Assert.Equal("NWF", root.GetProperty("orderIntakeRouteAlternatives")[0].GetProperty("customerCode").GetString());
        Assert.False(root.TryGetProperty("sourceBodyText", out _));
        Assert.False(root.TryGetProperty("sourceBodyHtml", out _));
        Assert.False(root.TryGetProperty("sourceToRecipients", out _));
        Assert.False(root.TryGetProperty("sourceCcRecipients", out _));
        Assert.False(root.TryGetProperty("sourceAttachments", out _));
    }

    [Fact]
    public void BuildPayloadSummary_returns_empty_object_for_invalid_legacy_json()
    {
        Assert.Equal("{}", StagingQueueProjection.BuildPayloadSummary("not-json"));
    }
}
