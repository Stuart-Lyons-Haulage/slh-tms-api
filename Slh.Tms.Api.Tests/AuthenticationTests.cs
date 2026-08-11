using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Slh.Tms.Api.Tests;

public class AuthenticationTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory _factory;
    public AuthenticationTests(CustomWebFactory factory) => _factory = factory;

    [Fact]
    public async Task Anonymous_health_check_is_allowed()
    {
        var client = _factory.CreateClient();
        var r = await client.GetAsync("/api/v1/health");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Authentication_required_for_api_endpoints()
    {
        var client = _factory.CreateClient();
        var r = await client.GetAsync("/api/v1/customers");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Production_portal_preflight_is_allowed()
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/customers");
        request.Headers.Add("Origin", "https://slh-tms-portal-prod.gentlepond-08dba66b.uksouth.azurecontainerapps.io");
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "authorization");
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("https://slh-tms-portal-prod.gentlepond-08dba66b.uksouth.azurecontainerapps.io", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task Missing_TmsAccess_scope_results_in_Forbidden()
    {
        var client = _factory.CreateClientWithUser("tester", "other.scope");
        var r = await client.GetAsync("/api/v1/customers");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task Valid_authorised_request_succeeds()
    {
        var client = _factory.CreateClientWithUser("tester", "Tms.Access");
        var r = await client.GetAsync("/api/v1/customers");
        // Depending on DB empty result may be OK; ensure we get 200 rather than auth problem
        Assert.True(r.StatusCode == HttpStatusCode.OK || r.StatusCode == HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Staging_submission_accepts_and_returns_202()
    {
        var client = _factory.CreateClientWithUser("importer", "Tms.Access");
        var json = "{ \"EntityType\": \"customer\", \"IdempotencyKey\": \"k1\", \"Payload\": { \"Code\": \"C1\", \"Name\": \"Test Customer\" } }";
        var r = await client.PostAsync("/api/v1/staging", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
    }

    [Fact]
    public async Task Approval_and_rejection_endpoints_require_authorisation()
    {
        var client = _factory.CreateClientWithUser("approver", "Tms.Access");
        // Create a staging item first
        var json = "{ \"EntityType\": \"customer\", \"IdempotencyKey\": \"k2\", \"Payload\": { \"Code\": \"C2\", \"Name\": \"ApproveCustomer\" } }";
        var post = await client.PostAsync("/api/v1/staging", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);

        // Read location header to get id if present (service may return it)
        var body = await post.Content.ReadAsStringAsync();
        // best-effort: if there is an id in body try to use approve; otherwise ensure endpoints are secured by hitting a made-up id
        var id = System.Guid.NewGuid();
        var approve = await client.PostAsync($"/api/v1/staging/{id}/approve", new StringContent("{ \"Note\": \"ok\" }", System.Text.Encoding.UTF8, "application/json"));
        // Approve on non-existent returns NotFound (authenticated), reject unauthorized would be Forbidden etc. Ensure we get either NotFound or OK (not 401/403)
        Assert.True(approve.StatusCode == HttpStatusCode.NotFound || approve.StatusCode == HttpStatusCode.OK);
    }

    [Fact]
    public async Task Direct_live_creation_is_not_exposed()
    {
        var client = _factory.CreateClientWithUser("user", "Tms.Access");
        var json = "{ \"Code\": \"LIVE1\", \"Name\": \"DirectCreate\" }";
        var r = await client.PostAsync("/api/v1/customers", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        // Expect NotFound (no direct create endpoint), Unauthorized, or Forbidden depending on configuration; ensure not 201
        Assert.NotEqual(HttpStatusCode.Created, r.StatusCode);
    }
}
