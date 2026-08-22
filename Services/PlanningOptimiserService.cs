using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public sealed class PlanningOptimiserService(TmsDbContext db, ILogger<PlanningOptimiserService> logger)
{
    private const string AllocationType = "planningpalletallocation";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
    private readonly PlanningConstraintEvaluator constraintEvaluator = new();
    private readonly PlanningCandidateRanker candidateRanker = new();

    public async Task<PlanProposalResult> GenerateAsync(
        GeneratePlanProposalRequest request,
        string? actor,
        CancellationToken ct)
    {
        var period = NormalisePeriod(request.Period);
        var evidenceAt = DateTimeOffset.UtcNow;
        var movements = await db.OrderMovements.AsNoTracking()
            .Where(item => item.LifecycleStatus == OrderMovementStatus.PlannerReady && item.CurrentRevisionId != null)
            .OrderBy(item => item.CustomerCode).ThenBy(item => item.StableMovementKey)
            .ToListAsync(ct);
        var revisionIds = movements.Select(item => item.CurrentRevisionId!.Value).ToList();
        var lines = revisionIds.Count == 0
            ? []
            : await db.OrderSourceLines.AsNoTracking()
                .Where(line => revisionIds.Contains(line.RevisionId) && line.CollectionDate == request.PlanningDate && line.Pallets > 0)
                .OrderBy(line => line.CollectionTimeFrom).ThenBy(line => line.CollectionSite).ThenBy(line => line.DeliverySite).ThenBy(line => line.SourceRowKey)
                .ToListAsync(ct);
        lines = lines.Where(line => Period(line.CollectionTimeFrom) == period).ToList();

        var allocated = await AllocatedPalletsAsync(request.PlanningDate, ct);
        var balances = lines
            .Select(line => new Balance(line, Math.Max(0, line.Pallets!.Value - allocated.GetValueOrDefault(line.Id))))
            .Where(item => item.Remaining > 0)
            .ToList();

        var warnings = new List<PlanProposalWarning>();
        var drivers = await db.Drivers.AsNoTracking().Where(item => item.Active).ToListAsync(ct);
        await MasterDetailStore.EnrichDriversAsync(db, drivers, ct);
        var vehicles = await db.Vehicles.AsNoTracking().Where(item => item.Active).OrderBy(item => item.Registration).ToListAsync(ct);
        var sites = await db.Sites.AsNoTracking().Where(item => item.Active).ToListAsync(ct);
        await MasterDetailStore.EnrichSitesAsync(db, sites, ct);
        var liveStatuses = await db.VehicleLiveStatuses.AsNoTracking().OrderByDescending(item => item.LastEventTimeUtc).ToListAsync(ct);
        var recentLoads = await RecentLoadsAsync(request.PlanningDate, ct);
        var tachoVerified = drivers.Any(driver => driver.TachoDriveAvailableTodayMinutes is not null && driver.LastTachoSyncUtc is not null && evidenceAt - driver.LastTachoSyncUtc <= TimeSpan.FromHours(6));
        if (!tachoVerified)
            warnings.Add(new PlanProposalWarning("TachoEvidenceMissing", "Warning", "Driver hours evidence is missing or stale; this proposal requires planner acknowledgement before application."));

        var inputHash = Hash(request.PlanningDate, period, balances, allocated, drivers, evidenceAt);
        var nextVersion = (await db.PlanProposals.AsNoTracking()
            .Where(item => item.PlanningDate == request.PlanningDate && item.Period == period)
            .MaxAsync(item => (int?)item.Version, ct) ?? 0) + 1;
        var classification = warnings.Count > 0 ? "Unverified" : "Recommended";
        var proposal = new PlanProposal
        {
            PlanningDate = request.PlanningDate,
            Period = period,
            Version = nextVersion,
            Status = "Generated",
            Classification = classification,
            InputHash = inputHash,
            EvidenceCapturedAtUtc = evidenceAt,
            CreatedAtUtc = evidenceAt,
            CreatedBy = actor,
            EvidenceJson = JsonSerializer.Serialize(new
            {
                capturedAtUtc = evidenceAt,
                sourceLineCount = balances.Count,
                sourcePallets = balances.Sum(item => item.Remaining),
                activeDriverCount = drivers.Count,
                tachoVerified
            }, JsonOptions),
            WarningsJson = JsonSerializer.Serialize(warnings, JsonOptions)
        };

        var lockedRuns = await PlanLockStore.BaselineAsync(db, request.PlanningDate, ct);
        AddLockedRuns(proposal, lockedRuns);
        BuildRuns(proposal, balances, classification, request.PlanningDate, period);
        AssignCandidateEvidence(proposal, drivers, vehicles, sites, liveStatuses, recentLoads, evidenceAt);
        proposal.Classification = WorstClassification(proposal.Runs.Select(run => run.Classification).Append(proposal.Classification));
        db.PlanProposals.Add(proposal);
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Generated immutable planning proposal {ProposalId} version {Version} for {PlanningDate} {Period} with {RunCount} runs and classification {Classification}.",
            proposal.Id, proposal.Version, proposal.PlanningDate, proposal.Period, proposal.Runs.Count, proposal.Classification);
        return ToResult(proposal, warnings);
    }

    public async Task<PlanProposalResult?> GetAsync(Guid id, CancellationToken ct)
    {
        var proposal = await db.PlanProposals.AsNoTracking()
            .Include(item => item.Runs).ThenInclude(run => run.Allocations)
            .SingleOrDefaultAsync(item => item.Id == id, ct);
        if (proposal is null) return null;
        return ToResult(proposal, DeserializeWarnings(proposal.WarningsJson));
    }

    private static void BuildRuns(PlanProposal proposal, IReadOnlyList<Balance> balances, string classification, DateOnly date, string period)
    {
        var runSequence = proposal.Runs.Count;
        PlanProposalRun? current = null;
        foreach (var balance in balances)
        {
            var remaining = balance.Remaining;
            var capacity = Capacity(balance.Line.PalletType);
            while (remaining > 0)
            {
                if (current is null || current.CapacityPallets != capacity || current.PlannedPallets >= current.CapacityPallets)
                {
                    runSequence++;
                    current = new PlanProposalRun
                    {
                        Sequence = runSequence,
                        Reference = $"OPT-{date:yyyyMMdd}-{period}-{runSequence:00}",
                        IsLocked = false,
                        Classification = classification,
                        CapacityPallets = capacity,
                        PlannedPallets = 0,
                        Score = 0m,
                        ScoreComponentsJson = "[]",
                        ExplanationJson = "[]"
                    };
                    proposal.Runs.Add(current);
                }

                var quantity = Math.Min(remaining, current.CapacityPallets - current.PlannedPallets);
                var allocation = new PlanProposalAllocation
                {
                    SourceLineId = balance.Line.Id,
                    Pallets = quantity,
                    PalletType = balance.Line.PalletType,
                    CollectionSite = balance.Line.CollectionSite,
                    DeliverySite = balance.Line.DeliverySite,
                    CollectionSequence = current.Allocations.Count + 1,
                    DeliverySequence = 0
                };
                current.Allocations.Add(allocation);
                current.PlannedPallets += quantity;
                remaining -= quantity;
            }
        }

        foreach (var run in proposal.Runs)
        {
            if (run.IsLocked) continue;
            var collectionCount = run.Allocations.Count;
            for (var index = 0; index < collectionCount; index++)
                run.Allocations[index].DeliverySequence = collectionCount + index + 1;
            var utilisation = run.CapacityPallets == 0 ? 0m : Math.Round((decimal)run.PlannedPallets / run.CapacityPallets * 100m, 2);
            run.Score = utilisation;
            run.ExplanationJson = JsonSerializer.Serialize(new[]
            {
                $"{run.PlannedPallets}/{run.CapacityPallets} pallet spaces ({utilisation}% utilisation).",
                "All collection sequences precede delivery sequences."
            }, JsonOptions);
        }
    }

    private async Task<Dictionary<Guid, int>> AllocatedPalletsAsync(DateOnly date, CancellationToken ct)
    {
        var rows = await db.StagedImports.AsNoTracking()
            .Where(item => item.EntityType == AllocationType && item.Status == StagingStatus.Promoted)
            .OrderByDescending(item => item.ReceivedAtUtc)
            .Take(20000)
            .ToListAsync(ct);
        var latest = new Dictionary<string, (Guid SourceLineId, int Pallets)>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            try
            {
                using var document = JsonDocument.Parse(row.PayloadJson);
                var root = document.RootElement;
                if (!root.TryGetProperty("sourceLineId", out var source) || source.ValueKind != JsonValueKind.String || !source.TryGetGuid(out var sourceLineId)) continue;
                if (root.TryGetProperty("date", out var dateElement) && DateOnly.TryParse(dateElement.GetString(), out var allocationDate) && allocationDate != date) continue;
                var loadId = root.TryGetProperty("loadId", out var load) ? load.ToString() : string.Empty;
                var key = $"{sourceLineId:N}:{loadId}";
                if (latest.ContainsKey(key)) continue;
                var pallets = root.TryGetProperty("pallets", out var quantity) && quantity.TryGetInt32(out var value) ? Math.Max(value, 0) : 0;
                latest[key] = (sourceLineId, pallets);
            }
            catch (JsonException) { }
        }
        return latest.Values.GroupBy(item => item.SourceLineId).ToDictionary(group => group.Key, group => group.Sum(item => item.Pallets));
    }

    private void AssignCandidateEvidence(
        PlanProposal proposal,
        IReadOnlyList<Driver> drivers,
        IReadOnlyList<Vehicle> vehicles,
        IReadOnlyList<Site> sites,
        IReadOnlyList<Slh.Tms.Api.Models.Tracking.VehicleLiveStatus> liveStatuses,
        IReadOnlyList<Load> recentLoads,
        DateTimeOffset evidenceAt)
    {
        foreach (var run in proposal.Runs)
        {
            if (run.IsLocked) continue;
            var first = run.Allocations.OrderBy(item => item.CollectionSequence).FirstOrDefault();
            var last = run.Allocations.OrderByDescending(item => item.DeliverySequence).FirstOrDefault();
            var collection = MatchSite(sites, first?.CollectionSite);
            var delivery = MatchSite(sites, last?.DeliverySite);
            var requiredDrive = RequiredDriveMinutes(collection?.Latitude, delivery?.Latitude);
            Candidate? selected = null;

            foreach (var driver in drivers.OrderBy(item => item.DisplayName).ThenBy(item => item.Id))
            foreach (var vehicle in vehicles.OrderBy(item => item.Registration).ThenBy(item => item.Id))
            {
                var consecutiveDays = ConsecutiveDays(recentLoads, driver.Id, proposal.PlanningDate);
                var constraints = constraintEvaluator.EvaluateDriver(new PlanningDriverEvidence(
                    driver.Id,
                    requiredDrive,
                    driver.TachoDriveAvailableTodayMinutes,
                    driver.LastTachoSyncUtc,
                    evidenceAt,
                    consecutiveDays,
                    SixthDayAllowed(driver)));
                var live = MatchLive(vehicle, liveStatuses);
                var previous = recentLoads
                    .Where(load => load.DriverId == driver.Id || load.VehicleId == vehicle.Id)
                    .OrderByDescending(load => load.PlanningDate).ThenByDescending(load => load.CreatedAtUtc)
                    .FirstOrDefault();
                var previousEnd = previous?.Stops.OrderByDescending(stop => stop.Sequence).FirstOrDefault(stop => stop.Latitude is not null);
                var score = candidateRanker.Score(new PlanningCandidateEvidence(
                    vehicle.Id,
                    driver.Id,
                    collection?.Latitude,
                    delivery?.Latitude,
                    live?.Latitude,
                    live?.LastEventTimeUtc,
                    previousEnd?.Latitude,
                    previous is null ? null : StartOfDayUtc(previous.PlanningDate).AddDays(1),
                    consecutiveDays,
                    evidenceAt,
                    run.CapacityPallets == 0 ? 0m : Math.Round((decimal)run.PlannedPallets / run.CapacityPallets * 100m, 2)));
                var candidate = new Candidate(driver, vehicle, constraints, score);
                if (selected is null || CandidateOrder(candidate, selected) < 0) selected = candidate;
            }

            if (selected is null) continue;
            run.DriverId = selected.Driver.Id;
            run.VehicleId = selected.Vehicle.Id;
            run.PositionSource = selected.Score.PositionSource;
            run.Classification = selected.Constraints.Classification;
            run.Score = selected.Score.Total;
            run.ScoreComponentsJson = JsonSerializer.Serialize(selected.Score.Components, JsonOptions);
            run.ExplanationJson = JsonSerializer.Serialize(
                selected.Score.Explanations.Concat(selected.Constraints.Results.Select(result => result.Explanation)).ToList(),
                JsonOptions);
        }
    }

    private async Task<List<Load>> RecentLoadsAsync(DateOnly planningDate, CancellationToken ct)
    {
        var from = planningDate.AddDays(-7);
        try
        {
            return await db.Loads.AsNoTracking().Include(load => load.Stops)
                .Where(load => load.PlanningDate >= from && load.PlanningDate < planningDate && load.Status != LoadStatus.Cancelled)
                .OrderBy(load => load.PlanningDate).ThenBy(load => load.Reference).ToListAsync(ct);
        }
        catch (Exception exception) when (SchemaUnavailable(exception))
        {
            db.ChangeTracker.Clear();
            return (await PlanningRegisterStore.ReadLoadsAsync(db, null, ct))
                .Where(load => load.PlanningDate >= from && load.PlanningDate < planningDate && load.Status != LoadStatus.Cancelled)
                .ToList();
        }
    }

    private static PlanProposalResult ToResult(PlanProposal proposal, IReadOnlyList<PlanProposalWarning> warnings) =>
        new(
            proposal.Id,
            proposal.PlanningDate,
            proposal.Period,
            proposal.Version,
            proposal.Status,
            proposal.Classification,
            proposal.InputHash,
            proposal.EvidenceCapturedAtUtc,
            proposal.CreatedAtUtc,
            proposal.CreatedBy,
            warnings,
            proposal.Runs.OrderBy(run => run.Sequence).Select(run => new PlanProposalRunResult(
                run.Id,
                run.Sequence,
                run.Reference,
                run.IsLocked,
                run.LiveLoadId,
                run.Classification,
                run.DriverId,
                run.VehicleId,
                run.PositionSource,
                run.CapacityPallets,
                run.PlannedPallets,
                run.Score,
                DeserializeComponents(run.ScoreComponentsJson),
                DeserializeExplanations(run.ExplanationJson),
                run.Allocations.OrderBy(allocation => allocation.CollectionSequence).Select(allocation => new PlanProposalAllocationResult(
                    allocation.Id,
                    allocation.SourceLineId,
                    allocation.Pallets,
                    allocation.PalletType,
                    allocation.CollectionSite,
                    allocation.DeliverySite,
                    allocation.CollectionSequence,
                    allocation.DeliverySequence)).ToList())).ToList());

    private static IReadOnlyList<PlanProposalWarning> DeserializeWarnings(string value)
    {
        try { return JsonSerializer.Deserialize<List<PlanProposalWarning>>(value, JsonOptions) ?? []; }
        catch (JsonException) { return []; }
    }

    private static IReadOnlyList<string> DeserializeExplanations(string value)
    {
        try { return JsonSerializer.Deserialize<List<string>>(value, JsonOptions) ?? []; }
        catch (JsonException) { return []; }
    }

    private static IReadOnlyList<PlanningScoreComponent> DeserializeComponents(string value)
    {
        try { return JsonSerializer.Deserialize<List<PlanningScoreComponent>>(value, JsonOptions) ?? []; }
        catch (JsonException) { return []; }
    }

    private static string Hash(DateOnly date, string period, IReadOnlyList<Balance> balances, IReadOnlyDictionary<Guid, int> allocated, IReadOnlyList<Driver> drivers, DateTimeOffset evidenceAt)
    {
        var source = string.Join("|", new[]
        {
            date.ToString("yyyy-MM-dd"),
            period,
            string.Join(";", balances.Select(item => $"{item.Line.Id:N}:{item.Remaining}:{item.Line.CollectionSite}:{item.Line.DeliverySite}:{item.Line.PalletType}")),
            string.Join(";", allocated.OrderBy(item => item.Key).Select(item => $"{item.Key:N}:{item.Value}")),
            string.Join(";", drivers.OrderBy(item => item.Id).Select(item => $"{item.Id:N}:{item.TachoDriveAvailableTodayMinutes}:{item.LastTachoSyncUtc?.ToUnixTimeSeconds()}")),
            evidenceAt.ToString("yyyy-MM-ddTHH:mm")
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
    }

    private static string NormalisePeriod(string? value) => string.Equals(value?.Trim(), "PM", StringComparison.OrdinalIgnoreCase) ? "PM" : "AM";
    private static string Period(TimeOnly? value) => value is not null && value.Value >= new TimeOnly(17, 0) ? "PM" : "AM";
    private static int Capacity(string? palletType) => palletType?.Contains("euro", StringComparison.OrdinalIgnoreCase) == true ? 33 : 26;
    private static void AddLockedRuns(PlanProposal proposal, IReadOnlyList<LoadBaseline> lockedRuns)
    {
        foreach (var baseline in lockedRuns.OrderBy(run => run.Reference, StringComparer.OrdinalIgnoreCase))
        {
            proposal.Runs.Add(new PlanProposalRun
            {
                Sequence = proposal.Runs.Count + 1,
                Reference = baseline.Reference,
                IsLocked = true,
                LiveLoadId = baseline.Id,
                DriverId = baseline.DriverId,
                VehicleId = baseline.VehicleId,
                Classification = "Recommended",
                PositionSource = "LockedPlan",
                CapacityPallets = 0,
                PlannedPallets = 0,
                Score = 0m,
                ScoreComponentsJson = "[]",
                ExplanationJson = JsonSerializer.Serialize(new[] { "Existing locked run retained unchanged as a fixed planning constraint." }, JsonOptions)
            });
        }
    }
    private static Site? MatchSite(IEnumerable<Site> sites, string? name) => string.IsNullOrWhiteSpace(name) ? null : sites
        .OrderBy(site => string.Equals(site.Name, name, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
        .FirstOrDefault(site => string.Equals(site.Name, name, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(site.Aliases) && site.Aliases.Contains(name, StringComparison.OrdinalIgnoreCase)));
    private static Slh.Tms.Api.Models.Tracking.VehicleLiveStatus? MatchLive(Vehicle vehicle, IEnumerable<Slh.Tms.Api.Models.Tracking.VehicleLiveStatus> statuses)
    {
        var keys = new[] { vehicle.Registration, vehicle.FleetNumber, vehicle.Abbreviation }.Where(value => !string.IsNullOrWhiteSpace(value)).Select(Normalise).ToHashSet();
        return statuses.Where(status => keys.Contains(Normalise(status.VehicleIdentifier))).OrderByDescending(status => status.LastEventTimeUtc).FirstOrDefault();
    }
    private static int RequiredDriveMinutes(decimal? collectionLatitude, decimal? deliveryLatitude) => collectionLatitude is null || deliveryLatitude is null
        ? 240
        : Math.Max(60, (int)Math.Ceiling(Math.Abs(collectionLatitude.Value - deliveryLatitude.Value) * 69m / 45m * 60m) + 60);
    private static int ConsecutiveDays(IEnumerable<Load> loads, Guid driverId, DateOnly planningDate)
    {
        var days = loads.Where(load => load.DriverId == driverId).Select(load => load.PlanningDate).ToHashSet();
        var count = 0;
        for (var day = planningDate.AddDays(-1); days.Contains(day); day = day.AddDays(-1)) count++;
        return count;
    }
    private static bool SixthDayAllowed(Driver driver) => driver.Notes?.Contains("sixth day allowed", StringComparison.OrdinalIgnoreCase) == true;
    private static int CandidateOrder(Candidate left, Candidate right)
    {
        var classification = Rank(left.Constraints.Classification).CompareTo(Rank(right.Constraints.Classification));
        if (classification != 0) return classification;
        var score = right.Score.Total.CompareTo(left.Score.Total);
        if (score != 0) return score;
        var driver = string.Compare(left.Driver.DisplayName, right.Driver.DisplayName, StringComparison.OrdinalIgnoreCase);
        return driver != 0 ? driver : string.Compare(left.Vehicle.Registration, right.Vehicle.Registration, StringComparison.OrdinalIgnoreCase);
    }
    private static int Rank(string classification) => classification switch { "Recommended" => 0, "Alternative" => 1, "Unverified" => 2, _ => 3 };
    private static string WorstClassification(IEnumerable<string> values) => values.OrderByDescending(Rank).FirstOrDefault() ?? "Unverified";
    private static string Normalise(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static DateTimeOffset StartOfDayUtc(DateOnly date) => new(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
    private static bool SchemaUnavailable(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        return exception is InvalidOperationException or DbUpdateException ||
            message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Cannot find the object", StringComparison.OrdinalIgnoreCase);
    }
    private sealed record Balance(OrderSourceLine Line, int Remaining);
    private sealed record Candidate(Driver Driver, Vehicle Vehicle, PlanningConstraintEvaluation Constraints, PlanningCandidateScore Score);
}
