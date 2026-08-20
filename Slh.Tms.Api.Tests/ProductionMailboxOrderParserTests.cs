using System.Text;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class ProductionMailboxOrderParserTests
{
    [Fact]
    public void HhpWaitroseDirectDepot_PreservesPalletsCasesDatesAndPo()
    {
        var parser = new KnownCustomerMailboxOrderParser();
        var request = new MailboxEmailIntakeRequest(
            "hhp-1", "internet-1", "info@lyonshaulage.com", "Shaun.bennett@primafruit.co.uk", "Shaun Bennett",
            "HHP WAITROSE DIRECT DEPOT DELIVERY Friday 21/08/26", DateTimeOffset.Parse("2026-08-20T09:40:23Z"),
            "Good morning\nPlease collect 5 pallets from Hall Hunter today Thursday 20/08/2026.\n- Leyland 5 pallets\nFor Delivery date Friday 21/08/2026.\nPO number: A58971. 196 cases of Berries.",
            null, "https://outlook/source", null);

        var result = Assert.IsType<EmailIntakeParseResult>(parser.TryParse(request));
        var order = Assert.Single(result.Orders);
        Assert.Equal("WAITROSE", order.Payload.GetProperty("customerCode").GetString());
        Assert.Equal("A58971", order.Payload.GetProperty("poNumber").GetString());
        Assert.Equal("2026-08-20", order.Payload.GetProperty("collectionDate").GetString());
        Assert.Equal("2026-08-21", order.Payload.GetProperty("deliveryDate").GetString());
        Assert.Equal("Hall Hunter", order.Payload.GetProperty("sellerName").GetString());
        Assert.Equal("Leyland", order.Payload.GetProperty("stallNumber").GetString());
        Assert.Equal(5, order.Payload.GetProperty("pallets").GetInt32());
        Assert.Equal(196, order.Payload.GetProperty("cases").GetInt32());
    }

    [Fact]
    public void WaitroseBodyTable_CreatesOneOrderPerDepotWithExactPalletCounts()
    {
        var parser = new KnownCustomerMailboxOrderParser();
        var body = """
            Good morning,
            These are the Waitrose pallet counts for delivery tomorrow.
            DELIVERY DATE| 21/08 /2026| +10
            DEPOT| PO NUMBER| PALLET COUNT
            AYLESFORD| X58619| 3
            BRACKNELL| J58778| 4
            BRINKLOW| R58683| 3
            LEYLAND| Z57494| 1
            """;
        var request = new MailboxEmailIntakeRequest(
            "waitrose-table", null, "info@lyonshaulage.com", "Tom.Whiting@fowlerwelch.co.uk", "Tom Whiting",
            "Waitrose 21/08/26", DateTimeOffset.Parse("2026-08-20T05:19:29Z"), body, null, null, null);

        var result = Assert.IsType<EmailIntakeParseResult>(parser.TryParse(request));
        Assert.Equal(4, result.Orders.Count);
        var aylesford = result.Orders.Single(x => x.Payload.GetProperty("stallNumber").GetString() == "AYLESFORD");
        Assert.Equal("X58619", aylesford.Payload.GetProperty("poNumber").GetString());
        Assert.Equal(3, aylesford.Payload.GetProperty("pallets").GetInt32());
        Assert.Equal("2026-08-21", aylesford.Payload.GetProperty("deliveryDate").GetString());
        Assert.Equal("Fowler Welch / Waitrose pallet counts", aylesford.Payload.GetProperty("mappingTemplate").GetString());
    }

    [Fact]
    public void GenericCsv_ParsesMultipleOrdersAndDoesNotLosePallets()
    {
        var csv = "Customer,PO,Collection Date,Collection Site,Delivery Date,Delivery Site,Pallets,Cases,Temperature\nALDI,A100,21/08/2026,Greenhouse,21/08/2026,ALDI-BOLTON,8,320,+3C\nMORRISONS,M200,21/08/2026,Groves Farm,21/08/2026,MORRISONS-LATIMER PARK,4,120,+3C";
        var attachment = new MailboxAttachmentRequest("orders.csv", "text/csv", Convert.ToBase64String(Encoding.UTF8.GetBytes(csv)), false);
        var request = new MailboxEmailIntakeRequest(
            "csv-message", null, "info@lyonshaulage.com", "orders@example.com", "Orders",
            "Aldi & Morrisons orders", DateTimeOffset.Parse("2026-08-20T12:00:00Z"), "Please see attached orders", null, null, [attachment]);

        var result = Assert.IsType<EmailIntakeParseResult>(new GenericCsvOrderParser().TryParse(request));
        Assert.Equal(2, result.Orders.Count);
        Assert.Equal(8, result.Orders[0].Payload.GetProperty("pallets").GetInt32());
        Assert.Equal(4, result.Orders[1].Payload.GetProperty("pallets").GetInt32());
        Assert.Equal("A100", result.Orders[0].Payload.GetProperty("poNumber").GetString());
        Assert.Equal("M200", result.Orders[1].Payload.GetProperty("poNumber").GetString());
    }
}
