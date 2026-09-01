using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class EndToEndWorkflowTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory factory;

    public EndToEndWorkflowTests(CustomWebFactory factory) => this.factory = factory;

    [Fact]
    public async Task Order_lifecycle_retains_quantities_from_raw_email_through_run_allocation()
    {
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write,Tms.Approve");
        var suffix = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var messageId = $"e2e-order-{suffix}";
        var po = $"E2E{suffix}";
        var planningDate = new DateOnly(2026, 9, 8);

        var intake = await PostJson(client, "/api/v1/order-intake/email", MailboxOrder(
            messageId,
            po,
            4,
            planningDate,
            planningDate.AddDays(1)));
        Assert.Equal(HttpStatusCode.Accepted, intake.StatusCode);

        Guid stagingId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            var staged = Assert.Single(await db.StagedImports
                .Where(x => x.EntityType == "order" && x.PayloadJson.Contains(messageId))
                .ToListAsync());
            stagingId = staged.Id;
            Assert.Equal(StagingStatus.PendingReview, staged.Status);
            using var payload = JsonDocument.Parse(staged.PayloadJson);
            Assert.Equal(4, payload.RootElement.GetProperty("pallets").GetInt32());
        }

        var approve = await PostJson(client, $"/api/v1/staging/{stagingId}/approve", new { note = "E2E lifecycle approval" });
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

        TransportOrder order;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            order = Assert.Single(await db.TransportOrders.Where(x => x.SourceStagedImportId == stagingId).ToListAsync());
            Assert.Equal(4, order.Pallets);
            Assert.Equal(planningDate, order.CollectionDate);
        }

        var createRun = await PostJson(client, "/api/v1/loads", new
        {
            reference = $"E2E-RUN-{suffix}",
            planningDate,
            vehicleId = (Guid?)null,
            driverId = (Guid?)null,
            trailerId = (Guid?)null,
            stops = new[]
            {
                new
                {
                    orderId = (Guid?)null,
                    name = "Collect · E2E origin",
                    address = "Test origin",
                    latitude = (decimal?)null,
                    longitude = (decimal?)null,
                    plannedArrivalUtc = new DateTimeOffset(2026, 9, 8, 8, 0, 0, TimeSpan.Zero)
                }
            },
            palletSpacesUsed = (decimal?)0,
            totalPalletSpaces = (decimal?)26,
            capacityType = "Standard pallets"
        });
        Assert.Equal(HttpStatusCode.Created, createRun.StatusCode);
        var runId = (await Json(createRun)).GetProperty("id").GetGuid();

        var allocate = await PostJson(client, "/api/v1/planning-control/allocations", new
        {
            orderId = order.Id,
            loadId = runId,
            date = planningDate,
            pallets = 4,
            note = "E2E quantity allocation"
        });
        Assert.Equal(HttpStatusCode.OK, allocate.StatusCode);
        var allocation = await Json(allocate);
        Assert.Equal(4, allocation.GetProperty("allocatedToRun").GetInt32());
        Assert.Equal(4, allocation.GetProperty("plannedPallets").GetInt32());
        Assert.Equal(4, allocation.GetProperty("orderedPallets").GetInt32());
        Assert.Equal(0, allocation.GetProperty("outstandingPallets").GetInt32());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            var run = await db.Loads.Include(x => x.Stops).SingleAsync(x => x.Id == runId);
            Assert.Equal(4m, run.PalletSpacesUsed);
            Assert.Contains(run.Stops, x => x.OrderId == order.Id);
            var persistedOrder = await db.TransportOrders.SingleAsync(x => x.Id == order.Id);
            Assert.Equal(4, persistedOrder.Pallets);
        }
    }

    [Fact]
    public async Task Overnight_run_started_sunday_2330_belongs_to_monday_wallboard_and_completion_day()
    {
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write");
        client.DefaultRequestHeaders.Add("X-TV-Display-Key", "test-tv-wallboard-key-20260824");
        var monday = new DateOnly(2026, 9, 7);
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var driverId = Guid.NewGuid();
        var vehicleId = Guid.NewGuid();
        var loadId = Guid.NewGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.Drivers.Add(new Driver { Id = driverId, EmployeeNumber = $"E2E-{suffix}", DisplayName = $"E2E Driver {suffix}", Active = true });
            db.Vehicles.Add(new Vehicle { Id = vehicleId, Registration = $"E2E{suffix[..4]}", Active = true });
            db.Loads.Add(new Load
            {
                Id = loadId,
                Reference = $"OVERNIGHT-{suffix}",
                PlanningDate = monday,
                DriverId = driverId,
                VehicleId = vehicleId,
                Status = LoadStatus.InProgress,
                Stops =
                [
                    new LoadStop
                    {
                        LoadId = loadId,
                        Sequence = 1,
                        Name = "Collect · Sunday night",
                        // 22:30 UTC = 23:30 Europe/London on Sunday 6 September 2026.
                        PlannedArrivalUtc = new DateTimeOffset(2026, 9, 6, 22, 30, 0, TimeSpan.Zero)
                    },
                    new LoadStop
                    {
                        LoadId = loadId,
                        Sequence = 2,
                        Name = "Deliver · Monday morning",
                        PlannedArrivalUtc = new DateTimeOffset(2026, 9, 7, 1, 30, 0, TimeSpan.Zero)
                    }
                ]
            });
            await db.SaveChangesAsync();
        }

        var wallboard = await client.GetAsync($"/api/v1/tv-display/live-runs?date={monday:yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.OK, wallboard.StatusCode);
        var before = await Json(wallboard);
        Assert.Equal("2026-09-07", before.GetProperty("planningDate").GetString());
        Assert.Contains(before.GetProperty("runs").EnumerateArray(), row => row.GetProperty("id").GetGuid() == loadId);

        var complete = await PutJson(client, $"/api/v1/loads/{loadId}/status", new { status = "Completed" });
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            var persisted = await db.Loads.SingleAsync(x => x.Id == loadId);
            Assert.Equal(monday, persisted.PlanningDate);
            Assert.Equal(LoadStatus.Completed, persisted.Status);
        }

        var afterResponse = await client.GetAsync($"/api/v1/tv-display/live-runs?date={monday:yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.OK, afterResponse.StatusCode);
        var after = await Json(afterResponse);
        Assert.DoesNotContain(after.GetProperty("runs").EnumerateArray(), row => row.GetProperty("id").GetGuid() == loadId);
    }

    [Fact]
    public async Task Revised_order_creates_revision_not_duplicate_and_flags_existing_allocation_mismatch()
    {
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write,Tms.Approve");
        var suffix = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var po = $"REV{suffix}";
        var planningDate = new DateOnly(2026, 9, 9);

        var originalMessage = $"e2e-revision-original-{suffix}";
        var originalResponse = await PostJson(client, "/api/v1/order-intake/email", MailboxOrder(
            originalMessage, po, 4, planningDate, planningDate.AddDays(1)));
        Assert.Equal(HttpStatusCode.Accepted, originalResponse.StatusCode);
        var originalStagingId = await FindStagingId(originalMessage);
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, $"/api/v1/staging/{originalStagingId}/approve", new { note = "Original approved" })).StatusCode);

        TransportOrder order;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            order = Assert.Single(await db.TransportOrders.Where(x => x.SourceStagedImportId == originalStagingId).ToListAsync());
        }

        var createRun = await PostJson(client, "/api/v1/loads", new
        {
            reference = $"REV-RUN-{suffix}",
            planningDate,
            stops = new[]
            {
                new { orderId = (Guid?)null, name = "Collect · revision test", address = (string?)null, latitude = (decimal?)null, longitude = (decimal?)null, plannedArrivalUtc = (DateTimeOffset?)null }
            },
            palletSpacesUsed = (decimal?)0,
            totalPalletSpaces = (decimal?)26,
            capacityType = "Standard pallets"
        });
        Assert.Equal(HttpStatusCode.Created, createRun.StatusCode);
        var loadId = (await Json(createRun)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/v1/planning-control/allocations", new
        {
            orderId = order.Id,
            loadId,
            date = planningDate,
            pallets = 4,
            note = "Original allocation"
        })).StatusCode);

        var revisedMessage = $"e2e-revision-amended-{suffix}";
        var revisedResponse = await PostJson(client, "/api/v1/order-intake/email", MailboxOrder(
            revisedMessage, po, 7, planningDate, planningDate.AddDays(1)));
        Assert.Equal(HttpStatusCode.Accepted, revisedResponse.StatusCode);
        var revisedBody = await Json(revisedResponse);
        Assert.Equal(1, revisedBody.GetProperty("staged").GetInt32());
        Assert.Equal(0, revisedBody.GetProperty("existing").GetInt32());

        var revisedStagingId = await FindStagingId(revisedMessage);
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, $"/api/v1/staging/{revisedStagingId}/approve", new { note = "Revision approved" })).StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            var movement = Assert.Single(await db.OrderMovements.Where(x => x.Id == order.SourceMovementId).ToListAsync());
            var revisions = await db.OrderRevisions.Where(x => x.MovementId == movement.Id).OrderBy(x => x.RevisionNumber).ToListAsync();
            Assert.Equal(2, revisions.Count);
            Assert.Equal(revisions[0].Id, revisions[1].SupersedesRevisionId);
            Assert.Equal(revisions[1].Id, movement.CurrentRevisionId);
            var updatedOrder = await db.TransportOrders.SingleAsync(x => x.Id == order.Id);
            Assert.Equal(7, updatedOrder.Pallets);
            Assert.Equal(revisedStagingId, updatedOrder.SourceStagedImportId);
        }

        var planning = await client.GetAsync($"/api/v1/planning-control/pallets?date={planningDate:yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.OK, planning.StatusCode);
        var planningJson = await Json(planning);
        var orderRow = planningJson.GetProperty("orders").EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == order.Id);
        Assert.Equal(7, orderRow.GetProperty("orderedPallets").GetInt32());
        Assert.Equal(4, orderRow.GetProperty("plannedPallets").GetInt32());
        Assert.Equal(3, orderRow.GetProperty("outstandingPallets").GetInt32());
        Assert.Contains(orderRow.GetProperty("allocations").EnumerateArray(), allocation => allocation.GetProperty("loadId").GetGuid() == loadId);
    }

    [Fact]
    public async Task RoadTech_503_keeps_planner_runs_and_returns_explicit_unavailable_tracking_state()
    {
        await using var unavailableFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(new DotTrackingOptions
                {
                    Enabled = true,
                    BaseUrl = "https://roadtech.test",
                    ApiKey = "test",
                    CompanyCode = "SLH"
                });
                services.AddHttpClient<DotTrackingClient>()
                    .ConfigurePrimaryHttpMessageHandler(() => new FixedStatusHandler(HttpStatusCode.ServiceUnavailable));
            });
        });

        var client = unavailableFactory.CreateClient();
        client.DefaultRequestHeaders.Add("X-TV-Display-Key", "test-tv-wallboard-key-20260824");
        var date = new DateOnly(2026, 9, 10);
        var loadId = Guid.NewGuid();

        using (var scope = unavailableFactory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.Loads.Add(new Load
            {
                Id = loadId,
                Reference = $"ROADTECH-DOWN-{Guid.NewGuid():N}"[..28],
                PlanningDate = date,
                Status = LoadStatus.Planned,
                Stops =
                [
                    new LoadStop { LoadId = loadId, Sequence = 1, Name = "Collect · resilient planner" },
                    new LoadStop { LoadId = loadId, Sequence = 2, Name = "Deliver · resilient planner" }
                ]
            });
            await db.SaveChangesAsync();
        }

        var planner = await client.GetAsync($"/api/v1/loads?date={date:yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.OK, planner.StatusCode);
        var plannerRows = await Json(planner);
        Assert.Contains(plannerRows.EnumerateArray(), row => row.GetProperty("id").GetGuid() == loadId);

        var progress = await client.GetAsync($"/api/v1/run-progress?date={date:yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.OK, progress.StatusCode);
        var progressJson = await Json(progress);
        Assert.Equal("Unavailable", progressJson.GetProperty("trackingState").GetString());
        Assert.True(progressJson.GetProperty("count").GetInt32() > 0);
        Assert.Contains(progressJson.GetProperty("records").EnumerateArray(), row => row.GetProperty("loadId").GetGuid() == loadId);
        Assert.Contains("unavailable", progressJson.GetProperty("warning").GetString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<Guid> FindStagingId(string messageId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        return (await db.StagedImports.SingleAsync(x => x.EntityType == "order" && x.PayloadJson.Contains(messageId))).Id;
    }

    private static object MailboxOrder(string messageId, string po, int pallets, DateOnly collectionDate, DateOnly deliveryDate) => new
    {
        messageId,
        internetMessageId = $"<{messageId}@example.test>",
        mailbox = "info@lyonshaulage.com",
        senderAddress = "chris.benning@primafruit.co.uk",
        senderName = "Chris Benning",
        subject = $"HHP WAITROSE DIRECT DEPOT DELIVERY {deliveryDate:dd/MM/yy}",
        receivedAtUtc = new DateTimeOffset(collectionDate.Year, collectionDate.Month, collectionDate.Day, 8, 0, 0, TimeSpan.Zero),
        bodyText = $"Please collect {pallets} pallets from Hall Hunter today {collectionDate:dd/MM/yyyy}.\n* Leyland {pallets} pallets\nFor Delivery date {deliveryDate:dd/MM/yyyy}.\nPO number: {po}. 174 cases of Berries.",
        webLink = "https://outlook.office.com/mail/e2e"
    };

    private static async Task<HttpResponseMessage> PostJson(HttpClient client, string url, object payload) =>
        await client.PostAsync(url, new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

    private static async Task<HttpResponseMessage> PutJson(HttpClient client, string url, object payload) =>
        await client.PutAsync(url, new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private sealed class FixedStatusHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode)
            {
                RequestMessage = request,
                Content = new StringContent("RoadTech unavailable")
            });
    }
}
