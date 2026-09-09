using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
namespace Slh.Tms.Api.Tests;
public sealed class PlannerIncidentRegressionTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory factory;
    public PlannerIncidentRegressionTests(CustomWebFactory factory) => this.factory = factory;
    [Fact]
    public async Task Portal_can_create_a_run_and_read_it_back()
    {
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write");
        var date = "2027-02-01";
        var response = await client.PostAsJsonAsync("/api/v1/runs", new { reference = $"REGRESSION-{Guid.NewGuid():N}", planningDate = date,
            stops = new[] { new { name = "Collection" }, new { name = "Delivery" } }, palletSpacesUsed = 4, totalPalletSpaces = 26 });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        var runs = await client.GetFromJsonAsync<JsonElement>($"/api/v1/runs?date={date}");
        Assert.Contains(runs.EnumerateArray(), run => run.GetProperty("id").GetGuid() == created.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task New_run_endpoint_still_requires_a_reason_on_a_locked_day()
    {
        var date = new DateOnly(2027, 2, 2);
        using (var scope = factory.Services.CreateScope())
            await PlanLockStore.LockAsync(scope.ServiceProvider.GetRequiredService<TmsDbContext>(), date, "test", CancellationToken.None);
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write");
        var response = await client.PostAsJsonAsync("/api/v1/runs", new { reference = "LOCKED-REGRESSION", planningDate = date,
            stops = new[] { new { name = "Collection" } } });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("PLAN_LOCKED:", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Active_subcontractor_is_visible_in_Driver_Dispatch_without_Sage_or_Tacho_identity()
    {
        var name = $"Bannisters Test {Guid.NewGuid():N}";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.Drivers.Add(new Driver
            {
                EmployeeNumber = $"SUB-{Guid.NewGuid():N}"[..24],
                DisplayName = name,
                DriverType = "Subcontractor",
                DriverGroup = "Bannisters",
                Active = true
            });
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write");
        var response = await client.GetAsync("/api/v1/driver-dispatch?date=2027-02-03");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var driver = payload.GetProperty("drivers").EnumerateArray().Single(item => item.GetProperty("displayName").GetString() == name);
        Assert.Equal("Subcontractor", driver.GetProperty("driverType").GetString());
        Assert.Equal("Bannisters", driver.GetProperty("driverGroup").GetString());
    }

    [Theory]
    [InlineData("Employed", "Office", null, false)]
    [InlineData("Employed", null, null, false)]
    [InlineData("Driver Manager", "Office", null, false)]
    [InlineData("Employed", "Day Drivers", null, true)]
    [InlineData("Agency", null, null, true)]
    [InlineData("Subcontractor", "Bannisters", null, true)]
    [InlineData("Employed", "Operating Centre", "DRIVER-CARD", true)]
    public void Member_number_does_not_make_an_office_worker_a_driver(string type, string? group, string? card, bool expected)
    {
        Assert.Equal(expected, DriverPopulationRules.IsDriver(new Driver { EmployeeNumber = "TEST", DisplayName = "Test Employee", DriverType = type, DriverGroup = group,
            TachoMasterDriverId = "12345", TachoCardNumber = card }));
    }
    [Theory]
    [InlineData("Office", "Driver Manager", false)]
    [InlineData("Office", "Non-driver", false)]
    [InlineData("Office", "Administrator", false)]
    [InlineData("Drivers", "HGV Driver", true)]
    [InlineData("Transport", "HGV Driver", true)]
    public void Sage_import_requires_a_driving_role(string team, string position, bool expected)
    {
        Assert.Equal(expected, DriverPopulationRules.IsSageDriver(new SageHrEmployee(1, "1", "Test", "Employee", team, position, null), "Drivers", "Driver"));
    }
}