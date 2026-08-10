using System.Linq;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Models.Integrations;
using Slh.Tms.Api.Services;

var builder = WebApplication.CreateBuilder(args);
var tenantId = builder.Configuration["Entra:TenantId"] ?? throw new InvalidOperationException("Entra:TenantId is required");
var audience = builder.Configuration["Entra:Audience"] ?? throw new InvalidOperationException("Entra:Audience is required");

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddDbContext<TmsDbContext>(o => o.UseSqlServer(builder.Configuration.GetConnectionString("TmsDb")));
builder.Services.AddScoped<StagingService>();

// Bind DOT tracking configuration from Tracking:Dot section
// Sensitive values (BaseUrl, Username, Password) are loaded from environment variables or Azure Key Vault at runtime
var dotTrackingOptions = new DotTrackingOptions();
builder.Configuration.GetSection("Tracking:Dot").Bind(dotTrackingOptions);
builder.Services.AddSingleton(dotTrackingOptions);
var sageHrOptions = new SageHrOptions();
builder.Configuration.GetSection("Integrations:SageHr").Bind(sageHrOptions);
builder.Services.AddSingleton(sageHrOptions);
builder.Services.AddHttpClient<SageHrClient>();
builder.Services.AddHttpClient<DotTrackingClient>();

var portalOrigins = builder.Configuration.GetSection("Cors:PortalOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddPolicy("Portal", policy =>
{
    if (portalOrigins.Length > 0)
        policy.WithOrigins(portalOrigins).AllowAnyHeader().AllowAnyMethod();
}));

// Database readiness is checked separately from the public liveness check.
builder.Services.AddHealthChecks().AddDbContextCheck<TmsDbContext>();

// JWT Bearer authentication - validate tenant and audience
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.Authority = $"https://login.microsoftonline.com/{tenantId}/v2.0";
    // audience must match the configured API audience (api://<client-id>)
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        // Power Automate may request delegated Entra tokens using either the
        // v2 issuer or the tenant's established v1 issuer. Both are restricted
        // to this configured tenant; audience, lifetime and Tms.Access scope
        // remain mandatory below.
        ValidIssuers = new[]
        {
            $"https://login.microsoftonline.com/{tenantId}/v2.0",
            $"https://sts.windows.net/{tenantId}/"
        },
        ValidateAudience = true,
        ValidAudience = audience,
        ValidateLifetime = true
    };

    // Ensure the middleware returns 401 for invalid/malformed tokens
    o.Events = new JwtBearerEvents
    {
        OnAuthenticationFailed = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        },
        OnChallenge = ctx =>
        {
            // preserve default behavior but ensure 401
            if (!ctx.Handled)
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            }
            return Task.CompletedTask;
        }
    };
});

// Authorization: require authenticated users by default for all endpoints except explicitly allowed ones (health)
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();

    // Require the delegated scope Tms.Access - scp claim contains space-delimited scopes for Delegated tokens
    options.AddPolicy("TmsAccess", policy => policy.RequireAssertion(context =>
    {
        if (!context.User.Identity?.IsAuthenticated ?? true) return false;
        var scp = context.User.FindFirst(c => c.Type == "scp")?.Value;
        if (string.IsNullOrEmpty(scp)) return false;
        return scp.Split(' ').Contains("Tms.Access");
    }));

    // Keep named policies used by controllers but map them to require the Tms.Access scope
    options.AddPolicy("TmsWrite", p => p.RequireAssertion(context =>
    {
        var scp = context.User.FindFirst(c => c.Type == "scp")?.Value;
        return !string.IsNullOrEmpty(scp) && scp.Split(' ').Contains("Tms.Access");
    }));
    options.AddPolicy("TmsApprove", p => p.RequireAssertion(context =>
    {
        var scp = context.User.FindFirst(c => c.Type == "scp")?.Value;
        return !string.IsNullOrEmpty(scp) && scp.Split(' ').Contains("Tms.Access");
    }));
});

var app = builder.Build();
app.UseHttpsRedirection();
app.UseCors("Portal");
app.UseAuthentication();
app.UseAuthorization();

// Anonymous liveness is intentionally lightweight; readiness verifies Azure SQL connectivity.
app.MapGet("/api/v1/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();
app.MapHealthChecks("/api/v1/health/ready", new HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        if (report.Status != Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy)
        {
            var logger = context.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("Tms.SqlReadiness");

            foreach (var entry in report.Entries)
            {
                logger.LogError(entry.Value.Exception,
                    "Health check {HealthCheckName} returned {HealthStatus}.",
                    entry.Key,
                    entry.Value.Status);
            }
        }

        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new { status = report.Status.ToString() });
    }
}).AllowAnonymous();

// Keep all operational endpoints under /api/v1 via controller routes
app.MapControllers();

if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
app.Run();

public partial class Program { }
