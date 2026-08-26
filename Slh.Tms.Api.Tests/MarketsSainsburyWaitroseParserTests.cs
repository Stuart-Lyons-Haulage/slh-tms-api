using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class MarketsSainsburyWaitroseParserTests
{
    private readonly MarketsSainsburyWaitroseParser parser = new();

    [Fact]
    public void TangmereMarketMatrix_CreatesEveryMarketDrop()
    {
        var body = """
        WESTERN| | |SPITALFIELD| | 
        JAYSTAR|1| |FRESH IMPORT|7|
        SMITH|2| |M&M|3|
        UNIVERSAL|5| |PUNJAB|1|
        SPITALFIELD| | |SUNFRESH|17|
        WALDON|19| |QUALITY|5|
        Kind Regards
        """;
        var result = parser.TryParse(Request("Tangmere market 26/08", "Keiran@PMTransport.co.uk", body));

        Assert.NotNull(result);
        Assert.Null(result!.IgnoredReason);
        Assert.Equal(9, result.Orders.Count);
        Assert.Equal(60, result.Orders.Sum(order => order.Payload.GetProperty("pallets").GetInt32()));
        Assert.Contains(result.Orders, order => order.Payload.GetProperty("marketName").GetString() == "Western" && order.Payload.GetProperty("stallNumber").GetString() == "WALDON" && order.Payload.GetProperty("pallets").GetInt32() == 19);
        Assert.Contains(result.Orders, order => order.Payload.GetProperty("marketName").GetString() == "Spitalfields" && order.Payload.GetProperty("stallNumber").GetString() == "SUNFRESH" && order.Payload.GetProperty("pallets").GetInt32() == 17);
    }

    [Fact]
    public void AdditionalMarket_OnlyCreatesIncrementalThreePallets()
    {
        var result = parser.TryParse(Request(
            "Additional market",
            "Keiran@PMTransport.co.uk",
            "Another 3pt sunstar spit please\nAll pallets will be ready about 6pm,\nThey may get 11pt ready for about 5 if needed"));

        var order = Assert.Single(result!.Orders);
        Assert.Equal(3, order.Payload.GetProperty("pallets").GetInt32());
        Assert.Equal("Sunstar", order.Payload.GetProperty("stallNumber").GetString());
        Assert.Equal("18:00", order.Payload.GetProperty("requestedTime").GetString());
    }

    [Fact]
    public void SainsburyCrosspointPrePlan_PreservesTwoTwentySixPalletLoadsForReview()
    {
        var rows = new List<object?[]>
        {
            ["Collection Date", "Collecting Depot / Haulier", "Collection Site", "Destination", "SCION Order Number", "Pallets", "Collection Time", "Original Delivery Time", "Actual Delivery Time"],
            [new DateTime(2026, 8, 27), "STUART LYONS", "CROSSPOINT PCC", "BASINGSTOKE", "388393", 26d, 10d/24d, 4d/24d, 13d/24d],
            [new DateTime(2026, 8, 27), "STUART LYONS", "CROSSPOINT PCC", "BASINGSTOKE", "388826", 26d, 11d/24d, 19d/24d, 14d/24d]
        };

        var orders = MarketsSainsburyWaitroseParser.ParseSainsburyPrePlanRows(
            Request("[CrosspointPCC] STUART LYONS - Crosspoint PCC Plan for delivery date 27/08/2026", "Central.Transport@sainsburys.co.uk", ""),
            rows,
            new DateOnly(2026, 8, 27));

        Assert.Equal(2, orders.Count);
        Assert.Equal(52, orders.Sum(order => order.Payload.GetProperty("pallets").GetInt32()));
        Assert.All(orders, order =>
        {
            Assert.Equal("SAINSBURY", order.Payload.GetProperty("customerCode").GetString());
            Assert.False(order.Payload.GetProperty("plannerReady").GetBoolean());
            Assert.Equal("ReviewRequired", order.Payload.GetProperty("intakeStatus").GetString());
        });
    }

    [Fact]
    public void BarfootsWholesaleRows_IgnoreBlankTemplateRowsAndKeepPositiveBookings()
    {
        var rows = new List<object?[]>
        {
            ["COLLECTION SEFTER", null, null, null, null, null, null, null],
            ["Market", "Market Customer", "Delivery addess", "Delivery Date", "Delivery Time", "Temp.", "No. of Pallets", "SO"],
            ["NEW COVENT GARDEN", "Foodpoint Produce", "D53 - 56", new DateTime(2026, 8, 27), "02:00", "+3", 2d, "3540725"],
            ["NEW COVENT GARDEN", "Premier Foods Exotics", "", new DateTime(2026, 8, 27), "02:00", "+3", 2d, "3541704"],
            ["NEW COVENT GARDEN", "Premier Foods Veg & Salad", "", new DateTime(2026, 8, 27), "02:00", "+3", 1d, "3541705"],
            ["NEW SPITALFIELDS", "Canim Fruit & Veg", "", new DateTime(2026, 8, 27), "03:00", "+3", 1d, "3541709"],
            ["BRIGHTON MARKET", "TG Fruits Brighton", "", new DateTime(2026, 8, 27), "04:00", "+3", 1d, "3541711"],
            ["Unused template customer", "Blank", "", new DateTime(2026, 8, 27), "", "", null, null],
            ["COLLECTION LEYTHORNE", null, null, null, null, null, null, null],
            ["Market", "Market Customer", "Delivery addess", "Delivery Date", "Delivery Time", "Temp.", "No. of Pallets", "SO"],
            ["NEW COVENT GARDEN", "Unused", "", new DateTime(2026, 8, 27), "", "", 0d, null]
        };

        var orders = MarketsSainsburyWaitroseParser.ParseBarfootsWholesaleRows(
            Request("Wholesale Market Pallet Bookings for delivery on 27/08/26", "Mariela.Popova@barfoots.co.uk", ""), rows);

        Assert.Equal(5, orders.Count);
        Assert.Equal(7, orders.Sum(order => order.Payload.GetProperty("pallets").GetInt32()));
        Assert.All(orders, order => Assert.Equal("Sefter", order.Payload.GetProperty("sellerName").GetString()));
    }

    [Fact]
    public void BarfootsWaitroseWaveBody_CreatesFourOrdersAndSixteenPallets()
    {
        var body = """
        Please see attached Waitrose confirmed pallet booking:
        Aylesford WAVE 1 from Sefter 2 pallets PO O78057 & Aylesford Wave 3 10 pallets PO O78077.
        Leyland WAVE 1 from Sefter 1 pallet PO B78374 & Leyland Wave 3 3 pallets PO B78353.
        """;
        var result = parser.TryParse(Request("Waitrose from Sefter & Leythorne for depot 27/08/26", "Agnieszka.Zawislan@barfoots.co.uk", body));

        Assert.Equal(4, result!.Orders.Count);
        Assert.Equal(16, result.Orders.Sum(order => order.Payload.GetProperty("pallets").GetInt32()));
        Assert.All(result.Orders, order => Assert.Equal("WAITROSE", order.Payload.GetProperty("customerCode").GetString()));
    }

    [Fact]
    public void FowlerWaitroseTable_CreatesFourDepotOrdersAndNinePallets()
    {
        var body = """
        These are the Waitrose pallet counts for delivery tomorrow.
        DELIVERY DATE|27/08/2026|+10
        DEPOT|PO NUMBER|PALLET COUNT
        AYLESFORD|X58833|2
        BRACKNELL|J59023|3
        BRINKLOW|R58937|3
        LEYLAND|Z57729|1
        """;
        var result = parser.TryParse(Request("Waitrose 27/08/26", "Tom.Whiting@fowlerwelch.co.uk", body));

        Assert.Equal(4, result!.Orders.Count);
        Assert.Equal(9, result.Orders.Sum(order => order.Payload.GetProperty("pallets").GetInt32()));
    }

    [Fact]
    public void WaitrosePleaseIgnoreThis_IsAWithdrawalNotANewOrder()
    {
        var result = parser.TryParse(Request(
            "RE: HHP WAITROSE DIRECT DEPOT DELIVERY Wednesday 27/08/26",
            "chris.benning@primafruit.co.uk",
            "Please ignore this.\nEarlier quoted text: Please collect 4 pallets from Hall Hunter."));

        Assert.NotNull(result);
        Assert.Empty(result!.Orders);
        Assert.Contains("retraction", result.IgnoredReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NeweySainsburyImageRequirement_IsNotSilentlyIgnored()
    {
        var result = parser.TryParse(Request(
            "Sainsbury's Week 36",
            "raul.marin@newey.com",
            "Hi All, Please see our transport requirements for week 36."));

        Assert.NotNull(result);
        Assert.Empty(result!.Orders);
        Assert.Contains("mapping review", result.IgnoredReason!, StringComparison.OrdinalIgnoreCase);
    }

    private static MailboxEmailIntakeRequest Request(string subject, string sender, string body) => new(
        Guid.NewGuid().ToString("N"),
        null,
        "info@lyonshaulage.com",
        sender,
        "Planner",
        subject,
        DateTimeOffset.Parse("2026-08-26T09:00:00Z"),
        body,
        null,
        null,
        null);
}
