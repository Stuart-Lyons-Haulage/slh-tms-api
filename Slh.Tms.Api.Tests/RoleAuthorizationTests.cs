using System.Net;
using System.Text;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class RoleAuthorizationTests : IClassFixture<CustomWebFactory>
{
    private const string CompanyUser = "planner@lyonshaulage.com";
    private readonly CustomWebFactory _factory;

    public RoleAuthorizationTests(CustomWebFactory factory) => _factory = factory;

    private HttpClient Client(string? roles)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", CompanyUser);
        if (!string.IsNullOrWhiteSpace(roles)) client.DefaultRequestHeaders.Add("X-Test-Roles", roles);
        return client;
    }

    [Fact]
    public async Task Viewer_can_read_but_cannot_write()
    {
        var client = Client("TMS.Viewer");
        var read = await client.GetAsync("/api/v1/customers");
        Assert.NotEqual(HttpStatusCode.Forbidden, read.StatusCode);

        var body = new StringContent(
            "{\"EntityType\":\"customer\",\"IdempotencyKey\":\"rbac-viewer\",\"Payload\":{\"Code\":\"RBAC\",\"Name\":\"RBAC\"}}",
            Encoding.UTF8,
            "application/json");
        var write = await client.PostAsync("/api/v1/staging", body);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }

    [Fact]
    public async Task Planner_can_write_but_cannot_dispatch()
    {
        var client = Client("TMS.Planner");
        var body = new StringContent(
            "{\"EntityType\":\"customer\",\"IdempotencyKey\":\"rbac-planner\",\"Payload\":{\"Code\":\"RBACP\",\"Name\":\"RBAC Planner\"}}",
            Encoding.UTF8,
            "application/json");
        var write = await client.PostAsync("/api/v1/staging", body);
        Assert.NotEqual(HttpStatusCode.Forbidden, write.StatusCode);

        var dispatch = await client.PostAsync($"/api/v1/loads/{Guid.NewGuid()}/dispatch/sms", null);
        Assert.Equal(HttpStatusCode.Forbidden, dispatch.StatusCode);
    }

    [Fact]
    public async Task Dispatcher_can_dispatch_but_cannot_approve()
    {
        var client = Client("TMS.Dispatcher");
        var dispatch = await client.PostAsync($"/api/v1/loads/{Guid.NewGuid()}/dispatch/sms", null);
        Assert.NotEqual(HttpStatusCode.Forbidden, dispatch.StatusCode);

        var approve = await client.PostAsync(
            $"/api/v1/staging/{Guid.NewGuid()}/approve",
            new StringContent("{\"note\":\"test\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, approve.StatusCode);
    }

    [Fact]
    public async Task Approver_cannot_edit_master_data()
    {
        var client = Client("TMS.Approver");
        var payload = new StringContent(
            "{\"displayName\":\"Test Driver\",\"employeeNumber\":\"RBAC1\"}",
            Encoding.UTF8,
            "application/json");
        var response = await client.PutAsync($"/api/v1/operational-master-data/drivers/{Guid.NewGuid()}", payload);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task MasterDataAdmin_can_reach_master_data_but_not_admin()
    {
        var client = Client("TMS.MasterDataAdmin");
        var payload = new StringContent(
            "{\"displayName\":\"Test Driver\",\"employeeNumber\":\"RBAC2\"}",
            Encoding.UTF8,
            "application/json");
        var masterData = await client.PutAsync($"/api/v1/operational-master-data/drivers/{Guid.NewGuid()}", payload);
        Assert.NotEqual(HttpStatusCode.Forbidden, masterData.StatusCode);

        var admin = await client.PostAsync("/api/v1/system-sync/force/not-a-provider", null);
        Assert.Equal(HttpStatusCode.Forbidden, admin.StatusCode);
    }

    [Fact]
    public async Task SystemAdmin_can_reach_admin_policy()
    {
        var client = Client("TMS.SystemAdmin");
        var response = await client.PostAsync("/api/v1/system-sync/force/not-a-provider", null);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Company_user_without_an_app_role_is_forbidden()
    {
        var client = Client(null);
        var response = await client.GetAsync("/api/v1/customers");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
