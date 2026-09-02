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
    DistributedLeaseManager leases,
    ILogger<TachoCanonicalDriverMasterOrchestrator> logger)
{
    public async Task<TachoCanonicalOrchestrationResult> RunAsync(string actor, CancellationToken ct)
    {
        await using var lease = await leases.TryAcquireAsync(IntegrationLeaseNames.TachoMaster, TimeSpan.FromMinutes(60), ct);
        if (lease is null)
        {
            var now = DateTimeOffset.UtcNow;
            var canonicalResult = new TachoDriverMasterSyncResult(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                "Canonical TachoMaster sync skipped because another distributed writer currently holds the integration lease.", now);
            var enrichment = new IntegrationSyncResult("TachoMaster", false, now, canonicalResult.Message);
            return new TachoCanonicalOrchestrationResult(false, canonicalResult, enrichment, now, canonicalResult.Message);
        }
        return await RunCoreAsync(actor, ct);
    }

    private async Task<TachoCanonicalOrchestrationResult> RunCoreAsync(string actor, CancellationToken ct)
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
            enrichment = await integration.SyncTachoMasterCoreAsync($"{actor}:identity-enrichment", ct);
            if (!enrichment.Success)
                logger.LogWarning("TachoMaster identity-enrichment pass did not complete before canonical sync: {Message}", enrichment.Message);

            canonicalResult = await canonical.SyncCoreAsync(actor, ct);
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
