using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Tests;

public sealed class SharePointCustomerContactIntegrationTests
{
    [Fact]
    public void SharePoint_options_include_governed_customer_contacts_list()
    {
        var options = new SharePointMasterDataOptions();
        Assert.Equal("Hub Customer Contacts", options.Lists["customercontact"]);
    }

    [Fact]
    public async Task Customer_contact_payload_promotes_into_operational_projection()
    {
        await using var db = CreateDb();
        var service = new StagingService(db);
        var payload = JsonSerializer.SerializeToElement(new
        {
            customerCode = "COOP",
            name = "Transport Desk",
            email = "transport@example.test",
            mobileNumber = "07123456789",
            receivesEtaUpdates = true,
            active = true
        });

        await service.PromoteDirect("customercontact", payload, CancellationToken.None);

        var contact = await db.CustomerContacts.SingleAsync();
        Assert.Equal("COOP", contact.CustomerCode);
        Assert.Equal("Transport Desk", contact.Name);
        Assert.Equal("transport@example.test", contact.Email);
        Assert.True(contact.ReceivesEtaUpdates);
        Assert.True(contact.Active);
        Assert.True(await db.Customers.AnyAsync(customer => customer.Code == "COOP"));
    }

    [Fact]
    public async Task Customer_contact_payload_can_disable_eta_suggestions()
    {
        await using var db = CreateDb();
        var service = new StagingService(db);
        var enabled = JsonSerializer.SerializeToElement(new
        {
            customerCode = "WAITROSE",
            name = "Inbound Desk",
            email = "inbound@example.test",
            receivesEtaUpdates = true,
            active = true
        });
        var disabled = JsonSerializer.SerializeToElement(new
        {
            customerCode = "WAITROSE",
            name = "Inbound Desk",
            email = "inbound@example.test",
            receivesEtaUpdates = false,
            active = true
        });

        await service.PromoteDirect("customercontact", enabled, CancellationToken.None);
        await service.PromoteDirect("customercontact", disabled, CancellationToken.None);

        var contact = await db.CustomerContacts.SingleAsync();
        Assert.False(contact.ReceivesEtaUpdates);
    }

    private static TmsDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new TmsDbContext(options);
    }
}
