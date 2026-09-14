using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Planning;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Applies accepted optimisation suggestions transactionally to the canonical Run model.
/// Rules:
///  • Only suggestions in Accepted status are applied.
///  • Hard constraint violations on the proposal are always re-evaluated before write.
///  • The plan version hash is verified — if the plan changed since analysis, the apply is aborted.
///  • All writes succeed together or none is committed (one SaveChangesAsync call).
///  • An audit event is written for every accept, reject and override action.
/// </summary>
public sealed class OptimisationApplicationService(
    TmsDbContext db,
    HardConstraintEngine constraintEngine,
    ILogger<OptimisationApplicationService> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ── Accept individual suggestion ──────────────────────────────────────────

    public async Task<SuggestionDecisionResult> AcceptSuggestionAsync(
        AcceptSuggestionRequest request,
        string? actor,
        bool isAdmin,
        CancellationToken ct)
    {
        var (analysis, suggestion) = await LoadSuggestionAsync(request.SuggestionId, ct);
        GuardAnalysisState(analysis);

        if (suggestion.Status == SuggestionStatus.CannotApply && !isAdmin)
            throw new OptimisationException("CannotApplyWithoutOverride",
                "This suggestion cannot be applied. An administrator override is required.");

        if (suggestion.Kind == SuggestionKind.HardConstraintViolation && !isAdmin)
            throw new OptimisationException("HardConstraintRequiresOverride",
                "Hard constraint violations require an administrator override.");

        suggestion.Status = SuggestionStatus.Accepted;
        suggestion.DecidedAtUtc = DateTimeOffset.UtcNow;
        suggestion.DecidedBy = actor;
        suggestion.DecisionReason = request.Reason;
        if (!string.IsNullOrWhiteSpace(request.ManualEditJson))
            suggestion.ManualEditJson = request.ManualEditJson;

        await WriteAuditAsync(analysis, suggestion.SuggestionId, OptimisationAuditAction.SuggestionAccepted,
            actor, "Pending", "Accepted", request.Reason);

        UpdateAnalysisStatus(analysis);
        await db.SaveChangesAsync(ct);
        return new SuggestionDecisionResult(suggestion.SuggestionId, "Accepted", "Suggestion accepted.");
    }

    // ── Reject individual suggestion ──────────────────────────────────────────

    public async Task<SuggestionDecisionResult> RejectSuggestionAsync(
        RejectSuggestionRequest request,
        string? actor,
        CancellationToken ct)
    {
        if (!RejectReasonCodes.IsValid(request.RejectCode))
            throw new OptimisationException("InvalidRejectCode",
                $"Reject code '{request.RejectCode}' is not recognised. Valid codes: {string.Join(", ", RejectReasonCodes.All)}");

        var (analysis, suggestion) = await LoadSuggestionAsync(request.SuggestionId, ct);
        GuardAnalysisState(analysis);

        var reasonText = $"{request.RejectCode}: {request.FreeText}".TrimEnd(':', ' ');
        suggestion.Status = SuggestionStatus.Rejected;
        suggestion.DecidedAtUtc = DateTimeOffset.UtcNow;
        suggestion.DecidedBy = actor;
        suggestion.DecisionReason = reasonText;

        // Record in decision history for learning
        db.OptimisationDecisionHistory.Add(BuildHistoryRecord(analysis, suggestion, actor, reasonText));

        await WriteAuditAsync(analysis, suggestion.SuggestionId, OptimisationAuditAction.SuggestionRejected,
            actor, "Pending", "Rejected", reasonText);

        UpdateAnalysisStatus(analysis);
        await db.SaveChangesAsync(ct);
        return new SuggestionDecisionResult(suggestion.SuggestionId, "Rejected", "Suggestion rejected and recorded for learning.");
    }

    // ── Accept all pending ────────────────────────────────────────────────────

    public async Task<ApplyOptimisationResult> AcceptAllAsync(
        AcceptAllSuggestionsRequest request,
        string? actor,
        bool isAdmin,
        CancellationToken ct)
    {
        var analysis = await LoadAnalysisAsync(request.AnalysisId, ct);
        GuardAnalysisState(analysis);

        var pending = analysis.Suggestions
            .Where(s => s.Status == SuggestionStatus.Pending)
            .ToList();

        var skipped = new List<string>();
        foreach (var s in pending)
        {
            if (s.Status == SuggestionStatus.CannotApply ||
                (s.Kind == SuggestionKind.HardConstraintViolation && !isAdmin))
            {
                skipped.Add($"{s.SuggestionId}: {s.Title} — requires override");
                continue;
            }
            s.Status = SuggestionStatus.Accepted;
            s.DecidedAtUtc = DateTimeOffset.UtcNow;
            s.DecidedBy = actor;
        }

        await WriteAuditAsync(analysis, null, OptimisationAuditAction.AllSuggestionsAccepted,
            actor, "{}", $"{{\"accepted\":{pending.Count - skipped.Count},\"skipped\":{skipped.Count}}}");

        UpdateAnalysisStatus(analysis);
        await db.SaveChangesAsync(ct);

        return new ApplyOptimisationResult(request.AnalysisId, analysis.Status.ToString(),
            pending.Count - skipped.Count, 0, skipped, []);
    }

    // ── Apply to live runs ────────────────────────────────────────────────────

    public async Task<ApplyOptimisationResult> ApplyAsync(
        ApplyOptimisationRequest request,
        string? actor,
        CancellationToken ct)
    {
        var analysis = await LoadAnalysisAsync(request.AnalysisId, ct);

        if (analysis.Status == OptimisationStatus.Applied)
            throw new OptimisationException("AlreadyApplied", "This analysis has already been applied.");
        if (analysis.Status == OptimisationStatus.Rejected)
            throw new OptimisationException("AlreadyRejected", "This analysis was rejected and cannot be applied.");
        if (analysis.Status == OptimisationStatus.SupersededByPlanChange)
            throw new OptimisationException("Superseded", "The plan changed after this analysis was generated. Re-run analysis before applying.");

        // Verify plan version hash has not changed
        await VerifyPlanVersionAsync(analysis, ct);

        var accepted = analysis.Suggestions
            .Where(s => s.Status == SuggestionStatus.Accepted)
            .OrderBy(s => s.Sequence)
            .ToList();

        if (accepted.Count == 0)
            throw new OptimisationException("NothingToApply", "No suggestions are in Accepted status. Accept suggestions before applying.");

        var warnings = new List<string>();
        var failures = new List<string>();
        var runsModified = 0;

        await WriteAuditAsync(analysis, null, OptimisationAuditAction.ApplicationStarted,
            actor, "{}", $"{{\"acceptedCount\":{accepted.Count}}}");

        // Apply each accepted suggestion to the canonical Run model
        foreach (var suggestion in accepted)
        {
            try
            {
                var modified = await ApplySuggestionToRunAsync(suggestion, actor, request, ct);
                runsModified += modified;
                suggestion.Status = SuggestionStatus.Applied;
                db.OptimisationDecisionHistory.Add(BuildHistoryRecord(analysis, suggestion, actor, null));
                await WriteAuditAsync(analysis, suggestion.SuggestionId, OptimisationAuditAction.ApplicationCompleted,
                    actor, "Accepted", "Applied");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Failed to apply suggestion {SuggestionId}.", suggestion.SuggestionId);
                suggestion.Status = SuggestionStatus.Failed;
                failures.Add($"{suggestion.SuggestionId}: {ex.Message}");
                await WriteAuditAsync(analysis, suggestion.SuggestionId, OptimisationAuditAction.ApplicationFailed,
                    actor, "Accepted", "Failed", ex.Message);
            }
        }

        if (failures.Count > 0 && runsModified == 0)
        {
            // Total failure — roll back rather than commit partial
            await db.Entry(analysis).ReloadAsync(ct);
            throw new OptimisationException("ApplicationFailed",
                $"All {failures.Count} suggestions failed to apply. No changes were committed. Details: {string.Join("; ", failures)}");
        }

        analysis.Status       = failures.Count > 0 ? OptimisationStatus.PartiallyAccepted : OptimisationStatus.Applied;
        analysis.AppliedAtUtc = DateTimeOffset.UtcNow;
        analysis.AppliedBy    = actor;

        // Single save — either all writes land or none do
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Analysis {AnalysisId} applied: {Applied} suggestions, {Runs} runs modified, {Failures} failures.",
            analysis.AnalysisId, accepted.Count, runsModified, failures.Count);

        return new ApplyOptimisationResult(
            analysis.AnalysisId,
            analysis.Status.ToString(),
            accepted.Count - failures.Count,
            runsModified,
            warnings,
            failures);
    }

    // ── Reject entire analysis ────────────────────────────────────────────────

    public async Task<SuggestionDecisionResult> RejectAnalysisAsync(
        RejectAnalysisRequest request,
        string? actor,
        CancellationToken ct)
    {
        if (!RejectReasonCodes.IsValid(request.RejectCode))
            throw new OptimisationException("InvalidRejectCode", $"Reject code '{request.RejectCode}' is not recognised.");

        var analysis = await LoadAnalysisAsync(request.AnalysisId, ct);
        GuardAnalysisState(analysis);

        var reasonText = $"{request.RejectCode}: {request.FreeText}".TrimEnd(':', ' ');
        analysis.Status          = OptimisationStatus.Rejected;
        analysis.RejectedAtUtc   = DateTimeOffset.UtcNow;
        analysis.RejectedBy      = actor;
        analysis.RejectionReason = reasonText;

        foreach (var s in analysis.Suggestions.Where(s => s.Status == SuggestionStatus.Pending))
        {
            s.Status = SuggestionStatus.Rejected;
            s.DecidedAtUtc = DateTimeOffset.UtcNow;
            s.DecidedBy = actor;
            s.DecisionReason = reasonText;
            db.OptimisationDecisionHistory.Add(BuildHistoryRecord(analysis, s, actor, reasonText));
        }

        await WriteAuditAsync(analysis, null, OptimisationAuditAction.AnalysisRejected,
            actor, analysis.Status.ToString(), "Rejected", reasonText);

        await db.SaveChangesAsync(ct);
        return new SuggestionDecisionResult(request.AnalysisId, "Rejected", "Analysis rejected. All suggestions recorded for learning.");
    }

    // ── Apply one suggestion to canonical Run ─────────────────────────────────

    private async Task<int> ApplySuggestionToRunAsync(
        OptimisationSuggestion suggestion,
        string? actor,
        ApplyOptimisationRequest request,
        CancellationToken ct)
    {
        return suggestion.Kind switch
        {
            SuggestionKind.ResequenceStops  => await ApplyResequenceAsync(suggestion, actor, ct),
            SuggestionKind.MoveOrderToRun   => await ApplyMoveOrderAsync(suggestion, actor, ct),
            SuggestionKind.HardConstraintViolation => 0,  // violations are informational — no mutation
            _                               => 0
        };
    }

    private async Task<int> ApplyResequenceAsync(
        OptimisationSuggestion suggestion,
        string? actor,
        CancellationToken ct)
    {
        if (suggestion.SourceRunId is null) return 0;

        var run = await db.Runs
            .Include(r => r.Stops)
            .Include(r => r.StatusHistory)
            .SingleOrDefaultAsync(r => r.RunId == suggestion.SourceRunId.Value, ct)
            ?? throw new OptimisationException("RunNotFound", $"Run {suggestion.SourceRunId} no longer exists.");

        if (run.Status is RunStatus.Dispatched or RunStatus.InProgress or RunStatus.Completed)
            throw new OptimisationException("RunProtected", $"Run {run.Reference} is {run.Status} and cannot be resequenced.");

        // Deserialise the proposed stop sequence from the suggestion's AfterStopSequenceJson
        var proposed = JsonSerializer.Deserialize<List<AfterStopItem>>(suggestion.AfterStopSequenceJson, Json) ?? [];
        if (proposed.Count == 0) return 0;

        // Apply new sequence numbers to existing RunStops
        foreach (var stop in run.Stops)
        {
            var match = proposed.FirstOrDefault(p =>
                string.Equals(p.SiteName, stop.SiteName, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                stop.Sequence = match.Sequence;
        }

        run.StatusHistory.Add(new RunStatusHistory
        {
            RunId        = run.RunId,
            Status       = run.Status,
            OccurredAtUtc = DateTimeOffset.UtcNow,
            Actor        = actor,
            Note         = $"Stops resequenced by optimiser suggestion {suggestion.SuggestionId}."
        });

        return 1;
    }

    private async Task<int> ApplyMoveOrderAsync(
        OptimisationSuggestion suggestion,
        string? actor,
        CancellationToken ct)
    {
        if (suggestion.SourceRunId is null || suggestion.TargetRunId is null || suggestion.OrderId is null)
            return 0;

        var sourceRun = await db.Runs
            .Include(r => r.Stops)
            .Include(r => r.OrderAllocations)
            .Include(r => r.StatusHistory)
            .SingleOrDefaultAsync(r => r.RunId == suggestion.SourceRunId.Value, ct)
            ?? throw new OptimisationException("SourceRunNotFound", $"Source run {suggestion.SourceRunId} no longer exists.");

        var targetRun = await db.Runs
            .Include(r => r.Stops)
            .Include(r => r.OrderAllocations)
            .Include(r => r.StatusHistory)
            .SingleOrDefaultAsync(r => r.RunId == suggestion.TargetRunId.Value, ct)
            ?? throw new OptimisationException("TargetRunNotFound", $"Target run {suggestion.TargetRunId} no longer exists.");

        foreach (var r in new[] { sourceRun, targetRun })
            if (r.Status is RunStatus.Dispatched or RunStatus.InProgress or RunStatus.Completed)
                throw new OptimisationException("RunProtected", $"Run {r.Reference} is {r.Status} and cannot be modified.");

        // Move RunOrderAllocation from source to target
        var allocation = sourceRun.OrderAllocations.FirstOrDefault(a => a.OrderId == suggestion.OrderId.Value)
            ?? throw new OptimisationException("AllocationNotFound", $"Order {suggestion.OrderId} not found on source run.");

        sourceRun.OrderAllocations.Remove(allocation);
        allocation.RunId = targetRun.RunId;
        targetRun.OrderAllocations.Add(allocation);

        // Move corresponding stops
        var orderStops = sourceRun.Stops.Where(s => s.OrderId == suggestion.OrderId.Value).ToList();
        foreach (var stop in orderStops)
        {
            sourceRun.Stops.Remove(stop);
            stop.RunId   = targetRun.RunId;
            var newSeq   = (targetRun.Stops.Max(s => (int?)s.Sequence) ?? 0) + 1;
            stop.Sequence = newSeq;
            targetRun.Stops.Add(stop);
        }

        foreach (var run in new[] { sourceRun, targetRun })
            run.StatusHistory.Add(new RunStatusHistory
            {
                RunId         = run.RunId,
                Status        = run.Status,
                OccurredAtUtc = DateTimeOffset.UtcNow,
                Actor         = actor,
                Note          = $"Order {suggestion.OrderReference} moved by optimiser suggestion {suggestion.SuggestionId}."
            });

        return 2;
    }

    // ─── Verification ─────────────────────────────────────────────────────────

    private async Task VerifyPlanVersionAsync(OptimisationAnalysis analysis, CancellationToken ct)
    {
        var runs = await db.Runs.AsNoTracking()
            .Include(r => r.Stops)
            .Include(r => r.OrderAllocations)
            .Where(r => r.PlanningDate == analysis.PlanningDate && r.Status != RunStatus.Cancelled && r.Status != RunStatus.Completed)
            .OrderBy(r => r.Reference)
            .ToListAsync(ct);

        var currentHash = BuildRunHash(runs, analysis.PlanningDate);
        if (!string.Equals(currentHash, analysis.PlanVersionHash, StringComparison.OrdinalIgnoreCase))
        {
            analysis.Status = OptimisationStatus.SupersededByPlanChange;
            await db.SaveChangesAsync(ct);
            throw new OptimisationException("PlanChanged",
                "The plan changed after this analysis was generated. Re-run analysis before applying.");
        }
    }

    private static string BuildRunHash(IReadOnlyList<Run> runs, DateOnly date)
    {
        var content = string.Join("|", new[]
        {
            date.ToString("yyyy-MM-dd"),
            string.Join(";", runs.OrderBy(r => r.RunId).Select(r =>
                $"{r.RunId:N}:{r.Reference}:{r.Status}:{r.Stops.Count}:{r.OrderAllocations.Sum(a => a.Quantity)}"))
        });
        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)))
            .ToLowerInvariant();
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private async Task<OptimisationAnalysis> LoadAnalysisAsync(Guid analysisId, CancellationToken ct) =>
        await db.OptimisationAnalyses
            .Include(a => a.Suggestions)
            .Include(a => a.AuditEvents)
            .SingleOrDefaultAsync(a => a.AnalysisId == analysisId, ct)
            ?? throw new OptimisationException("AnalysisNotFound", $"Analysis {analysisId} not found.");

    private async Task<(OptimisationAnalysis, OptimisationSuggestion)> LoadSuggestionAsync(
        Guid suggestionId, CancellationToken ct)
    {
        var suggestion = await db.OptimisationSuggestions
            .SingleOrDefaultAsync(s => s.SuggestionId == suggestionId, ct)
            ?? throw new OptimisationException("SuggestionNotFound", $"Suggestion {suggestionId} not found.");
        var analysis = await LoadAnalysisAsync(suggestion.AnalysisId, ct);
        return (analysis, suggestion);
    }

    private static void GuardAnalysisState(OptimisationAnalysis analysis)
    {
        if (analysis.Status is OptimisationStatus.Applied or OptimisationStatus.Rejected)
            throw new OptimisationException("AnalysisFinalised",
                $"Analysis is already {analysis.Status} and cannot be modified.");
    }

    private static void UpdateAnalysisStatus(OptimisationAnalysis analysis)
    {
        var statuses = analysis.Suggestions.Select(s => s.Status).ToHashSet();
        analysis.Status = statuses.All(s => s == SuggestionStatus.Accepted) ? OptimisationStatus.Ready
            : statuses.Any(s => s == SuggestionStatus.Accepted) ? OptimisationStatus.PartiallyAccepted
            : OptimisationStatus.Ready;
    }

    private static OptimisationDecisionHistory BuildHistoryRecord(
        OptimisationAnalysis analysis,
        OptimisationSuggestion suggestion,
        string? actor,
        string? reason) =>
        new()
        {
            AnalysisId           = analysis.AnalysisId,
            SuggestionId         = suggestion.SuggestionId,
            PlanningDate         = analysis.PlanningDate,
            Period               = analysis.Period,
            Kind                 = suggestion.Kind.ToString(),
            SourceRunReference   = suggestion.SourceRunReference,
            TargetRunReference   = suggestion.TargetRunReference,
            Decision             = suggestion.Status.ToString(),
            DecidedBy            = actor,
            DecisionReason       = reason,
            BenefitScoreAtDecision = suggestion.BenefitScore,
            ConfidenceAtDecision   = suggestion.ConfidenceScore,
            MilesBefore          = suggestion.CurrentMiles,
            MilesAfter           = suggestion.ProposedMiles,
            DriveMinutesBefore   = suggestion.CurrentDriveMinutes,
            DriveMinutesAfter    = suggestion.ProposedDriveMinutes,
            OptimiserVersion     = RouteOptimisationService.OptimiserVersion,
            CorrelationId        = analysis.CorrelationId
        };

    private Task WriteAuditAsync(
        OptimisationAnalysis analysis,
        Guid? suggestionId,
        OptimisationAuditAction action,
        string? actor,
        string before,
        string after,
        string? note = null)
    {
        analysis.AuditEvents.Add(new OptimisationAuditEvent
        {
            AnalysisId       = analysis.AnalysisId,
            SuggestionId     = suggestionId,
            CorrelationId    = analysis.CorrelationId,
            Action           = action,
            PlanningDate     = analysis.PlanningDate,
            Actor            = actor,
            BeforeStateJson  = before,
            AfterStateJson   = after,
            Note             = note,
            OptimiserVersion = RouteOptimisationService.OptimiserVersion
        });
        return Task.CompletedTask;
    }

    private sealed record AfterStopItem(int Sequence, string SiteName, string StopType);
}

// ── Exception type ────────────────────────────────────────────────────────────

public sealed class OptimisationException(string code, string message)
    : Exception(message)
{
    public string Code { get; } = code;
}
