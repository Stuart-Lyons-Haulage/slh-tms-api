using System.Text.Json;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class EmailOrderIntakeServiceTests
{
    private readonly EmailOrderIntakeService service = new();

    [Fact]
    public void InternalLyonsPlannerEmail_IsIgnored()
    {
        var result = service.Parse(new MailboxEmailIntakeRequest(
            "message-1", null, "info@lyonshaulage.com", "joe@lyonshaulage.com", "Joe",
            "Lyons Collections-18082026", DateTimeOffset.Parse("2026-08-17T15:00:00Z"),
            "Please find attached Load plan for tomorrow.", null, null, null));

        Assert.Empty(result.Orders);
        Assert.NotNull(result.IgnoredReason);
    }

    [Fact]
    public void TrayCollectionBody_CreatesPendingOrderShape()
    {
        var result = service.Parse(new MailboxEmailIntakeRequest(
            "message-tray", null, "info@lyonshaulage.com", "Ioana-Andreea.Pascalau@summerberry.co.uk", "Ioana",
            "Tray collection Northampton 18/08", DateTimeOffset.Parse("2026-08-17T07:20:05Z"),
            "Could you please organise collection for the below\nNorthampton\n18/08\nTHE359/348310", null, null, null));

        var order = Assert.Single(result.Orders);
        Assert.Equal("TSBC", order.Payload.GetProperty("customerCode").GetString());
        Assert.Equal("2026-08-18", order.Payload.GetProperty("collectionDate").GetString());
        Assert.Equal("Northampton", order.Payload.GetProperty("sellerName").GetString());
        Assert.Equal("THE359/348310/NORTHAMPTON", order.Payload.GetProperty("poNumber").GetString());
        Assert.Equal("Tray collection", order.Payload.GetProperty("jobType").GetString());
    }

    [Fact]
    public void SummerBerryCoopBody_ExtractsPalletsTimeTemperatureAndDate()
    {
        var result = service.Parse(new MailboxEmailIntakeRequest(
            "message-coop", null, "info@lyonshaulage.com", "Ioana-Andreea.Pascalau@summerberry.co.uk", "Ioana",
            "TSBC COOP - 18.08.2026", DateTimeOffset.Parse("2026-08-17T09:53:57Z"),
            "Please find attached pallet requirements. Total Pallets : 2 Collection time: 17:00 Transport at +3 degrees Collect from: Groves Farm", null, null, null));

        var order = Assert.Single(result.Orders);
        Assert.Equal("COOP", order.Payload.GetProperty("customerCode").GetString());
        Assert.Equal(2, order.Payload.GetProperty("pallets").GetInt32());
        Assert.Equal("Groves Farm", order.Payload.GetProperty("sellerName").GetString());
        Assert.Contains("Requested time: 17:00", order.Payload.GetProperty("driverInstructions").GetString());
        Assert.Contains("Temperature: +3°C", order.Payload.GetProperty("driverInstructions").GetString());
    }

    [Fact]
    public void SummerBerryLabelledPalletBody_IsStaged()
    {
        var result = service.Parse(new MailboxEmailIntakeRequest(
            "message-summerberry-spinneys", null, "info@lyonshaulage.com", "Ioana-Andreea.Pascalau@summerberry.co.uk", "Ioana",
            "26.08.2026 Spinneys/JHF delivery", DateTimeOffset.Parse("2026-08-25T07:10:15Z"),
            "Customer : Spinneys/JHF order\nDepot Date: 26.08.2026\nCollection : 25.08.2026- 17:00\nPallets : 5\nAdress of delivery : K&N facility", null, null, null));

        var order = Assert.Single(result.Orders);
        Assert.Equal("TSBC", order.Payload.GetProperty("customerCode").GetString());
        Assert.Equal("2026-08-26", order.Payload.GetProperty("collectionDate").GetString());
        Assert.Equal("Spinneys/JHF", order.Payload.GetProperty("stallNumber").GetString());
        Assert.Equal(5, order.Payload.GetProperty("pallets").GetInt32());
    }

    [Fact]
    public void HillBrothersHamsHallTrolleyBody_IsStaged()
    {
        var result = service.Parse(new MailboxEmailIntakeRequest(
            "message-hams-hall", null, "info@lyonshaulage.com", "eva@hillsplants.com", "Eva Jaskulska",
            "Hams Hall - depot THU", DateTimeOffset.Parse("2026-08-25T06:54:52Z"),
            "Good morning,\nHams Hall order for depot Thursday 27th Aug is 19 trolleys.\nHills collection.\nKind regards,\nEva", null, null, null));

        var order = Assert.Single(result.Orders);
        Assert.Equal("HILLBROTHERS", order.Payload.GetProperty("customerCode").GetString());
        Assert.Equal("2026-08-27", order.Payload.GetProperty("collectionDate").GetString());
        Assert.Equal("Hams Hall", order.Payload.GetProperty("stallNumber").GetString());
        Assert.Equal("Hill Brothers", order.Payload.GetProperty("sellerName").GetString());
        Assert.Equal(19, order.Payload.GetProperty("pallets").GetInt32());
    }

    [Fact]
    public void LangmeadsDeliveryToBody_IsStagedWithCollectionTime()
    {
        var result = service.Parse(new MailboxEmailIntakeRequest(
            "message-langmeads-zenith", null, "info@lyonshaulage.com", "EmiliaMargas@langmeadherbs.co.uk", "Emilia Margas",
            "Delivery to Zenith Nurseries  26.08.2026", DateTimeOffset.Parse("2026-08-25T07:59:51Z"),
            "Good morning\nCould you please collect below for delivery to Zenith Nurseries - Station Road, Evesham, WR11 8LW.\n* Wednesday (26.08.2026) collection, the same day delivery - 16 pallets\nCollection 8 am.\nLangmead Herbs Limited\nHam Farm, Main Road, Bosham", null, null, null));

        var order = Assert.Single(result.Orders);
        Assert.Equal("LANGMEADS", order.Payload.GetProperty("customerCode").GetString());
        Assert.Equal("2026-08-26", order.Payload.GetProperty("collectionDate").GetString());
        Assert.Equal("Ham Farm", order.Payload.GetProperty("sellerName").GetString());
        Assert.Equal("Zenith Nurseries", order.Payload.GetProperty("stallNumber").GetString());
        Assert.Equal(16, order.Payload.GetProperty("pallets").GetInt32());
        Assert.Contains("Requested time: 08:00", order.Payload.GetProperty("driverInstructions").GetString());
    }

    [Theory]
    [InlineData("Langmeads Aldi booking 26/08", "Please book delivery to Aldi Atherstone. Collection 8 am.", "LANGMEADS", "Aldi Atherstone")]
    [InlineData("Barfoots Morrisons delivery 26/08", "Please find attached correct pallet booking.", "BARFOOTS", "Barfoots")]
    [InlineData("Natures Way Waitrose 26/08", "Waitrose booking confirmed for tomorrow.", "WAITROSE", "Waitrose")]
    public void RecognisedSupplierOrSupermarket_WithDateAndNoQuantity_IsStillStagedForReview(
        string subject,
        string body,
        string expectedCustomer,
        string expectedSite)
    {
        var result = service.Parse(new MailboxEmailIntakeRequest(
            $"message-low-detail-{expectedCustomer}", null, "info@lyonshaulage.com", "loads@example.com", "Loads",
            subject, DateTimeOffset.Parse("2026-08-25T07:00:00Z"),
            body, null, null, null));

        var order = Assert.Single(result.Orders);
        Assert.Equal(expectedCustomer, order.Payload.GetProperty("customerCode").GetString());
        Assert.Equal(expectedSite, order.Payload.GetProperty("stallNumber").GetString());
        Assert.Equal(JsonValueKind.Null, order.Payload.GetProperty("pallets").ValueKind);
        Assert.Contains(order.Warnings, warning => warning.Contains("Pallet quantity was not explicit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DatedBodyWithoutOrderFields_IsNotStagedAsZeroPalletFallback()
    {
        var result = service.Parse(new MailboxEmailIntakeRequest(
            "message-vague", null, "info@lyonshaulage.com", "loads@example.com", "Loads",
            "Available work 26/08", DateTimeOffset.Parse("2026-08-24T09:00:00Z"),
            "Can you look at these from tomorrow. Pallets may be exchanged.", null, null, null));

        Assert.Empty(result.Orders);
        Assert.Contains("No transport order", result.IgnoredReason);
        Assert.Contains(result.Warnings, warning => warning.Contains("not enough order detail", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("Sainsburys order 26/08", "Please arrange 12 pallets.", null, "SAINSBURY", "Sainsbury")]
    [InlineData("Collection request 26/08", "Waitrose Bracknell needs 8 pallets.", null, "WAITROSE", "Waitrose")]
    [InlineData("Collection request 26/08", "Please arrange 10 pallets.", "Natures Way collections.xlsx", "NWF", "Natures Way")]
    [InlineData("Collection request 26/08", "Barfoots Sefter 6 pallets.", null, "BARFOOTS", "Barfoots")]
    [InlineData("Weightrose order 26/08", "Please arrange 4 pallets.", null, "WAITROSE", "Waitrose")]
    public void RecognisedCustomerOrSite_WithDateAndPallets_IsStagedForReview(
        string subject,
        string body,
        string? attachmentName,
        string expectedCustomer,
        string expectedSite)
    {
        var attachments = attachmentName is null
            ? null
            : new List<MailboxAttachmentRequest> { new(attachmentName, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", null, false) };

        var result = service.Parse(new MailboxEmailIntakeRequest(
            $"message-{expectedCustomer}-{expectedSite}", null, "info@lyonshaulage.com", "loads@example.com", "Loads",
            subject, DateTimeOffset.Parse("2026-08-24T09:00:00Z"),
            body, null, null, attachments));

        var order = Assert.Single(result.Orders);
        Assert.Equal(expectedCustomer, order.Payload.GetProperty("customerCode").GetString());
        Assert.Equal(expectedSite, order.Payload.GetProperty("stallNumber").GetString());
        Assert.Equal("2026-08-26", order.Payload.GetProperty("collectionDate").GetString());
        Assert.Contains(order.Warnings, warning => warning.Contains("Collection site was not explicit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MasterDataSiteName_WithDateAndPallets_IsStagedForReview()
    {
        var result = service.Parse(new MailboxEmailIntakeRequest(
            "message-master-site", null, "info@lyonshaulage.com", "loads@example.com", "Loads",
            "Collection request 26/08", DateTimeOffset.Parse("2026-08-24T09:00:00Z"),
            "Sainsbury Waltham Point has 16 pallets for collection.", null, null, null),
            ["Sainsbury Waltham Point"]);

        var order = Assert.Single(result.Orders);
        Assert.Equal("SAINSBURY", order.Payload.GetProperty("customerCode").GetString());
        Assert.Equal("Sainsbury Waltham Point", order.Payload.GetProperty("stallNumber").GetString());
        Assert.Equal(16, order.Payload.GetProperty("pallets").GetInt32());
    }

    [Fact]
    public void NightShunting_IsNotMisclassifiedAsOrder()
    {
        var result = service.Parse(new MailboxEmailIntakeRequest(
            "message-shunt", null, "info@lyonshaulage.com", "DanielLawes@nwfltd.co.uk", "Daniel Lawes",
            "NWF Night Shunting", DateTimeOffset.Parse("2026-08-17T12:07:47Z"),
            "Please confirm if you can cover the night shift again this evening.", null, null, null));

        Assert.Empty(result.Orders);
        Assert.Contains("Operational request", result.IgnoredReason);
    }

    [Fact]
    public void AvailableLoadsMailshot_IsIgnoredAsOperationalNoise()
    {
        var result = service.Parse(new MailboxEmailIntakeRequest(
            "message-monarch-loads", null, "info@lyonshaulage.com", "mailshot@monarchtransport.co.uk", "Monarch Transport",
            "URGENT - Monarch Transport Available Loads", DateTimeOffset.Parse("2026-08-25T06:16:39Z"),
            "Monarch Available Loads. Can you cover the below loads? You are receiving this email because you opted in via our site.",
            null, null, null));

        Assert.Empty(result.Orders);
        Assert.Contains("Operational request", result.IgnoredReason);
    }
}
