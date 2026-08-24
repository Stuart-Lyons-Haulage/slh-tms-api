using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class OrderIntakeMappingExceptionTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory factory;
    public OrderIntakeMappingExceptionTests(CustomWebFactory factory) => this.factory = factory;

    [Fact]
    public async Task Nwf_pallet_order_sender_without_readable_attachment_is_staged_for_mapping_review()
    {
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write");
        var payload = JsonSerializer.Serialize(new
        {
            messageId = $"nwf-mapping-{Guid.NewGuid():N}",
            internetMessageId = "<nwf-mapping@example.test>",
            mailbox = "info@lyonshaulage.com",
            senderAddress = "D_StuartLyonsPalletOrdering@nwfltd.co.uk",
            senderName = "D_Stuart Lyons Pallet Ordering",
            subject = "NWAY Pallet Order 25/08/2026",
            receivedAtUtc = "2026-08-24T08:15:00Z",
            bodyText = "NWAY pallet order attached.",
            webLink = "https://outlook.office.com/mail/test",
            attachments = new[]
            {
                new
                {
                    name = "NWAY Pallet Order 25-08-2026.csv",
                    contentType = "text/csv",
                    isInline = false,
                    size = 4096
                }
            }
        });

        var response = await client.PostAsync("/api/v1/order-intake/email", new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var staged = Assert.Single(db.StagedImports.Where(item => item.EntityType == "order" && item.Source.Contains("mapping exception", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(StagingStatus.PendingReview, staged.Status);
        using var document = JsonDocument.Parse(staged.PayloadJson);
        var root = document.RootElement;
        Assert.Equal("NWF", root.GetProperty("customerCode").GetString());
        Assert.Equal("2026-08-25", root.GetProperty("collectionDate").GetString());
        Assert.Equal("MappingException", root.GetProperty("intakeStatus").GetString());
        Assert.False(root.GetProperty("plannerReady").GetBoolean());
        Assert.Equal("D_StuartLyonsPalletOrdering@nwfltd.co.uk", root.GetProperty("sourceSender").GetString());
        Assert.Contains("NWAY Pallet Order 25-08-2026.csv", root.GetProperty("sourceAttachmentNames").EnumerateArray().Select(item => item.GetString()));
    }
}
