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

        BuildRuns(proposal, balances, classification, request.PlanningDate, period);
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
        var runSequence = 0;
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
                        Classification = classification,
                        CapacityPallets = capacity,
                        PlannedPallets = 0,
                        Score = 0m,
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
                run.Classification,
                run.CapacityPallets,
                run.PlannedPallets,
                run.Score,
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
    private sealed record Balance(OrderSourceLine Line, int Remaining);
}
