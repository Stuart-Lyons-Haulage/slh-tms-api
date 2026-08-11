using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;

namespace Slh.Tms.Api.Tests;

public class CustomWebFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            var dbRegistrations = services.Where(descriptor => descriptor.ServiceType == typeof(DbContextOptions<TmsDbContext>) || descriptor.ServiceType == typeof(TmsDbContext)).ToList();
            foreach (var registration in dbRegistrations) services.Remove(registration);
            services.AddDbContext<TmsDbContext>(options => options.UseInMemoryDatabase($"slh-tms-tests-{Guid.NewGuid()}"));
            // Replace authentication with test scheme
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
            }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        });
    }

    public HttpClient CreateClientWithUser(string userName, string scopes = "")
    {
        var c = CreateClient();
        c.DefaultRequestHeaders.Add("X-Test-User", userName);
        if (!string.IsNullOrEmpty(scopes)) c.DefaultRequestHeaders.Add("X-Test-Scopes", scopes);
        return c;
    }
}
