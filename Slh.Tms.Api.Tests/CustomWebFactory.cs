using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;

namespace Slh.Tms.Api.Tests;

public class CustomWebFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = $"slh-tms-tests-{Guid.NewGuid():N}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(configuration =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TvWallboard:AccessKey"] = "test-tv-wallboard-key-20260824"
            });
        });
        builder.ConfigureTestServices(services =>
        {
            var dbRegistrations = services.Where(descriptor => descriptor.ServiceType == typeof(DbContextOptions<TmsDbContext>) || descriptor.ServiceType == typeof(TmsDbContext)).ToList();
            foreach (var registration in dbRegistrations) services.Remove(registration);
            services.AddDbContext<TmsDbContext>(options => options.UseInMemoryDatabase(_databaseName));
            // Replace authentication with test scheme
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
            }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        });
    }

    public HttpClient CreateClientWithUser(string userName, string scopes = "", string roles = "TMS.SystemAdmin")
    {
        var c = CreateClient();
        c.DefaultRequestHeaders.Add("X-Test-User", userName);
        if (!string.IsNullOrEmpty(scopes)) c.DefaultRequestHeaders.Add("X-Test-Scopes", scopes);
        if (!string.IsNullOrWhiteSpace(roles)) c.DefaultRequestHeaders.Add("X-Test-Roles", roles);
        return c;
    }
}
