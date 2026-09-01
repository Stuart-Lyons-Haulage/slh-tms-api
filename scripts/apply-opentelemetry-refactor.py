from pathlib import Path


def replace_exact(path: str, old: str, new: str, count: int = 1) -> None:
    p = Path(path)
    text = p.read_text(encoding='utf-8')
    found = text.count(old)
    if found != count:
        raise SystemExit(f'{path}: expected {count} occurrence(s), found {found}: {old[:120]!r}')
    p.write_text(text.replace(old, new), encoding='utf-8')


def write(path: str, content: str) -> None:
    p = Path(path)
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(content, encoding='utf-8')


# NuGet packages: current stable OpenTelemetry .NET 1.18 and Azure Monitor exporter 1.8.3.
replace_exact(
    'Slh.Tms.Api.csproj',
    '    <PackageReference Include="Azure.Identity" Version="1.13.1" />\n',
    '    <PackageReference Include="Azure.Identity" Version="1.13.1" />\n'
    '    <PackageReference Include="Azure.Monitor.OpenTelemetry.Exporter" Version="1.8.3" />\n'
    '    <PackageReference Include="OpenTelemetry.Extensions.Hosting" Version="1.18.0" />\n'
    '    <PackageReference Include="OpenTelemetry.Instrumentation.AspNetCore" Version="1.18.0" />\n'
    '    <PackageReference Include="OpenTelemetry.Instrumentation.Http" Version="1.18.0" />\n'
    '    <PackageReference Include="OpenTelemetry.Instrumentation.SqlClient" Version="1.18.0" />\n'
)

# Program.cs OpenTelemetry setup, custom metrics registration and SQL interceptor.
replace_exact(
    'Program.cs',
    'using Microsoft.IdentityModel.Tokens;\n',
    'using Microsoft.IdentityModel.Tokens;\n'
    'using Azure.Monitor.OpenTelemetry.Exporter;\n'
    'using OpenTelemetry.Logs;\n'
    'using OpenTelemetry.Metrics;\n'
    'using OpenTelemetry.Resources;\n'
    'using OpenTelemetry.Trace;\n'
)
replace_exact(
    'Program.cs',
    'var deploymentRevision = builder.Configuration["Deployment:Revision"] ?? "local";\n\n',
    '''var deploymentRevision = builder.Configuration["Deployment:Revision"] ?? "local";\nvar applicationInsightsConnectionString =\n    builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"] ??\n    builder.Configuration["ApplicationInsights:ConnectionString"];\n\nvar openTelemetry = builder.Services.AddOpenTelemetry()\n    .ConfigureResource(resource => resource.AddService(\n        serviceName: "slh-tms-api",\n        serviceVersion: deploymentRevision));\n\nopenTelemetry.WithTracing(tracing =>\n{\n    tracing\n        .AddAspNetCoreInstrumentation()\n        .AddHttpClientInstrumentation()\n        .AddSqlClientInstrumentation();\n    if (!string.IsNullOrWhiteSpace(applicationInsightsConnectionString))\n        tracing.AddAzureMonitorTraceExporter(options => options.ConnectionString = applicationInsightsConnectionString);\n});\n\nopenTelemetry.WithMetrics(metrics =>\n{\n    metrics\n        .AddMeter(TmsMetrics.MeterName)\n        .AddAspNetCoreInstrumentation()\n        .AddHttpClientInstrumentation();\n    if (!string.IsNullOrWhiteSpace(applicationInsightsConnectionString))\n        metrics.AddAzureMonitorMetricExporter(options => options.ConnectionString = applicationInsightsConnectionString);\n});\n\nbuilder.Logging.AddOpenTelemetry(logging =>\n{\n    logging.IncludeFormattedMessage = true;\n    logging.IncludeScopes = true;\n    if (!string.IsNullOrWhiteSpace(applicationInsightsConnectionString))\n        logging.AddAzureMonitorLogExporter(options => options.ConnectionString = applicationInsightsConnectionString);\n});\n\n'''
)
replace_exact(
    'Program.cs',
    'builder.Services.AddDbContext<TmsDbContext>(o => o.UseSqlServer(builder.Configuration.GetConnectionString("TmsDb")));\n',
    '''builder.Services.AddSingleton(TmsMetrics.Shared);\nbuilder.Services.AddSingleton<SqlLatencyInterceptor>();\nbuilder.Services.AddScoped<DependencyHealthService>();\nbuilder.Services.AddHostedService<DependencyTelemetrySampler>();\nbuilder.Services.AddDbContext<TmsDbContext>((services, options) =>\n    options.UseSqlServer(builder.Configuration.GetConnectionString("TmsDb"))\n        .AddInterceptors(services.GetRequiredService<SqlLatencyInterceptor>()));\n'''
)
replace_exact(
    'Program.cs',
    'app.UseCors("Portal");\napp.UseAuthentication();\n',
    'app.UseCors("Portal");\napp.UseMiddleware<Slh.Tms.Api.Middleware.ApiLatencyMiddleware>();\napp.UseAuthentication();\n'
)

write('Services/TmsMetrics.cs', r'''using System.Diagnostics.Metrics;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Low-cardinality SLH TMS operational metrics. The shared process instance is registered in DI
/// and is also used by static resilience helpers that cannot receive scoped services.
/// </summary>
public sealed class TmsMetrics
{
    public const string MeterName = "Slh.Tms";
    public static TmsMetrics Shared { get; } = new();

    private readonly Meter _meter = new(MeterName, "1.0.0");
    private readonly Histogram<double> _apiEndpointLatency;
    private readonly Histogram<double> _sqlQueryLatency;
    private readonly Counter<long> _emailOrderIntakeSuccess;
    private readonly Counter<long> _emailOrderIntakeFailure;
    private readonly Counter<long> _importsProcessed;
    private readonly Counter<long> _importDuplicates;
    private readonly Counter<long> _planningRecoveryUsed;
    private readonly Counter<long> _logicalDuplicateCollapse;

    private long _roadTechObservedTicks;
    private long _fleetioContactTicks;
    private long _tachoMasterContactTicks;
    private long _sageHrContactTicks;

    private TmsMetrics()
    {
        _apiEndpointLatency = _meter.CreateHistogram<double>("api_endpoint_latency_ms", "ms", "SLH TMS API endpoint latency.");
        _sqlQueryLatency = _meter.CreateHistogram<double>("sql_query_latency_ms", "ms", "EF Core SQL command latency.");
        _emailOrderIntakeSuccess = _meter.CreateCounter<long>("email_order_intake_success_total", "requests", "Successful mailbox order intake requests.");
        _emailOrderIntakeFailure = _meter.CreateCounter<long>("email_order_intake_failure_total", "requests", "Failed mailbox order intake requests.");
        _importsProcessed = _meter.CreateCounter<long>("import_processed_total", "records", "Validated import records evaluated for staging.");
        _importDuplicates = _meter.CreateCounter<long>("import_duplicate_total", "records", "Import records resolved as an existing idempotent record. Divide by import_processed_total for duplicate rate.");
        _planningRecoveryUsed = _meter.CreateCounter<long>("planning_recovery_used_total", "runs", "Runs supplied by a secondary PlanningResilience source.");
        _logicalDuplicateCollapse = _meter.CreateCounter<long>("logical_duplicate_collapse_total", "runs", "Duplicate persisted run copies collapsed into one logical run.");

        _meter.CreateObservableGauge<double>("roadtech_data_age_seconds", ObserveRoadTechAge, "s", "Age of the newest RoadTech vehicle event.");
        _meter.CreateObservableGauge<double>("fleetio_last_successful_sync_age_seconds", ObserveFleetioAge, "s", "Age of the latest durable Fleetio sync receipt.");
        _meter.CreateObservableGauge<double>("tachomaster_last_successful_sync_age_seconds", ObserveTachoMasterAge, "s", "Age of the latest durable TachoMaster sync receipt.");
        _meter.CreateObservableGauge<double>("sagehr_last_successful_sync_age_seconds", ObserveSageHrAge, "s", "Age of the latest durable Sage HR sync receipt.");
    }

    public void RecordApiEndpointLatency(double milliseconds, string method, string route, int statusCode) =>
        _apiEndpointLatency.Record(milliseconds,
            new KeyValuePair<string, object?>("http.request.method", method),
            new KeyValuePair<string, object?>("http.route", route),
            new KeyValuePair<string, object?>("http.response.status_code", statusCode));

    public void RecordSqlQueryLatency(double milliseconds, string operation) =>
        _sqlQueryLatency.Record(milliseconds, new KeyValuePair<string, object?>("db.operation.name", operation));

    public void RecordEmailOrderIntake(bool success)
    {
        if (success) _emailOrderIntakeSuccess.Add(1);
        else _emailOrderIntakeFailure.Add(1);
    }

    public void RecordImportBatch(long processed, long duplicates, string source)
    {
        if (processed > 0)
            _importsProcessed.Add(processed, new KeyValuePair<string, object?>("import.source", source));
        if (duplicates > 0)
            _importDuplicates.Add(duplicates, new KeyValuePair<string, object?>("import.source", source));
    }

    public void RecordPlanningRecovery(long count, string source)
    {
        if (count > 0)
            _planningRecoveryUsed.Add(count, new KeyValuePair<string, object?>("planning.recovery.source", source));
    }

    public void RecordLogicalDuplicateCollapse(long count)
    {
        if (count > 0) _logicalDuplicateCollapse.Add(count);
    }

    public void UpdateFreshness(DateTimeOffset? roadTechObservedUtc, DateTimeOffset? fleetioContactUtc, DateTimeOffset? tachoMasterContactUtc, DateTimeOffset? sageHrContactUtc)
    {
        Update(ref _roadTechObservedTicks, roadTechObservedUtc);
        Update(ref _fleetioContactTicks, fleetioContactUtc);
        Update(ref _tachoMasterContactTicks, tachoMasterContactUtc);
        Update(ref _sageHrContactTicks, sageHrContactUtc);
    }

    private IEnumerable<Measurement<double>> ObserveRoadTechAge() => ObserveAge(Interlocked.Read(ref _roadTechObservedTicks));
    private IEnumerable<Measurement<double>> ObserveFleetioAge() => ObserveAge(Interlocked.Read(ref _fleetioContactTicks));
    private IEnumerable<Measurement<double>> ObserveTachoMasterAge() => ObserveAge(Interlocked.Read(ref _tachoMasterContactTicks));
    private IEnumerable<Measurement<double>> ObserveSageHrAge() => ObserveAge(Interlocked.Read(ref _sageHrContactTicks));

    private static IEnumerable<Measurement<double>> ObserveAge(long utcTicks)
    {
        if (utcTicks <= 0) return [];
        var timestamp = new DateTimeOffset(utcTicks, TimeSpan.Zero);
        return [new Measurement<double>(Math.Max(0, (DateTimeOffset.UtcNow - timestamp).TotalSeconds))];
    }

    private static void Update(ref long target, DateTimeOffset? value)
    {
        if (value is not null)
            Interlocked.Exchange(ref target, value.Value.UtcDateTime.Ticks);
    }
}
''')

write('Middleware/ApiLatencyMiddleware.cs', r'''using System.Diagnostics;
using Microsoft.AspNetCore.Routing;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Middleware;

public sealed class ApiLatencyMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, TmsMetrics metrics)
    {
        var started = Stopwatch.GetTimestamp();
        var failed = false;
        try
        {
            await next(context);
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText
                ?? context.Request.Path.Value
                ?? "unknown";
            metrics.RecordApiEndpointLatency(elapsedMs, context.Request.Method, route, context.Response.StatusCode);

            if (context.Request.Path.Equals("/api/v1/order-intake/email", StringComparison.OrdinalIgnoreCase))
                metrics.RecordEmailOrderIntake(!failed && context.Response.StatusCode < 400);
        }
    }
}
''')

write('Services/SqlLatencyInterceptor.cs', r'''using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Slh.Tms.Api.Services;

public sealed class SqlLatencyInterceptor(TmsMetrics metrics) : DbCommandInterceptor
{
    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        metrics.RecordSqlQueryLatency(eventData.Duration.TotalMilliseconds, "reader");
        return result;
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        metrics.RecordSqlQueryLatency(eventData.Duration.TotalMilliseconds, "reader");
        return ValueTask.FromResult(result);
    }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        metrics.RecordSqlQueryLatency(eventData.Duration.TotalMilliseconds, "scalar");
        return result;
    }

    public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
    {
        metrics.RecordSqlQueryLatency(eventData.Duration.TotalMilliseconds, "scalar");
        return ValueTask.FromResult(result);
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        metrics.RecordSqlQueryLatency(eventData.Duration.TotalMilliseconds, "non_query");
        return result;
    }

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        metrics.RecordSqlQueryLatency(eventData.Duration.TotalMilliseconds, "non_query");
        return ValueTask.FromResult(result);
    }
}
''')

write('Services/DependencyHealthService.cs', r'''using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Integrations;
using Slh.Tms.Api.Models.Tracking;

namespace Slh.Tms.Api.Services;

public sealed record DependencyState(string Status, DateTimeOffset? LastSuccessfulContactUtc, double? AgeSeconds, string? Detail = null);
public sealed record DependencyHealthSnapshot(DateTimeOffset CheckedAtUtc, string Status, IReadOnlyDictionary<string, DependencyState> Dependencies);

public sealed class DependencyHealthService(
    TmsDbContext db,
    DotTrackingOptions dot,
    FleetioOptions fleetio,
    TachoMasterOptions tacho,
    SageHrClient sage,
    ILogger<DependencyHealthService> logger)
{
    public async Task<DependencyHealthSnapshot> GetSnapshotAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        bool sqlAvailable;
        try { sqlAvailable = await db.Database.CanConnectAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Dependency health SQL connectivity check failed.");
            sqlAvailable = false;
        }

        if (!sqlAvailable)
        {
            var unavailable = new Dictionary<string, DependencyState>(StringComparer.OrdinalIgnoreCase)
            {
                ["SQL"] = new("Unavailable", null, null, "Azure SQL could not be reached."),
                ["RoadTech"] = new("Unavailable", null, null, "Freshness could not be read because SQL is unavailable."),
                ["Fleetio"] = new("Unavailable", null, null, "Freshness could not be read because SQL is unavailable."),
                ["TachoMaster"] = new("Unavailable", null, null, "Freshness could not be read because SQL is unavailable."),
                ["SageHR"] = new("Unavailable", null, null, "Freshness could not be read because SQL is unavailable.")
            };
            return new(now, "Unavailable", unavailable);
        }

        var roadTechUtc = await SafeTimestamp(async () => await db.VehicleLiveStatuses.AsNoTracking()
            .MaxAsync(item => (DateTimeOffset?)item.LastEventTimeUtc, ct), "RoadTech", ct);
        var fleetioUtc = await SafeTimestamp(async () => await db.IntegrationMappings.AsNoTracking()
            .Where(item => item.Provider == "Fleetio" && item.Active)
            .MaxAsync(item => (DateTimeOffset?)item.UpdatedAtUtc, ct), "Fleetio", ct);
        var tachoUtc = await SafeTimestamp(async () => await db.StagedImports.AsNoTracking()
            .Where(item => item.Status == StagingStatus.Promoted &&
                (item.EntityType == "tachodrivermastersync" || item.EntityType == "tachomastersync" || item.EntityType == "tachodriverprofile"))
            .MaxAsync(item => (DateTimeOffset?)(item.ReviewedAtUtc ?? item.ReceivedAtUtc), ct), "TachoMaster", ct);
        var sageUtc = await SafeTimestamp(async () => await db.StagedImports.AsNoTracking()
            .Where(item => item.EntityType == "sagehrsync" && item.Status == StagingStatus.Promoted)
            .MaxAsync(item => (DateTimeOffset?)(item.ReviewedAtUtc ?? item.ReceivedAtUtc), ct), "SageHR", ct);

        var dependencies = new Dictionary<string, DependencyState>(StringComparer.OrdinalIgnoreCase)
        {
            ["SQL"] = new("Healthy", now, 0),
            ["RoadTech"] = Evaluate(dot.IsConfigured, roadTechUtc, now, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)),
            ["Fleetio"] = Evaluate(fleetio.IsConfigured, fleetioUtc, now, TimeSpan.FromMinutes(90), TimeSpan.FromHours(3)),
            ["TachoMaster"] = Evaluate(tacho.IsConfigured, tachoUtc, now, TimeSpan.FromMinutes(15), TimeSpan.FromHours(1)),
            ["SageHR"] = Evaluate(sage.IsConfigured, sageUtc, now, TimeSpan.FromHours(30), TimeSpan.FromHours(48))
        };

        var overall = dependencies.Values.Any(item => item.Status == "Unavailable")
            ? "Unavailable"
            : dependencies.Values.Any(item => item.Status == "Degraded") ? "Degraded" : "Healthy";
        return new(now, overall, dependencies);
    }

    internal static DependencyState Evaluate(bool configured, DateTimeOffset? lastSuccessfulContactUtc, DateTimeOffset now, TimeSpan healthyAge, TimeSpan unavailableAge)
    {
        if (!configured) return new("Unavailable", lastSuccessfulContactUtc, Age(lastSuccessfulContactUtc, now), "Dependency is not configured.");
        if (lastSuccessfulContactUtc is null) return new("Unavailable", null, null, "No successful contact has been recorded.");
        var age = now - lastSuccessfulContactUtc.Value;
        if (age <= healthyAge) return new("Healthy", lastSuccessfulContactUtc, Math.Max(0, age.TotalSeconds));
        if (age <= unavailableAge) return new("Degraded", lastSuccessfulContactUtc, Math.Max(0, age.TotalSeconds), "Last successful contact is older than the normal schedule threshold.");
        return new("Unavailable", lastSuccessfulContactUtc, Math.Max(0, age.TotalSeconds), "Last successful contact is outside the dependency availability window.");
    }

    private async Task<DateTimeOffset?> SafeTimestamp(Func<Task<DateTimeOffset?>> query, string dependency, CancellationToken ct)
    {
        try { return await query(); }
        catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not read durable {Dependency} freshness timestamp.", dependency);
            db.ChangeTracker.Clear();
            return null;
        }
    }

    private static double? Age(DateTimeOffset? value, DateTimeOffset now) =>
        value is null ? null : Math.Max(0, (now - value.Value).TotalSeconds);
}
''')

write('Services/DependencyTelemetrySampler.cs', r'''namespace Slh.Tms.Api.Services;

public sealed class DependencyTelemetrySampler(
    IServiceScopeFactory scopeFactory,
    TmsMetrics metrics,
    ILogger<DependencyTelemetrySampler> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var health = scope.ServiceProvider.GetRequiredService<DependencyHealthService>();
                var snapshot = await health.GetSnapshotAsync(stoppingToken);
                metrics.UpdateFreshness(
                    snapshot.Dependencies["RoadTech"].LastSuccessfulContactUtc,
                    snapshot.Dependencies["Fleetio"].LastSuccessfulContactUtc,
                    snapshot.Dependencies["TachoMaster"].LastSuccessfulContactUtc,
                    snapshot.Dependencies["SageHR"].LastSuccessfulContactUtc);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Dependency telemetry sampler failed; retaining the previous freshness observations.");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
''')

write('Controllers/DependencyHealthController.cs', r'''using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("health/dependencies")]
[Route("api/v1/health/dependencies")]
[AllowAnonymous]
public sealed class DependencyHealthController(DependencyHealthService health) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct) => Ok(await health.GetSnapshotAsync(ct));
}
''')

# PlanningResilience: count real secondary-source recoveries and logical duplicate collapses.
replace_exact(
    'Controllers/PlanningResilienceController.cs',
    '        var merged = new Dictionary<Guid, Load>();\n\n        try\n',
    '        var merged = new Dictionary<Guid, Load>();\n        var registeredLogicalKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);\n\n        try\n'
)
replace_exact(
    'Controllers/PlanningResilienceController.cs',
    '            var registered = await PlanningRegisterStore.ReadLoadsAsync(db, date, ct);\n            foreach (var load in registered) merged[load.Id] = load;\n',
    '''            var registered = await PlanningRegisterStore.ReadLoadsAsync(db, date, ct);\n            foreach (var load in registered)\n            {\n                merged[load.Id] = load;\n                registeredLogicalKeys.Add(LogicalRunKey(load));\n            }\n'''
)
replace_exact(
    'Controllers/PlanningResilienceController.cs',
    '            var live = await query.OrderBy(x => x.PlanningDate).ThenBy(x => x.Reference).Take(2000).ToListAsync(ct);\n            foreach (var load in live)\n            {\n',
    '''            var live = await query.OrderBy(x => x.PlanningDate).ThenBy(x => x.Reference).Take(2000).ToListAsync(ct);\n            var relationalFallbacks = live.Count(load => !registeredLogicalKeys.Contains(LogicalRunKey(load)));\n            TmsMetrics.Shared.RecordPlanningRecovery(relationalFallbacks, "relational_loads");\n            foreach (var load in live)\n            {\n'''
)
replace_exact(
    'Controllers/PlanningResilienceController.cs',
    '            foreach (var load in audited)\n            {\n',
    '            var auditRecoveries = 0;\n            foreach (var load in audited)\n            {\n'
)
replace_exact(
    'Controllers/PlanningResilienceController.cs',
    '                merged[load.Id] = load;\n                activeKeys.Add(logicalKey);\n            }\n',
    '                merged[load.Id] = load;\n                activeKeys.Add(logicalKey);\n                auditRecoveries++;\n            }\n            TmsMetrics.Shared.RecordPlanningRecovery(auditRecoveries, "planner_audit");\n'
)
replace_exact(
    'Controllers/PlanningResilienceController.cs',
    '            var preferred = candidates[0];\n            foreach (var duplicate in candidates.Skip(1))\n                MergeMissingOperationalData(preferred, duplicate);\n',
    '            var preferred = candidates[0];\n            TmsMetrics.Shared.RecordLogicalDuplicateCollapse(candidates.Count - 1);\n            foreach (var duplicate in candidates.Skip(1))\n                MergeMissingOperationalData(preferred, duplicate);\n'
)
replace_exact(
    'Controllers/PlanningResilienceController.cs',
    '            var live = await db.Loads.AsNoTracking().Include(x => x.Stops).SingleOrDefaultAsync(x => x.Id == id, ct);\n            if (live is not null) return live;\n',
    '            var live = await db.Loads.AsNoTracking().Include(x => x.Stops).SingleOrDefaultAsync(x => x.Id == id, ct);\n            if (live is not null)\n            {\n                TmsMetrics.Shared.RecordPlanningRecovery(1, "relational_loads");\n                return live;\n            }\n'
)
replace_exact(
    'Controllers/PlanningResilienceController.cs',
    '            return recovered;\n',
    '            TmsMetrics.Shared.RecordPlanningRecovery(1, "planner_audit");\n            return recovered;\n',
    1
)

# Generic staging duplicate rate. Rates are derived from duplicate / processed counters.
replace_exact(
    'Controllers/StagingController.cs',
    '        var existing = await db.StagedImports.AsNoTracking().SingleOrDefaultAsync(x => x.IdempotencyKey == request.IdempotencyKey, ct);\n        if (existing is not null) return Ok(service.ToResponse(existing, Request));\n',
    '''        var existing = await db.StagedImports.AsNoTracking().SingleOrDefaultAsync(x => x.IdempotencyKey == request.IdempotencyKey, ct);\n        if (existing is not null)\n        {\n            TmsMetrics.Shared.RecordImportBatch(1, 1, "staging_single");\n            return Ok(service.ToResponse(existing, Request));\n        }\n'''
)
replace_exact(
    'Controllers/StagingController.cs',
    '            await db.SaveChangesAsync(ct);\n            return Accepted(service.ToResponse(item, Request));\n',
    '            await db.SaveChangesAsync(ct);\n            TmsMetrics.Shared.RecordImportBatch(1, 0, "staging_single");\n            return Accepted(service.ToResponse(item, Request));\n',
    1
)
replace_exact(
    'Controllers/StagingController.cs',
    '            await db.SaveChangesAsync(ct);\n            return Accepted(new { received = requests.Count, existing = existingCount, created = responses.Count - existingCount, skippedZeroPallets, records = responses });\n',
    '            await db.SaveChangesAsync(ct);\n            TmsMetrics.Shared.RecordImportBatch(filteredRequests.Count, existingCount, "staging_batch");\n            return Accepted(new { received = requests.Count, existing = existingCount, created = responses.Count - existingCount, skippedZeroPallets, records = responses });\n'
)

# Mailbox order intake performs its own direct staged-import idempotency check, so include it in duplicate telemetry too.
replace_exact(
    'Controllers/OrderIntakeController.cs',
    '        logger.LogInformation(\n            "Info mailbox intake {MessageId}: staged {Staged}, existing {Existing}, superseded {Superseded}, parser warnings {Warnings}.",\n',
    '        TmsMetrics.Shared.RecordImportBatch(staged + existing, existing, "email_order");\n\n        logger.LogInformation(\n            "Info mailbox intake {MessageId}: staged {Staged}, existing {Existing}, superseded {Superseded}, parser warnings {Warnings}.",\n'
)

write('Slh.Tms.Api.Tests/TelemetryContractTests.cs', r'''using System.Diagnostics.Metrics;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class TelemetryContractTests
{
    [Fact]
    public void Custom_meter_publishes_required_operational_instruments()
    {
        _ = TmsMetrics.Shared;
        var names = new HashSet<string>(StringComparer.Ordinal);
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == TmsMetrics.MeterName)
                    names.Add(instrument.Name);
            }
        };
        listener.Start();

        var required = new[]
        {
            "api_endpoint_latency_ms",
            "sql_query_latency_ms",
            "roadtech_data_age_seconds",
            "fleetio_last_successful_sync_age_seconds",
            "tachomaster_last_successful_sync_age_seconds",
            "email_order_intake_success_total",
            "email_order_intake_failure_total",
            "import_processed_total",
            "import_duplicate_total",
            "planning_recovery_used_total",
            "logical_duplicate_collapse_total"
        };

        foreach (var name in required)
            Assert.Contains(name, names);
    }

    [Fact]
    public void Dependency_status_thresholds_are_structured_and_deterministic()
    {
        var now = new DateTimeOffset(2026, 9, 1, 6, 0, 0, TimeSpan.Zero);
        Assert.Equal("Healthy", DependencyHealthService.Evaluate(true, now.AddMinutes(-4), now, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)).Status);
        Assert.Equal("Degraded", DependencyHealthService.Evaluate(true, now.AddMinutes(-10), now, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)).Status);
        Assert.Equal("Unavailable", DependencyHealthService.Evaluate(true, now.AddMinutes(-20), now, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)).Status);
        Assert.Equal("Unavailable", DependencyHealthService.Evaluate(false, now, now, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)).Status);
    }
}
''')

print('OpenTelemetry refactor applied.')
