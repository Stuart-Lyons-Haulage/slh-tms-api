using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class DispatchIntelligenceRulesTests
{
    [Fact]
    public async Task MarketRun_named_skill_is_accepted_for_market_run_suggestion()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase($"dispatch-intelligence-{Guid.NewGuid():N}")
            .Options;
        await using var db = new TmsDbContext(options);

        var driver = new Driver
        {
            Id = Guid.NewGuid(),
            EmployeeNumber = "SLH001",
            DisplayName = "Market Driver",
            DriverType = "Employed",
            Skills = "MarketRun",
            Active = true
        };
        var run = new Load
        {
            Id = Guid.NewGuid(),
            Reference = "PM Market Run 1",
            PlanningDate = new DateOnly(2026, 9, 10),
            Status = LoadStatus.Draft,
            Stops =
            [
                new LoadStop { Sequence = 1, Name = "Collect · Barnham", Latitude = 50.84m, Longitude = -0.64m },
                new LoadStop { Sequence = 2, Name = "Deliver · New Spitalfields Market", Latitude = 51.57m, Longitude = 0.01m }
            ]
        };

        var result = await DriverDispatchAssistantService.BuildAsync(
            db,
            run.PlanningDate,
            [driver],
            [run],
            [],
            [],
            [],
            new HashSet<Guid>(),
            CancellationToken.None);

        Assert.True(result.TryGetValue(driver.Id, out var suggestion));
        Assert.Equal(run.Id, suggestion!.LoadId);
    }
}
