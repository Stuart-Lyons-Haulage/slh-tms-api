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
var deploymentRevision = builder.Configuration["Deployment:Revision"] ?? "local";

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddPolicy("Portal", policy => policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddDbContext<TmsDbContext>(o => o.UseSqlServer(builder.Configuration.GetConnectionString("TmsDb")));
builder.Services.AddScoped<StagingService>();
builder.Services.AddScoped<DotTrackingTelemetryStore>();

// Bind DOT tracking configuration from Tracking:Dot section
// Sensitive values (BaseUrl, Username, Password) are loaded from environment variables or Azure Key Vault at runtime
var dotTrackingOptions = new DotTrackingOptions();
builder.Configuration.GetSection("Tracking:Dot").Bind(dotTrackingOptions);
builder.Services.AddSingleton(dotTrackingOptions);
var sageHrOptions = new SageHrOptions();
builder.Configuration.GetSection("Integrations:SageHr").Bind(sageHrOptions);
builder.Services.AddSingleton(sageHrOptions);
var azureSmsOptions = new AzureSmsOptions();
builder.Configuration.GetSection("Integrations:AzureSms").Bind(azureSmsOptions);
builder.Services.AddSingleton(azureSmsOptions);
builder.Services.AddScoped<AzureSmsDispatchService>();
builder.Services.AddHttpClient<SageHrClient>();
builder.Services.AddHttpClient<DotTrackingClient>();
builder.Services.AddHttpClient<AzureMapsRouteClient>();
builder.Services.AddHostedService<DotTrackingIngestionService>();

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
    var tmsAccessPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireAssertion(context => IsLyonsUser(context.User))
        .Build();
    options.DefaultPolicy = tmsAccessPolicy;
    options.FallbackPolicy = tmsAccessPolicy;
    options.AddPolicy("TmsAccess", tmsAccessPolicy);
    options.AddPolicy("TmsWrite", tmsAccessPolicy);
    options.AddPolicy("TmsApprove", tmsAccessPolicy);
});

static bool IsLyonsUser(ClaimsPrincipal user)
{
    var values = user.Claims
        .Where(claim => claim.Type is "preferred_username" or "upn" or "email" || claim.Type == ClaimTypes.Email || claim.Type == ClaimTypes.Name)
        .Select(claim => claim.Value)
        .Where(value => !string.IsNullOrWhiteSpace(value));

    return values.Any(value => value.EndsWith("@lyonshaulage.com", StringComparison.OrdinalIgnoreCase));
}

var app = builder.Build();

if (!app.Environment.IsEnvironment("Testing"))
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
    await PlanningSchemaInitializer.Apply(db, CancellationToken.None);
}

app.UseHttpsRedirection();
app.UseCors("Portal");
app.UseAuthentication();
app.UseAuthorization();

// Anonymous liveness is intentionally lightweight; readiness verifies Azure SQL connectivity.
app.MapGet("/api/v1/health", () => Results.Ok(new { status = "healthy", revision = deploymentRevision })).AllowAnonymous();
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
