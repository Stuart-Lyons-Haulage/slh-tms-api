using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Slh.Tms.Api.Authentication;

public sealed class LocalTestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "LocalTest";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Context.Connection.RemoteIpAddress?.IsLoopback() ?? true)
            return Task.FromResult(AuthenticateResult.Fail("Local test authentication is loopback-only."));

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "local-test-user"),
            new Claim(ClaimTypes.Name, "SLH Local Test User"),
            new Claim(ClaimTypes.Email, "local.test@lyonshaulage.com"),
            new Claim("preferred_username", "local.test@lyonshaulage.com"),
            new Claim(ClaimTypes.Role, "TMS.Admin"),
            new Claim("roles", "TMS.Admin"),
            new Claim(ClaimTypes.Role, "TMS.ReadMaster"),
            new Claim("roles", "TMS.ReadMaster")
        };
        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}

internal static class IPAddressExtensions
{
    public static bool IsLoopback(this System.Net.IPAddress address) =>
        System.Net.IPAddress.IsLoopback(address) ||
        address.Equals(System.Net.IPAddress.IPv6Loopback);
}
