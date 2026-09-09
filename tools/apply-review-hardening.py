from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    file = Path(path)
    text = file.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected exactly one match, found {count}: {old[:120]!r}")
    file.write_text(text.replace(old, new, 1), encoding="utf-8")


def write(path: str, content: str) -> None:
    file = Path(path)
    file.parent.mkdir(parents=True, exist_ok=True)
    file.write_text(content, encoding="utf-8")


# AREA 1 + 2: configurable Beta optimiser policy and non-poisoning per-request route cache.
write("Services/BetaOptimiserOptions.cs", r'''namespace Slh.Tms.Api.Services;

/// <summary>
/// Operational policy for the read-only Beta optimiser. Latitude values are deliberately only
/// coarse directional gates; live Azure Maps HGV routing remains authoritative for road mileage.
/// Defaults preserve the reviewed September 2026 behaviour and can be overridden through
/// BetaOptimiser__* configuration/environment settings without a code deployment.
/// </summary>
public sealed class BetaOptimiserOptions
{
    public const string SectionName = "BetaOptimiser";

    /// <summary>Minimum latitude reduction for a movement to be treated as southbound (~3.5 miles at UK latitudes).</summary>
    public decimal MinSouthboundLatitudeDelta { get; set; } = 0.05m;

    /// <summary>Maximum permitted northward latitude reposition from the outbound terminal to the backhaul collection.</summary>
    public decimal MaxNorthwardBackhaulDetourLatitude { get; set; } = 0.25m;

    /// <summary>Multiplier applied to standalone backhaul road miles when deciding whether the incremental combined route is sensible.</summary>
    public decimal MaxBackhaulIncrementFactor { get; set; } = 1.35m;

    /// <summary>Fixed road-mile allowance added to the standalone backhaul threshold.</summary>
    public decimal BackhaulIncrementAllowanceMiles { get; set; } = 15m;

    public int RouteDeadlineSeconds { get; set; } = 12;
    public int RequestRoutingBudgetSeconds { get; set; } = 60;

    public TimeSpan RouteDeadline => TimeSpan.FromSeconds(RouteDeadlineSeconds);
    public TimeSpan RequestRoutingBudget => TimeSpan.FromSeconds(RequestRoutingBudgetSeconds);

    public BetaOptimiserOptions Validate()
    {
        if (MinSouthboundLatitudeDelta <= 0m || MinSouthboundLatitudeDelta > 5m)
            throw new InvalidOperationException("BetaOptimiser:MinSouthboundLatitudeDelta must be greater than 0 and no more than 5 degrees.");
        if (MaxNorthwardBackhaulDetourLatitude < 0m || MaxNorthwardBackhaulDetourLatitude > 5m)
            throw new InvalidOperationException("BetaOptimiser:MaxNorthwardBackhaulDetourLatitude must be between 0 and 5 degrees.");
        if (MaxBackhaulIncrementFactor < 1m || MaxBackhaulIncrementFactor > 10m)
            throw new InvalidOperationException("BetaOptimiser:MaxBackhaulIncrementFactor must be between 1 and 10.");
        if (BackhaulIncrementAllowanceMiles < 0m || BackhaulIncrementAllowanceMiles > 500m)
            throw new InvalidOperationException("BetaOptimiser:BackhaulIncrementAllowanceMiles must be between 0 and 500 miles.");
        if (RouteDeadlineSeconds is < 1 or > 60)
            throw new InvalidOperationException("BetaOptimiser:RouteDeadlineSeconds must be between 1 and 60 seconds.");
        if (RequestRoutingBudgetSeconds is < 5 or > 300)
            throw new InvalidOperationException("BetaOptimiser:RequestRoutingBudgetSeconds must be between 5 and 300 seconds.");
        if (RequestRoutingBudgetSeconds < RouteDeadlineSeconds)
            throw new InvalidOperationException("BetaOptimiser:RequestRoutingBudgetSeconds must not be shorter than RouteDeadlineSeconds.");
        return this;
    }
}
''')

write("Services/BetaRequestRouteCache.cs", r'''namespace Slh.Tms.Api.Services;

/// <summary>
/// Shares identical live-route work inside one Beta request. Only successful route evidence is
/// retained. Null/unavailable results and faulted/cancelled tasks are evicted so a transient
/// Azure Maps failure cannot poison the same route key for the rest of the request.
/// </summary>
internal sealed class BetaRequestRouteCache
{
    private readonly Dictionary<string, Task<BetaHgvRouteCost?>> entries = new(StringComparer.Ordinal);
    private readonly object gate = new();

    public async Task<BetaHgvRouteCost?> GetOrCreateAsync(
        string key,
        Func<Task<BetaHgvRouteCost?>> factory)
    {
        Task<BetaHgvRouteCost?> task;
        lock (gate)
        {
            if (!entries.TryGetValue(key, out task!))
            {
                task = factory();
                entries[key] = task;
            }
        }

        try
        {
            var result = await task.ConfigureAwait(false);
            if (result is null) RemoveIfCurrent(key, task);
            return result;
        }
        catch
        {
            RemoveIfCurrent(key, task);
            throw;
        }
    }

    private void RemoveIfCurrent(string key, Task<BetaHgvRouteCost?> task)
    {
        lock (gate)
        {
            if (entries.TryGetValue(key, out var current) && ReferenceEquals(current, task))
                entries.Remove(key);
        }
    }
}
''')

replace_once(
    "Services/BetaDayPlanBuilder.cs",
    '''public sealed class BetaDayPlanBuilder(IBetaHgvRouteProvider routeProvider)\n{\n    private const int StandardCapacity = 26;\n    private const int EuroCapacity = 33;\n    private const int MaxCandidateRouteChecksPerFill = 16;\n    private const int MaxPhaseSwapChecks = 12;\n    private const decimal SouthboundLatitudeDelta = 0.05m;\n    private const decimal MaxNorthwardBackhaulDetourLatitude = 0.25m;\n    private const decimal MaxBackhaulIncrementFactor = 1.35m;\n    private const decimal BackhaulIncrementAllowanceMiles = 15m;''',
    '''public sealed class BetaDayPlanBuilder(\n    IBetaHgvRouteProvider routeProvider,\n    BetaOptimiserOptions? optimiserOptions = null)\n{\n    private const int StandardCapacity = 26;\n    private const int EuroCapacity = 33;\n    private const int MaxCandidateRouteChecksPerFill = 16;\n    private const int MaxPhaseSwapChecks = 12;\n    private readonly BetaOptimiserOptions options = (optimiserOptions ?? new BetaOptimiserOptions()).Validate();''')

replace_once(
    "Services/BetaDayPlanBuilder.cs",
    '''        var backhaulCandidates = orders\n            .Where(IsSouthboundBackhaulCandidate)''',
    '''        var backhaulCandidates = orders\n            .Where(order => IsSouthboundBackhaulCandidate(order, options.MinSouthboundLatitudeDelta))''')

replace_once(
    "Services/BetaDayPlanBuilder.cs",
    '''            var terminal = run.Stops[^1];\n            if (backhaul.Collection.Latitude > terminal.Latitude + MaxNorthwardBackhaulDetourLatitude) continue;\n            if (backhaul.Delivery.Latitude >= backhaul.Collection.Latitude - SouthboundLatitudeDelta) continue;''',
    '''            var terminal = run.Stops[^1];\n            if (backhaul.Collection.Latitude > terminal.Latitude + options.MaxNorthwardBackhaulDetourLatitude) continue;\n            if (backhaul.Collection.Latitude - backhaul.Delivery.Latitude < options.MinSouthboundLatitudeDelta) continue;''')

replace_once(
    "Services/BetaDayPlanBuilder.cs",
    '''            if (increment > standalone.Miles * MaxBackhaulIncrementFactor + BackhaulIncrementAllowanceMiles) continue;''',
    '''            if (increment > standalone.Miles * options.MaxBackhaulIncrementFactor + options.BackhaulIncrementAllowanceMiles) continue;''')

replace_once(
    "Services/BetaDayPlanBuilder.cs",
    '''    private static bool IsSouthboundBackhaulCandidate(BetaDayOrderInput order) =>\n        NormalisePeriod(order.Period) != "W3" &&\n        order.RoutingMapped &&\n        order.Delivery.Latitude < order.Collection.Latitude - SouthboundLatitudeDelta;''',
    '''    internal static bool IsSouthboundBackhaulCandidate(BetaDayOrderInput order, decimal minSouthboundLatitudeDelta) =>\n        NormalisePeriod(order.Period) != "W3" &&\n        order.RoutingMapped &&\n        order.Collection.Latitude - order.Delivery.Latitude >= minSouthboundLatitudeDelta;''')

replace_once(
    "Services/BetaHgvRouteProvider.cs",
    '''public sealed class AzureMapsHgvRouteProvider(\n    AzureMapsRouteClient maps,\n    ILogger<AzureMapsHgvRouteProvider> logger) : IBetaHgvRouteProvider\n{\n    private static readonly TimeSpan RouteDeadline = TimeSpan.FromSeconds(12);\n    private readonly Dictionary<string, Task<BetaHgvRouteCost?>> routeCache = new(StringComparer.Ordinal);\n    private readonly object cacheGate = new();\n\n    public Task<BetaHgvRouteCost?> GetRouteAsync(IReadOnlyList<BetaRoutePoint> points, CancellationToken ct)\n    {\n        if (points.Count < 2) return Task.FromResult<BetaHgvRouteCost?>(new BetaHgvRouteCost(0m, 0, "AzureMapsHgv"));\n        var key = string.Join(";", points.Select(point => $"{point.Latitude:0.000000},{point.Longitude:0.000000}"));\n        lock (cacheGate)\n        {\n            if (routeCache.TryGetValue(key, out var cached)) return cached;\n            var task = GetRouteCoreAsync(points, ct);\n            routeCache[key] = task;\n            return task;\n        }\n    }''',
    '''public sealed class AzureMapsHgvRouteProvider(\n    AzureMapsRouteClient maps,\n    ILogger<AzureMapsHgvRouteProvider> logger,\n    BetaOptimiserOptions? optimiserOptions = null) : IBetaHgvRouteProvider\n{\n    private readonly BetaOptimiserOptions options = (optimiserOptions ?? new BetaOptimiserOptions()).Validate();\n    private readonly BetaRequestRouteCache routeCache = new();\n\n    public Task<BetaHgvRouteCost?> GetRouteAsync(IReadOnlyList<BetaRoutePoint> points, CancellationToken ct)\n    {\n        if (points.Count < 2) return Task.FromResult<BetaHgvRouteCost?>(new BetaHgvRouteCost(0m, 0, "AzureMapsHgv"));\n        var key = string.Join(";", points.Select(point => $"{point.Latitude:0.000000},{point.Longitude:0.000000}"));\n        var snapshot = points.ToArray();\n        return routeCache.GetOrCreateAsync(key, () => GetRouteCoreAsync(snapshot, ct));\n    }''')

replace_once(
    "Services/BetaHgvRouteProvider.cs",
    '''        routeCts.CancelAfter(RouteDeadline);''',
    '''        routeCts.CancelAfter(options.RouteDeadline);''')
replace_once(
    "Services/BetaHgvRouteProvider.cs",
    '''            logger.LogWarning("Azure Maps HGV evidence exceeded the {DeadlineSeconds}s Beta route deadline for {StopCount} stops; the run will be returned as unrouted rather than failing the comparison.", RouteDeadline.TotalSeconds, points.Count);''',
    '''            logger.LogWarning("Azure Maps HGV evidence exceeded the {DeadlineSeconds}s Beta route deadline for {StopCount} stops; the run will be returned as unrouted rather than failing the comparison.", options.RouteDeadline.TotalSeconds, points.Count);''')

replace_once(
    "Controllers/BetaDayPlanController.cs",
    '''public sealed class BetaDayPlanController(\n    TmsDbContext db,\n    AzureMapsRouteClient maps,\n    ILoggerFactory loggerFactory,\n    ILogger<BetaDayPlanController> logger) : ControllerBase''',
    '''public sealed class BetaDayPlanController(\n    TmsDbContext db,\n    AzureMapsRouteClient maps,\n    IConfiguration configuration,\n    ILoggerFactory loggerFactory,\n    ILogger<BetaDayPlanController> logger) : ControllerBase''')

replace_once(
    "Controllers/BetaDayPlanController.cs",
    '''    private BetaDayPlanService Service()\n    {\n        var liveProvider = new AzureMapsHgvRouteProvider(maps, loggerFactory.CreateLogger<AzureMapsHgvRouteProvider>());\n        // One shared budget covers both the independent Beta build and the uploaded-plan\n        // routing within this HTTP request. When exhausted, remaining routes are marked\n        // unavailable instead of allowing the gateway to terminate the entire comparison.\n        var provider = new BudgetedBetaHgvRouteProvider(\n            liveProvider,\n            loggerFactory.CreateLogger<BudgetedBetaHgvRouteProvider>());\n        var builder = new BetaDayPlanBuilder(provider);\n        return new BetaDayPlanService(db, builder, provider, loggerFactory.CreateLogger<BetaDayPlanService>());\n    }''',
    '''    private BetaDayPlanService Service()\n    {\n        var options = configuration.GetSection(BetaOptimiserOptions.SectionName)\n            .Get<BetaOptimiserOptions>() ?? new BetaOptimiserOptions();\n        options.Validate();\n\n        var liveProvider = new AzureMapsHgvRouteProvider(\n            maps,\n            loggerFactory.CreateLogger<AzureMapsHgvRouteProvider>(),\n            options);\n        // One shared budget covers both the independent Beta build and the uploaded-plan\n        // routing within this HTTP request. When exhausted, remaining routes are marked\n        // unavailable instead of allowing the gateway to terminate the entire comparison.\n        var provider = new BudgetedBetaHgvRouteProvider(\n            liveProvider,\n            loggerFactory.CreateLogger<BudgetedBetaHgvRouteProvider>(),\n            options.RequestRoutingBudget);\n        var builder = new BetaDayPlanBuilder(provider, options);\n        return new BetaDayPlanService(db, builder, provider, loggerFactory.CreateLogger<BetaDayPlanService>());\n    }''')

# AREA 3: one ready-time pattern, safe sender-domain suffix matching, and Sunday planner warning.
replace_once(
    "Services/EmailOrderIntakeService.cs",
    r'''        @"\bready\s+for\s+collection\s+(?:from|at)\s*(?<time>(?:[01]?\d|2[0-3])(?:[:.]\d{2})?\s*(?:am|pm)?)\b",''',
    r'''        @"\bready(?:\s+for\s+collection)?\s+(?:from|at|about)\s*(?<time>(?:[01]?\d|2[0-3])(?:[:.]\d{2})?\s*(?:am|pm)?)\b",''')

replace_once(
    "Services/EmailOrderIntakeService.cs",
    r'''        var collectionTime = NormaliseTime(ExtractTime(collectionLabel)\n            ?? ExtractMatch(CollectionTimeRegex, body, "time")\n            ?? ExtractMatch(new Regex(@"\bready\s+(?:from|at|about)?\s*(?<time>(?:[01]?\d|2[0-3])(?:[:.]\d{2})?\s*(?:am|pm)?)", RegexOptions.IgnoreCase), body, "time"));''',
    r'''        var collectionTime = NormaliseTime(ExtractTime(collectionLabel)\n            ?? ExtractMatch(CollectionTimeRegex, body, "time")\n            ?? ExtractMatch(ReadyForCollectionTimeRegex, body, "time"));''')

replace_once(
    "Services/EmailOrderIntakeService.cs",
    '''        var senderCustomer = (request.SenderAddress ?? string.Empty).EndsWith("@langmeadherbs.co.uk", StringComparison.OrdinalIgnoreCase) ||\n            (request.SenderAddress ?? string.Empty).EndsWith("@langmeadfarms.co.uk", StringComparison.OrdinalIgnoreCase)\n            ? "LANGMEADS" : null;''',
    '''        var senderDomain = SenderDomain(request.SenderAddress);\n        var senderCustomer = senderDomain is not null &&\n            (DomainMatches(senderDomain, "langmeadherbs.co.uk") || DomainMatches(senderDomain, "langmeadfarms.co.uk"))\n            ? "LANGMEADS" : null;''')

replace_once(
    "Services/EmailOrderIntakeService.cs",
    '''        sources["customer"] = !string.IsNullOrWhiteSpace(explicitCustomer) ? "body.explicit"\n            : !string.IsNullOrWhiteSpace(masterCustomer) ? "master-data.body-site"\n            : bodySignal is not null ? "template.body-signal"\n            : IsTemplateSource(order.SourceKey) ? "template" : subjectSignal is not null ? "subject" : "fallback";''',
    '''        sources["customer"] = !string.IsNullOrWhiteSpace(explicitCustomer) ? "body.explicit"\n            : !string.IsNullOrWhiteSpace(masterCustomer) ? "master-data.body-site"\n            : senderCustomer is not null ? "sender.domain"\n            : bodySignal is not null ? "template.body-signal"\n            : IsTemplateSource(order.SourceKey) ? "template" : subjectSignal is not null ? "subject" : "fallback";''')

replace_once(
    "Services/EmailOrderIntakeService.cs",
    '''    private static string? InferCollectionSiteFromSender(string? senderAddress)\n    {\n        var domain = SenderDomain(senderAddress);\n        return domain is not null && SenderDomainCollectionSites.TryGetValue(domain, out var site) ? site : null;\n    }''',
    '''    private static string? InferCollectionSiteFromSender(string? senderAddress)\n    {\n        var domain = SenderDomain(senderAddress);\n        if (domain is null) return null;\n        foreach (var (rootDomain, site) in SenderDomainCollectionSites)\n            if (DomainMatches(domain, rootDomain)) return site;\n        return null;\n    }\n\n    private static bool DomainMatches(string domain, string rootDomain) =>\n        domain.Equals(rootDomain, StringComparison.OrdinalIgnoreCase) ||\n        domain.EndsWith("." + rootDomain, StringComparison.OrdinalIgnoreCase);''')

replace_once(
    "Services/EmailOrderIntakeService.cs",
    '''            var collectionDate = deliveryDate.Value.AddDays(-1);\n            var baseReference = customerPo ?? StableEmailReference(request.MessageId);''',
    '''            var collectionDate = deliveryDate.Value.AddDays(-1);\n            if (collectionDate.DayOfWeek == DayOfWeek.Sunday)\n                warnings.Add($"Overnight collection inferred as Sunday {collectionDate:dd/MM/yyyy} for Monday delivery {deliveryDate.Value:dd/MM/yyyy}; confirm Sunday PM operation before approval.");\n            var baseReference = customerPo ?? StableEmailReference(request.MessageId);''')

# AREA 4: null-safe dispatch start fallback and sort only after enrichment/fallback timestamps exist.
write("Services/WallboardPlannedRunPreparation.cs", r'''using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

internal static class WallboardPlannedRunPreparation
{
    public static void ApplyDispatchStartFallback(Load load, DateTimeOffset? dispatchPlannedStartUtc)
    {
        if (dispatchPlannedStartUtc is null) return;
        var firstStop = load.Stops.OrderBy(stop => stop.Sequence).FirstOrDefault();
        if (firstStop is not null && firstStop.PlannedArrivalUtc is null)
            firstStop.PlannedArrivalUtc = dispatchPlannedStartUtc;
    }

    public static List<Load> OrderByOperationalStart(IEnumerable<Load> loads) =>
        loads.OrderBy(load => load.Stops\n                .Where(stop => stop.PlannedArrivalUtc is not null)\n                .Select(stop => stop.PlannedArrivalUtc)\n                .Min() ?? DateTimeOffset.MaxValue)\n            .ThenBy(load => load.Reference, StringComparer.OrdinalIgnoreCase)\n            .ToList();
}
''')

replace_once(
    "Controllers/TvPlannedRunsController.cs",
    '''            var firstStop = load.Stops.OrderBy(stop => stop.Sequence).FirstOrDefault();\n            if (firstStop?.PlannedArrivalUtc is null\n                && dispatchStates.TryGetValue(load.Id, out var dispatchState)\n                && dispatchState.PlannedStartUtc is not null)\n            {\n                firstStop.PlannedArrivalUtc = dispatchState.PlannedStartUtc;\n            }\n        }\n        return Ok(loads);''',
    '''            WallboardPlannedRunPreparation.ApplyDispatchStartFallback(\n                load,\n                dispatchStates.TryGetValue(load.Id, out var dispatchState)\n                    ? dispatchState.PlannedStartUtc\n                    : null);\n        }\n\n        // Sorting before resilient/dispatch enrichment leaves recovered early-start runs at the\n        // bottom of the wallboard. Re-sort only after the authoritative operational timestamps\n        // have been applied.\n        loads = WallboardPlannedRunPreparation.OrderByOperationalStart(loads);\n        return Ok(loads);''')

# AREA 5: semantic read policy and fail closed when live comparison evidence is unavailable.
replace_once(
    "Program.cs",
    '''    options.AddPolicy("TmsAccess", tmsAccessPolicy);\n    options.AddPolicy("TmsWrite", tmsAccessPolicy);''',
    '''    options.AddPolicy("TmsAccess", tmsAccessPolicy);\n    // Read/write/approve currently share the company-user assertion. Keeping separate policy\n    // names lets Entra app-role enforcement be introduced deliberately without mislabelling GETs.\n    options.AddPolicy("TmsRead", tmsAccessPolicy);\n    options.AddPolicy("TmsWrite", tmsAccessPolicy);''')

replace_once(
    "Controllers/OrderIntakeDuplicateCheckController.cs",
    '''    [HttpGet("staging/{stagingId:guid}/comparison")]\n    [Authorize(Policy = "TmsWrite")]''',
    '''    [HttpGet("staging/{stagingId:guid}/comparison")]\n    [Authorize(Policy = "TmsRead")]''')

replace_once(
    "Controllers/OrderIntakeDuplicateCheckController.cs",
    '''            catch (Exception ex) when (ex.GetBaseException().Message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase))\n            {\n                current = null;\n            }''',
    '''            catch (Exception ex) when (IsSchemaUnavailable(ex))\n            {\n                logger.LogWarning(ex,\n                    "Staged-order comparison could not read live TransportOrders for staging item {StagingId}; comparison is unavailable and will not be reported as a new order.",\n                    stagingId);\n                return StatusCode(StatusCodes.Status503ServiceUnavailable, new\n                {\n                    code = "LiveOrderComparisonUnavailable",\n                    message = "The live order register could not be checked. Do not treat this staged order as new until comparison is available."\n                });\n            }''')

replace_once(
    "Controllers/OrderIntakeDuplicateCheckController.cs",
    '''    private static List<object> BuildChanges(OrderSnapshot current, OrderSnapshot incoming)\n    {''',
    '''    private static bool IsSchemaUnavailable(Exception exception)\n    {\n        var message = exception.GetBaseException().Message;\n        return message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase) ||\n               message.Contains("Cannot find the object", StringComparison.OrdinalIgnoreCase) ||\n               message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase) ||\n               message.Contains("no such table", StringComparison.OrdinalIgnoreCase);\n    }\n\n    private static List<object> BuildChanges(OrderSnapshot current, OrderSnapshot incoming)\n    {''')

# AREA 6: robust summary lookup and incident-grade rollback status.
replace_once(
    ".github/workflows/deploy.yml",
    '''      - name: Roll back traffic on deployment failure\n        if: ${{ failure() && env.PREVIOUS_REVISION != '' && env.TARGET_REVISION != '' }}''',
    '''      - name: Roll back traffic on deployment failure\n        id: rollback\n        if: ${{ failure() && env.PREVIOUS_REVISION != '' && env.TARGET_REVISION != '' }}''')

replace_once(
    ".github/workflows/deploy.yml",
    '''          az containerapp ingress traffic set \\\n            --name "$AZURE_CONTAINER_APP_NAME" \\\n            --resource-group "$AZURE_RESOURCE_GROUP" \\\n            --revision-weight "$PREVIOUS_REVISION=90" "$TARGET_REVISION=10"\n\n          echo "CANARY_START=$(date -u +%Y-%m-%dT%H:%M:%SZ)" >> "$GITHUB_ENV"''',
    '''          az containerapp ingress traffic set \\\n            --name "$AZURE_CONTAINER_APP_NAME" \\\n            --resource-group "$AZURE_RESOURCE_GROUP" \\\n            --revision-weight "$PREVIOUS_REVISION=90" "$TARGET_REVISION=10"\n\n          echo "TRAFFIC_SHIFTED=true" >> "$GITHUB_ENV"\n          echo "CANARY_START=$(date -u +%Y-%m-%dT%H:%M:%SZ)" >> "$GITHUB_ENV"''')

replace_once(
    ".github/workflows/deploy.yml",
    '''          revision_json="$(az containerapp revision show \\\n            --name "$AZURE_CONTAINER_APP_NAME" \\\n            --resource-group "$AZURE_RESOURCE_GROUP" \\\n            --revision "${TARGET_REVISION:-${PREVIOUS_REVISION:-}}" \\\n            --output json 2>/dev/null || echo '{}')"\n          image_digest="$(jq -r '.properties.template.containers[0].image // "unknown"' <<<"$revision_json")"\n          health_state="$(jq -r '.properties.healthState // "unknown"' <<<"$revision_json")"\n          rollback_status="not-required"\n          if [[ "${{ job.status }}" != "success" ]]; then\n            rollback_status="attempted-or-required"\n          fi''',
    '''          revision_name="${TARGET_REVISION:-${PREVIOUS_REVISION:-}}"\n          revision_json='{}'\n          if test -n "$revision_name"; then\n            revision_json="$(az containerapp revision show \\\n              --name "$AZURE_CONTAINER_APP_NAME" \\\n              --resource-group "$AZURE_RESOURCE_GROUP" \\\n              --revision "$revision_name" \\\n              --output json 2>/dev/null || echo '{}')"\n          fi\n          image_digest="$(jq -r '.properties.template.containers[0].image // "unknown"' <<<"$revision_json")"\n          health_state="$(jq -r '.properties.healthState // "unknown"' <<<"$revision_json")"\n          rollback_status="not-required"\n          if [[ "${{ job.status }}" != "success" ]]; then\n            if [[ "${TRAFFIC_SHIFTED:-false}" != "true" ]]; then\n              rollback_status="not-required-pre-traffic-shift"\n            elif [[ "${{ steps.rollback.outcome }}" == "success" ]]; then\n              rollback_status="completed-after-traffic-shift"\n            elif [[ "${{ steps.rollback.outcome }}" == "failure" ]]; then\n              rollback_status="failed-after-traffic-shift"\n            else\n              rollback_status="required-but-not-run"\n            fi\n          fi''')

# Regression tests. Existing Andover/Avonmouth coverage is intentionally retained rather than duplicated.
write("Slh.Tms.Api.Tests/BetaDayPlanBackhaulHardeningTests.cs", r'''using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class BetaDayPlanBackhaulHardeningTests
{
    [Fact]
    public async Task ExactSouthboundThreshold_IsEligibleForBackhaulAttachment()
    {
        var builder = new BetaDayPlanBuilder(new FakeRouteProvider());
        var outbound = Movement("NORTH-1", 10,
            new BetaRoutePoint("Selsey", 50.74m, -0.78m),
            new BetaRoutePoint("Darlington", 54.52m, -1.56m));
        var backhaul = Movement("BACK-1", 10,
            new BetaRoutePoint("Bedford", 52.14m, -0.46m),
            new BetaRoutePoint("Bedford South", 52.09m, -0.45m));

        var runs = await builder.BuildAsync(new DateOnly(2026, 9, 9), [outbound, backhaul], CancellationToken.None);

        var run = Assert.Single(runs);
        Assert.Equal(2, run.Orders.Count);
        Assert.Contains(run.Warnings, warning => warning.Contains("Backhaul attached: BACK-1", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NoCompatibleOutboundRun_LeavesSouthboundMovementStandaloneForPlannerReview()
    {
        var builder = new BetaDayPlanBuilder(new FakeRouteProvider());
        var outbound = Movement("NORTH-1", 10,
            new BetaRoutePoint("Selsey", 50.74m, -0.78m),
            new BetaRoutePoint("Darlington", 54.52m, -1.56m));
        var backhaul = Movement("BACK-1", 10,
            new BetaRoutePoint("Edinburgh", 55.95m, -3.19m),
            new BetaRoutePoint("Chichester", 50.84m, -0.78m));

        var runs = await builder.BuildAsync(new DateOnly(2026, 9, 9), [outbound, backhaul], CancellationToken.None);

        Assert.Equal(2, runs.Count);
        var standalone = Assert.Single(runs.Where(run => run.Orders.Any(order => order.Reference == "BACK-1")));
        Assert.Single(standalone.Orders);
        Assert.Contains(standalone.Warnings, warning => warning.Contains("remains standalone", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ConfiguredSouthboundThreshold_ChangesDirectionalGateWithoutCodeChange()
    {
        var options = new BetaOptimiserOptions { MinSouthboundLatitudeDelta = 0.10m };
        var builder = new BetaDayPlanBuilder(new FakeRouteProvider(), options);
        var outbound = Movement("NORTH-1", 10,
            new BetaRoutePoint("Selsey", 50.74m, -0.78m),
            new BetaRoutePoint("Darlington", 54.52m, -1.56m));
        var shallowSouthbound = Movement("BACK-1", 10,
            new BetaRoutePoint("Bedford", 52.14m, -0.46m),
            new BetaRoutePoint("Bedford South", 52.09m, -0.45m));

        var runs = await builder.BuildAsync(new DateOnly(2026, 9, 9), [outbound, shallowSouthbound], CancellationToken.None);

        Assert.DoesNotContain(runs.SelectMany(run => run.Warnings), warning => warning.Contains("Backhaul attached", StringComparison.OrdinalIgnoreCase));
    }

    private static BetaDayOrderInput Movement(string reference, int pallets, BetaRoutePoint collection, BetaRoutePoint delivery) =>
        new(Guid.NewGuid(), Guid.NewGuid(), reference, "TEST", "AM", "Standard", pallets, new TimeOnly(8, 0), collection, delivery);

    private sealed class FakeRouteProvider : IBetaHgvRouteProvider
    {
        public Task<BetaHgvRouteCost?> GetRouteAsync(IReadOnlyList<BetaRoutePoint> points, CancellationToken ct)
        {
            var miles = Math.Max(points.Count - 1, 0) * 10m;
            return Task.FromResult<BetaHgvRouteCost?>(new BetaHgvRouteCost(miles, (int)miles, "AzureMapsHgv"));
        }
    }
}
''')

write("Slh.Tms.Api.Tests/BetaRequestRouteCacheHardeningTests.cs", r'''using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class BetaRequestRouteCacheHardeningTests
{
    [Fact]
    public async Task UnavailableRoute_IsEvictedSoSameKeyCanRecoverWithinRequest()
    {
        var cache = new BetaRequestRouteCache();
        var calls = 0;

        var first = await cache.GetOrCreateAsync("route", () =>
        {
            calls++;
            return Task.FromResult<BetaHgvRouteCost?>(null);
        });
        var recovered = new BetaHgvRouteCost(42m, 55, "AzureMapsHgv");
        var second = await cache.GetOrCreateAsync("route", () =>
        {
            calls++;
            return Task.FromResult<BetaHgvRouteCost?>(recovered);
        });

        Assert.Null(first);
        Assert.Same(recovered, second);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task SuccessfulRoute_IsSharedForRepeatedKey()
    {
        var cache = new BetaRequestRouteCache();
        var calls = 0;
        var expected = new BetaHgvRouteCost(12m, 15, "AzureMapsHgv");

        var first = await cache.GetOrCreateAsync("route", () =>
        {
            calls++;
            return Task.FromResult<BetaHgvRouteCost?>(expected);
        });
        var second = await cache.GetOrCreateAsync("route", () =>
        {
            calls++;
            return Task.FromResult<BetaHgvRouteCost?>(new BetaHgvRouteCost(99m, 99, "unexpected"));
        });

        Assert.Same(expected, first);
        Assert.Same(first, second);
        Assert.Equal(1, calls);
    }
}
''')

write("Slh.Tms.Api.Tests/EmailOrderIntakeHardeningTests.cs", r'''using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class EmailOrderIntakeHardeningTests
{
    private readonly EmailOrderIntakeService service = new();

    [Fact]
    public void LangmeadSubdomainSender_ResolvesToLangmeadsCustomer()
    {
        var result = service.Parse(new MailboxEmailIntakeRequest(
            "langmead-subdomain", null, "info@lyonshaulage.com", "planner@ops.langmeadherbs.co.uk", "Planner",
            "Aldi order 10/09/2026", DateTimeOffset.Parse("2026-09-09T12:00:00Z"),
            "Please arrange 2 pallets for delivery to Aldi Atherstone. Product is ready for collection at 16:30.",
            null, null, null));

        var order = Assert.Single(result.Orders);
        Assert.Equal("LANGMEADS", order.Payload.GetProperty("customerCode").GetString());
        Assert.Equal("sender.domain", order.Payload.GetProperty("intakeFieldSources").GetProperty("customer").GetString());
        Assert.Equal("16:30", order.Payload.GetProperty("requestedTime").GetString());
    }

    [Fact]
    public void LookalikeDomain_DoesNotMatchLangmeadRootDomain()
    {
        var result = service.Parse(new MailboxEmailIntakeRequest(
            "langmead-lookalike", null, "info@lyonshaulage.com", "planner@langmeadherbs.co.uk.evil.example", "Planner",
            "Aldi order 10/09/2026", DateTimeOffset.Parse("2026-09-09T12:00:00Z"),
            "Please arrange 2 pallets for delivery to Aldi Atherstone. Product is ready for collection at 16:30.",
            null, null, null));

        var order = Assert.Single(result.Orders);
        Assert.Equal("ALDI", order.Payload.GetProperty("customerCode").GetString());
    }

    [Fact]
    public void MondayWholesaleDelivery_FlagsInferredSundayCollectionForPlannerReview()
    {
        var rows = new List<object?[]>
        {
            new object?[] { "COLLECTION Sefter", "", "", "", "" },
            new object?[] { "Market", "Customer", "Delivery addess", "Pallets", "Delivery Date" },
            new object?[] { "New Covent Garden", "Premier Foods", "PFW01", "2", "14/09/2026" }
        };
        var request = new MailboxEmailIntakeRequest(
            "market-monday", null, "info@lyonshaulage.com", "mariela.popova@barfoots.co.uk", "Mariela Popova",
            "Wholesale Market Pallet Bookings for delivery on 14/09/26", DateTimeOffset.Parse("2026-09-11T10:00:00Z"),
            "Wholesale Market Pallet Bookings", null, null, null);

        var order = Assert.Single(EmailOrderIntakeService.ParseBarfootsWholesaleMarketRows(
            request, "Wholesale Market Pallet Bookings.xlsx", "Sheet1", rows));

        Assert.Equal("2026-09-13", order.Payload.GetProperty("collectionDate").GetString());
        Assert.Equal("2026-09-14", order.Payload.GetProperty("deliveryDate").GetString());
        Assert.Contains(order.Warnings, warning => warning.Contains("Sunday", StringComparison.OrdinalIgnoreCase));
    }
}
''')

write("Slh.Tms.Api.Tests/WallboardPlannedRunPreparationHardeningTests.cs", r'''using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class WallboardPlannedRunPreparationHardeningTests
{
    [Fact]
    public void DispatchFallback_DoesNotThrowWhenRunHasNoStops()
    {
        var load = Load("RUN 1");
        var start = DateTimeOffset.Parse("2026-09-09T03:30:00Z");

        var exception = Record.Exception(() => WallboardPlannedRunPreparation.ApplyDispatchStartFallback(load, start));

        Assert.Null(exception);
        Assert.Empty(load.Stops);
    }

    [Fact]
    public void DispatchFallback_FillsOnlyBlankFirstStopTime()
    {
        var load = Load("RUN 1");
        load.Stops.Add(new LoadStop { Sequence = 1, Name = "Selsey" });
        var start = DateTimeOffset.Parse("2026-09-09T03:30:00Z");

        WallboardPlannedRunPreparation.ApplyDispatchStartFallback(load, start);

        Assert.Equal(start, load.Stops[0].PlannedArrivalUtc);
    }

    [Fact]
    public void OperationalSort_UsesRecoveredStartTime()
    {
        var later = Load("RUN 2");
        later.Stops.Add(new LoadStop { Sequence = 1, Name = "Runcton", PlannedArrivalUtc = DateTimeOffset.Parse("2026-09-09T06:00:00Z") });
        var recoveredEarly = Load("RUN 1");
        recoveredEarly.Stops.Add(new LoadStop { Sequence = 1, Name = "Selsey" });
        WallboardPlannedRunPreparation.ApplyDispatchStartFallback(recoveredEarly, DateTimeOffset.Parse("2026-09-09T04:00:00Z"));

        var ordered = WallboardPlannedRunPreparation.OrderByOperationalStart([later, recoveredEarly]);

        Assert.Equal(new[] { "RUN 1", "RUN 2" }, ordered.Select(load => load.Reference));
    }

    private static Load Load(string reference) => new()
    {
        Reference = reference,
        PlanningDate = new DateOnly(2026, 9, 9),
        Status = LoadStatus.Planned
    };
}
''')

write("Slh.Tms.Api.Tests/OrderIntakeComparisonHardeningTests.cs", r'''using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Slh.Tms.Api.Controllers;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class OrderIntakeComparisonHardeningTests
{
    [Fact]
    public void ComparisonGet_UsesReadPolicy()
    {
        var method = typeof(OrderIntakeDuplicateCheckController)
            .GetMethod(nameof(OrderIntakeDuplicateCheckController.CompareStagedOrder), BindingFlags.Instance | BindingFlags.Public)!;
        var policies = method.GetCustomAttributes<AuthorizeAttribute>().Select(attribute => attribute.Policy).ToList();

        Assert.Contains("TmsRead", policies);
        Assert.DoesNotContain("TmsWrite", policies);
    }

    [Fact]
    public async Task MissingLiveOrderSchema_Returns503InsteadOfClassifyingAsNewOrder()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TmsDbContext>().UseSqlite(connection).Options;
        await using var db = new TmsDbContext(options);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE "StagedImports" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "EntityType" TEXT NOT NULL,
                "IdempotencyKey" TEXT NOT NULL,
                "PayloadJson" TEXT NOT NULL,
                "Status" INTEGER NOT NULL,
                "Source" TEXT NULL,
                "ReceivedAtUtc" TEXT NOT NULL,
                "ReviewedAtUtc" TEXT NULL,
                "ReviewedBy" TEXT NULL,
                "ReviewNote" TEXT NULL,
                "RowVersion" BLOB NOT NULL DEFAULT X''
            );
            """);

        var staged = new StagedImport
        {
            EntityType = "order",
            IdempotencyKey = "comparison-schema-test",
            Source = "test",
            PayloadJson = JsonSerializer.Serialize(new
            {
                customerCode = "ALDI",
                customerPo = "PO-12345",
                poNumber = "PO-12345",
                collectionDate = "2026-09-09",
                deliveryDate = "2026-09-09",
                sellerName = "Selsey",
                stallNumber = "Aldi Atherstone",
                pallets = 2
            })
        };
        db.StagedImports.Add(staged);
        await db.SaveChangesAsync();

        var controller = new OrderIntakeDuplicateCheckController(db, NullLogger<OrderIntakeDuplicateCheckController>.Instance);
        var result = await controller.CompareStagedOrder(staged.Id, CancellationToken.None);

        var unavailable = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, unavailable.StatusCode);
        Assert.Contains("LiveOrderComparisonUnavailable", JsonSerializer.Serialize(unavailable.Value));
    }

    [Fact]
    public async Task IncomingNonBlankValue_IsReportedWhenLiveValueIsNull()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new TmsDbContext(options);
        var staged = new StagedImport
        {
            EntityType = "order",
            IdempotencyKey = "comparison-null-from",
            Source = "test",
            PayloadJson = JsonSerializer.Serialize(new
            {
                customerCode = "ALDI",
                customerPo = "PO-999",
                poNumber = "PO-999",
                collectionDate = "2026-09-09",
                deliveryDate = "2026-09-09",
                sellerName = "Selsey",
                stallNumber = "Aldi Atherstone",
                pallets = 2
            })
        };
        db.StagedImports.Add(staged);
        db.TransportOrders.Add(new TransportOrder
        {
            Reference = "PO-999",
            CustomerCode = "ALDI",
            CollectionDate = new DateOnly(2026, 9, 9),
            DeliveryDate = new DateOnly(2026, 9, 9),
            SellerName = null,
            StallNumber = "Aldi Atherstone",
            Pallets = 2
        });
        await db.SaveChangesAsync();

        var controller = new OrderIntakeDuplicateCheckController(db, NullLogger<OrderIntakeDuplicateCheckController>.Instance);
        var result = await controller.CompareStagedOrder(staged.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value);
        Assert.Contains("Collection site", json);
        Assert.Contains("Selsey", json);
    }
}
''')

print("Review hardening patch applied successfully.")
