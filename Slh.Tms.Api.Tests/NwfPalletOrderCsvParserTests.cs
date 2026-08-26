using System.Text;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class NwfPalletOrderCsvParserTests
{
    private readonly NwfPalletOrderCsvParser parser = new();

    [Fact]
    public void NwfCsv_StagesZeroPalletRows_WhenRouteIsComplete_AndUsesPoAsTmsReference()
    {
        const string csv = """
Haulier Name,Requested Ship Date,04. Collection Site,Customer Name,DepotID,Depot Description,Delivery Address,Sales Order ID,CustomerRef,Pallet Name,PalletQty,PO REF
Stuart Lyons,19/08/2026,Drayton,Aldi,ALD20,Aldi SAWLEY Distribution Centre,DE72 2HP,SO000367762,6511786146,IPP Euro,2,PO00499461
Stuart Lyons,19/08/2026,Drayton,Tesco,ONE01,One Stop Tamworth,B78 1ST,SO000367751,8000053488,IPP STD,0,PO00499461
Stuart Lyons,19/08/2026,Selsey,Morrisons,MOR06,Morrisons FRUITBRIDGWATER 718,TA6 4FG,SO000367789,12345,IPP STD,9,PO00499461
""";
        var request = Request(csv, "message-19");

        var result = parser.TryParse(request);

        Assert.NotNull(result);
        Assert.Null(result!.IgnoredReason);
        Assert.Equal(3, result.Orders.Count);
        Assert.Contains(result.Warnings, warning => warning.Contains("zero-pallet", StringComparison.OrdinalIgnoreCase));
        var zero = Assert.Single(result.Orders.Where(order => order.Payload.GetProperty("salesOrderId").GetString() == "SO000367751"));
        Assert.Equal(0, zero.Payload.GetProperty("pallets").GetInt32());
        Assert.Equal("Drayton", zero.Payload.GetProperty("sellerName").GetString());
        Assert.Equal("One Stop Tamworth", zero.Payload.GetProperty("stallNumber").GetString());

        var first = result.Orders[0].Payload;
        Assert.Equal("NWF", first.GetProperty("customerCode").GetString());
        Assert.Equal("2026-08-19", first.GetProperty("collectionDate").GetString());
        Assert.Equal("Drayton", first.GetProperty("sellerName").GetString());
        Assert.Equal("Aldi SAWLEY Distribution Centre", first.GetProperty("stallNumber").GetString());
        Assert.Equal("DE72 2HP", first.GetProperty("deliveryAddress").GetString());
        Assert.Equal("SO000367762", first.GetProperty("salesOrderId").GetString());
        Assert.Equal("6511786146", first.GetProperty("customerRef").GetString());
        Assert.Equal("IPP Euro", first.GetProperty("palletName").GetString());
        Assert.Equal("PO00499461", first.GetProperty("poRef").GetString());
        Assert.Equal("PO00499461", first.GetProperty("customerPo").GetString());
        Assert.StartsWith("PO00499461/SO000367762/Drayton/ALD20", first.GetProperty("poNumber").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, first.GetProperty("pallets").GetInt32());
        Assert.Equal("NWF Pallet Order CSV", first.GetProperty("intakeParser").GetString());
        Assert.Contains(first.GetProperty("intakeMatchKeys").EnumerateArray().Select(item => item.GetString()),
            key => key is not null && key.Contains("PO:PO00499461", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(first.GetProperty("intakeMatchKeys").EnumerateArray().Select(item => item.GetString()),
            key => key is not null && key.Contains("SALES:SO000367762", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UpdatedCsv_KeepsStableNaturalKey_WhenQuantityChanges()
    {
        const string firstCsv = """
Haulier Name,Requested Ship Date,04. Collection Site,Customer Name,DepotID,Depot Description,Delivery Address,Sales Order ID,CustomerRef,Pallet Name,PalletQty,PO REF
Stuart Lyons,19/08/2026,Drayton,Aldi,ALD20,Aldi SAWLEY Distribution Centre,DE72 2HP,SO000367762,6511786146,IPP Euro,2,PO00499461
""";
        const string updatedCsv = """
Haulier Name,Requested Ship Date,04. Collection Site,Customer Name,DepotID,Depot Description,Delivery Address,Sales Order ID,CustomerRef,Pallet Name,PalletQty,PO REF
Stuart Lyons,19/08/2026,Drayton,Aldi,ALD20,Aldi SAWLEY Distribution Centre,DE72 2HP,SO000367762,6511786146,IPP Euro,4,PO00499461
""";

        var first = Assert.Single(parser.TryParse(Request(firstCsv, "message-a"))!.Orders);
        var updated = Assert.Single(parser.TryParse(Request(updatedCsv, "message-b"))!.Orders);

        Assert.Equal(first.NaturalKey, updated.NaturalKey);
        Assert.Equal(2, first.Payload.GetProperty("pallets").GetInt32());
        Assert.Equal(4, updated.Payload.GetProperty("pallets").GetInt32());
    }

    [Fact]
    public void OutlookContentBytesAttachment_IsParsed()
    {
        const string csv = """
Haulier Name,Requested Ship Date,04. Collection Site,Customer Name,DepotID,Depot Description,Delivery Address,Sales Order ID,CustomerRef,Pallet Name,PalletQty,PO REF
Stuart Lyons,19/08/2026,Drayton,Aldi,ALD20,Aldi SAWLEY Distribution Centre,DE72 2HP,SO000367762,6511786146,IPP Euro,2,PO00499461
""";

        var request = new MailboxEmailIntakeRequest(
            "message-content-bytes",
            null,
            "info@lyonshaulage.com",
            "ShiftLogisticalPlanner@nwfltd.co.uk",
            "Shift Logistical Planner",
            "NWAY Stuart Lyons Transport Pallet Order Report 19/08/2026",
            DateTimeOffset.Parse("2026-08-18T18:00:00Z"),
            "Please see attached pallet order report.",
            null,
            null,
            [new MailboxAttachmentRequest(
                "NWAY PALLET ORDER REPORT SLH.csv",
                "text/csv",
                null,
                false,
                ContentBytes: Convert.ToBase64String(Encoding.UTF8.GetBytes(csv)))]);

        var order = Assert.Single(parser.TryParse(request)!.Orders);

        Assert.Equal("SO000367762", order.Payload.GetProperty("salesOrderId").GetString());
        Assert.Equal(2, order.Payload.GetProperty("pallets").GetInt32());
    }

    [Fact]
    public void NwfEmailBodyPipeTable_IsParsed_WhenAttachmentContentIsUnavailable()
    {
        const string body = """
Hello,

Please see below and attached.

Haulier Name| Requested Ship Date| 04. Collection Site| Customer Name| DepotID| Depot Description| Delivery Address| Sales Order ID| CustomerRef| Pallet Name| PalletQty| PO REF
---|---|---|---|---|---|---|---|---|---|---|---
Stuart Lyons| 25/08/2026| Drayton| Aldi| ALD20| Aldi SAWLEY Distribution Centre| DE72 2HP| SO000368395| 6511967794| IPP Euro| 2| PO00500547
Stuart Lyons| 25/08/2026| Selsey| Aldi| ALD26| Aldi ATHERSTONE Distribution Centre| CV9 2SQ| SO000368427| 6511972761| IPP Euro| 1| PO00500547
Stuart Lyons| 25/08/2026| Selsey| Tesco| ONE01| One Stop Tamworth| B78 1ST| SO000368432| 8000053695| IPP STD| 0| PO00500547
""";
        var request = new MailboxEmailIntakeRequest(
            "message-body-table",
            null,
            "info@lyonshaulage.com",
            "ShiftLogisticalPlanner@nwfltd.co.uk",
            "Shift Logistical Planner",
            "NWAY Stuart Lyons Transport Pallet Order Report 25/08/2026",
            DateTimeOffset.Parse("2026-08-24T13:26:16Z"),
            body,
            body,
            null,
            [new MailboxAttachmentRequest("NWAY Stuart Lyons Transport Pallet Order Report 25-08-2026.csv", "text/csv", null, false, Size: 4096)]);

        var result = parser.TryParse(request);

        Assert.NotNull(result);
        Assert.Null(result!.IgnoredReason);
        Assert.Equal(3, result.Orders.Count);
        Assert.Contains(result.Warnings, warning => warning.Contains("zero-pallet", StringComparison.OrdinalIgnoreCase));
        var zero = Assert.Single(result.Orders.Where(order => order.Payload.GetProperty("salesOrderId").GetString() == "SO000368432"));
        Assert.Equal(0, zero.Payload.GetProperty("pallets").GetInt32());
        Assert.Equal("Selsey", zero.Payload.GetProperty("sellerName").GetString());
        Assert.Equal("One Stop Tamworth", zero.Payload.GetProperty("stallNumber").GetString());
        var first = result.Orders[0].Payload;
        Assert.Equal("PO00500547/SO000368395/Drayton/ALD20", first.GetProperty("poNumber").GetString());
        Assert.Equal("NWF pallet order email body table.csv", first.GetProperty("sourceAttachmentName").GetString());
        Assert.Equal("Aldi SAWLEY Distribution Centre", first.GetProperty("stallNumber").GetString());
        Assert.Equal(2, first.GetProperty("pallets").GetInt32());
    }

    [Fact]
    public void MissingPo_UsesSalesOrderOnlyAsFallbackAndFlagsReview()
    {
        const string csv = """
Haulier Name,Requested Ship Date,04. Collection Site,Customer Name,DepotID,Depot Description,Delivery Address,Sales Order ID,CustomerRef,Pallet Name,PalletQty,PO REF
Stuart Lyons,19/08/2026,Drayton,Aldi,ALD20,Aldi SAWLEY Distribution Centre,DE72 2HP,SO000367762,6511786146,IPP Euro,2,
""";

        var row = Assert.Single(parser.TryParse(Request(csv, "message-no-po"))!.Orders);
        Assert.StartsWith("SO000367762/Drayton/ALD20", row.Payload.GetProperty("poNumber").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Medium", row.Payload.GetProperty("intakeConfidence").GetString());
        Assert.Contains(row.Payload.GetProperty("intakeWarnings").EnumerateArray().Select(item => item.GetString()),
            warning => warning is not null && warning.Contains("PO REF is missing", StringComparison.OrdinalIgnoreCase));
    }

    private static MailboxEmailIntakeRequest Request(string csv, string messageId) =>
        new(
            messageId,
            null,
            "info@lyonshaulage.com",
            "ShiftLogisticalPlanner@nwfltd.co.uk",
            "Shift Logistical Planner",
            "NWAY Stuart Lyons Transport Pallet Order Report 19/08/2026",
            DateTimeOffset.Parse("2026-08-18T18:00:00Z"),
            "Please see attached pallet order report.",
            null,
            null,
            [new MailboxAttachmentRequest(
                "NWAY PALLET ORDER REPORT SLH.csv",
                "text/csv",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(csv)),
                false)]);
}
