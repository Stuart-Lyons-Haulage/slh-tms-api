using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public sealed record TachoDriverMasterSyncJobStatus(
    Guid JobId,
    string Status,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? Message,
    TachoCanonicalOrchestrationResult? Result);

internal sealed record TachoDriverMasterSyncJobClaim(Guid JobId, string Actor);
internal sealed record TachoDriverMasterSyncJobEnvelope(
    string Actor,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? StartedAtUtc = null,
    DateTimeOffset? CompletedAtUtc = null,
    string? Message = null,
    TachoCanonicalOrchestrationResult? Result = null);

public sealed class TachoDriverMasterSyncJobService(TmsDbContext db)
{
    internal const string EntityType = "tachodrivermastersyncjob";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<TachoDriverMasterSyncJobStatus> EnqueueAsync(string actor, CancellationToken ct)
    {
        var existing = await db.StagedImports.AsNoTracking()
            .Where(row => row.EntityType == EntityType &&
                          (row.Status == StagingStatus.PendingReview || row.Status == StagingStatus.Approved))
            .OrderBy(row => row.ReceivedAtUtc)
            .FirstOrDefaultAsync(ct);
        if (existing is not null) return ToStatus(existing);

        var now = DateTimeOffset.UtcNow;
        var envelope = new TachoDriverMasterSyncJobEnvelope(actor, now, Message: "Queued. The canonical cleanse runs independently of the browser request.");
        var row = new StagedImport
        {
            EntityType = EntityType,
            IdempotencyKey = $"tachodrivermastersyncjob:{Guid.NewGuid():N}",
            PayloadJson = JsonSerializer.Serialize(envelope, JsonOptions),
            Source = actor.StartsWith("system:", StringComparison.OrdinalIgnoreCase)
                ? "System canonical Driver Master queue"
                : "Manual canonical Driver Master queue",
            Status = StagingStatus.PendingReview,
            ReceivedAtUtc = now,
            ReviewedBy = actor,
            ReviewNote = envelope.Message
        };
        db.StagedImports.Add(row);
        await db.SaveChangesAsync(ct);
        return ToStatus(row, envelope);
    }

    public async Task<TachoDriverMasterSyncJobStatus?> GetAsync(Guid jobId, CancellationToken ct)
    {
        var row = await db.StagedImports.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == jobId && item.EntityType == EntityType, ct);
        return row is null ? null : ToStatus(row);
    }

    internal async Task<TachoDriverMasterSyncJobClaim?> TryClaimNextAsync(CancellationToken ct)
    {
        var row = await db.StagedImports
            .Where(item => item.EntityType == EntityType && item.Status == StagingStatus.PendingReview)
            .OrderBy(item => item.ReceivedAtUtc)
            .FirstOrDefaultAsync(ct);
        if (row is null) return null;

        var envelope = ReadEnvelope(row);
        var started = DateTimeOffset.UtcNow;
        envelope = envelope with { StartedAtUtc = started, Message = "Canonical TachoMaster Driver Master sync is running." };
        row.Status = StagingStatus.Approved;
        row.ReviewedAtUtc = started;
        row.ReviewedBy = envelope.Actor;
        row.ReviewNote = envelope.Message;
        row.PayloadJson = JsonSerializer.Serialize(envelope, JsonOptions);
        try
        {
            await db.SaveChangesAsync(ct);
            return new TachoDriverMasterSyncJobClaim(row.Id, envelope.Actor);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return null;
        }
    }

    internal async Task CompleteAsync(Guid jobId, TachoCanonicalOrchestrationResult result, CancellationToken ct)
    {
        var row = await db.StagedImports.SingleAsync(item => item.Id == jobId && item.EntityType == EntityType, ct);
        var envelope = ReadEnvelope(row) with
        {
            CompletedAtUtc = DateTimeOffset.UtcNow,
            Message = result.Message,
            Result = result
        };
        row.Status = result.Success ? StagingStatus.Promoted : StagingStatus.Failed;
        row.ReviewedAtUtc = envelope.CompletedAtUtc;
        row.ReviewNote = result.Message;
        row.PayloadJson = JsonSerializer.Serialize(envelope, JsonOptions);
        await db.SaveChangesAsync(ct);
    }

    internal async Task FailAsync(Guid jobId, Exception exception, CancellationToken ct)
    {
        var row = await db.StagedImports.SingleOrDefaultAsync(item => item.Id == jobId && item.EntityType == EntityType, ct);
        if (row is null) return;
        var message = $"Canonical TachoMaster Driver Master sync failed: {exception.GetBaseException().Message}";
        var envelope = ReadEnvelope(row) with { CompletedAtUtc = DateTimeOffset.UtcNow, Message = message };
        row.Status = StagingStatus.Failed;
        row.ReviewedAtUtc = envelope.CompletedAtUtc;
        row.ReviewNote = message;
        row.PayloadJson = JsonSerializer.Serialize(envelope, JsonOptions);
        await db.SaveChangesAsync(ct);
    }

    internal async Task RecoverInterruptedAsync(CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-30);
        var rows = await db.StagedImports
            .Where(item => item.EntityType == EntityType && item.Status == StagingStatus.Approved &&
                           (item.ReviewedAtUtc ?? item.ReceivedAtUtc) < cutoff)
            .ToListAsync(ct);
        foreach (var row in rows)
        {
            var message = "A previous canonical sync worker stopped before completion. Queue a new Force Sync if required.";
            var envelope = ReadEnvelope(row) with { CompletedAtUtc = DateTimeOffset.UtcNow, Message = message };
            row.Status = StagingStatus.Failed;
            row.ReviewedAtUtc = envelope.CompletedAtUtc;
            row.ReviewNote = message;
            row.PayloadJson = JsonSerializer.Serialize(envelope, JsonOptions);
        }
        if (rows.Count > 0) await db.SaveChangesAsync(ct);
    }

    private static TachoDriverMasterSyncJobStatus ToStatus(StagedImport row, TachoDriverMasterSyncJobEnvelope? envelope = null)
    {
        envelope ??= ReadEnvelope(row);
        return new TachoDriverMasterSyncJobStatus(
            row.Id,
            StatusName(row.Status),
            envelope.RequestedAtUtc,
            envelope.StartedAtUtc,
            envelope.CompletedAtUtc,
            envelope.Message ?? row.ReviewNote,
            envelope.Result);
    }

    private static TachoDriverMasterSyncJobEnvelope ReadEnvelope(StagedImport row)
    {
        try
        {
            return JsonSerializer.Deserialize<TachoDriverMasterSyncJobEnvelope>(row.PayloadJson, JsonOptions)
                   ?? new TachoDriverMasterSyncJobEnvelope(row.ReviewedBy ?? "unknown", row.ReceivedAtUtc);
        }
        catch (JsonException)
        {
            return new TachoDriverMasterSyncJobEnvelope(row.ReviewedBy ?? "unknown", row.ReceivedAtUtc, Message: row.ReviewNote);
        }
    }

    private static string StatusName(StagingStatus status) => status switch
    {
        StagingStatus.PendingReview => "queued",
        StagingStatus.Approved => "running",
        StagingStatus.Promoted => "succeeded",
        StagingStatus.Failed or StagingStatus.Rejected => "failed",
        _ => status.ToString().ToLowerInvariant()
    };
}

public sealed class TachoDriverMasterSyncJobWorker(
    IServiceScopeFactory scopeFactory,
    IHostEnvironment environment,
    ILogger<TachoDriverMasterSyncJobWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (environment.IsEnvironment("Testing")) return;

        try
        {
            await using var startupScope = scopeFactory.CreateAsyncScope();
            var jobs = startupScope.ServiceProvider.GetRequiredService<TachoDriverMasterSyncJobService>();
            await jobs.RecoverInterruptedAsync(stoppingToken);
            var quality = await startupScope.ServiceProvider.GetRequiredService<TachoDriverMasterSyncService>().QualityAsync(stoppingToken);
            if (quality.LatestCanonicalSyncUtc is null ||
                quality.LatestCanonicalSyncUtc < DateTimeOffset.UtcNow.AddMinutes(-15) ||
                quality.DuplicateMemberGroups > 0 || quality.DuplicateCardGroups > 0 || quality.ActiveWithoutMember > 0)
            {
                await jobs.EnqueueAsync("system:tachomaster-canonical-driver-master-startup", stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not initialise the Driver Master sync queue; the worker will continue polling.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = false;
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var jobs = scope.ServiceProvider.GetRequiredService<TachoDriverMasterSyncJobService>();
                var claim = await jobs.TryClaimNextAsync(stoppingToken);
                if (claim is not null)
                {
                    processed = true;
                    try
                    {
                        var orchestrator = scope.ServiceProvider.GetRequiredService<TachoCanonicalDriverMasterOrchestrator>();
                        var result = await orchestrator.RunAsync(claim.Actor, stoppingToken);
                        await jobs.CompleteAsync(claim.JobId, result, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Queued canonical Driver Master sync {JobId} failed unexpectedly.", claim.JobId);
                        await jobs.FailAsync(claim.JobId, ex, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Driver Master sync queue poll failed.");
            }

            if (!processed)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            }
        }
    }
}
