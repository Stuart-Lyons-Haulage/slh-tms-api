using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class DotTrackingClientTests
{
    [Theory]
    [InlineData("https://api-v1-alpha.roadtech.co.uk", "https://api-v1-alpha.roadtech.co.uk/api/")]
    [InlineData("https://api-v1-alpha.roadtech.co.uk/api", "https://api-v1-alpha.roadtech.co.uk/api/")]
    [InlineData("https://api-v1-alpha.roadtech.co.uk/api/", "https://api-v1-alpha.roadtech.co.uk/api/")]
    public void RoadTech_base_url_is_normalised_to_api_root(string input, string expected)
    {
        Assert.Equal(expected, DotTrackingClient.NormaliseBaseUrl(input));
    }

    [Fact]
    public async Task RoadTech_client_calls_documented_falcon_paths()
    {
        var handler = new CapturingHandler();
        var client = new DotTrackingClient(new HttpClient(handler), new DotTrackingOptions
        {
            Enabled = true,
            BaseUrl = "https://api-v1-alpha.roadtech.co.uk",
            ApiKey = "test-key",
            Username = "planner",
            Password = "secret",
            CompanyCode = "SLH"
        }, NullLogger<DotTrackingClient>.Instance);

        await client.GetLatestVehicleEventsAsync();

        Assert.Equal(new[]
        {
            "https://api-v1-alpha.roadtech.co.uk/api/auth/login",
            "https://api-v1-alpha.roadtech.co.uk/api/Falcon/GetCurrentTelemetry"
        }, handler.Requests.Select(request => request.RequestUri!.ToString()));

        var telemetryJson = handler.Bodies[1];
        using var payload = JsonDocument.Parse(telemetryJson);
        Assert.Equal(0, payload.RootElement.GetProperty("DataMask").GetInt32());
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            Requests.Add(request);
            var content = request.RequestUri!.AbsolutePath.EndsWith("/auth/login", StringComparison.OrdinalIgnoreCase)
                ? "{\"token\":\"sid-123\"}"
                : "{\"moreData\":false,\"recordOffset\":0,\"recordCount\":0,\"data\":[]}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            };
        }
    }
}
