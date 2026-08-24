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
}
