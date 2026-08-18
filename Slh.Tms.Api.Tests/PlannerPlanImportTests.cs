using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Contracts;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class PlannerPlanImportTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory _factory;

    public PlannerPlanImportTests(CustomWebFactory factory)
    {
        _factory = factory;
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        db.Database.EnsureDeleted();
        db.Database.EnsureCreated();
    }

    [Fact]
    public void Mixed_capacity_uses_26_standard_and_33_euro()
    {
        var run = Run(new DateOnly(2026, 8, 18), "COL-01", true, [
            Stop(1, 13, "Std"),
            Stop(2, 16, "Euro")
        ]);
        var result = PlannerPlanImportRules.Capacity(run);
        Assert.Equal("Green", result.Status);
        Assert.Equal(98.5m, result.UtilisationPercent);
    }

    [Fact]
    public async Task Import_is_idempotent_and_held_runs_are_not_created()
    {
        var date = new DateOnly(2026, 8, 18);
        await SeedMasterData();
        var client = _factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Access");
        var request = new PlannerPlanImportRequest("slh-planner-plan-v2", date, [
            Run(date, "COL-01", true, [Stop(1, 13, "Std"), Stop(2, 16, "Euro")], driver: "Test Driver", vehicle: "ABC", trailer: "22"),
            Run(date, "S3", false, [Stop(1, 20, "Euro")], reconciliation: "HOLD - MAILBOX_CANCELLATION")
        ]);

        var first = await client.PostAsJsonAsync("/api/v1/planning/import-plan", request);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstSummary = await first.Content.ReadFromJsonAsync<PlannerPlanImportSummary>();
        Assert.NotNull(firstSummary);
        Assert.Equal(1, firstSummary!.Created);
        Assert.Equal(1, firstSummary.Held);

        var second = await client.PostAsJsonAsync("/api/v1/planning/import-plan", request);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondSummary = await second.Content.ReadFromJsonAsync<PlannerPlanImportSummary>();
        Assert.NotNull(secondSummary);
        Assert.Equal(1, secondSummary!.Unchanged);
        Assert.Equal(1, secondSummary.Held);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        Assert.Single(db.Loads);
        var load = db.Loads.Single();
        Assert.Equal("PLAN-20260818-COL-01", load.Reference);
        Assert.Equal(LoadStatus.Planned, load.Status);
        Assert.Equal(2, db.LoadStops.Count());
        Assert.DoesNotContain(db.Loads, x => x.Reference.Contains("S3"));
    }

    [Fact]
    public async Task Unresolved_allocations_leave_run_draft_without_failing_import()
    {
        var date = new DateOnly(2026, 8, 19);
        var client = _factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Access");
        var request = new PlannerPlanImportRequest("slh-planner-plan-v2", date, [
            Run(date, "S10", true, [Stop(1, 10, "Euro")], driver: "Missing Driver", vehicle: "ZZZ", trailer: "999")
        ]);

        var response = await client.PostAsJsonAsync("/api/v1/planning/import-plan", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = await response.Content.ReadFromJsonAsync<PlannerPlanImportSummary>();
        Assert.NotNull(summary);
        Assert.Contains("Missing Driver", summary!.UnresolvedDrivers);
        Assert.Contains("ZZZ", summary.UnresolvedVehicles);
        Assert.Contains("999", summary.UnresolvedTrailers);
        Assert.Equal("ImportedDraft", summary.Runs.Single().Outcome);
    }

    [Fact]
    public async Task Red_capacity_warns_but_does_not_block_import()
    {
        var date = new DateOnly(2026, 8, 20);
        var client = _factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Access");
        var request = new PlannerPlanImportRequest("slh-planner-plan-v2", date, [
            Run(date, "COL-23", true, [Stop(1, 38, "Std")])
        ]);

        var response = await client.PostAsJsonAsync("/api/v1/planning/import-plan", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = await response.Content.ReadFromJsonAsync<PlannerPlanImportSummary>();
        Assert.NotNull(summary);
        Assert.Equal("Red", summary!.Runs.Single().CapacityStatus);
        Assert.Equal(146.2m, summary.Runs.Single().UtilisationPercent);
        Assert.Contains(summary.Warnings, warning => warning.Contains("COL-23"));
    }

    private async Task SeedMasterData()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        db.Drivers.Add(new Driver { EmployeeNumber = "T001", DisplayName = "Test Driver", Active = true });
        db.Vehicles.Add(new Vehicle { Registration = "AB12ABC", Abbreviation = "ABC", Active = true });
        db.Trailers.Add(new Trailer { TrailerNumber = "22", StandardCapacity = 26, EuroCapacity = 33, Active = true });
        await db.SaveChangesAsync();
    }

    private static PlannerPlanRunRequest Run(DateOnly date, string runRef, bool include, List<PlannerPlanStopRequest> stops,
        string? driver = null, string? vehicle = null, string? trailer = null, string? reconciliation = "Matched planner plan") =>
        new(runRef, runRef, "Collection", date, driver, vehicle, trailer, null, include, reconciliation,
            new PlannerPlanSourceRequest("planner.xlsm", "Plan"), stops);

    private static PlannerPlanStopRequest Stop(int sequence, decimal pallets, string palletType) =>
        new(sequence, "Collection", "Delivery", pallets, $"REF-{sequence}", palletType, null, null, null, sequence + 1);
}
