using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class ZeroQuantityRoutedOrderTests : IClassFixture<CustomWebFactory>
{
    private const string Planner = "planner@lyonshaulage.com";
    private readonly CustomWebFactory factory;

    public ZeroQuantityRoutedOrderTests(CustomWebFactory factory) => this.factory = factory;

    [Fact]
    public async Task Routed_zero_pallet_order_can_be_staged_bulk_approved_and_promoted()
    {
        // Production defect caught: staging and bulk approval both discard/block
        // a real transport movement solely because its explicit pallet count is zero.
        var client = factory.CreateClientWithUser(Planner, "Tms.Approve");
        var reference = $"ZERO-ROUTE-{Guid.NewGuid():N}";
        var key = $"zero-route-{Guid.NewGuid():N}";
        var response = await client.PostAsync("/api/v1/staging", Json(JsonSerializer.Serialize(new
        {
            entityType = "order",
            idempotencyKey = key,
            source = "test",
            payload = new
            {
                poNumber = reference,
                customerCode = "SAINSBURY",
                collectionDate = "2026-08-26",
                deliveryDate = "2026-08-26",
                pallets = 0,
                sellerName = "Tamworth - Transhipment",
                stallNumber = "Basingstoke",
                plannerReady = true,
                intakeConfidence = "High",
                intakeWarnings = Array.Empty<string>()
            }
        })));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var staged = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var id = staged.RootElement.GetProperty("stagingId").GetGuid();

        var approval = await client.PostAsync("/api/v1/staging/orders/bulk-approve", Json(JsonSerializer.Serialize(new
        {
            date = "2026-08-26",
            ids = new[] { id },
            acknowledgeReviewFlags = false
        })));
        Assert.Equal(HttpStatusCode.OK, approval.StatusCode);
        using var approvalBody = JsonDocument.Parse(await approval.Content.ReadAsStringAsync());
        Assert.Equal(1, approvalBody.RootElement.GetProperty("approved").GetInt32());
        Assert.Equal(0, approvalBody.RootElement.GetProperty("skipped").GetInt32());

        var ordersResponse = await client.GetAsync("/api/v1/orders");
        Assert.Equal(HttpStatusCode.OK, ordersResponse.StatusCode);
        using var orders = JsonDocument.Parse(await ordersResponse.Content.ReadAsStringAsync());
        var order = Assert.Single(orders.RootElement.EnumerateArray().Where(x => x.GetProperty("reference").GetString() == reference));
        Assert.Equal(0, order.GetProperty("pallets").GetInt32());
        Assert.Equal("Tamworth - Transhipment", order.GetProperty("sellerName").GetString());
        Assert.Equal("Basingstoke", order.GetProperty("stallNumber").GetString());
    }

    [Fact]
    public async Task Zero_pallet_order_without_complete_collection_and_delivery_is_rejected()
    {
        var client = factory.CreateClientWithUser(Planner, "Tms.Approve");
        var response = await client.PostAsync("/api/v1/staging", Json(JsonSerializer.Serialize(new
        {
            entityType = "order",
            idempotencyKey = $"zero-incomplete-{Guid.NewGuid():N}",
            source = "test",
            payload = new
            {
                poNumber = $"ZERO-INCOMPLETE-{Guid.NewGuid():N}",
                customerCode = "SAINSBURY",
                collectionDate = "2026-08-26",
                deliveryDate = "2026-08-26",
                pallets = 0,
                sellerName = "Tamworth - Transhipment"
            }
        })));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("zero_pallet_route_required", body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Negative_pallet_order_is_rejected()
    {
        var client = factory.CreateClientWithUser(Planner, "Tms.Approve");
        var response = await client.PostAsync("/api/v1/staging", Json(JsonSerializer.Serialize(new
        {
            entityType = "order",
            idempotencyKey = $"negative-{Guid.NewGuid():N}",
            source = "test",
            payload = new
            {
                poNumber = $"NEGATIVE-{Guid.NewGuid():N}",
                customerCode = "SAINSBURY",
                collectionDate = "2026-08-26",
                deliveryDate = "2026-08-26",
                pallets = -1,
                sellerName = "Tamworth - Transhipment",
                stallNumber = "Basingstoke"
            }
        })));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("negative_pallet_quantity", body.RootElement.GetProperty("code").GetString());
    }

    private static StringContent Json(string value) => new(value, Encoding.UTF8, "application/json");
}
