using System.Text.Json;
using Slh.Tms.Api.Controllers;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class OrderIntakeSourceEvidenceTests
{
    [Fact]
    public void EnrichSourceEvidence_RetainsAuditEnvelopeWithoutAttachmentBytes()
    {
        // Production defect caught: a staged order cannot be traced to its
        // conversation, recipients and exact attachment when extra request
        // metadata is accepted but silently discarded.
        var payload = JsonSerializer.SerializeToElement(new { customerCode = "COOP", pallets = 26 });
        using var toDocument = JsonDocument.Parse("[{\"address\":\"info@lyonshaulage.com\"}]");
        using var ccDocument = JsonDocument.Parse("[{\"address\":\"planner@example.com\"}]");
        var request = new MailboxEmailIntakeRequest(
            "outlook-id", "<internet-id@example>", "info@lyonshaulage.com", "orders@example.com", "Orders Team",
            "PO 123", DateTimeOffset.Parse("2026-08-22T08:00:00Z"), "plain", "<p>html</p>",
            "https://outlook.example/message", [new("order.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "SECRET-BYTES", false, "cid-1", 2048)],
            "conversation-1", toDocument.RootElement.Clone(), ccDocument.RootElement.Clone(), "html", "high", "correlation-1");

        var enriched = OrderIntakeController.EnrichSourceEvidence(payload, request);

        Assert.Equal("conversation-1", enriched.GetProperty("sourceConversationId").GetString());
        Assert.Equal("outlook-id", enriched.GetProperty("sourceMessageId").GetString());
        Assert.Equal("PO 123", enriched.GetProperty("sourceSubject").GetString());
        Assert.Equal("https://outlook.example/message", enriched.GetProperty("sourceWebLink").GetString());
        Assert.Equal("correlation-1", enriched.GetProperty("importCorrelationId").GetString());
        Assert.Equal("Pending Review", enriched.GetProperty("reviewStatus").GetString());
        var attachment = Assert.Single(enriched.GetProperty("sourceAttachments").EnumerateArray());
        Assert.Equal("order.xlsx", attachment.GetProperty("name").GetString());
        Assert.Equal(2048, attachment.GetProperty("size").GetInt64());
        Assert.DoesNotContain("SECRET-BYTES", enriched.GetRawText());
    }
}