using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class CustomerEmailRouteServiceTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory factory;

    public CustomerEmailRouteServiceTests(CustomWebFactory factory) => this.factory = factory;

    [Fact]
    public async Task Approved_exact_sender_route_supplies_customer_and_collection_site()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var sender = $"orders-{suffix}@customer.example";
        var site = new Site { ExternalCode = $"SITE-{suffix}", Name = $"Farm {suffix}", Active = true };
        db.Sites.Add(site);
        db.CustomerEmailRoutes.Add(new CustomerEmailRoute
        {
            CustomerCode = $"CUS-{suffix}", SenderEmail = sender, DefaultSiteCode = site.ExternalCode,
            RequiresReview = false, Active = true
        });
        await db.SaveChangesAsync();

        var parsed = new EmailIntakeParseResult(
            [new ParsedEmailOrder("one", "one", JsonSerializer.SerializeToElement(new
            {
                poNumber = "PO-1", customerCode = "UNKNOWN", collectionDate = "2026-09-13",
                deliveryDate = "2026-09-13", deliverySite = "Depot", pallets = 12, plannerReady = true
            }), [])], [], null);
        var request = Request(sender, "New order");

        var result = await CustomerEmailRouteService.ApplyAsync(db, parsed, request, CancellationToken.None);
        var payload = result.Orders.Single().Payload;

        Assert.Equal($"CUS-{suffix}".ToUpperInvariant(), payload.GetProperty("customerCode").GetString());
        Assert.Equal(site.ExternalCode, payload.GetProperty("collectionSiteCode").GetString());
        Assert.Equal(site.Name, payload.GetProperty("collectionSite").GetString());
        Assert.True(payload.GetProperty("emailRouteMatched").GetBoolean());
        Assert.False(payload.GetProperty("emailRouteRequiresReview").GetBoolean());
    }

    [Fact]
    public async Task Conflicting_sender_routes_do_not_modify_the_order()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var sender = $"shared-{suffix}@customer.example";
        db.CustomerEmailRoutes.AddRange(
            new CustomerEmailRoute { CustomerCode = $"A-{suffix}", SenderEmail = sender, RequiresReview = false, Active = true },
            new CustomerEmailRoute { CustomerCode = $"B-{suffix}", SenderEmail = sender, RequiresReview = false, Active = true });
        await db.SaveChangesAsync();
        var payload = JsonSerializer.SerializeToElement(new { customerCode = "UNKNOWN" });
        var parsed = new EmailIntakeParseResult([new ParsedEmailOrder("one", "one", payload, [])], [], null);

        var result = await CustomerEmailRouteService.ApplyAsync(db, parsed, Request(sender, "Order"), CancellationToken.None);

        Assert.Equal("UNKNOWN", result.Orders.Single().Payload.GetProperty("customerCode").GetString());
        Assert.Contains(result.Warnings, warning => warning.Contains("conflicting CRM routes", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Approved_orders_learn_sender_but_remove_unsafe_site_default_when_site_varies()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var sender = $"repeat-{suffix}@customer.example";
        var customer = $"CUS-{suffix}";

        await CustomerEmailRouteService.LearnFromApprovedOrderAsync(db,
            JsonSerializer.SerializeToElement(new { sourceSender = sender, collectionSiteCode = $"ONE-{suffix}" }),
            customer, CancellationToken.None);
        await db.SaveChangesAsync();
        await CustomerEmailRouteService.LearnFromApprovedOrderAsync(db,
            JsonSerializer.SerializeToElement(new { sourceSender = sender, collectionSiteCode = $"TWO-{suffix}" }),
            customer, CancellationToken.None);
        await db.SaveChangesAsync();

        var route = db.CustomerEmailRoutes.Single(item => item.SenderEmail == sender);
        Assert.Equal(customer.ToUpperInvariant(), route.CustomerCode);
        Assert.Null(route.DefaultSiteCode);
        Assert.False(route.RequiresReview);
    }

    private static MailboxEmailIntakeRequest Request(string sender, string subject) => new(
        Guid.NewGuid().ToString("N"), null, "info@lyonshaulage.com", sender, null, subject,
        DateTimeOffset.UtcNow, null, null, null, []);
}
