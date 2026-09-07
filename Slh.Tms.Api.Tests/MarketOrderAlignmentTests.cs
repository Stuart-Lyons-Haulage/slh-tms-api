using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class MarketOrderAlignmentTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory factory;

    public MarketOrderAlignmentTests(CustomWebFactory factory) => this.factory = factory;

    [Fact]
    public async Task Market_customer_resolves_to_market_site_and_keeps_stall_in_driver_text()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var site = new Site
        {
            Id = Guid.NewGuid(),
            ExternalCode = $"COV-{suffix}",
            Name = $"New Covent Garden Market {suffix}",
            DriverTextName = $"New Covent Garden Market {suffix}",
            Active = true
        };
        var customer = new MarketContact
        {
            Id = Guid.NewGuid(),
            Market = "Covent",
            Name = $"ABC Produce {suffix}",
            StandOrLocation = $"Stand D12-{suffix}",
            Salesman = $"Joe {suffix}",
            Active = true
        };
        db.Sites.Add(site);
        db.MarketContacts.Add(customer);
        await db.SaveChangesAsync();

        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            sellerName = "NWF - Merston",
            marketName = site.Name,
            stallNumber = customer.Name
        }));

        var alignment = await OrderSiteMasterAlignment.ResolveAsync(db, payload.RootElement, CancellationToken.None);

        Assert.Equal(site.DriverTextName, alignment.DeliveryName);
        Assert.Contains($"Market: {site.DriverTextName}", alignment.DriverInstructions);
        Assert.Contains($"Market customer: {customer.Name}", alignment.DriverInstructions);
        Assert.Contains($"Stall / stand: {customer.StandOrLocation}", alignment.DriverInstructions);
        Assert.Contains($"Salesman: {customer.Salesman}", alignment.DriverInstructions);
    }

    [Fact]
    public async Task Ambiguous_market_customer_is_not_guessed()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var site = new Site
        {
            Id = Guid.NewGuid(),
            ExternalCode = $"SPI-{suffix}",
            Name = $"Spitalfields Market {suffix}",
            DriverTextName = $"Spitalfields Market {suffix}",
            Active = true
        };
        db.Sites.Add(site);
        db.MarketContacts.AddRange(
            new MarketContact { Market = "Spit", Name = $"Same Trader {suffix}", StandOrLocation = "A1", Active = true },
            new MarketContact { Market = "Spit", Name = $"Same Trader {suffix}", StandOrLocation = "B2", Active = true });
        await db.SaveChangesAsync();

        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            marketName = site.Name,
            stallNumber = $"Same Trader {suffix}"
        }));

        var alignment = await OrderSiteMasterAlignment.ResolveAsync(db, payload.RootElement, CancellationToken.None);

        Assert.Equal($"Same Trader {suffix}", alignment.DeliveryName);
        Assert.DoesNotContain("Market customer:", alignment.DriverInstructions ?? string.Empty);
        Assert.DoesNotContain("Stall / stand:", alignment.DriverInstructions ?? string.Empty);
    }
}
