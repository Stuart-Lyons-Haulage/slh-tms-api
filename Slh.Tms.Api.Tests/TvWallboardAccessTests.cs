using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class TvWallboardAccessTests
{
    private const string DisplayKey = "office-display-key-2026-08-20";

    [Fact]
    public void Configured_display_key_allows_wallboard_read()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[TvWallboardAccess.HeaderName] = DisplayKey;

        Assert.True(TvWallboardAccess.IsAllowed(context, Configuration(DisplayKey)));
    }

    [Fact]
    public void Missing_or_wrong_display_key_is_rejected()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[TvWallboardAccess.HeaderName] = "wrong-display-key-2026-08-20";

        Assert.False(TvWallboardAccess.IsAllowed(context, Configuration(DisplayKey)));
    }

    [Fact]
    public void Lyons_user_is_allowed_without_display_key()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("preferred_username", "planner@lyonshaulage.com")
            ], "Test"))
        };

        Assert.True(TvWallboardAccess.IsAllowed(context, Configuration(null)));
    }

    private static IConfiguration Configuration(string? displayKey) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(displayKey is null
                ? []
                : new Dictionary<string, string?> { ["TvWallboard:AccessKey"] = displayKey })
            .Build();
}
