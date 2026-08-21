using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class RoadTechHistoricalRecoveryTests
{
    [Fact]
    public async Task Historical_recovery_paginates_and_requests_all_vehicles()
    {
        var handler = new HistoricalPagingHandler();
        var client = new DotTrackingClient(new HttpClient(handler), new DotTrackingOptions
        {
            Enabled = true,
            BaseUrl = "https://api-v1-alpha.roadtech.co.uk",
            ApiKey = "test-key",
            Username = "planner",
            Password = "secret",
            CompanyCode = "SLH",
            OnlyLive = true,
            MaxPages = 5
        }, NullLogger<DotTrackingClient>.Instance);

        var rows = await client.GetHistoricalVehicleEventsAsync(new DateOnly(2026, 8, 21));

        Assert.Equal(3, rows.Count);
        Assert.Equal(3, handler.Requests.Count); // login + two history pages

        using var firstPage = JsonDocument.Parse(handler.Bodies[1]);
        using var secondPage = JsonDocument.Parse(handler.Bodies[2]);
        Assert.False(firstPage.RootElement.GetProperty("OnlyLive").GetBoolean());
        Assert.False(secondPage.RootElement.GetProperty("OnlyLive").GetBoolean());
        Assert.Equal(0, firstPage.RootElement.GetProperty("Offset").GetInt32());
        Assert.Equal(2, secondPage.RootElement.GetProperty("Offset").GetInt32());
        Assert.Equal("2026-08-21", firstPage.RootElement.GetProperty("T").GetString());
    }

    [Fact]
    public void Recovery_days_include_current_uk_operating_day_and_previous_day()
    {
        var days = DotTrackingIngestionService.RecoveryDays(new DateTimeOffset(2026, 8, 21, 23, 30, 0, TimeSpan.Zero));

        Assert.Equal(new DateOnly(2026, 8, 22), days[0]);
        Assert.Equal(new DateOnly(2026, 8, 21), days[1]);
    }

    private sealed class HistoricalPagingHandler : HttpMessageHandler
    {
        private int _historyPage;
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            Requests.Add(request);

            if (request.RequestUri!.AbsolutePath.EndsWith("/auth/login", StringComparison.OrdinalIgnoreCase))
            {
                return Json("{\"token\":\"sid-123\"}");
            }

            _historyPage++;
            return _historyPage == 1
                ? Json("{\"moreData\":true,\"recordOffset\":0,\"recordCount\":2,\"data\":[{\"vehCode\":\"AB12CDE\"},{\"vehCode\":\"EF34GHI\"}]}")
                : Json("{\"moreData\":false,\"recordOffset\":2,\"recordCount\":1,\"data\":[{\"vehCode\":\"JK56LMN\"}]}");
        }

        private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json")
        };
    }
}
