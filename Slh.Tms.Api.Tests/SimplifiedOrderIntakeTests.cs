using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class SimplifiedOrderIntakeTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory factory;

    public SimplifiedOrderIntakeTests(CustomWebFactory factory) => this.factory = factory;

    [Fact]
    public async Task Nwf_morrisons_structured_order_enters_pending_review()
    {
        var messageId = $"simplified-nwf-morrisons-{Guid.NewGuid():N}";
        const string body = """
Haulier Name| Requested Ship Date| 04. Collection Site| Customer Name| DepotID| Depot Description| Delivery Address| Sales Order ID| CustomerRef| Pallet Name| PalletQty| PO REF
---|---|---|---|---|---|---|---|---|---|---|---
Stuart Lyons| 20/09/2026| Selsey| Morrisons| MOR09| Morrisons FRUITSITTINGBOURNE 763| ME10 2FD| SO000999001| 91329634| IPP STD| 10| PO00999001
Stuart Lyons| 20/09/2026| Selsey| NISA| NISA01| NISA depot| UK| SO000999099| REF99| IPP STD| 4| PO00999099
""";

        var response = await Post(new
        {
            messageId,
            mailbox = "info@lyonshaulage.com",
            senderAddress = "ShiftLogisticalPlanner@nwfltd.co.uk",
            senderName = "Shift Logistical Planner",
            subject = "NWAY Stuart Lyons Transport Pallet Order Report 20/09/2026",
            receivedAtUtc = "2026-09-19T05:37:05Z",
            bodyText = body,
            attachments = new[] { new { name = "NWAY Pallet Order 20-09-2026.csv", contentType = "text/csv", isInline = false, size = 8192 } }
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var order = Assert.Single(db.StagedImports.Where(item => item.EntityType == "order" && item.PayloadJson.Contains(messageId)));
        Assert.Equal(StagingStatus.PendingReview, order.Status);
        using var payload = JsonDocument.Parse(order.PayloadJson);
        Assert.Equal("Selsey", payload.RootElement.GetProperty("sellerName").GetString());
        Assert.Contains("Morrisons", payload.RootElement.GetProperty("stallNumber").GetString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Nwf_non_target_customer_is_evidence_only()
    {
        var messageId = $"simplified-nwf-other-{Guid.NewGuid():N}";
        const string body = """
Haulier Name| Requested Ship Date| 04. Collection Site| Customer Name| DepotID| Depot Description| Delivery Address| Sales Order ID| CustomerRef| Pallet Name| PalletQty| PO REF
---|---|---|---|---|---|---|---|---|---|---|---
Stuart Lyons| 20/09/2026| Selsey| NISA| NISA01| NISA depot| UK| SO000999002| REF1| IPP STD| 4| PO00999002
""";

        var response = await Post(new
        {
            messageId,
            mailbox = "info@lyonshaulage.com",
            senderAddress = "ShiftLogisticalPlanner@nwfltd.co.uk",
            senderName = "Shift Logistical Planner",
            subject = "NWAY Stuart Lyons Transport Pallet Order Report 20/09/2026",
            receivedAtUtc = "2026-09-19T05:38:05Z",
            bodyText = body
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"ignored\":true", await response.Content.ReadAsStringAsync());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        Assert.Empty(db.StagedImports.Where(item => item.EntityType == "order" && item.PayloadJson.Contains(messageId)));
        Assert.Single(db.StagedImports.Where(item => item.EntityType == "email-evidence" && item.PayloadJson.Contains(messageId)));
    }

    private Task<HttpResponseMessage> Post(object payload)
    {
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write");
        return client.PostAsync("/api/v1/order-intake/email", new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
    }
}
