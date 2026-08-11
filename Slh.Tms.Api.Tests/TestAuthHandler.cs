using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

namespace Slh.Tms.Api.Tests;

public class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Test";
    public TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, ISystemClock clock) : base(options, logger, encoder, clock) { }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // If no test header present, do not authenticate - this allows testing 401 behavior
        if (!Request.Headers.TryGetValue("X-Test-User", out var user)) return Task.FromResult(AuthenticateResult.NoResult());

        var claims = new List<Claim> { new Claim(ClaimTypes.Name, user.ToString()) };
        if (Request.Headers.TryGetValue("X-Test-Scopes", out var scopes))
        {
            claims.Add(new Claim("scp", scopes.ToString()));
        }
        if (Request.Headers.TryGetValue("X-Test-Oid", out var oid)) claims.Add(new Claim("oid", oid.ToString()));

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
