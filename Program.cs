using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json.Serialization;
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
using Slh.Tms.Api.Models.Assistant;
using Slh.Tms.Api.Services;

[assembly: InternalsVisibleTo("Slh.Tms.Api.Tests")]

var builder = WebApplication.CreateBuilder(args);
var tenantId = builder.Configuration["Entra:TenantId"] ?? throw new InvalidOperationException("Entra:TenantId is required");
var audience = builder.Configuration["Entra:Audience"] ?? throw new InvalidOperationException("Entra:Audience is required");
var deploymentRevision = builder.Configuration["Deployment:Revision"] ?? "local";

builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddPolicy("Portal", policy => policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddDbContext<TmsDbContext>(o => o.UseSqlServer(builder.Configuration.GetConnectionString("TmsDb")));
builder.Services.AddScoped<StagingService>();
builder.Services.AddScoped<OrderIntakeLedgerService>();
builder.Services.AddScoped<DotTrackingTelemetryStore>();
var assistantOptions = new AssistantOptions();
builder.Configuration.GetSection("Integrations:OpenAI").Bind(assistantOptions);
assistantOptions.Enabled = ReadBool(builder.Configuration, assistantOptions.Enabled,
    "Integrations:OpenAI:Enabled", "Integrations__OpenAI__Enabled", "openai-enabled", "OpenAI--Enabled");
assistantOptions.ApiKey = ReadSetting(builder.Configuration, assistantOptions.ApiKey,
    "Integrations:OpenAI:ApiKey", "Integrations__OpenAI__ApiKey", "openai-api-key", "OpenAI--ApiKey");
assistantOptions.Model = ReadSetting(builder.Configuration, assistantOptions.Model,
    "Integrations:OpenAI:Model", "Integrations__OpenAI__Model", "openai-model", "OpenAI--Model");
builder.Services.AddSingleton(assistantOptions);
builder.Services.AddHttpClient<TmsAssistantService>();

var dotTrackingOptions = new DotTrackingOptions();
builder.Configuration.GetSection("Tracking:Dot").Bind(dotTrackingOptions);
builder.Services.AddSingleton(dotTrackingOptions);
var tachoMasterOptions = new TachoMasterOptions();
builder.Configuration.GetSection("Integrations:TachoMaster").Bind(tachoMasterOptions);
tachoMasterOptions.Enabled = ReadBool(builder.Configuration, tachoMasterOptions.Enabled,
    "Integrations:TachoMaster:Enabled", "Integrations__TachoMaster__Enabled", "tachomaster-enabled", "tacho-enabled", "TachoMaster--Enabled");
tachoMasterOptions.BaseUrl = ReadSetting(builder.Configuration, tachoMasterOptions.BaseUrl,
    "Integrations:TachoMaster:BaseUrl", "Integrations__TachoMaster__BaseUrl", "tachomaster-base-url", "tacho-base-url", "TachoMaster--BaseUrl");
tachoMasterOptions.ApiKey = ReadSetting(builder.Configuration, tachoMasterOptions.ApiKey,
    "Integrations:TachoMaster:ApiKey", "Integrations__TachoMaster__ApiKey", "tachomaster-api-key", "tacho-api-key", "TachoMaster--ApiKey");
tachoMasterOptions.Username = ReadSetting(builder.Configuration, tachoMasterOptions.Username,
    "Integrations:TachoMaster:Username", "Integrations__TachoMaster__Username", "tachomaster-username", "tacho-username", "TachoMaster--Username");
tachoMasterOptions.Password = ReadSetting(builder.Configuration, tachoMasterOptions.Password,
    "Integrations:TachoMaster:Password", "Integrations__TachoMaster__Password", "tachomaster-password", "tacho-password", "TachoMaster--Password");
var hasDedicatedTachoCredentials = !string.IsNullOrWhiteSpace(tachoMasterOptions.ApiKey) ||
    !string.IsNullOrWhiteSpace(tachoMasterOptions.Username) ||
    !string.IsNullOrWhiteSpace(tachoMasterOptions.Password);
if (!hasDedicatedTachoCredentials && dotTrackingOptions.IsConfigured)
{
    tachoMasterOptions.Enabled = true;
    tachoMasterOptions.BaseUrl = dotTrackingOptions.BaseUrl;
    tachoMasterOptions.ApiKey = dotTrackingOptions.ApiKey;
    tachoMasterOptions.Username = dotTrackingOptions.Username;
    tachoMasterOptions.Password = dotTrackingOptions.Password;
    tachoMasterOptions.UsesSharedRoadTechCredentials = true;
}
builder.Services.AddSingleton(tachoMasterOptions);
var sageHrOptions = new SageHrOptions();
builder.Configuration.GetSection("Integrations:SageHr").Bind(sageHrOptions);
builder.Services.AddSingleton(sageHrOptions);
var azureSmsOptions = new AzureSmsOptions();
builder.Configuration.GetSection("Integrations:AzureSms").Bind(azureSmsOptions);
builder.Services.AddSingleton(azureSmsOptions);
var textBeeOptions = new TextBeeOptions();
builder.Configuration.GetSection("Integrations:TextBee").Bind(textBeeOptions);
builder.Services.AddSingleton(textBeeOptions);
var fleetioOptions = new FleetioOptions();
builder.Configuration.GetSection("Integrations:Fleetio").Bind(fleetioOptions);
fleetioOptions.Enabled = ReadBool(builder.Configuration, fleetioOptions.Enabled,
    "Integrations:Fleetio:Enabled", "Integrations__Fleetio__Enabled", "fleetio-enabled", "Fleetio--Enabled");
fleetioOptions.BaseUrl = ReadSetting(builder.Configuration, fleetioOptions.BaseUrl,
    "Integrations:Fleetio:BaseUrl", "Integrations__Fleetio__BaseUrl", "fleetio-base-url", "Fleetio--BaseUrl");
fleetioOptions.ApiKey = ReadSetting(builder.Configuration, fleetioOptions.ApiKey,
    "Integrations:Fleetio:ApiKey", "Integrations__Fleetio__ApiKey", "fleetio-api-key", "Fleetio--ApiKey");
fleetioOptions.AccountToken = ReadSetting(builder.Configuration, fleetioOptions.AccountToken,
    "Integrations:Fleetio:AccountToken", "Integrations__Fleetio__AccountToken", "fleetio-account-token", "Fleetio--AccountToken");
fleetioOptions.ApiVersion = ReadSetting(builder.Configuration, fleetioOptions.ApiVersion,
    "Integrations:Fleetio:ApiVersion", "Integrations__Fleetio__ApiVersion", "fleetio-api-version", "Fleetio--ApiVersion");
if (fleetioOptions.BaseUrl.EndsWith("/api/v2", StringComparison.OrdinalIgnoreCase)) fleetioOptions.BaseUrl = fleetioOptions.BaseUrl[..^1] + "1";
builder.Services.AddSingleton(fleetioOptions);
builder.Services.AddScoped<AzureSmsDispatchService>();
builder.Services.AddScoped<IntegrationSyncCoordinator>();
builder.Services.AddHttpClient<DriverSmsDispatchService>();
builder.Services.AddHttpClient<SageHrClient>();
builder.Services.AddHttpClient<DotTrackingClient>();
builder.Services.AddHttpClient<TachoMasterClient>();
builder.Services.AddHttpClient<AzureMapsRouteClient>();
builder.Services.AddHttpClient<FleetioClient>();
builder.Services.AddHostedService<DotTrackingIngestionService>();
builder.Services.AddHostedService<IntegrationBackgroundSyncService>();

builder.Services.AddHealthChecks().AddDbContextCheck<TmsDbContext>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.Authority = $"https://login.microsoftonline.com/{tenantId}/v2.0";
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuers = new[]
        {
            $"https://login.microsoftonline.com/{tenantId}/v2.0",
            $"https://sts.windows.net/{tenantId}/"
        },
        ValidateAudience = true,
        ValidAudience = audience,
        ValidateLifetime = true
    };
    o.Events = new JwtBearerEvents
    {
        OnAuthenticationFailed = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        },
        OnChallenge = ctx =>
        {
            if (!ctx.Handled) ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }
    };
});

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

static string ReadSetting(IConfiguration configuration, string fallback, params string[] keys) =>
    keys.Select(key => configuration[key]).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? fallback;

static bool ReadBool(IConfiguration configuration, bool fallback, params string[] keys) =>
    bool.TryParse(ReadSetting(configuration, fallback.ToString(), keys), out var value) ? value : fallback;

var app = builder.Build();

if (!app.Environment.IsEnvironment("Testing"))
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Tms.SchemaInitializer");
    try
    {
        await PlanningSchemaInitializer.Apply(db, logger, CancellationToken.None);
        var quarantinedFleetioPlaceholders = await MasterDetailStore.QuarantineFleetioPlaceholdersAsync(db, CancellationToken.None);
        if (quarantinedFleetioPlaceholders > 0)
            logger.LogWarning("Quarantined {PlaceholderCount} Fleetio placeholder vehicle records from operational master data.", quarantinedFleetioPlaceholders);
        var trailerMerge = await MasterDetailStore.MergeSlhTrailerAliasesAsync(db, CancellationToken.None);
        if (trailerMerge.Renamed > 0 || trailerMerge.Merged > 0)
            logger.LogWarning(
                "Canonicalised trailer register: {Renamed} numeric trailers renamed, {Merged} duplicate aliases merged, {LoadsReassigned} loads, {MappingsReassigned} mappings and {AuditEntriesReassigned} audit entries reassigned.",
                trailerMerge.Renamed, trailerMerge.Merged, trailerMerge.LoadsReassigned, trailerMerge.MappingsReassigned, trailerMerge.AuditEntriesReassigned);
        var register = scope.ServiceProvider.GetRequiredService<StagingService>();
        await register.LinkRegistered(25, CancellationToken.None);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "TMS schema repair failed during startup; continuing so health and diagnostics remain available.");
    }
}

app.UseHttpsRedirection();
app.UseCors("Portal");
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<Slh.Tms.Api.Middleware.PlanLockMiddleware>();

app.MapGet("/api/v1/health", () => Results.Ok(new { status = "healthy", revision = deploymentRevision })).AllowAnonymous();
app.MapHealthChecks("/api/v1/health/ready", new HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        if (report.Status != Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy)
        {
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Tms.SqlReadiness");
            foreach (var entry in report.Entries)
                logger.LogError(entry.Value.Exception, "Health check {HealthCheckName} returned {HealthStatus}.", entry.Key, entry.Value.Status);
        }
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new { status = report.Status.ToString() });
    }
}).AllowAnonymous();

app.MapControllers();

if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
app.Run();

public partial class Program { }
