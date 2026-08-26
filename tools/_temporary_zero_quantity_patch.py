from __future__ import annotations

import argparse
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def replace_once(path: str, old: str, new: str) -> None:
    target = ROOT / path
    text = target.read_text(encoding="utf-8")
    if old not in text:
        raise SystemExit(f"Expected patch anchor not found in {path}: {old[:120]!r}")
    if text.count(old) != 1:
        raise SystemExit(f"Patch anchor occurs {text.count(old)} times in {path}; refusing ambiguous replacement")
    target.write_text(text.replace(old, new, 1), encoding="utf-8")


def patch_tests() -> None:
    path = "Slh.Tms.Api.Tests/NwfPalletOrderCsvParserTests.cs"
    replace_once(path,
        "public void NwfCsv_StagesOnlyPositivePalletRows_AndUsesPoAsTmsReference()",
        "public void NwfCsv_StagesZeroPalletRows_WhenRouteIsComplete_AndUsesPoAsTmsReference()")
    replace_once(path,
        "Assert.Equal(2, result.Orders.Count);\n        Assert.Contains(result.Warnings, warning => warning.Contains(\"zero-pallet\", StringComparison.OrdinalIgnoreCase));\n\n        var first = result.Orders[0].Payload;",
        "Assert.Equal(3, result.Orders.Count);\n        Assert.Contains(result.Warnings, warning => warning.Contains(\"zero-pallet\", StringComparison.OrdinalIgnoreCase));\n        var zero = Assert.Single(result.Orders.Where(order => order.Payload.GetProperty(\"salesOrderId\").GetString() == \"SO000367751\"));\n        Assert.Equal(0, zero.Payload.GetProperty(\"pallets\").GetInt32());\n        Assert.Equal(\"Drayton\", zero.Payload.GetProperty(\"sellerName\").GetString());\n        Assert.Equal(\"One Stop Tamworth\", zero.Payload.GetProperty(\"stallNumber\").GetString());\n\n        var first = result.Orders[0].Payload;")
    replace_once(path,
        "Assert.Equal(2, result.Orders.Count);\n        Assert.Contains(result.Warnings, warning => warning.Contains(\"zero-pallet\", StringComparison.OrdinalIgnoreCase));\n        var first = result.Orders[0].Payload;",
        "Assert.Equal(3, result.Orders.Count);\n        Assert.Contains(result.Warnings, warning => warning.Contains(\"zero-pallet\", StringComparison.OrdinalIgnoreCase));\n        var zero = Assert.Single(result.Orders.Where(order => order.Payload.GetProperty(\"salesOrderId\").GetString() == \"SO000368432\"));\n        Assert.Equal(0, zero.Payload.GetProperty(\"pallets\").GetInt32());\n        Assert.Equal(\"Selsey\", zero.Payload.GetProperty(\"sellerName\").GetString());\n        Assert.Equal(\"One Stop Tamworth\", zero.Payload.GetProperty(\"stallNumber\").GetString());\n        var first = result.Orders[0].Payload;")

    zero_test = ROOT / "Slh.Tms.Api.Tests/ZeroQuantityRoutedOrderTests.cs"
    zero_test.write_text(r'''using System.Net;
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
''', encoding="utf-8")


def patch_production() -> None:
    path = "Services/NwfPalletOrderCsvParser.cs"
    replace_once(path,
        "/// The CSV is a daily customer snapshot. Zero-pallet rows are evidence only and\n/// are deliberately not staged as transport orders.",
        "/// The CSV is a daily customer snapshot. Explicit zero-pallet rows are valid\n/// transport movements when their collection and delivery routing is complete.")
    replace_once(path,
        "                    if (palletQty is null or <= 0)\n                    {\n                        zeroPalletRows++;\n                        continue;\n                    }\n\n                    var rowWarnings = new List<string>();",
        "                    if (palletQty is null || palletQty < 0)\n                    {\n                        invalidRows++;\n                        continue;\n                    }\n                    if (palletQty == 0) zeroPalletRows++;\n\n                    var rowWarnings = new List<string>();")
    replace_once(path,
        "                    warnings.Add($\"{zeroPalletRows} zero-pallet row(s) were retained in the source evidence but not staged as transport orders.\");",
        "                    warnings.Add($\"{zeroPalletRows} zero-pallet row(s) were staged as routed transport movements.\");")
    replace_once(path,
        "                    warnings.Add($\"{invalidRows} positive-pallet row(s) were not staged because mandatory routing fields were missing.\");",
        "                    warnings.Add($\"{invalidRows} row(s) were not staged because quantity was missing/negative or mandatory routing fields were missing.\");")
    replace_once(path,
        "                    ? new EmailIntakeParseResult([], warnings, \"NWF pallet-order CSV was recognised but contained no valid positive-pallet transport orders.\")",
        "                    ? new EmailIntakeParseResult([], warnings, \"NWF pallet-order CSV was recognised but contained no valid routed transport orders.\")")

    path = "Controllers/StagingController.cs"
    replace_once(path,
        "        if (IsExplicitZeroPalletOrder(request))\n            return Ok(new { ignored = true, reason = \"zero_pallet_order\", message = \"The source row has zero pallets and was retained as source evidence rather than staged as a transport order.\" });",
        "        if (IsNegativePalletOrder(request))\n            return BadRequest(new ErrorResponse(\"negative_pallet_quantity\", \"Order pallet quantity cannot be negative.\", HttpContext.TraceIdentifier));\n        if (IsZeroPalletOrderWithoutCompleteRoute(request))\n            return BadRequest(new ErrorResponse(\"zero_pallet_route_required\", \"A zero-pallet order is valid only when collection site, delivery site, collection date, and delivery date are all supplied.\", HttpContext.TraceIdentifier));")
    replace_once(path,
        "        var filteredRequests = requests.Where(request => !IsExplicitZeroPalletOrder(request)).ToList();\n        var skippedZeroPallets = requests.Count - filteredRequests.Count;\n        if (filteredRequests.Count == 0)\n            return Accepted(new { received = requests.Count, existing = 0, created = 0, skippedZeroPallets, records = Array.Empty<StageImportResponse>() });\n        var keys = filteredRequests.Select(request => request.IdempotencyKey).ToList();",
        "        if (requests.Any(IsNegativePalletOrder))\n            return BadRequest(new ErrorResponse(\"negative_pallet_quantity\", \"Order pallet quantity cannot be negative.\", HttpContext.TraceIdentifier));\n        if (requests.Any(IsZeroPalletOrderWithoutCompleteRoute))\n            return BadRequest(new ErrorResponse(\"zero_pallet_route_required\", \"Every zero-pallet order must include collection site, delivery site, collection date, and delivery date.\", HttpContext.TraceIdentifier));\n        var keys = requests.Select(request => request.IdempotencyKey).ToList();")
    replace_once(path,
        "            foreach (var request in filteredRequests)",
        "            foreach (var request in requests)")
    replace_once(path,
        "            return Accepted(new { received = requests.Count, existing = existingCount, created = responses.Count - existingCount, skippedZeroPallets, records = responses });",
        "            return Accepted(new { received = requests.Count, existing = existingCount, created = responses.Count - existingCount, skippedZeroPallets = 0, records = responses });")
    replace_once(path,
        "    private static bool IsExplicitZeroPalletOrder(StageImportRequest request)\n    {\n        if (!string.Equals(request.EntityType, \"order\", StringComparison.OrdinalIgnoreCase)) return false;\n        if (!TryGetProperty(request.Payload, \"pallets\", out var pallets)\n            && !TryGetProperty(request.Payload, \"palletQty\", out pallets)\n            && !TryGetProperty(request.Payload, \"palletQuantity\", out pallets))\n            return false;\n\n        return pallets.ValueKind switch\n        {\n            JsonValueKind.Number => pallets.TryGetDecimal(out var number) && number <= 0,\n            JsonValueKind.String => decimal.TryParse(pallets.GetString(), out var number) && number <= 0,\n            _ => false\n        };\n    }",
        "    private static bool IsNegativePalletOrder(StageImportRequest request) =>\n        TryGetOrderPalletQuantity(request, out var quantity) && quantity < 0;\n\n    private static bool IsZeroPalletOrderWithoutCompleteRoute(StageImportRequest request) =>\n        TryGetOrderPalletQuantity(request, out var quantity) && quantity == 0 && !HasCompleteZeroPalletRoute(request.Payload);\n\n    private static bool TryGetOrderPalletQuantity(StageImportRequest request, out decimal quantity)\n    {\n        quantity = 0;\n        if (!string.Equals(request.EntityType, \"order\", StringComparison.OrdinalIgnoreCase)) return false;\n        if (!TryGetProperty(request.Payload, \"pallets\", out var pallets)\n            && !TryGetProperty(request.Payload, \"palletQty\", out pallets)\n            && !TryGetProperty(request.Payload, \"palletQuantity\", out pallets)\n            && !TryGetProperty(request.Payload, \"quantity\", out pallets))\n            return false;\n\n        return pallets.ValueKind switch\n        {\n            JsonValueKind.Number => pallets.TryGetDecimal(out quantity),\n            JsonValueKind.String => decimal.TryParse(pallets.GetString(), out quantity),\n            _ => false\n        };\n    }\n\n    private static bool HasCompleteZeroPalletRoute(JsonElement payload)\n    {\n        var collectionSite = Text(payload, \"collectionSite\") ?? Text(payload, \"collectionLocation\") ?? Text(payload, \"sellerName\");\n        var deliverySite = Text(payload, \"deliverySite\") ?? Text(payload, \"deliveryLocation\") ?? Text(payload, \"stallNumber\");\n        return !string.IsNullOrWhiteSpace(collectionSite)\n            && !string.IsNullOrWhiteSpace(deliverySite)\n            && DateOnly.TryParse(Text(payload, \"collectionDate\"), out _)\n            && DateOnly.TryParse(Text(payload, \"deliveryDate\"), out _);\n    }\n\n    private static string? Text(JsonElement payload, string name)\n    {\n        if (!TryGetProperty(payload, name, out var value)) return null;\n        return value.ValueKind switch\n        {\n            JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString()!.Trim(),\n            JsonValueKind.Number => value.GetRawText(),\n            _ => null\n        };\n    }")

    path = "Services/StagingService.cs"
    replace_once(path,
        "            plannerReady |= !string.IsNullOrWhiteSpace(collectionSite) && !string.IsNullOrWhiteSpace(deliverySite)\n                && lineCollectionDate is not null && lineDeliveryDate is not null && pallets is > 0;",
        "            plannerReady |= !string.IsNullOrWhiteSpace(collectionSite) && !string.IsNullOrWhiteSpace(deliverySite)\n                && lineCollectionDate is not null && lineDeliveryDate is not null && pallets is >= 0;")

    path = "Controllers/BulkOrderApprovalController.cs"
    replace_once(path,
        "            var pallets = Int(payload, \"pallets\", \"palletQty\", \"palletQuantity\", \"quantity\");\n            if (pallets is null or <= 0) return \"Zero or missing pallet quantity.\";",
        "            var pallets = Int(payload, \"pallets\", \"palletQty\", \"palletQuantity\", \"quantity\");\n            if (pallets is null) return \"Pallet quantity is missing.\";\n            if (pallets < 0) return \"Pallet quantity cannot be negative.\";\n            if (pallets == 0)\n            {\n                var collectionSite = Text(payload, \"collectionSite\") ?? Text(payload, \"collectionLocation\") ?? Text(payload, \"sellerName\");\n                var deliverySite = Text(payload, \"deliverySite\") ?? Text(payload, \"deliveryLocation\") ?? Text(payload, \"stallNumber\");\n                if (string.IsNullOrWhiteSpace(collectionSite) || string.IsNullOrWhiteSpace(deliverySite))\n                    return \"Zero-pallet order requires collection and delivery sites.\";\n                if (!DateOnly.TryParse(Text(payload, \"deliveryDate\"), CultureInfo.InvariantCulture, DateTimeStyles.None, out _))\n                    return \"Zero-pallet order requires a valid delivery date.\";\n            }")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("phase", choices=["tests", "production"])
    args = parser.parse_args()
    if args.phase == "tests":
        patch_tests()
    else:
        patch_production()
