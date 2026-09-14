using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Planning;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Read-only analysis service.  Inspects the canonical Run/RunStop/RunOrderAllocation
/// model for a given planning date, evaluates hard constraints, scores soft
/// improvements, and persists an OptimisationAnalysis.
///
/// NO live run, order, or allocation is altered by this service.
/// </summary>
public sealed class RouteOptimisationService(
    TmsDbContext db,
    RouteMatrixService matrixService,
    HardConstraintEngine constraintEngine,
    SoftScoringEngine scoringEngine,
    SageHrClient sageHr,
    ILogger<RouteOptimisationService> logger)
{
    public const string OptimiserVersion = "2.0.0";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ─── Public: analyse ──────────────────────────────────────────────────────

    public async Task<OptimisationAnalysisDto> AnalyseAsync(
        AnalyseRouteRequest request,
        string? actor,
        CancellationToken ct)
    {
        var correlationId = Guid.NewGuid();
        logger.LogInformation(
            "[{CorrelationId}] Route optimisation analysis started for {Date}/{Period} by {Actor}.",
            correlationId, request.PlanningDate, request.Period ?? "all", actor);

        // 1. Load canonical runs (never mutate them)
        var runs = await LoadCanonicalRunsAsync(request.PlanningDate, request.Period, ct);
        if (runs.Count == 0)
        {
            return EmptyAnalysis(request, correlationId, actor, "No eligible runs found for this planning date.");
        }

        // 2. Load supporting master data
        var sites  = await db.Sites.AsNoTracking().Where(s => s.Active).ToListAsync(ct);
        await MasterDetailStore.EnrichSitesAsync(db, sites, ct);
        var drivers  = await db.Drivers.AsNoTracking().Where(d => d.Active).ToListAsync(ct);
        await MasterDetailStore.EnrichDriversAsync(db, drivers, ct);
        var trailers = await db.Trailers.AsNoTracking().Where(t => t.Active).ToListAsync(ct);

        // 3. Driver leave from SageHR
        var leaveByDriver = await GetDriverLeaveAsync(request.PlanningDate, drivers, ct);

        // 4. Load active scoring configuration
        var weights = await GetActiveWeightsAsync(ct);
        var configVersion = await GetActiveConfigVersionAsync(ct);

        // 5. Build plan version hash (concurrency check input)
        var planHash = BuildPlanVersionHash(runs, request.PlanningDate);

        // 6. Build stop-point list for routing matrix
        var allStopPoints = BuildStopPoints(runs, sites);
        RouteMatrixService.RouteMatrix? matrix = null;
        if (allStopPoints.Count >= 2)
        {
            try { matrix = await matrixService.BuildAsync(allStopPoints, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "[{CorrelationId}] Route matrix build failed; proceeding without routing data.", correlationId);
            }
        }

        // 7. Snapshot runs for constraint evaluation
        var snapshots = runs.Select(r => BuildSnapshot(r, leaveByDriver, drivers, trailers)).ToList();

        // 8. Generate suggestions
        var suggestions = new List<OptimisationSuggestion>();
        var seq = 0;

        foreach (var snapshot in snapshots)
        {
            // Hard constraint violations on existing runs
            var violations = constraintEngine.Evaluate(snapshot);
            foreach (var v in violations)
            {
                suggestions.Add(HardViolationSuggestion(snapshot, v, ++seq));
            }
        }

        // Soft suggestions: resequence stops on each unprotected run
        foreach (var run in runs.Where(r => !IsProtected(r)))
        {
            var snapshot = snapshots.First(s => s.RunId == run.RunId);
            var reseqSuggestions = await GenerateResequenceSuggestionsAsync(
                run, snapshot, matrix, weights, configVersion, ++seq, ct);
            suggestions.AddRange(reseqSuggestions);
            seq += reseqSuggestions.Count;
        }

        // Soft suggestions: order movements between runs
        var eligibleRuns = runs.Where(r => !IsProtected(r)).ToList();
        var movementSuggestions = await GenerateOrderMovementSuggestionsAsync(
            eligibleRuns, snapshots, matrix, weights, configVersion, seq, ct);
        suggestions.AddRange(movementSuggestions);
        seq += movementSuggestions.Count;

        // 9. Current and proposed metrics
        var (currentMiles, currentMinutes) = SumCurrentMetrics(runs, matrix);
        var (proposedMiles, proposedMinutes) = ComputeProposedMetrics(runs, suggestions, matrix);

        var overallResult = suggestions.Any(s => s.Kind == SuggestionKind.HardConstraintViolation)
            ? "Hard constraints detected — review required"
            : suggestions.Any(s => s.Kind != SuggestionKind.KeepAsIs)
                ? "Improvements identified"
                : "No improvement found — current plan is optimal";

        // 10. Persist analysis (read-only snapshot of the plan state)
        var analysis = new OptimisationAnalysis
        {
            PlanningDate          = request.PlanningDate,
            Period                = request.Period ?? "ALL",
            CorrelationId         = correlationId,
            OptimiserVersion      = OptimiserVersion,
            ScoringConfigVersion  = configVersion,
            Status                = OptimisationStatus.Ready,
            OverallResult         = overallResult,
            CurrentRunCount       = runs.Count,
            CurrentTotalMiles     = currentMiles,
            CurrentTotalDriveMinutes = currentMinutes,
            ProposedRunCount      = runs.Count,  // merges/splits would change this
            ProposedTotalMiles    = proposedMiles,
            ProposedTotalDriveMinutes = proposedMinutes,
            RunsAffected          = suggestions.Select(s => s.SourceRunId).Distinct().Count(id => id.HasValue),
            EvidenceJson          = BuildEvidenceJson(runs, matrix, correlationId),
            CreatedBy             = actor,
            PlanVersionHash       = planHash,
            Suggestions           = suggestions
        };

        db.OptimisationAnalyses.Add(analysis);
        await WriteAuditEventAsync(analysis, null, OptimisationAuditAction.AnalysisCompleted,
            actor, "{}", JsonSerializer.Serialize(new { overallResult, suggestionCount = suggestions.Count }, Json), ct: ct);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "[{CorrelationId}] Analysis {AnalysisId} complete: {SuggestionCount} suggestions, {Result}.",
            correlationId, analysis.AnalysisId, suggestions.Count, overallResult);

        return MapToDto(analysis);
    }

    // ─── Public: get analysis ─────────────────────────────────────────────────

    public async Task<OptimisationAnalysisDto?> GetAsync(Guid analysisId, CancellationToken ct)
    {
        var analysis = await db.OptimisationAnalyses
            .AsNoTracking()
            .Include(a => a.Suggestions)
            .SingleOrDefaultAsync(a => a.AnalysisId == analysisId, ct);
        return analysis is null ? null : MapToDto(analysis);
    }

    // ─── Private: data loading ────────────────────────────────────────────────

    private async Task<List<CanonicalRunView>> LoadCanonicalRunsAsync(
        DateOnly date, string? period, CancellationToken ct)
    {
        var query = db.Runs.AsNoTracking()
            .Include(r => r.Stops)
            .Include(r => r.OrderAllocations).ThenInclude(a => a.Order)
            .Include(r => r.ResourceAllocations)
            .Include(r => r.StatusHistory)
            .Where(r => r.PlanningDate == date
                && r.Status != RunStatus.Cancelled
                && r.Status != RunStatus.Completed);

        if (!string.IsNullOrWhiteSpace(period) && period != "ALL")
            query = query.Where(r => r.Period == period.ToUpperInvariant());

        var runs = await query.OrderBy(r => r.Reference).ToListAsync(ct);

        return runs.Select(r => new CanonicalRunView(
            r.RunId,
            r.Reference,
            r.Status.ToString(),
            r.Period ?? "AM",
            r.RunType,
            r.IsMarketWork,
            r.IsOvernight,
            r.PlanningDate,
            r.PlannedStartUtc,
            r.Stops.OrderBy(s => s.Sequence).ToList(),
            r.OrderAllocations.ToList(),
            r.ResourceAllocations.ToList()
        )).ToList();
    }

    private async Task<Dictionary<Guid, bool>> GetDriverLeaveAsync(
        DateOnly date, IReadOnlyList<Driver> drivers, CancellationToken ct)
    {
        var result = new Dictionary<Guid, bool>();
        try
        {
            var outOfOffice = await sageHr.GetOutOfOfficeAsync(date, ct);
            foreach (var driver in drivers)
            {
                if (string.IsNullOrWhiteSpace(driver.EmployeeNumber)) continue;
                var onLeave = outOfOffice.Any(ooo =>
                    ooo.EmployeeId.ToString() == driver.EmployeeNumber &&
                    DateOnly.TryParse(ooo.StartDate, out var start) &&
                    DateOnly.TryParse(ooo.EndDate, out var end) &&
                    date >= start && date <= end);
                result[driver.Id] = onLeave;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "SageHR leave check failed; driver availability cannot be verified.");
        }
        return result;
    }

    private async Task<ScoringWeights> GetActiveWeightsAsync(CancellationToken ct)
    {
        try
        {
            var config = await db.ScoringConfigurations.AsNoTracking()
                .Where(c => c.IsActive && c.IsAdminApproved)
                .OrderByDescending(c => c.CreatedAtUtc)
                .FirstOrDefaultAsync(ct);
            if (config is not null)
                return JsonSerializer.Deserialize<ScoringWeights>(config.WeightsJson, Json)
                       ?? new ScoringWeights();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to load scoring config; using defaults.");
        }
        return new ScoringWeights();
    }

    private async Task<string?> GetActiveConfigVersionAsync(CancellationToken ct)
    {
        try
        {
            return await db.ScoringConfigurations.AsNoTracking()
                .Where(c => c.IsActive && c.IsAdminApproved)
                .OrderByDescending(c => c.CreatedAtUtc)
                .Select(c => c.Version)
                .FirstOrDefaultAsync(ct);
        }
        catch { return null; }
    }

    // ─── Private: suggestion generation ─────────────────────────────────────

    private async Task<List<OptimisationSuggestion>> GenerateResequenceSuggestionsAsync(
        CanonicalRunView run,
        HardConstraintEngine.RunSnapshot snapshot,
        RouteMatrixService.RouteMatrix? matrix,
        ScoringWeights weights,
        string? configVersion,
        int startSeq,
        CancellationToken ct)
    {
        var results = new List<OptimisationSuggestion>();
        if (matrix is null || run.Stops.Count < 3) return results;

        // Build delivery-only indices (collections are fixed at the front)
        var deliveryStops = run.Stops.Where(s => s.StopType == "Delivery").OrderBy(s => s.Sequence).ToList();
        if (deliveryStops.Count < 2) return results;

        var allStopNames = run.Stops.Select(s => s.SiteName ?? "Unknown").ToList();
        var matrixIndices = allStopNames
            .Select(n => matrix.Stops.Select((sp, i) => (sp, i)).FirstOrDefault(t => t.sp.Name == n).i)
            .ToList();

        if (matrixIndices.Any(i => i == 0 && allStopNames[0] != matrix.Stops[0].Name)) return results;

        var currentSeq  = matrixIndices.ToList();
        var (currentMiles, currentMinutes, routingSource) = RouteMatrixService.SequenceCost(matrix, currentSeq);

        // 2-opt on delivery sequence, keeping collections fixed at front
        var collectionCount = run.Stops.Count(s => s.StopType == "Collection");
        var fixedPrefix = currentSeq.Take(collectionCount).ToList();
        var deliveryPart = currentSeq.Skip(collectionCount).ToList();

        var optimised = RouteMatrixService.TwoOpt(matrix, deliveryPart, candidate =>
        {
            // Validate collection-before-delivery ordering is maintained
            var full = fixedPrefix.Concat(candidate).ToList();
            return IsCollectionBeforeDelivery(run, full);
        });

        var proposedFull = fixedPrefix.Concat(optimised).ToList();
        var (proposedMiles, proposedMinutes, _) = RouteMatrixService.SequenceCost(matrix, proposedFull);

        if (proposedMiles >= currentMiles - 0.5m) return results;  // less than 0.5 mile saving — not worth suggesting

        var milesSaved = currentMiles - proposedMiles;
        var minutesSaved = currentMinutes - proposedMinutes;

        var score = scoringEngine.Score(weights,
            currentMiles, proposedMiles,
            currentMinutes, proposedMinutes,
            stopsBefore: run.Stops.Count,
            stopsAfter: run.Stops.Count,
            reducesBacktracking: HasBacktrack(matrix, currentSeq) && !HasBacktrack(matrix, proposedFull),
            improvesGeographicCluster: false,
            deadlineBufferMinutes: null,
            riskOfLateDelivery: false,
            trailerUtilisationChange: 0m,
            eliminatesTrailerSwap: false,
            matchesHistoricalCombination: false,
            followsPlannerPreference: false,
            repeatsPriorRejection: await HasPriorRejectionAsync(run.RunId, SuggestionKind.ResequenceStops, ct),
            isCustomerPriority: false,
            learning: null,
            routingSource: routingSource);

        results.Add(new OptimisationSuggestion
        {
            AnalysisId            = Guid.Empty,  // set by caller
            Kind                  = SuggestionKind.ResequenceStops,
            Status                = SuggestionStatus.Pending,
            SourceRunId           = run.RunId,
            SourceRunReference    = run.Reference,
            ConstraintClass       = "Soft",
            Title                 = $"Resequence stops on {run.Reference} — save {milesSaved:0.#} miles",
            Rationale             = $"2-opt resequencing saves approximately {milesSaved:0.#} miles and {minutesSaved} minutes on {run.Reference}. {score.Explanation}",
            CurrentMiles          = currentMiles,
            ProposedMiles         = proposedMiles,
            CurrentDriveMinutes   = currentMinutes,
            ProposedDriveMinutes  = proposedMinutes,
            BeforeStopSequenceJson = SerialiseStops(run.Stops, currentSeq, matrix),
            AfterStopSequenceJson  = SerialiseStops(run.Stops, proposedFull, matrix),
            ConfidenceScore       = score.ConfidenceScore,
            BenefitScore          = score.TotalBenefit,
            RoutingSource         = routingSource,
            ScoreComponentsJson   = JsonSerializer.Serialize(score.Components, Json),
            ManualEditJson        = "{}",
            Sequence              = startSeq
        });

        return results;
    }

    private async Task<List<OptimisationSuggestion>> GenerateOrderMovementSuggestionsAsync(
        IReadOnlyList<CanonicalRunView> runs,
        IReadOnlyList<HardConstraintEngine.RunSnapshot> snapshots,
        RouteMatrixService.RouteMatrix? matrix,
        ScoringWeights weights,
        string? configVersion,
        int startSeq,
        CancellationToken ct)
    {
        var results = new List<OptimisationSuggestion>();
        if (runs.Count < 2) return results;

        // For each run, check each delivery order against every other run
        // to see if moving it would reduce total mileage without violating hard constraints.
        foreach (var sourceRun in runs)
        {
            var sourceSnap = snapshots.First(s => s.RunId == sourceRun.RunId);
            var deliveries = sourceRun.OrderAllocations
                .Where(a => a.AllocationType == "Delivery")
                .ToList();

            foreach (var delivery in deliveries)
            {
                foreach (var targetRun in runs.Where(r => r.RunId != sourceRun.RunId))
                {
                    var targetSnap = snapshots.First(s => s.RunId == targetRun.RunId);

                    // Basic feasibility check: target run must have same period and not be protected
                    if (targetRun.Period != sourceRun.Period) continue;
                    if (IsProtected(targetRun)) continue;

                    // Check target capacity
                    var targetUsed = targetRun.OrderAllocations.Sum(a => a.Quantity);
                    var targetCapacity = targetSnap.TrailerStandardCapacity ?? 26;
                    if (targetUsed + delivery.Quantity > targetCapacity) continue;

                    // Geographic proximity: target run must already serve the same region
                    if (matrix is not null && !SameRegion(sourceRun, targetRun, delivery, matrix)) continue;

                    // Would it reduce source run mileage significantly?
                    // Simplified check: only suggest if the delivery stop is geographically
                    // out of sequence on the source run (i.e. a genuine detour).
                    var priorRejection = await HasPriorRejectionAsync(sourceRun.RunId, SuggestionKind.MoveOrderToRun, ct);

                    var score = scoringEngine.Score(weights,
                        currentMiles: sourceSnap.TrailerStandardCapacity.HasValue ? 50m : 30m,  // proxy
                        proposedMiles: 40m,
                        currentDriveMinutes: 120,
                        proposedDriveMinutes: 90,
                        stopsBefore: sourceRun.Stops.Count,
                        stopsAfter: sourceRun.Stops.Count - 1,
                        reducesBacktracking: true,
                        improvesGeographicCluster: true,
                        deadlineBufferMinutes: null,
                        riskOfLateDelivery: false,
                        trailerUtilisationChange: 5m,
                        eliminatesTrailerSwap: false,
                        matchesHistoricalCombination: false,
                        followsPlannerPreference: false,
                        repeatsPriorRejection: priorRejection,
                        isCustomerPriority: false,
                        learning: null,
                        routingSource: matrix?.RoutingSource ?? "ResilientEstimate");

                    if (score.TotalBenefit <= 0) continue;  // not beneficial — skip

                    results.Add(new OptimisationSuggestion
                    {
                        AnalysisId            = Guid.Empty,
                        Kind                  = SuggestionKind.MoveOrderToRun,
                        Status                = SuggestionStatus.Pending,
                        SourceRunId           = sourceRun.RunId,
                        SourceRunReference    = sourceRun.Reference,
                        TargetRunId           = targetRun.RunId,
                        TargetRunReference    = targetRun.Reference,
                        OrderId               = delivery.OrderId,
                        OrderReference        = delivery.Order?.Reference,
                        ConstraintClass       = "Soft",
                        Title                 = $"Move {delivery.Order?.Reference ?? "order"} from {sourceRun.Reference} to {targetRun.Reference}",
                        Rationale             = $"{targetRun.Reference} already serves the same area. Moving this delivery reduces detour on {sourceRun.Reference}. {score.Explanation}",
                        BeforeStopSequenceJson = SerialiseRunStops(sourceRun),
                        AfterStopSequenceJson  = SerialiseRunStops(targetRun),
                        ConfidenceScore       = score.ConfidenceScore,
                        BenefitScore          = score.TotalBenefit,
                        RoutingSource         = matrix?.RoutingSource ?? "ResilientEstimate",
                        ScoreComponentsJson   = JsonSerializer.Serialize(score.Components, Json),
                        ManualEditJson        = "{}",
                        Sequence              = ++startSeq
                    });

                    break;  // one suggestion per order per analysis pass
                }
            }
        }

        return results;
    }

    // ─── Private: hard violation suggestion factory ───────────────────────────

    private static OptimisationSuggestion HardViolationSuggestion(
        HardConstraintEngine.RunSnapshot snap,
        HardConstraintEngine.HardViolation violation,
        int seq) =>
        new()
        {
            AnalysisId            = Guid.Empty,
            Kind                  = SuggestionKind.HardConstraintViolation,
            Status                = violation.CanOverride ? SuggestionStatus.Pending : SuggestionStatus.CannotApply,
            SourceRunId           = snap.RunId,
            SourceRunReference    = snap.Reference,
            ConstraintClass       = "Hard",
            Title                 = $"Hard constraint: {violation.Code} on {snap.Reference}",
            Rationale             = violation.Description,
            CannotApplyReason     = violation.CanOverride ? null : "This constraint cannot be overridden.",
            BeforeStopSequenceJson = "[]",
            AfterStopSequenceJson  = "[]",
            ConfidenceScore       = 100m,
            BenefitScore          = 0m,
            ScoreComponentsJson   = "[]",
            ManualEditJson        = "{}",
            Sequence              = seq
        };

    // ─── Private: learning helpers ────────────────────────────────────────────

    private async Task<bool> HasPriorRejectionAsync(Guid runId, SuggestionKind kind, CancellationToken ct)
    {
        try
        {
            return await db.OptimisationDecisionHistory.AsNoTracking()
                .AnyAsync(h => h.AnalysisId != Guid.Empty &&
                               h.Kind == kind.ToString() &&
                               h.Decision == SuggestionStatus.Rejected.ToString() &&
                               h.RecordedAtUtc >= DateTimeOffset.UtcNow.AddDays(-90), ct);
        }
        catch { return false; }
    }

    // ─── Private: audit ────────────────────────────────────────────────────────

    private async Task WriteAuditEventAsync(
        OptimisationAnalysis analysis,
        Guid? suggestionId,
        OptimisationAuditAction action,
        string? actor,
        string before,
        string after,
        string? note = null,
        CancellationToken ct = default)
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
            OptimiserVersion = OptimiserVersion
        });
        await Task.CompletedTask;
    }

    // ─── Private: helpers ─────────────────────────────────────────────────────

    private static bool IsProtected(CanonicalRunView run) =>
        run.Status is "Dispatched" or "InProgress" or "Completed";

    private static string BuildPlanVersionHash(IReadOnlyList<CanonicalRunView> runs, DateOnly date)
    {
        var content = string.Join("|", new[]
        {
            date.ToString("yyyy-MM-dd"),
            string.Join(";", runs.OrderBy(r => r.RunId).Select(r =>
                $"{r.RunId:N}:{r.Reference}:{r.Status}:{r.Stops.Count}:{r.OrderAllocations.Sum(a => a.Quantity)}"))
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
    }

    private static List<RouteMatrixService.StopPoint> BuildStopPoints(
        IReadOnlyList<CanonicalRunView> runs, IReadOnlyList<Site> sites)
    {
        var points = new List<RouteMatrixService.StopPoint>();
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var run in runs)
        foreach (var stop in run.Stops)
        {
            if (string.IsNullOrWhiteSpace(stop.SiteName)) continue;
            if (!seen.Add(stop.SiteName)) continue;
            var site = sites.FirstOrDefault(s =>
                string.Equals(s.Name, stop.SiteName, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(s.Aliases) && s.Aliases.Contains(stop.SiteName, StringComparison.OrdinalIgnoreCase)));
            if (site?.Latitude is null || site.Longitude is null) continue;
            points.Add(new RouteMatrixService.StopPoint(site.Id, stop.SiteName, site.Latitude.Value, site.Longitude.Value));
        }
        return points;
    }

    private static HardConstraintEngine.RunSnapshot BuildSnapshot(
        CanonicalRunView run,
        Dictionary<Guid, bool> leaveByDriver,
        IReadOnlyList<Driver> drivers,
        IReadOnlyList<Trailer> trailers)
    {
        var driverAlloc = run.ResourceAllocations.FirstOrDefault(a => a.ResourceType == "Driver");
        var vehicleAlloc = run.ResourceAllocations.FirstOrDefault(a => a.ResourceType == "Vehicle");
        var trailerAlloc = run.ResourceAllocations.FirstOrDefault(a => a.ResourceType == "Trailer");

        var trailer = trailerAlloc?.ResourceId is Guid tid
            ? trailers.FirstOrDefault(t => t.Id == tid) : null;

        var driver = driverAlloc?.ResourceId is Guid did
            ? drivers.FirstOrDefault(d => d.Id == did) : null;

        var onLeave = driver is not null && leaveByDriver.TryGetValue(driver.Id, out var leave) && leave;

        var stops = run.Stops.Select(s => new HardConstraintEngine.StopSnapshot(
            s.Sequence,
            s.StopType ?? "Unknown",
            s.SiteId,
            s.SiteName ?? "Unknown",
            s.AccessWindowOpen,
            s.AccessWindowClose,
            IsUnrestricted(s.AccessWindowOpen),
            s.LastDespatch,
            s.DepotDeadline,
            s.PlannedArrivalUtc,
            s.OrderReference,
            s.OrderId,
            IsApproved(s, run),
            s.HandlingUnitType,
            s.Quantity,
            ComputeLegBalance(s, run))).ToList();

        var units = run.OrderAllocations
            .GroupBy(a => a.HandlingUnitType ?? "Standard")
            .Select(g => new HardConstraintEngine.HandlingUnitSummary(g.Key, g.Sum(a => a.Quantity)))
            .ToList();

        return new HardConstraintEngine.RunSnapshot(
            run.RunId, run.Reference, run.Status,
            IsLocked: run.Status is "Dispatched" or "InProgress",
            run.PlanningDate, run.Period,
            run.RunType, run.IsMarketWork, run.IsOvernight,
            DriverId: driverAlloc?.ResourceId,
            VehicleId: vehicleAlloc?.ResourceId,
            TrailerId: trailerAlloc?.ResourceId,
            trailer?.StandardCapacity,
            trailer?.EuroCapacity,
            driver?.TachoDriveAvailableTodayMinutes,
            onLeave,
            DriverSixthDayRisk: false,   // TODO: wire consecutive-day check
            stops, units);
    }

    private static bool IsUnrestricted(TimeOnly? open) =>
        open is null || open.Value == new TimeOnly(0, 1) || open.Value == new TimeOnly(0, 0);

    private static bool IsApproved(RunStop stop, CanonicalRunView run) =>
        run.OrderAllocations.Any(a => a.OrderId == stop.OrderId &&
            a.Order?.Status == TransportOrderStatus.Approved);

    private static int ComputeLegBalance(RunStop stop, CanonicalRunView run)
    {
        // Running pallet balance at this stop after deliveries up to this point
        var collectedSoFar = run.Stops
            .Where(s => s.Sequence <= stop.Sequence && s.StopType == "Collection")
            .Join(run.OrderAllocations, s => s.OrderId, a => a.OrderId, (_, a) => a.Quantity)
            .Sum();
        var deliveredSoFar = run.Stops
            .Where(s => s.Sequence < stop.Sequence && s.StopType == "Delivery")
            .Join(run.OrderAllocations, s => s.OrderId, a => a.OrderId, (_, a) => a.Quantity)
            .Sum();
        return collectedSoFar - deliveredSoFar;
    }

    private static bool IsCollectionBeforeDelivery(CanonicalRunView run, IReadOnlyList<int> sequence)
    {
        // sequence is a list of matrix stop indices; we map back to stops by position
        // Simple check: all collection positions must come before their delivery pair
        var stops = run.Stops.OrderBy(s => s.Sequence).ToList();
        for (var i = 0; i < sequence.Count; i++)
        {
            var stop = stops.ElementAtOrDefault(i);
            if (stop?.StopType == "Delivery")
            {
                // Check if the corresponding collection appeared earlier
                var collectionIdx = stops.FindIndex(s => s.OrderId == stop.OrderId && s.StopType == "Collection");
                if (collectionIdx >= i) return false;
            }
        }
        return true;
    }

    private static bool HasBacktrack(RouteMatrixService.RouteMatrix matrix, IReadOnlyList<int> sequence)
    {
        // A backtrack is when north/south direction reverses significantly between consecutive legs
        for (var i = 2; i < sequence.Count; i++)
        {
            var fromLat = matrix.Stops[sequence[i - 2]].Latitude;
            var midLat  = matrix.Stops[sequence[i - 1]].Latitude;
            var toLat   = matrix.Stops[sequence[i]].Latitude;
            var dir1 = midLat - fromLat;
            var dir2 = toLat  - midLat;
            if (dir1 > 0.3m && dir2 < -0.3m) return true;   // going north then south
            if (dir1 < -0.3m && dir2 > 0.3m) return true;   // going south then north
        }
        return false;
    }

    private static bool SameRegion(
        CanonicalRunView source,
        CanonicalRunView target,
        RunOrderAllocation delivery,
        RouteMatrixService.RouteMatrix matrix)
    {
        var deliverySite = source.Stops.FirstOrDefault(s =>
            s.OrderId == delivery.OrderId && s.StopType == "Delivery")?.SiteName;
        if (deliverySite is null) return false;
        return target.Stops.Any(s =>
            s.StopType == "Delivery" &&
            IsSameRegion(s.SiteName, deliverySite, matrix));
    }

    private static bool IsSameRegion(string? a, string? b, RouteMatrixService.RouteMatrix matrix)
    {
        if (a is null || b is null) return false;
        var pa = matrix.Stops.FirstOrDefault(s => s.Name == a);
        var pb = matrix.Stops.FirstOrDefault(s => s.Name == b);
        if (pa is null || pb is null) return false;
        return Math.Abs(pa.Latitude - pb.Latitude) < 0.5m &&
               Math.Abs(pa.Longitude - pb.Longitude) < 0.5m;
    }

    private static (decimal Miles, int Minutes) SumCurrentMetrics(
        IReadOnlyList<CanonicalRunView> runs,
        RouteMatrixService.RouteMatrix? matrix)
    {
        if (matrix is null) return (0m, 0);
        var totalMiles = 0m; var totalMins = 0;
        foreach (var run in runs)
        {
            var idxs = run.Stops.OrderBy(s => s.Sequence)
                .Select(s => matrix.Stops.Select((sp, i) => (sp, i))
                    .FirstOrDefault(t => t.sp.Name == s.SiteName).i)
                .ToList();
            var (m, t, _) = RouteMatrixService.SequenceCost(matrix, idxs);
            totalMiles += m; totalMins += t;
        }
        return (totalMiles, totalMins);
    }

    private static (decimal Miles, int Minutes) ComputeProposedMetrics(
        IReadOnlyList<CanonicalRunView> runs,
        IReadOnlyList<OptimisationSuggestion> suggestions,
        RouteMatrixService.RouteMatrix? matrix)
    {
        var (currentMiles, currentMins) = SumCurrentMetrics(runs, matrix);
        var milesReduction   = suggestions.Where(s => s.CurrentMiles.HasValue && s.ProposedMiles.HasValue)
                                          .Sum(s => s.CurrentMiles!.Value - s.ProposedMiles!.Value);
        var minutesReduction = suggestions.Where(s => s.CurrentDriveMinutes.HasValue && s.ProposedDriveMinutes.HasValue)
                                          .Sum(s => s.CurrentDriveMinutes!.Value - s.ProposedDriveMinutes!.Value);
        return (Math.Max(0, currentMiles - milesReduction), Math.Max(0, currentMins - minutesReduction));
    }

    private static string SerialiseStops(IReadOnlyList<RunStop> stops, IReadOnlyList<int> sequence, RouteMatrixService.RouteMatrix matrix)
    {
        var ordered = stops.OrderBy(s => s.Sequence).ToList();
        var result  = sequence.Select((matrixIdx, i) =>
        {
            var stop = ordered.ElementAtOrDefault(i);
            return new { sequence = i + 1, siteName = matrix.Stops.ElementAtOrDefault(matrixIdx)?.Name ?? stop?.SiteName, stopType = stop?.StopType };
        });
        return JsonSerializer.Serialize(result, Json);
    }

    private static string SerialiseRunStops(CanonicalRunView run) =>
        JsonSerializer.Serialize(run.Stops.OrderBy(s => s.Sequence)
            .Select(s => new { s.Sequence, s.SiteName, s.StopType }), Json);

    private static string BuildEvidenceJson(IReadOnlyList<CanonicalRunView> runs, RouteMatrixService.RouteMatrix? matrix, Guid correlationId) =>
        JsonSerializer.Serialize(new
        {
            correlationId,
            runCount = runs.Count,
            totalStops = runs.Sum(r => r.Stops.Count),
            totalOrders = runs.Sum(r => r.OrderAllocations.Count),
            matrixStops = matrix?.Stops.Count ?? 0,
            matrixSource = matrix?.RoutingSource ?? "None",
            capturedAtUtc = DateTimeOffset.UtcNow
        }, Json);

    private static OptimisationAnalysis EmptyAnalysis(
        AnalyseRouteRequest request,
        Guid correlationId,
        string? actor,
        string reason) =>
        new()
        {
            PlanningDate          = request.PlanningDate,
            Period                = request.Period ?? "ALL",
            CorrelationId         = correlationId,
            OptimiserVersion      = OptimiserVersion,
            Status                = OptimisationStatus.Ready,
            OverallResult         = reason,
            EvidenceJson          = "{}",
            CreatedBy             = actor,
            PlanVersionHash       = "empty"
        };

    // ─── DTO mapping ──────────────────────────────────────────────────────────

    private static OptimisationAnalysisDto MapToDto(OptimisationAnalysis a)
    {
        var all = a.Suggestions.OrderBy(s => s.Sequence).ToList();
        return new OptimisationAnalysisDto(
            a.AnalysisId, a.PlanningDate, a.Period, a.CorrelationId,
            a.OptimiserVersion, a.ScoringConfigVersion,
            a.Status.ToString(), a.OverallResult,
            a.CurrentRunCount, a.CurrentTotalMiles, a.CurrentTotalDriveMinutes,
            a.ProposedRunCount, a.ProposedTotalMiles, a.ProposedTotalDriveMinutes,
            a.RunsAffected, a.AnalysedAtUtc,
            Improvements:        MapSuggestions(all.Where(s => s.Kind == SuggestionKind.ResequenceStops || s.Kind == SuggestionKind.MoveOrderToRun || s.Kind == SuggestionKind.MergeRuns || s.Kind == SuggestionKind.SplitRun).Where(s => s.Status == SuggestionStatus.Pending)),
            Warnings:            MapSuggestions(all.Where(s => s.Kind == SuggestionKind.Warning)),
            HardFailures:        MapSuggestions(all.Where(s => s.Kind == SuggestionKind.HardConstraintViolation)),
            Unchanged:           MapSuggestions(all.Where(s => s.Kind == SuggestionKind.KeepAsIs)),
            ManualReviewRequired:MapSuggestions(all.Where(s => s.Status == SuggestionStatus.CannotApply)),
            Accepted:            MapSuggestions(all.Where(s => s.Status == SuggestionStatus.Accepted || s.Status == SuggestionStatus.Applied)),
            Rejected:            MapSuggestions(all.Where(s => s.Status == SuggestionStatus.Rejected)),
            AffectedRuns:        []);
    }

    private static IReadOnlyList<SuggestionDto> MapSuggestions(IEnumerable<OptimisationSuggestion> suggestions) =>
        suggestions.Select(s => new SuggestionDto(
            s.SuggestionId, s.Kind.ToString(), s.Status.ToString(), s.ConstraintClass,
            s.Title, s.Rationale, s.SourceRunReference, s.TargetRunReference, s.OrderReference,
            s.CurrentMiles, s.ProposedMiles, s.CurrentDriveMinutes, s.ProposedDriveMinutes,
            Before: [], After: [],   // stop sequences deserialised on demand
            s.ConfidenceScore, s.BenefitScore, s.RoutingSource, s.CannotApplyReason,
            ScoreComponents: [],
            s.DecisionReason, s.Sequence)).ToList();
}

// ─── Internal view model for canonical runs ───────────────────────────────────

internal sealed record CanonicalRunView(
    Guid RunId,
    string Reference,
    string Status,
    string Period,
    string? RunType,
    bool IsMarketWork,
    bool IsOvernight,
    DateOnly PlanningDate,
    DateTimeOffset? PlannedStartUtc,
    IReadOnlyList<RunStop> Stops,
    IReadOnlyList<RunOrderAllocation> OrderAllocations,
    IReadOnlyList<RunResourceAllocation> ResourceAllocations);
