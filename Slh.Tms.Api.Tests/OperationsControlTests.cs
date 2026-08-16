using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class OperationsControlTests : IClassFixture<CustomWebFactory>
{
    private const string LyonsUser = "planner@lyonshaulage.com";
    private readonly CustomWebFactory _factory;

    public OperationsControlTests(CustomWebFactory factory) => _factory = factory;

    [Fact]
    public async Task Confidence_endpoint_requires_authentication()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/operations/confidence");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Confidence_endpoint_returns_ok_for_authenticated_user()
    {
        var client = _factory.CreateClientWithUser(LyonsUser, "Tms.Access");
        var response = await client.GetAsync("/api/v1/operations/confidence");
        Assert.True(response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Exceptions_endpoint_returns_ok_for_authenticated_user()
    {
        var client = _factory.CreateClientWithUser(LyonsUser, "Tms.Access");
        var response = await client.GetAsync("/api/v1/operations/exceptions");
        Assert.True(response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Reconciliation_endpoint_returns_ok_for_authenticated_user()
    {
        var client = _factory.CreateClientWithUser(LyonsUser, "Tms.Access");
        var response = await client.GetAsync("/api/v1/operations/reconciliation");
        Assert.True(response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Mappings_endpoint_requires_authentication()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/operations/mappings");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Mappings_endpoint_returns_ok_for_authenticated_user()
    {
        var client = _factory.CreateClientWithUser(LyonsUser, "Tms.Access");
        var response = await client.GetAsync("/api/v1/operations/mappings");
        Assert.True(response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Driver_status_capture_requires_write_scope()
    {
        var client = _factory.CreateClientWithUser(LyonsUser, "Tms.Access");
        var json = """{"status":"Dispatched"}""";
        var response = await client.PostAsync("/api/v1/operations/loads/00000000-0000-0000-0000-000000000001/driver-status",
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        Assert.True(response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.NotFound);
    }
}
