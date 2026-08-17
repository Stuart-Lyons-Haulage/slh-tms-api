using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class SpecialistMailboxOrderParserTests
{
    private readonly SpecialistMailboxOrderParser parser = new();

    [Fact]
    public void Cancellation_DoesNotCreateNewOrder()
    {
        var result = parser.TryParse(new MailboxEmailIntakeRequest(
            "cancel-1", null, "info@lyonshaulage.com", "MalwinaFrasek@nwfltd.co.uk", "Malwina Frasek",
            "IFCO Glasshoughton 285349425 - cancelled 18.08", DateTimeOffset.Parse("2026-08-17T12:00:00Z"),
            "Due to tray wash shortage load ref Glasshoughton 285349425 has been cancelled 18.08 IFCO PO00500400", null, null, null));

        Assert.NotNull(result);
        Assert.Empty(result!.Orders);
        Assert.NotNull(result.IgnoredReason);
    }

    [Fact]
    public void AmazonBody_UsesSeparateCollectionAndDeliveryDates()
    {
        var result = parser.TryParse(new MailboxEmailIntakeRequest(
            "amazon-1", null, "info@lyonshaulage.com", "Kevin.Nicholls@iowtomatoes.co.uk", "Kevin Nicholls",
            "Amazon Delivery Wednesday 19th August", DateTimeOffset.Parse("2026-08-17T12:44:20Z"),
            "Please see below Amazon for delivery 19/08/2026.\nBooking Ref: 22246489013\nCollection: Tuesday 18th August from 16:00:\nAPS Produce\nChichester Food Park\nDelivery Wednesday 19th August:\nAmazon ALT2 - MILTON KEYNES\nALT2\n2 pallets TOTAL WEIGHT 973kgs", null, null, null));

        var order = Assert.Single(result!.Orders);
        Assert.Equal("AMAZON", order.Payload.GetProperty("customerCode").GetString());
        Assert.Equal("2026-08-18", order.Payload.GetProperty("collectionDate").GetString());
        Assert.Equal("2026-08-19", order.Payload.GetProperty("deliveryDate").GetString());
        Assert.Equal("22246489013", order.Payload.GetProperty("customerPo").GetString());
        Assert.Equal(2, order.Payload.GetProperty("pallets").GetInt32());
        Assert.Equal("APS Produce", order.Payload.GetProperty("sellerName").GetString());
        Assert.Equal("Amazon ALT2 - MILTON KEYNES", order.Payload.GetProperty("stallNumber").GetString());
    }

    [Fact]
    public void CoventGardenBody_CreatesOneOrderPerDrop()
    {
        var result = parser.TryParse(new MailboxEmailIntakeRequest(
            "covent-1", null, "info@lyonshaulage.com", "Kevin.Nicholls@iowtomatoes.co.uk", "Kevin Nicholls",
            "Covent Garden Deliveries Tues 18th August/Wed 19th August", DateTimeOffset.Parse("2026-08-17T12:40:55Z"),
            "Collection: Tuesday 18th August from 16:00:\nAPS Produce\nDelivery Tuesday evening/Wednesday morning 18th August/19th August:\nI A Harris - 1 pallet, 186kg\nKale & Damson - 1 pallet, 203kg\nPrimeur - 2 pallets, 575kg", null, null, null));

        Assert.Equal(3, result!.Orders.Count);
        Assert.Equal(new[] { 1, 1, 2 }, result.Orders.Select(order => order.Payload.GetProperty("pallets").GetInt32()).ToArray());
        Assert.All(result.Orders, order => Assert.Equal("2026-08-18", order.Payload.GetProperty("collectionDate").GetString()));
        Assert.All(result.Orders, order => Assert.Equal("2026-08-19", order.Payload.GetProperty("deliveryDate").GetString()));
    }

    [Fact]
    public void RouteSubject_ExtractsFromToReferenceAndGenericPalletCount()
    {
        var result = parser.TryParse(new MailboxEmailIntakeRequest(
            "transfer-1", null, "info@lyonshaulage.com", "Kamila.Biohn@barfoots.co.uk", "Kamila Biohn",
            "Collection from Milton Keynes to Sefter 18.08, 285351670", DateTimeOffset.Parse("2026-08-17T11:00:00Z"),
            "38 pallets Please deliver on the same day IFCO SYSTEMS", null, null, null));

        var order = Assert.Single(result!.Orders);
        Assert.Equal("IFCO", order.Payload.GetProperty("customerCode").GetString());
        Assert.Equal("Milton Keynes", order.Payload.GetProperty("sellerName").GetString());
        Assert.Equal("Sefter", order.Payload.GetProperty("stallNumber").GetString());
        Assert.Equal(38, order.Payload.GetProperty("pallets").GetInt32());
        Assert.Equal("2026-08-18", order.Payload.GetProperty("collectionDate").GetString());
    }
}
