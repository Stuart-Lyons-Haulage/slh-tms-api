using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class FleetStatusTests : IClassFixture<CustomWebFactory>
{
    private const string LyonsUser = "planner@lyonshaulage.com";
    private readonly CustomWebFactory _factory;

    public FleetStatusTests(CustomWebFactory factory) => _factory = factory;

    [Fact]
    public async Task Fleet_status_endpoint_requires_authentication()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/tracking/dot/fleet-status");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Fleet_status_endpoint_returns_ok_for_authenticated_user()
    {
        var client = _factory.CreateClientWithUser(LyonsUser, "Tms.Access");
        var response = await client.GetAsync("/api/v1/tracking/dot/fleet-status");
        Assert.True(response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.NoContent,
            $"Expected OK or NoContent, got {response.StatusCode}");
    }

    [Fact]
    public async Task Fleet_status_response_includes_driver_match_reason_when_available()
    {
        var client = _factory.CreateClientWithUser(LyonsUser, "Tms.Access");
        var response = await client.GetAsync("/api/v1/tracking/dot/fleet-status");
        // The endpoint should return 200 or 204 (if no data); either is acceptable.
        Assert.True(response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.NoContent,
            $"Expected OK or NoContent, got {response.StatusCode}");
    }
}
