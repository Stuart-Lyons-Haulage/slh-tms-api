using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class OrderIntakeMappingExceptionTests(CustomWebFactory factory) : IClassFixture<CustomWebFactory>
{
    [Fact]
    public async Task Internal_planner_load_plan_attachment_is_staged_for_mapping_review()
    {
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write");
        var messageId = $"internal-load-plan-{Guid.NewGuid():N}";
        var payload = JsonSerializer.Serialize(new
        {
            messageId,
            internetMessageId = "<internal-load-plan@example.test>",
            mailbox = "info@lyonshaulage.com",
            senderAddress = "michael@lyonshaulage.com",
            senderName = "Michael Lyons",
            subject = "Load plan",
            receivedAtUtc = "2026-08-25T16:29:41Z",
            bodyText = "Please find load plan attached for tonight",
            webLink = "https://outlook.office.com/mail/test",
            attachments = new[]
            {
                new
                {
                    name = "Load plan 25-08-2026.xlsx",
                    contentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    isInline = false,
                    size = 4096
                }
            }
        });

        var response = await client.PostAsync("/api/v1/order-intake/email", new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"outlookCategory\":\"TMS Review\"", responseBody);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        // Email evidence is now intentionally retained alongside the staged order. This
        // regression is about the order mapping exception, so do not count its audit/evidence
        // companion as a second mapping record.
        var staged = Assert.Single(db.StagedImports.Where(item =>
            item.EntityType == "order" &&
            item.PayloadJson.Contains(messageId, StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(StagingStatus.PendingReview, staged.Status);
        using var document = JsonDocument.Parse(staged.PayloadJson);
        Assert.Equal("MappingException", document.RootElement.GetProperty("intakeStatus").GetString());
    }

    [Fact]
    public async Task Aps_market_week_attachment_without_readable_content_is_staged_for_mapping_review()
    {
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write");
        var messageId = $"aps-market-week-{Guid.NewGuid():N}";
        var payload = JsonSerializer.Serialize(new
        {
            messageId,
            internetMessageId = "<aps-market-week@example.test>",
            mailbox = "info@lyonshaulage.com",
            senderAddress = "Marta.Rypien-Kabza@apsgroup.uk.com",
            senderName = "Marta Rypien-Kabza",
            subject = "Market Week 35.xls",
            receivedAtUtc = "2026-08-26T09:36:19Z",
            bodyText = "Please see attached.",
            webLink = "https://outlook.office.com/mail/test",
            attachments = new[]
            {
                new
                {
                    name = "Market Week 35.xls",
                    contentType = "application/vnd.ms-excel",
                    isInline = false,
                    size = 4096
                }
            }
        });

        var response = await client.PostAsync("/api/v1/order-intake/email", new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"outlookCategory\":\"TMS Review\"", responseBody);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var staged = Assert.Single(db.StagedImports.Where(item => item.EntityType == "order" && item.PayloadJson.Contains(messageId, StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(StagingStatus.PendingReview, staged.Status);
        using var document = JsonDocument.Parse(staged.PayloadJson);
        Assert.Equal("MappingException", document.RootElement.GetProperty("intakeStatus").GetString());
    }
}
