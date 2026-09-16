using System.Text.Json;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;

namespace Slh.Tms.Api.Services;

public sealed record TachoCanonicalOrchestrationResult(
    bool Success,
    TachoDriverMasterSyncResult Canonical,
    IntegrationSyncResult IdentityEnrichment,
    DateTimeOffset CompletedAtUtc,
    string Message);

/// <summary>
/// Single authority for manual and scheduled TachoMaster Driver Master cleansing.
/// TachoMaster Member Code is the canonical person identity. Card number, employee number and
/// compatible name are supporting evidence only and must never merge two different Member Codes.
/// </summary>
public sealed class TachoCanonicalDriverMasterOrchestrator(
    TmsDbContext db,
    IntegrationSyncCoordinator integration,
    DriverMasterClassificationService classification,
    DistributedLeaseManager leases,
    TachoMasterClient tachoMaster,
    TachoMasterOptions tachoMasterOptions,
    IHttpClientFactory httpClientFactory,
    ILogger<TachoCanonicalDriverMasterOrchestrator> logger)
{
    public async Task<TachoCanonicalOrchestrationResult> RunAsync(string actor, CancellationToken ct)
    {
        await using var lease = await leases.TryAcquireAsync(IntegrationLeaseNames.TachoMaster, TimeSpan.FromMinutes(2), ct);
        if (lease is null)
        {
            var now = DateTimeOffset.UtcNow;
            var canonicalResult = new TachoDriverMasterSyncResult(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                "Canonical TachoMaster sync skipped because another distributed writer currently holds the integration lease.", now);
            var enrichment = new IntegrationSyncResult("TachoMaster", false, now, canonicalResult.Message);
            return new TachoCanonicalOrchestrationResult(false, canonicalResult, enrichment, now, canonicalResult.Message);
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.LostToken);
        return await RunCoreAsync(actor, linked.Token);
    }

    private async Task<TachoCanonicalOrchestrationResult> RunCoreAsync(string actor, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        IntegrationSyncResult enrichment;
        TachoDriverMasterSyncResult canonicalResult;

        try
        {
            await classification.ApplyAsync(actor, ct);
            enrichment = await integration.SyncTachoMasterCoreAsync($"{actor}:identity-enrichment", ct);
            if (!enrichment.Success)
                logger.LogWarning("TachoMaster identity-enrichment pass did not complete before canonical sync: {Message}", enrichment.Message);

            canonicalResult = await TachoMemberCodeDriverMasterSync.RunAsync(
                db,
                tachoMaster,
                httpClientFactory,
                tachoMasterOptions,
                logger,
                actor,
                ct);

            if (canonicalResult.Success)
                await classification.ApplyAsync(actor, ct);

            try
            {
                db.ChangeTracker.Clear();
                var masterRepair = await MasterDataDuplicateConsolidation.RunAsync(db, actor, logger, ct);
                logger.LogInformation(
                    "Master duplicate consolidation completed: {SiteDuplicates} site duplicate(s), {DriverDuplicates} driver duplicate(s), {MarketDuplicates} market duplicate(s), {VehicleDuplicates} vehicle duplicate(s), {TrailerDuplicates} trailer duplicate(s), {FuelRecovered} vehicle fuel detail recovery/recoveries.",
                    masterRepair.Sites.ArchivedDuplicates,
                    masterRepair.DriverDuplicatesArchived,
                    masterRepair.MarketDuplicatesArchived,
                    masterRepair.VehicleDuplicatesArchived,
                    masterRepair.TrailerDuplicatesArchived,
                    masterRepair.VehicleFuelDetailsRecovered);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                db.ChangeTracker.Clear();
                logger.LogWarning(ex, "Master duplicate consolidation failed; Driver Master result is retained and the repair will retry on the next canonical pass.");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "TachoMaster Member Code canonical Driver Master orchestration failed.");
            enrichment = new IntegrationSyncResult("TachoMaster", false, DateTimeOffset.UtcNow, "Identity-enrichment pass did not complete.");
            canonicalResult = new TachoDriverMasterSyncResult(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                ex.GetBaseException().Message, DateTimeOffset.UtcNow);
        }

        var completed = DateTimeOffset.UtcNow;
        var success = canonicalResult.Success;
        var message = success
            ? $"Canonical TachoMaster Member Code Driver Master completed. {canonicalResult.Message}"
            : $"Canonical TachoMaster Member Code Driver Master failed safely. {canonicalResult.Message}";

        db.StagedImports.Add(new StagedImport
        {
            EntityType = "tachodrivermasterorchestration",
            IdempotencyKey = $"tachodrivermasterorchestration:{completed:yyyyMMddHHmmss}:{Guid.NewGuid():N}",
            PayloadJson = JsonSerializer.Serialize(new
            {
                startedAtUtc = started,
                completedAtUtc = completed,
                success,
                identityAuthority = "TachoMaster Member Code",
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
                ? "Scheduled TachoMaster Member Code canonical Driver Master"
                : "Manual TachoMaster Member Code canonical Driver Master",
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
