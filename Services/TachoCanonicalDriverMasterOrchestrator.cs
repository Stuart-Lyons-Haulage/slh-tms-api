using System.Text.Json;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public sealed record TachoCanonicalOrchestrationResult(
    bool Success,
    TachoDriverMasterSyncResult Canonical,
    IntegrationSyncResult IdentityEnrichment,
    DateTimeOffset CompletedAtUtc,
    string Message);

/// <summary>
/// Single authority for manual and scheduled TachoMaster Driver Master cleansing.
/// The normal integration pass runs first because it can resolve an existing TMS driver by
/// Member Code -> Tacho Card -> Employee Number -> Name and persist the strong Tacho identity.
/// The canonical pass then consolidates/archives against the live TachoMaster worker directory.
/// </summary>
public sealed class TachoCanonicalDriverMasterOrchestrator(
    TmsDbContext db,
    IntegrationSyncCoordinator integration,
    TachoDriverMasterSyncService canonical,
    DriverMasterClassificationService classification,
    ILogger<TachoCanonicalDriverMasterOrchestrator> logger)
{
    public async Task<TachoCanonicalOrchestrationResult> RunAsync(string actor, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        IntegrationSyncResult enrichment;
        TachoDriverMasterSyncResult canonicalResult;

        try
        {
            await classification.ApplyAsync(actor, ct);

            // This pass is intentionally first. It adds Employee Number as the safe fallback
            // between card and name, then persists Member Code/Card so the canonical pass can
            // use strong identities for consolidation.
            enrichment = await integration.SyncTachoMasterAsync($"{actor}:identity-enrichment", ct);
            if (!enrichment.Success)
                logger.LogWarning("TachoMaster identity-enrichment pass did not complete before canonical sync: {Message}", enrichment.Message);

            canonicalResult = await canonical.SyncAsync(actor, ct);
            if (canonicalResult.Success)
                await classification.ApplyAsync(actor, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "TachoMaster canonical Driver Master orchestration failed.");
            enrichment = new IntegrationSyncResult("TachoMaster", false, DateTimeOffset.UtcNow, "Identity-enrichment pass did not complete.");
            canonicalResult = new TachoDriverMasterSyncResult(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                ex.GetBaseException().Message, DateTimeOffset.UtcNow);
        }

        var completed = DateTimeOffset.UtcNow;
        var success = canonicalResult.Success;
        var message = success
            ? $"Canonical TachoMaster Driver Master completed. {canonicalResult.Message}"
            : $"Canonical TachoMaster Driver Master failed safely. {canonicalResult.Message}";

        // Record every orchestration attempt, including provider failures and safety-floor aborts.
        db.StagedImports.Add(new StagedImport
        {
            EntityType = "tachodrivermasterorchestration",
            IdempotencyKey = $"tachodrivermasterorchestration:{completed:yyyyMMddHHmmss}:{Guid.NewGuid():N}",
            PayloadJson = JsonSerializer.Serialize(new
            {
                startedAtUtc = started,
                completedAtUtc = completed,
                success,
                identityOrder = new[] { "TachoMaster Member Code", "Tacho Card Number", "Employee Number", "Unique compatible name" },
                identityEnrichment = new
                {
                    enrichment.Success,
                    enrichment.CompletedAtUtc,
                    enrichment.Message,
                    enrichment.Changed
                },
                canonical = new
                {
                    canonicalResult.Success,
                    canonicalResult.SourceWorkers,
                    canonicalResult.CanonicalActiveDrivers,
                    canonicalResult.Created,
                    canonicalResult.Updated,
                    canonicalResult.DuplicateRecordsRetired,
                    canonicalResult.DriversArchivedNotInTachoMaster,
                    canonicalResult.MatchedByMember,
                    canonicalResult.MatchedByCard,
                    canonicalResult.MatchedByUniqueName,
                    canonicalResult.SameNameDifferentIdentityGroups,
                    canonicalResult.WorkersWithoutCard,
                    canonicalResult.Message
                }
            }),
            Source = actor.StartsWith("system:", StringComparison.OrdinalIgnoreCase)
                ? "Scheduled TachoMaster canonical Driver Master"
                : "Manual TachoMaster canonical Driver Master",
            Status = success ? StagingStatus.Promoted : StagingStatus.Rejected,
            ReceivedAtUtc = started,
            ReviewedAtUtc = completed,
            ReviewedBy = actor,
            ReviewNote = message
        });
        await db.SaveChangesAsync(ct);

        return new TachoCanonicalOrchestrationResult(success, canonicalResult, enrichment, completed, message);
    }
}

/// <summary>
/// Runs the full canonical Driver Master once per day at 04:30 Europe/London.
/// DST is resolved from the local date each time, so the UTC execution time moves correctly.
/// </summary>
public sealed class TachoCanonicalDriverMasterDailyBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<TachoCanonicalDriverMasterDailyBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            var next = NextLondonRun(now);
            var delay = next - now;
            logger.LogInformation("Next canonical TachoMaster Driver Master sync scheduled for {NextRunUtc} UTC ({NextRunLondon} Europe/London).",
                next, TimeZoneInfo.ConvertTime(next, LondonTimeZone()));

            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }

            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var orchestrator = scope.ServiceProvider.GetRequiredService<TachoCanonicalDriverMasterOrchestrator>();
                var result = await orchestrator.RunAsync("system:tachomaster-canonical-driver-master-daily", stoppingToken);
                if (result.Success) logger.LogInformation("{Message}", result.Message);
                else logger.LogWarning("{Message}", result.Message);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduled daily TachoMaster canonical Driver Master sync failed unexpectedly.");
            }
        }
    }

    internal static DateTimeOffset NextLondonRun(DateTimeOffset utcNow)
    {
        var zone = LondonTimeZone();
        var londonNow = TimeZoneInfo.ConvertTime(utcNow, zone);
        var localDate = DateOnly.FromDateTime(londonNow.DateTime);
        var candidateLocal = localDate.ToDateTime(new TimeOnly(4, 30), DateTimeKind.Unspecified);
        if (candidateLocal <= londonNow.DateTime)
            candidateLocal = localDate.AddDays(1).ToDateTime(new TimeOnly(4, 30), DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(candidateLocal, zone), TimeSpan.Zero);
    }

    private static TimeZoneInfo LondonTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/London"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time"); }
    }
}
