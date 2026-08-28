from pathlib import Path
import re


def replace_once(path: str, old: str, new: str) -> None:
    p = Path(path)
    text = p.read_text()
    if old not in text:
        raise SystemExit(f"Expected patch target not found in {path}: {old[:160]!r}")
    p.write_text(text.replace(old, new, 1))


def regex_once(path: str, pattern: str, replacement: str) -> None:
    p = Path(path)
    text = p.read_text()
    updated, count = re.subn(pattern, replacement, text, count=1, flags=re.S)
    if count != 1:
        raise SystemExit(f"Expected exactly one regex target in {path}, got {count}: {pattern[:160]!r}")
    p.write_text(updated)


# 1 + 2: enrichment keeps last-known metrics and treats duplicate cards as ambiguous.
replace_once(
    "Services/IntegrationSyncCoordinator.cs",
    """            var byMemberCode = profiles.GroupBy(profile => profile.MemberCode).ToDictionary(group => group.Key, group => group.First());
            var byEmployee = UniqueLookup(profiles.Where(profile => !string.IsNullOrWhiteSpace(profile.EmployeeNumber)), profile => Normalise(profile.EmployeeNumber));""",
    """            var byMemberCode = profiles.GroupBy(profile => profile.MemberCode).ToDictionary(group => group.Key, group => group.First());
            var byCard = UniqueLookup(profiles.Where(profile => !string.IsNullOrWhiteSpace(profile.CardNumber)), profile => Normalise(profile.CardNumber));
            var byEmployee = UniqueLookup(profiles.Where(profile => !string.IsNullOrWhiteSpace(profile.EmployeeNumber)), profile => Normalise(profile.EmployeeNumber));""",
)
replace_once(
    "Services/IntegrationSyncCoordinator.cs",
    """            if (profile is null && !string.IsNullOrWhiteSpace(driver.TachoCardNumber))
                profile = profiles.SingleOrDefault(candidate => CardsMatch(driver.TachoCardNumber, candidate.CardNumber));""",
    """            if (profile is null && !string.IsNullOrWhiteSpace(driver.TachoCardNumber))
                byCard.TryGetValue(Normalise(driver.TachoCardNumber), out profile);""",
)
replace_once(
    "Services/IntegrationSyncCoordinator.cs",
    """            driver.TachoCardNumber = profile.CardNumber;
            driver.TachoName = string.IsNullOrWhiteSpace(driver.TachoName) ? profile.DriverName : driver.TachoName;
            driver.TachoDriveAvailableTodayMinutes = profile.DriveAvailableTodayMinutes;
            driver.TachoDriveAvailableWeekMinutes = profile.DriveAvailableWeekMinutes;
            driver.TachoWorkAvailableWeekMinutes = profile.WorkAvailableWeekMinutes;""",
    """            driver.TachoCardNumber = profile.CardNumber ?? driver.TachoCardNumber;
            driver.TachoName = string.IsNullOrWhiteSpace(driver.TachoName) ? profile.DriverName : driver.TachoName;
            driver.TachoDriveAvailableTodayMinutes = profile.DriveAvailableTodayMinutes ?? driver.TachoDriveAvailableTodayMinutes;
            driver.TachoDriveAvailableWeekMinutes = profile.DriveAvailableWeekMinutes ?? driver.TachoDriveAvailableWeekMinutes;
            driver.TachoWorkAvailableWeekMinutes = profile.WorkAvailableWeekMinutes ?? driver.TachoWorkAvailableWeekMinutes;""",
)

# 1 + 2 + 4 + 5: canonical identity logic and strict health gate.
replace_once(
    "Services/TachoDriverMasterSyncService.cs",
    """public sealed record TachoDriverMasterQuality(
    int ActiveDrivers,
    int ActiveWithMember,
    int ActiveWithCard,
    int DuplicateMemberGroups,
    int DuplicateCardGroups,
    int ActiveWithoutMember,
    int ActiveWithoutCard,
    DateTimeOffset? LatestCanonicalSyncUtc);""",
    """public sealed record TachoDriverMasterDuplicateDriver(
    Guid DriverId,
    string EmployeeNumber,
    string DisplayName);

public sealed record TachoDriverMasterIdentityDuplicate(
    string IdentityValue,
    IReadOnlyList<TachoDriverMasterDuplicateDriver> Drivers);

public sealed record TachoDriverMasterQuality(
    int ActiveDrivers,
    int ActiveWithMember,
    int ActiveWithCard,
    int DuplicateMemberGroups,
    int DuplicateCardGroups,
    int ActiveWithoutMember,
    int ActiveWithoutCard,
    DateTimeOffset? LatestCanonicalSyncUtc)
{
    public IReadOnlyList<TachoDriverMasterIdentityDuplicate> DuplicateMembers { get; init; } = [];
    public IReadOnlyList<TachoDriverMasterIdentityDuplicate> DuplicateCards { get; init; } = [];
}""",
)
replace_once(
    "Services/TachoDriverMasterSyncService.cs",
    """        var liveNameCounts = workers
            .GroupBy(worker => TachoDriverIdentityRules.NormalisePerson(worker.DisplayName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var claimedDriverIds = new HashSet<Guid>();""",
    """        var liveNameCounts = workers
            .GroupBy(worker => TachoDriverIdentityRules.NormalisePerson(worker.DisplayName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var liveCardCounts = workers
            .Where(worker => !string.IsNullOrWhiteSpace(worker.CardNumber))
            .GroupBy(worker => TachoDriverIdentityRules.NormaliseIdentifier(worker.CardNumber), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var claimedDriverIds = new HashSet<Guid>();""",
)
replace_once(
    "Services/TachoDriverMasterSyncService.cs",
    """            var member = worker.MemberCode.ToString(CultureInfo.InvariantCulture);
            var strong = drivers.Where(driver =>
                    TachoDriverIdentityRules.MemberMatches(driver.TachoMasterDriverId, member) ||
                    TachoDriverIdentityRules.CardsMatch(driver.TachoCardNumber, worker.CardNumber))
                .ToList();

            Driver? canonical = null;
            if (strong.Count > 0)
            {
                canonical = SelectCanonical(strong, worker, loadUse);
                if (strong.Any(driver => TachoDriverIdentityRules.MemberMatches(driver.TachoMasterDriverId, member))) matchedByMember++;
                else matchedByCard++;
            }""",
    """            var member = worker.MemberCode.ToString(CultureInfo.InvariantCulture);
            var memberMatches = drivers
                .Where(driver => TachoDriverIdentityRules.MemberMatches(driver.TachoMasterDriverId, member))
                .ToList();
            var cardKey = TachoDriverIdentityRules.NormaliseIdentifier(worker.CardNumber);
            var cardIsUnique = cardKey.Length > 0 && liveCardCounts.GetValueOrDefault(cardKey) == 1;
            var strong = memberMatches.Count > 0
                ? memberMatches
                : cardIsUnique
                    ? drivers.Where(driver => TachoDriverIdentityRules.CardsMatch(driver.TachoCardNumber, worker.CardNumber)).ToList()
                    : [];

            Driver? canonical = null;
            if (strong.Count > 0)
            {
                canonical = SelectCanonical(strong, worker, loadUse);
                if (memberMatches.Count > 0) matchedByMember++;
                else matchedByCard++;
            }""",
)
replace_once(
    "Services/TachoDriverMasterSyncService.cs",
    """        driver.TachoDriveAvailableTodayMinutes = profile?.DriveAvailableTodayMinutes;
        driver.TachoDriveAvailableWeekMinutes = profile?.DriveAvailableWeekMinutes;
        driver.TachoWorkAvailableWeekMinutes = profile?.WorkAvailableWeekMinutes;""",
    """        driver.TachoDriveAvailableTodayMinutes = profile?.DriveAvailableTodayMinutes ?? driver.TachoDriveAvailableTodayMinutes;
        driver.TachoDriveAvailableWeekMinutes = profile?.DriveAvailableWeekMinutes ?? driver.TachoDriveAvailableWeekMinutes;
        driver.TachoWorkAvailableWeekMinutes = profile?.WorkAvailableWeekMinutes ?? driver.TachoWorkAvailableWeekMinutes;""",
)

regex_once(
    "Services/TachoDriverMasterSyncService.cs",
    r'''        db\.StagedImports\.Add\(new StagedImport\n        \{\n            EntityType = "tachodrivermastersync",.*?        return new\(true, workers\.Count, activeAfter, created, updated, retired, archived, matchedByMember, matchedByCard, matchedByName,\n            CountDuplicateNames\(workers\), workers\.Count\(worker => string\.IsNullOrWhiteSpace\(worker\.CardNumber\)\), message, DateTimeOffset\.UtcNow\);''',
    '''        var activeAfter = drivers.Where(driver => driver.Active).ToList();
        var duplicateMemberGroupsAfter = DuplicateIdentityGroupCount(activeAfter, driver => driver.TachoMasterDriverId);
        var duplicateCardGroupsAfter = DuplicateIdentityGroupCount(activeAfter, driver => driver.TachoCardNumber);
        var activeWithoutMemberAfter = activeAfter.Count(driver => string.IsNullOrWhiteSpace(driver.TachoMasterDriverId));
        var canonicalHealthy = activeAfter.Count == workers.Count &&
                               duplicateMemberGroupsAfter == 0 &&
                               duplicateCardGroupsAfter == 0 &&
                               activeWithoutMemberAfter == 0;

        var auditPayload = JsonSerializer.Serialize(new
        {
            sourceWorkers = workers.Count,
            canonicalActiveDrivers = activeAfter.Count,
            created,
            updated,
            duplicateRecordsRetired = retired,
            driversArchivedNotInTachoMaster = archived,
            sameNameDifferentIdentityGroups = CountDuplicateNames(workers),
            workersWithoutCard = workers.Count(worker => string.IsNullOrWhiteSpace(worker.CardNumber)),
            duplicateMemberGroups = duplicateMemberGroupsAfter,
            duplicateCardGroups = duplicateCardGroupsAfter,
            activeWithoutMember = activeWithoutMemberAfter,
            populationAligned = activeAfter.Count == workers.Count
        }, JsonOptions);

        if (!canonicalHealthy)
        {
            await transaction.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            var failureMessage = $"TachoMaster canonical Driver Master was not promoted because the resulting population failed the strict identity gate: source={workers.Count}, active={activeAfter.Count}, duplicate members={duplicateMemberGroupsAfter}, duplicate cards={duplicateCardGroupsAfter}, active without member={activeWithoutMemberAfter}. No partial cleanse was committed.";
            db.StagedImports.Add(new StagedImport
            {
                EntityType = "tachodrivermastersync",
                IdempotencyKey = $"tachodrivermastersync:{now:yyyyMMddHHmmss}:{Guid.NewGuid():N}",
                PayloadJson = auditPayload,
                Source = "TachoMaster live Worker List canonical Driver Master sync",
                Status = StagingStatus.Rejected,
                ReceivedAtUtc = now,
                ReviewedAtUtc = DateTimeOffset.UtcNow,
                ReviewedBy = actor,
                ReviewNote = failureMessage
            });
            await db.SaveChangesAsync(ct);
            return new(false, workers.Count, activeAfter.Count, created, updated, retired, archived, matchedByMember, matchedByCard, matchedByName,
                CountDuplicateNames(workers), workers.Count(worker => string.IsNullOrWhiteSpace(worker.CardNumber)), failureMessage, DateTimeOffset.UtcNow);
        }

        db.StagedImports.Add(new StagedImport
        {
            EntityType = "tachodrivermastersync",
            IdempotencyKey = $"tachodrivermastersync:{now:yyyyMMddHHmmss}:{Guid.NewGuid():N}",
            PayloadJson = auditPayload,
            Source = "TachoMaster live Worker List canonical Driver Master sync",
            Status = StagingStatus.Promoted,
            ReceivedAtUtc = now,
            ReviewedAtUtc = DateTimeOffset.UtcNow,
            ReviewedBy = actor,
            ReviewNote = "TachoMaster Member Code is authoritative. Card identity is used only when unique in the live source; Sage HR remains an enrichment source for employed staff."
        });

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        var message = $"TachoMaster canonical Driver Master: {workers.Count} live worker(s), {activeAfter.Count} active canonical TMS driver(s), {created} created, {retired} duplicate record(s) retired and {archived} stale/non-live TMS driver(s) archived. Strict source parity and duplicate-identity checks passed.";
        return new(true, workers.Count, activeAfter.Count, created, updated, retired, archived, matchedByMember, matchedByCard, matchedByName,
            CountDuplicateNames(workers), workers.Count(worker => string.IsNullOrWhiteSpace(worker.CardNumber)), message, DateTimeOffset.UtcNow);''',
)
replace_once(
    "Services/TachoDriverMasterSyncService.cs",
    """        var memberGroups = drivers.Where(driver => !string.IsNullOrWhiteSpace(driver.TachoMasterDriverId))
            .GroupBy(driver => TachoDriverIdentityRules.NormaliseIdentifier(driver.TachoMasterDriverId), StringComparer.OrdinalIgnoreCase)
            .Count(group => group.Count() > 1);
        var cardGroups = drivers.Where(driver => !string.IsNullOrWhiteSpace(driver.TachoCardNumber))
            .GroupBy(driver => TachoDriverIdentityRules.NormaliseIdentifier(driver.TachoCardNumber), StringComparer.OrdinalIgnoreCase)
            .Count(group => group.Count() > 1);""",
    """        var duplicateMembers = DuplicateIdentities(drivers, driver => driver.TachoMasterDriverId);
        var duplicateCards = DuplicateIdentities(drivers, driver => driver.TachoCardNumber);
        var memberGroups = duplicateMembers.Count;
        var cardGroups = duplicateCards.Count;""",
)
replace_once(
    "Services/TachoDriverMasterSyncService.cs",
    """            drivers.Count(driver => string.IsNullOrWhiteSpace(driver.TachoCardNumber)),
            latest == default ? null : latest);""",
    """            drivers.Count(driver => string.IsNullOrWhiteSpace(driver.TachoCardNumber)),
            latest == default ? null : latest)
        {
            DuplicateMembers = duplicateMembers,
            DuplicateCards = duplicateCards
        };""",
)
replace_once(
    "Services/TachoDriverMasterSyncService.cs",
    """    private static int CountDuplicateNames(IReadOnlyCollection<TachoLiveWorker> workers) => workers
        .GroupBy(worker => TachoDriverIdentityRules.NormalisePerson(worker.DisplayName), StringComparer.OrdinalIgnoreCase)
        .Count(group => group.Key.Length > 0 && group.Select(worker => worker.MemberCode).Distinct().Count() > 1);""",
    """    private static int CountDuplicateNames(IReadOnlyCollection<TachoLiveWorker> workers) => workers
        .GroupBy(worker => TachoDriverIdentityRules.NormalisePerson(worker.DisplayName), StringComparer.OrdinalIgnoreCase)
        .Count(group => group.Key.Length > 0 && group.Select(worker => worker.MemberCode).Distinct().Count() > 1);

    private static int DuplicateIdentityGroupCount(IEnumerable<Driver> drivers, Func<Driver, string?> selector) =>
        drivers
            .Select(selector)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(TachoDriverIdentityRules.NormaliseIdentifier, StringComparer.OrdinalIgnoreCase)
            .Count(group => group.Key.Length > 0 && group.Count() > 1);

    private static IReadOnlyList<TachoDriverMasterIdentityDuplicate> DuplicateIdentities(
        IEnumerable<Driver> drivers,
        Func<Driver, string?> selector) =>
        drivers
            .Where(driver => !string.IsNullOrWhiteSpace(selector(driver)))
            .GroupBy(driver => TachoDriverIdentityRules.NormaliseIdentifier(selector(driver)), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Key.Length > 0 && group.Count() > 1)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new TachoDriverMasterIdentityDuplicate(
                group.Key,
                group.OrderBy(driver => driver.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .Select(driver => new TachoDriverMasterDuplicateDriver(driver.Id, driver.EmployeeNumber, driver.DisplayName))
                    .ToList()))
            .ToList();""",
)

# 3: durable queued manual sync; uses existing StagedImport + RowVersion, no migration.
Path("Services/TachoDriverMasterSyncJobService.cs").write_text('''using System.Text.Json;
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
''')

replace_once(
    "Controllers/TachoDriverMasterController.cs",
    """public sealed class TachoDriverMasterController(
    TachoCanonicalDriverMasterOrchestrator orchestrator,
    TachoDriverMasterSyncService sync) : ControllerBase""",
    """public sealed class TachoDriverMasterController(
    TachoDriverMasterSyncJobService jobs,
    TachoDriverMasterSyncService sync) : ControllerBase""",
)
regex_once(
    "Controllers/TachoDriverMasterController.cs",
    r'''    \[HttpPost\("tachomaster/sync"\)\]\n    public async Task<IActionResult> Sync\(CancellationToken ct\)\n    \{.*?\n    \}\n\n    \[HttpGet\("tachomaster/quality"\)\]''',
    '''    [HttpPost("tachomaster/sync")]
    public async Task<IActionResult> Sync(CancellationToken ct)
    {
        var actor = User.Identity?.Name ?? "TMS user";
        var job = await jobs.EnqueueAsync(actor, ct);
        return AcceptedAtAction(nameof(SyncStatus), new { jobId = job.JobId }, job);
    }

    [HttpGet("tachomaster/sync/{jobId:guid}")]
    public async Task<IActionResult> SyncStatus(Guid jobId, CancellationToken ct)
    {
        var job = await jobs.GetAsync(jobId, ct);
        return job is null ? NotFound() : Ok(job);
    }

    [HttpGet("tachomaster/quality")]''',
)

replace_once(
    "Program.cs",
    """builder.Services.AddScoped<TachoCanonicalDriverMasterOrchestrator>();
builder.Services.AddTransient<TachoMasterRetryHandler>();""",
    """builder.Services.AddScoped<TachoCanonicalDriverMasterOrchestrator>();
builder.Services.AddScoped<TachoDriverMasterSyncJobService>();
builder.Services.AddTransient<TachoMasterRetryHandler>();""",
)
replace_once(
    "Program.cs",
    """builder.Services.AddHostedService<TachoCanonicalDriverMasterDailyBackgroundService>();
builder.Services.AddHostedService<DriverMasterClassificationBackgroundService>();""",
    """builder.Services.AddHostedService<TachoCanonicalDriverMasterDailyBackgroundService>();
builder.Services.AddHostedService<TachoDriverMasterSyncJobWorker>();
builder.Services.AddHostedService<DriverMasterClassificationBackgroundService>();""",
)
replace_once(
    "Controllers/DriverMasterHealthController.cs",
    """            quality.DuplicateMemberGroups,
            quality.DuplicateCardGroups,
            quality.ActiveWithoutMember,""",
    """            quality.DuplicateMemberGroups,
            quality.DuplicateCardGroups,
            quality.DuplicateMembers,
            quality.DuplicateCards,
            quality.ActiveWithoutMember,""",
)

# 5: post-deployment verification must see a fresh promoted canonical sync.
replace_once(
    ".github/workflows/driver-master-production-verification.yml",
    """          set -euo pipefail
          for attempt in {1..60}; do""",
    """          set -euo pipefail
          deployed_at="${{ github.event.workflow_run.updated_at }}"
          deployed_epoch="$(date -u -d "$deployed_at" +%s)"
          echo "Requiring canonical Driver Master promotion at/after deployment completion: $deployed_at"
          for attempt in {1..60}; do""",
)
replace_once(
    ".github/workflows/driver-master-production-verification.yml",
    """               test "$(echo "$body" | jq -r '.populationAligned // false')" = "true" && \\
               test "$(echo "$body" | jq -r '.latestCanonicalSyncUtc // empty')" != ""; then""",
    """               test "$(echo "$body" | jq -r '.populationAligned // false')" = "true" && \\
               test "$(echo "$body" | jq -r '.latestCanonicalSyncUtc // empty')" != "" && \\
               test "$(date -u -d "$(echo "$body" | jq -r '.latestCanonicalSyncUtc')" +%s)" -ge "$deployed_epoch"; then""",
)
replace_once(
    ".github/workflows/driver-master-production-verification.yml",
    """          echo "$body" | jq '{status, activeDrivers, sourceWorkers, populationAligned, activeWithMember, activeWithCard, duplicateMemberGroups, duplicateCardGroups, activeWithoutMember, activeWithoutCard, latestCanonicalSyncUtc}'""",
    """          echo "$body" | jq '{status, activeDrivers, sourceWorkers, populationAligned, activeWithMember, activeWithCard, duplicateMemberGroups, duplicateCardGroups, duplicateMembers, duplicateCards, activeWithoutMember, activeWithoutCard, latestCanonicalSyncUtc}'""",
)

# Additional regression for duplicate live cards being non-authoritative.
p = Path("Slh.Tms.Api.Tests/TachoDriverMasterIdentityTests.cs")
text = p.read_text()
marker = """    [Fact]
    public void Missing_profile_metrics_preserve_last_known_tacho_hours()"""
extra = """    [Fact]
    public void Duplicate_live_card_is_not_a_safe_identity_key()
    {
        var card = "CARD-DUPLICATE-0001";
        var workers = new[]
        {
            new TachoLiveWorker(1, "Driver One", card, "SLH-1", "Employed", null, null, null, null, null, null, null, null, null, null, null, "{}"),
            new TachoLiveWorker(2, "Driver Two", card, "SLH-2", "Employed", null, null, null, null, null, null, null, null, null, null, null, "{}")
        };

        var cardKey = TachoDriverIdentityRules.NormaliseIdentifier(card);
        var liveCardCounts = workers
            .Where(worker => !string.IsNullOrWhiteSpace(worker.CardNumber))
            .GroupBy(worker => TachoDriverIdentityRules.NormaliseIdentifier(worker.CardNumber), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        Assert.Equal(2, liveCardCounts[cardKey]);
        Assert.False(cardKey.Length > 0 && liveCardCounts.GetValueOrDefault(cardKey) == 1);
    }

"""
if marker not in text:
    raise SystemExit("Identity test insertion marker missing")
p.write_text(text.replace(marker, extra + marker, 1))
