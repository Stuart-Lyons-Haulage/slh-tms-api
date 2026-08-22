using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class PalletPlanningControlTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory factory;
    public PalletPlanningControlTests(CustomWebFactory factory) => this.factory = factory;

    [Fact]
    public async Task Source_lines_preserve_provenance_and_partial_balance()
    {
        var seeded = await Seed();
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write");
        var allocate = await client.PostAsync("/api/v1/planning-control/allocations", Json(JsonSerializer.Serialize(new { orderId = seeded.OrderId, loadId = seeded.FirstLoadId, date = "2026-08-24", pallets = 7, note = "Split", sourceLineId = seeded.SourceLineId })));
        Assert.Equal(HttpStatusCode.OK, allocate.StatusCode);

        using var response = JsonDocument.Parse(await (await client.GetAsync("/api/v1/planning-control/pallets?date=2026-08-24")).Content.ReadAsStringAsync());
        var order = Assert.Single(response.RootElement.GetProperty("orders").EnumerateArray().Where(x => x.GetProperty("id").GetGuid() == seeded.OrderId));
        var line = Assert.Single(order.GetProperty("sourceLines").EnumerateArray());
        Assert.Equal(seeded.SourceLineId, line.GetProperty("sourceLineId").GetGuid());
        Assert.Equal("source-row-8", line.GetProperty("sourceRowKey").GetString());
        Assert.Equal(12, line.GetProperty("orderedPallets").GetInt32());
        Assert.Equal(7, line.GetProperty("plannedPallets").GetInt32());
        Assert.Equal(5, line.GetProperty("outstandingPallets").GetInt32());
        Assert.Equal("Standard", line.GetProperty("palletType").GetString());
    }

    [Fact]
    public async Task Allocation_above_source_line_quantity_is_rejected_without_mutation()
    {
        var seeded = await Seed();
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/v1/planning-control/allocations", Json(JsonSerializer.Serialize(new { orderId = seeded.OrderId, loadId = seeded.FirstLoadId, date = "2026-08-24", pallets = 7, sourceLineId = seeded.SourceLineId })))).StatusCode);
        var rejected = await client.PostAsync("/api/v1/planning-control/allocations", Json(JsonSerializer.Serialize(new { orderId = seeded.OrderId, loadId = seeded.SecondLoadId, date = "2026-08-24", pallets = 6, sourceLineId = seeded.SourceLineId })));
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        Assert.DoesNotContain(db.StagedImports, x => x.EntityType == "planningpalletallocation" && x.PayloadJson.Contains(seeded.SecondLoadId.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    private async Task<(Guid OrderId, Guid SourceLineId, Guid FirstLoadId, Guid SecondLoadId)> Seed()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var staged = new StagedImport { EntityType = "order", IdempotencyKey = $"pallet-{Guid.NewGuid():N}", Status = StagingStatus.Promoted, PayloadJson = """{"poNumber":"PO-PALLET","customerCode":"COOP","collectionDate":"2026-08-24","pallets":12}""" };
        var movement = new OrderMovement { CustomerCode = "COOP", StableMovementKey = $"COOP:PALLET:{Guid.NewGuid():N}", LifecycleStatus = OrderMovementStatus.PlannerReady };
        var revision = new OrderRevision { MovementId = movement.Id, StagedImportId = staged.Id, RevisionNumber = 1, PayloadJson = staged.PayloadJson };
        movement.CurrentRevisionId = revision.Id;
        var sourceLine = new OrderSourceLine { RevisionId = revision.Id, SourceRowKey = "source-row-8", CollectionSite = "Farm A", DeliverySite = "COOP Andover", CollectionDate = new DateOnly(2026, 8, 24), DeliveryDate = new DateOnly(2026, 8, 25), Pallets = 12, PalletType = "Standard", PayloadJson = "{}" };
        var order = new TransportOrder { SourceStagedImportId = staged.Id, SourceMovementId = movement.Id, Reference = ($"PO-{Guid.NewGuid():N}")[..20], CustomerCode = "COOP", CollectionDate = new DateOnly(2026, 8, 24), DeliveryDate = new DateOnly(2026, 8, 25), Pallets = 12 };
        var first = new Load { Reference = ($"RUN-{Guid.NewGuid():N}")[..20], PlanningDate = new DateOnly(2026, 8, 24) };
        var second = new Load { Reference = ($"RUN-{Guid.NewGuid():N}")[..20], PlanningDate = new DateOnly(2026, 8, 24) };
        db.AddRange(staged, movement, revision, sourceLine, order, first, second);
        await db.SaveChangesAsync();
        return (order.Id, sourceLine.Id, first.Id, second.Id);
    }

    private static StringContent Json(string value) => new(value, Encoding.UTF8, "application/json");
}
