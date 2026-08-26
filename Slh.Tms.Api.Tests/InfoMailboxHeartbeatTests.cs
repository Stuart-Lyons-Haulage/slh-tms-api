using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class InfoMailboxHeartbeatTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory factory;

    public InfoMailboxHeartbeatTests(CustomWebFactory factory) => this.factory = factory;

    [Fact]
    public async Task Heartbeat_records_latest_shared_mailbox_probe_and_system_state_uses_it()
    {
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write");
        var latestInbox = DateTimeOffset.UtcNow.AddMinutes(-2).ToString("O");

        var first = JsonSerializer.Serialize(new
        {
            mailbox = "info@lyonshaulage.com",
            flowName = "SLH-TMS | Info Mailbox | Heartbeat | PROD",
            flowRunId = $"run-first-{Guid.NewGuid():N}",
            checkedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-20),
            latestInboxReceivedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-3)
        });
        var secondRunId = $"run-second-{Guid.NewGuid():N}";
        var second = JsonSerializer.Serialize(new
        {
            mailbox = "info@lyonshaulage.com",
            flowName = "SLH-TMS | Info Mailbox | Heartbeat | PROD",
            flowRunId = secondRunId,
            checkedAtUtc = DateTimeOffset.UtcNow,
            latestInboxReceivedAtUtc = latestInbox
        });

        var firstResponse = await client.PostAsync(
            "/api/v1/order-intake/email/heartbeat",
            new StringContent(first, Encoding.UTF8, "application/json"));
        var secondResponse = await client.PostAsync(
            "/api/v1/order-intake/email/heartbeat",
            new StringContent(second, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Accepted, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, secondResponse.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            var heartbeats = db.StagedImports.Where(item => item.EntityType == "infomailboxheartbeat").ToList();
            var heartbeat = Assert.Single(heartbeats);
            Assert.Equal(StagingStatus.Promoted, heartbeat.Status);
            Assert.Equal("Info mailbox scheduled heartbeat", heartbeat.Source);

            using var payload = JsonDocument.Parse(heartbeat.PayloadJson);
            Assert.Equal("info@lyonshaulage.com", payload.RootElement.GetProperty("mailbox").GetString());
            Assert.Equal(secondRunId, payload.RootElement.GetProperty("flowRunId").GetString());
            Assert.Equal(latestInbox, payload.RootElement.GetProperty("latestInboxReceivedAtUtc").GetDateTimeOffset().ToString("O"));
        }

        var stateResponse = await client.GetAsync("/api/v1/system-sync/state");
        Assert.Equal(HttpStatusCode.OK, stateResponse.StatusCode);
        using var state = JsonDocument.Parse(await stateResponse.Content.ReadAsStringAsync());
        var root = state.RootElement;
        Assert.Equal("every 5 minutes heartbeat", root.GetProperty("schedules").GetProperty("infoMailbox").GetString());
        var mailboxProvider = root.GetProperty("providers").EnumerateArray().Single(item => item.GetProperty("name").GetString() == "Info mailbox");
        Assert.Equal("current", mailboxProvider.GetProperty("state").GetString());
        var mailbox = root.GetProperty("mailbox");
        Assert.Equal("info@lyonshaulage.com", mailbox.GetProperty("mailbox").GetString());
        Assert.Equal(latestInbox, mailbox.GetProperty("latestInboxReceivedAtUtc").GetDateTimeOffset().ToString("O"));
        Assert.True(mailbox.GetProperty("lastHeartbeatUtc").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(-2));
    }
}
