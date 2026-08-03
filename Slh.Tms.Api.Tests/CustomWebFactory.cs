using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;

namespace Slh.Tms.Api.Tests;

public class CustomWebFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
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
