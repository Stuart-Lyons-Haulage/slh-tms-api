using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;

namespace Slh.Tms.Api.Services;

public sealed record TachoDriverMasterSyncResult(
    bool Success,
    int SourceWorkers,
    int CanonicalActiveDrivers,
    int Created,
    int Updated,
    int DuplicateRecordsRetired,
    int DriversArchivedNotInTachoMaster,
    int MatchedByMember,
    int MatchedByCard,
    int MatchedByUniqueName,
    int SameNameDifferentIdentityGroups,
    int WorkersWithoutCard,
    string Message,
    DateTimeOffset CompletedAtUtc);

public sealed record TachoDriverMasterQuality(
    int ActiveDrivers,
    int ActiveWithMember,
    int ActiveWithCard,
    int DuplicateMemberGroups,
    int DuplicateCardGroups,
    int ActiveWithoutMember,
    int ActiveWithoutCard,
    DateTimeOffset? LatestCanonicalSyncUtc);

public sealed class TachoDriverMasterSyncService(
    TmsDbContext db,
    TachoMasterClient tachoMaster,
    IHttpClientFactory httpClientFactory,
    TachoMasterOptions options,
    ILogger<TachoDriverMasterSyncService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
    private const string DetailType = "masterdetail:driver";
    private const string ProfileType = "tachodriverprofile";

    public async Task<TachoDriverMasterSyncResult> SyncAsync(string actor, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (!options.IsConfigured)
            return new(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                "TachoMaster is not configured, so the canonical Driver Master was not changed.", now);

        var directory = new TachoLiveWorkerDirectory(httpClientFactory.CreateClient(), options, logger);
        IReadOnlyList<TachoLiveWorker> workers;
        try
        {
            workers = await directory.GetLiveWorkersAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "TachoMaster live worker directory could not be read; canonical driver sync aborted.");
            return new(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                $"TachoMaster live worker directory could not be read: {ex.GetBaseException().Message}. No Driver Master records were intentionally changed.", now);
        }

        // A tiny/empty provider result must never quarantine the real driver population.
        if (workers.Count < 25)
            return new(false, workers.Count, 0, 0, 0, 0, 0, 0, 0, 0, CountDuplicateNames(workers), workers.Count(worker => string.IsNullOrWhiteSpace(worker.CardNumber)),
                $"TachoMaster returned only {workers.Count} live worker(s). Canonicalisation was stopped because that is below the safety floor.", now);

        IReadOnlyDictionary<int, TachoDriverProfile> profileByMember = new Dictionary<int, TachoDriverProfile>();
        try
        {
            profileByMember = (await tachoMaster.GetDriverProfilesAsync(ct))
                .GroupBy(profile => profile.MemberCode)
                .ToDictionary(group => group.Key, group => group.First());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "TachoMaster metrics could not be read during canonical Driver Master sync; identity sync will continue without hours metrics.");
        }

        var drivers = await db.Drivers.OrderBy(driver => driver.DisplayName).ToListAsync(ct);
        await MasterDetailStore.EnrichDriversAsync(db, drivers, ct);
        var activeBefore = drivers.Count(driver => driver.Active);
        if (activeBefore > 0 && workers.Count < Math.Max(25, (int)Math.Floor(activeBefore * 0.35m)))
            return new(false, workers.Count, activeBefore, 0, 0, 0, 0, 0, 0, 0, CountDuplicateNames(workers), workers.Count(worker => string.IsNullOrWhiteSpace(worker.CardNumber)),
                $"TachoMaster returned {workers.Count} live workers against {activeBefore} active TMS drivers. The result failed the population safety check, so no records were archived.", now);

        var loadUse = await db.Loads.AsNoTracking()
            .Where(load => load.DriverId != null)
            .GroupBy(load => load.DriverId!.Value)
            .Select(group => new { DriverId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.DriverId, item => item.Count, ct);

        var detailRows = await db.StagedImports
            .Where(row => row.EntityType == DetailType)
            .OrderByDescending(row => row.ReviewedAtUtc ?? row.ReceivedAtUtc)
            .ToListAsync(ct);
        var detailByKey = detailRows
            .GroupBy(row => row.IdempotencyKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var profileRows = await db.StagedImports
            .Where(row => row.EntityType == ProfileType)
            .ToDictionaryAsync(row => row.IdempotencyKey, StringComparer.OrdinalIgnoreCase, ct);

        var liveNameCounts = workers
            .GroupBy(worker => TachoDriverIdentityRules.NormalisePerson(worker.DisplayName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var claimedDriverIds = new HashSet<Guid>();
        var created = 0;
        var updated = 0;
        var retired = 0;
        var matchedByMember = 0;
        var matchedByCard = 0;
        var matchedByName = 0;

        foreach (var worker in workers.OrderBy(worker => worker.DisplayName, StringComparer.OrdinalIgnoreCase).ThenBy(worker => worker.MemberCode))
        {
            var member = worker.MemberCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
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
            }
            else
            {
                var nameKey = TachoDriverIdentityRules.NormalisePerson(worker.DisplayName);
                if (nameKey.Length > 0 && liveNameCounts.GetValueOrDefault(nameKey) == 1)
                {
                    var nameMatches = drivers
                        .Where(driver => !claimedDriverIds.Contains(driver.Id))
                        .Where(driver => IdentityCompatible(driver, worker))
                        .Where(driver => TachoDriverIdentityRules.NormalisePerson(driver.TachoName) == nameKey ||
                                         TachoDriverIdentityRules.NormalisePerson(driver.DisplayName) == nameKey)
                        .ToList();
                    if (nameMatches.Count == 1)
                    {
                        canonical = nameMatches[0];
                        matchedByName++;
                    }
                }
            }

            if (canonical is null)
            {
                canonical = new Driver
                {
                    EmployeeNumber = UniqueEmployeeNumber(worker, drivers),
                    DisplayName = worker.DisplayName,
                    TachoName = worker.DisplayName,
                    DriverType = Clean(worker.WorkerType),
                    Active = true
                };
                db.Drivers.Add(canonical);
                drivers.Add(canonical);
                created++;
            }
            else updated++;

            var duplicates = strong.Where(driver => driver.Id != canonical.Id).ToList();
            foreach (var duplicate in duplicates)
            {
                await MergeDuplicateAsync(canonical, duplicate, detailRows, ct);
                retired++;
            }

            ApplyWorker(canonical, worker, profileByMember.GetValueOrDefault(worker.MemberCode), now);
            canonical.Active = true;
            claimedDriverIds.Add(canonical.Id);
            UpsertDetail(detailByKey, canonical, worker, actor, now);
            UpsertProfile(profileRows, worker, actor, now);
        }

        var archiveUnmatched = workers.Count >= Math.Max(25, (int)Math.Floor(Math.Max(1, activeBefore) * 0.35m));
        var archived = 0;
        if (archiveUnmatched)
        {
            foreach (var driver in drivers.Where(driver => driver.Active && !claimedDriverIds.Contains(driver.Id)))
            {
                driver.Active = false;
                archived++;
                db.MasterDataAudits.Add(new MasterDataAudit
                {
                    EntityType = "Driver",
                    EntityId = driver.Id,
                    Action = "ArchivedNotInTachoMaster",
                    ChangedBy = actor,
                    ChangesJson = JsonSerializer.Serialize(new
                    {
                        reason = "Not present in the current TachoMaster live worker directory",
                        driver.EmployeeNumber,
                        driver.DisplayName,
                        driver.TachoMasterDriverId,
                        driver.TachoCardNumber
                    }, JsonOptions)
                });
            }
        }

        db.StagedImports.Add(new StagedImport
        {
            EntityType = "tachodrivermastersync",
            IdempotencyKey = $"tachodrivermastersync:{now:yyyyMMddHHmmss}:{Guid.NewGuid():N}",
            PayloadJson = JsonSerializer.Serialize(new
            {
                sourceWorkers = workers.Count,
                created,
                updated,
                duplicateRecordsRetired = retired,
                driversArchivedNotInTachoMaster = archived,
                sameNameDifferentIdentityGroups = CountDuplicateNames(workers),
                workersWithoutCard = workers.Count(worker => string.IsNullOrWhiteSpace(worker.CardNumber))
            }, JsonOptions),
            Source = "TachoMaster live Worker List canonical Driver Master sync",
            Status = StagingStatus.Promoted,
            ReceivedAtUtc = now,
            ReviewedAtUtc = now,
            ReviewedBy = actor,
            ReviewNote = "TachoMaster Member Code and Driver Card are authoritative identity. Sage HR remains an enrichment source for employed staff."
        });

        await db.SaveChangesAsync(ct);
        var activeAfter = await db.Drivers.CountAsync(driver => driver.Active, ct);
        var message = $"TachoMaster canonical Driver Master: {workers.Count} live worker(s), {activeAfter} active canonical TMS driver(s), {created} created, {retired} duplicate record(s) retired and {archived} stale/non-live TMS driver(s) archived. Member Code and card are authoritative; Sage HR remains employed-staff enrichment.";
        return new(true, workers.Count, activeAfter, created, updated, retired, archived, matchedByMember, matchedByCard, matchedByName,
            CountDuplicateNames(workers), workers.Count(worker => string.IsNullOrWhiteSpace(worker.CardNumber)), message, DateTimeOffset.UtcNow);
    }

    public async Task<TachoDriverMasterQuality> QualityAsync(CancellationToken ct)
    {
        var drivers = await db.Drivers.Where(driver => driver.Active).OrderBy(driver => driver.DisplayName).ToListAsync(ct);
        await MasterDetailStore.EnrichDriversAsync(db, drivers, ct);
        var memberGroups = drivers.Where(driver => !string.IsNullOrWhiteSpace(driver.TachoMasterDriverId))
            .GroupBy(driver => TachoDriverIdentityRules.NormaliseIdentifier(driver.TachoMasterDriverId), StringComparer.OrdinalIgnoreCase)
            .Count(group => group.Count() > 1);
        var cardGroups = drivers.Where(driver => !string.IsNullOrWhiteSpace(driver.TachoCardNumber))
            .GroupBy(driver => TachoDriverIdentityRules.NormaliseIdentifier(driver.TachoCardNumber), StringComparer.OrdinalIgnoreCase)
            .Count(group => group.Count() > 1);
        var latest = await db.StagedImports.AsNoTracking()
            .Where(row => row.EntityType == "tachodrivermastersync" && row.Status == StagingStatus.Promoted)
            .OrderByDescending(row => row.ReviewedAtUtc ?? row.ReceivedAtUtc)
            .Select(row => row.ReviewedAtUtc ?? row.ReceivedAtUtc)
            .FirstOrDefaultAsync(ct);
        return new(
            drivers.Count,
            drivers.Count(driver => !string.IsNullOrWhiteSpace(driver.TachoMasterDriverId)),
            drivers.Count(driver => !string.IsNullOrWhiteSpace(driver.TachoCardNumber)),
            memberGroups,
            cardGroups,
            drivers.Count(driver => string.IsNullOrWhiteSpace(driver.TachoMasterDriverId)),
            drivers.Count(driver => string.IsNullOrWhiteSpace(driver.TachoCardNumber)),
            latest == default ? null : latest);
    }

    public async Task<TachoLiveWorker?> ProfileAsync(Guid driverId, CancellationToken ct)
    {
        var driver = await db.Drivers.AsNoTracking().SingleOrDefaultAsync(item => item.Id == driverId, ct);
        if (driver is null) return null;
        await MasterDetailStore.EnrichDriversAsync(db, [driver], ct);
        if (string.IsNullOrWhiteSpace(driver.TachoMasterDriverId)) return null;
        var key = $"tachodriverprofile:{TachoDriverIdentityRules.NormaliseIdentifier(driver.TachoMasterDriverId)}";
        var row = await db.StagedImports.AsNoTracking().SingleOrDefaultAsync(item => item.IdempotencyKey == key && item.Status == StagingStatus.Promoted, ct);
        if (row is null) return null;
        try { return JsonSerializer.Deserialize<TachoLiveWorker>(row.PayloadJson, JsonOptions); }
        catch (JsonException) { return null; }
    }

    private static Driver SelectCanonical(IReadOnlyCollection<Driver> candidates, TachoLiveWorker worker, IReadOnlyDictionary<Guid, int> loadUse)
        => candidates
            .OrderByDescending(driver => CanonicalScore(driver, worker, loadUse.GetValueOrDefault(driver.Id)))
            .ThenBy(driver => driver.EmployeeNumber, StringComparer.OrdinalIgnoreCase)
            .ThenBy(driver => driver.Id)
            .First();

    private static int CanonicalScore(Driver driver, TachoLiveWorker worker, int loadCount)
    {
        var score = Math.Min(loadCount, 100) * 10;
        if (driver.Active) score += 100;
        if (TachoDriverIdentityRules.MemberMatches(driver.TachoMasterDriverId, worker.MemberCode.ToString())) score += 800;
        if (TachoDriverIdentityRules.CardsMatch(driver.TachoCardNumber, worker.CardNumber)) score += 600;
        if (!driver.EmployeeNumber.StartsWith("TM-", StringComparison.OrdinalIgnoreCase)) score += 40;
        if (!string.IsNullOrWhiteSpace(driver.MobileNumber)) score += 20;
        if (!string.IsNullOrWhiteSpace(driver.DriverType)) score += 10;
        if (!string.IsNullOrWhiteSpace(driver.DriverGroup)) score += 10;
        if (!string.IsNullOrWhiteSpace(driver.Skills)) score += 10;
        if (!string.IsNullOrWhiteSpace(driver.AgencyName)) score += 10;
        if (!string.IsNullOrWhiteSpace(driver.DrivingLicenceNumber) || driver.LicenceExpiry is not null) score += 10;
        return score;
    }

    private static bool IdentityCompatible(Driver driver, TachoLiveWorker worker)
    {
        if (!string.IsNullOrWhiteSpace(driver.TachoMasterDriverId) &&
            !TachoDriverIdentityRules.MemberMatches(driver.TachoMasterDriverId, worker.MemberCode.ToString())) return false;
        if (!string.IsNullOrWhiteSpace(driver.TachoCardNumber) && !string.IsNullOrWhiteSpace(worker.CardNumber) &&
            !TachoDriverIdentityRules.CardsMatch(driver.TachoCardNumber, worker.CardNumber)) return false;
        return true;
    }

    private async Task MergeDuplicateAsync(Driver canonical, Driver duplicate, IReadOnlyCollection<StagedImport> detailRows, CancellationToken ct)
    {
        canonical.MobileNumber ??= duplicate.MobileNumber;
        canonical.DriverType ??= duplicate.DriverType;
        canonical.DriverGroup ??= duplicate.DriverGroup;
        canonical.Skills ??= duplicate.Skills;
        canonical.Coding ??= duplicate.Coding;
        canonical.AgencyName ??= duplicate.AgencyName;
        canonical.Notes ??= duplicate.Notes;
        canonical.DrivingLicenceNumber ??= duplicate.DrivingLicenceNumber;
        canonical.LicenceExpiry ??= duplicate.LicenceExpiry;
        canonical.LicenceStatus ??= duplicate.LicenceStatus;

        foreach (var load in await db.Loads.Where(load => load.DriverId == duplicate.Id).ToListAsync(ct)) load.DriverId = canonical.Id;
        try
        {
            foreach (var run in await db.PlanProposalRuns.Where(run => run.DriverId == duplicate.Id).ToListAsync(ct)) run.DriverId = canonical.Id;
            foreach (var candidate in await db.PlanProposalCandidates.Where(candidate => candidate.DriverId == duplicate.Id).ToListAsync(ct)) candidate.DriverId = canonical.Id;
        }
        catch (Exception ex) when (SchemaUnavailable(ex)) { db.ChangeTracker.Clear(); }

        try
        {
            foreach (var mapping in await db.IntegrationMappings.Where(mapping => mapping.TmsEntityType == "Driver" && mapping.TmsEntityId == duplicate.Id).ToListAsync(ct))
                mapping.TmsEntityId = canonical.Id;
        }
        catch (Exception ex) when (SchemaUnavailable(ex)) { db.ChangeTracker.Clear(); }

        try
        {
            foreach (var audit in await db.MasterDataAudits.Where(audit => audit.EntityType == "Driver" && audit.EntityId == duplicate.Id).ToListAsync(ct))
                audit.EntityId = canonical.Id;
        }
        catch (Exception ex) when (SchemaUnavailable(ex)) { db.ChangeTracker.Clear(); }

        var duplicateIdText = duplicate.Id.ToString();
        var planningRows = await db.StagedImports
            .Where(row => row.EntityType == "planningload" && row.Status == StagingStatus.Promoted && row.PayloadJson.Contains(duplicateIdText))
            .ToListAsync(ct);
        foreach (var row in planningRows)
        {
            try
            {
                var load = JsonSerializer.Deserialize<Load>(row.PayloadJson, JsonOptions);
                if (load?.DriverId != duplicate.Id) continue;
                load.DriverId = canonical.Id;
                row.PayloadJson = JsonSerializer.Serialize(load, JsonOptions);
                row.ReviewedAtUtc = DateTimeOffset.UtcNow;
                row.ReviewNote = $"Driver identity merged from {duplicate.Id} to canonical {canonical.Id}.";
            }
            catch (JsonException) { }
        }

        var documentPrefix = $"masterdocument:Driver:{duplicate.Id:N}:";
        var documents = await db.StagedImports.Where(row => row.EntityType == "masterdocument" && row.IdempotencyKey.StartsWith(documentPrefix)).ToListAsync(ct);
        foreach (var row in documents)
        {
            var suffix = row.IdempotencyKey[documentPrefix.Length..];
            row.IdempotencyKey = $"masterdocument:Driver:{canonical.Id:N}:{suffix}";
            try
            {
                var node = JsonNode.Parse(row.PayloadJson) as JsonObject;
                if (node is not null)
                {
                    node["entityId"] = canonical.Id;
                    row.PayloadJson = node.ToJsonString(JsonOptions);
                }
            }
            catch (JsonException) { }
        }

        foreach (var detail in detailRows.Where(row => string.Equals(row.IdempotencyKey, DetailKey(duplicate.EmployeeNumber), StringComparison.OrdinalIgnoreCase)))
        {
            detail.Status = StagingStatus.Archived;
            detail.ReviewedAtUtc = DateTimeOffset.UtcNow;
            detail.ReviewNote = $"Duplicate Driver Master detail retired into canonical driver {canonical.EmployeeNumber} ({canonical.Id}).";
        }

        duplicate.Active = false;
        db.MasterDataAudits.Add(new MasterDataAudit
        {
            EntityType = "Driver",
            EntityId = canonical.Id,
            Action = "MergedDuplicateTachoIdentity",
            ChangedBy = "TachoMaster canonical sync",
            ChangesJson = JsonSerializer.Serialize(new
            {
                canonicalDriverId = canonical.Id,
                duplicateDriverId = duplicate.Id,
                duplicate.EmployeeNumber,
                duplicate.DisplayName,
                duplicate.TachoMasterDriverId,
                duplicate.TachoCardNumber
            }, JsonOptions)
        });
    }

    private static void ApplyWorker(Driver driver, TachoLiveWorker worker, TachoDriverProfile? profile, DateTimeOffset now)
    {
        driver.DisplayName = string.IsNullOrWhiteSpace(driver.DisplayName) ? worker.DisplayName : driver.DisplayName;
        driver.TachoName = worker.DisplayName;
        driver.TachoMasterDriverId = worker.MemberCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
        driver.TachoCardNumber = Clean(worker.CardNumber);
        driver.AgencyName = Clean(worker.AgencyName) ?? driver.AgencyName;
        if (string.IsNullOrWhiteSpace(driver.DriverType) || string.Equals(driver.DriverType, "Agency", StringComparison.OrdinalIgnoreCase) || string.Equals(driver.DriverType, "Casual", StringComparison.OrdinalIgnoreCase))
            driver.DriverType = Clean(worker.WorkerType) ?? driver.DriverType;
        driver.LicenceExpiry = ParseDate(worker.DrivingLicenceExpiry) ?? driver.LicenceExpiry;
        driver.TachoDriveAvailableTodayMinutes = profile?.DriveAvailableTodayMinutes;
        driver.TachoDriveAvailableWeekMinutes = profile?.DriveAvailableWeekMinutes;
        driver.TachoWorkAvailableWeekMinutes = profile?.WorkAvailableWeekMinutes;
        driver.LastTachoSyncUtc = now;
    }

    private static string UniqueEmployeeNumber(TachoLiveWorker worker, IReadOnlyCollection<Driver> drivers)
    {
        var used = drivers.Select(driver => driver.EmployeeNumber).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var preferred = Clean(worker.EmployeeNumber);
        if (!string.IsNullOrWhiteSpace(preferred) && !used.Contains(preferred)) return Clip(preferred, 40)!;
        var root = $"TM-{worker.MemberCode}";
        if (!used.Contains(root)) return root;
        for (var suffix = 2; suffix < 1000; suffix++)
        {
            var candidate = $"{root}-{suffix}";
            if (!used.Contains(candidate)) return candidate;
        }
        return $"TM-{Guid.NewGuid():N}"[..40];
    }

    private static void UpsertDetail(Dictionary<string, StagedImport> rows, Driver driver, TachoLiveWorker worker, string actor, DateTimeOffset now)
    {
        var key = DetailKey(driver.EmployeeNumber);
        if (!rows.TryGetValue(key, out var row))
        {
            row = new StagedImport
            {
                EntityType = DetailType,
                IdempotencyKey = key,
                PayloadJson = "{}",
                Source = "TachoMaster canonical Driver Master",
                ReceivedAtUtc = now
            };
            rows[key] = row;
        }
        row.EntityType = DetailType;
        row.Status = StagingStatus.Promoted;
        row.Source = "TachoMaster canonical Driver Master";
        row.PayloadJson = JsonSerializer.Serialize(new
        {
            driver.EmployeeNumber,
            driver.DisplayName,
            driver.TachoName,
            driver.MobileNumber,
            driver.DriverType,
            driver.DriverGroup,
            driver.Skills,
            driver.Coding,
            driver.AgencyName,
            driver.Notes,
            driver.TachoMasterDriverId,
            driver.TachoCardNumber,
            driver.TachoDriveAvailableTodayMinutes,
            driver.TachoDriveAvailableWeekMinutes,
            driver.TachoWorkAvailableWeekMinutes,
            driver.DrivingLicenceNumber,
            driver.LicenceExpiry,
            driver.LicenceStatus,
            driver.LastTachoSyncUtc,
            tachoMasterProfile = worker
        }, JsonOptions);
        row.ReviewedAtUtc = now;
        row.ReviewedBy = actor;
        row.ReviewNote = "Canonical TachoMaster identity with TMS CRM enrichment. North/Preload legacy fields are intentionally not retained.";
    }

    private static void UpsertProfile(Dictionary<string, StagedImport> rows, TachoLiveWorker worker, string actor, DateTimeOffset now)
    {
        var key = $"tachodriverprofile:{TachoDriverIdentityRules.NormaliseIdentifier(worker.MemberCode.ToString())}";
        if (!rows.TryGetValue(key, out var row))
        {
            row = new StagedImport
            {
                EntityType = ProfileType,
                IdempotencyKey = key,
                PayloadJson = "{}",
                Source = "TachoMaster live Worker List",
                ReceivedAtUtc = now
            };
            rows[key] = row;
        }
        row.Status = StagingStatus.Promoted;
        row.PayloadJson = JsonSerializer.Serialize(worker, JsonOptions);
        row.ReviewedAtUtc = now;
        row.ReviewedBy = actor;
        row.ReviewNote = "Latest current/live TachoMaster Worker List profile for the canonical driver identity.";
    }

    private static int CountDuplicateNames(IReadOnlyCollection<TachoLiveWorker> workers) => workers
        .GroupBy(worker => TachoDriverIdentityRules.NormalisePerson(worker.DisplayName), StringComparer.OrdinalIgnoreCase)
        .Count(group => group.Key.Length > 0 && group.Select(worker => worker.MemberCode).Distinct().Count() > 1);

    private static string DetailKey(string employeeNumber) => $"masterdetail:driver:{NormaliseKey(employeeNumber)}";
    private static string NormaliseKey(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? Clip(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, max)];
    private static DateOnly? ParseDate(string? value) => DateOnly.TryParse(value, out var parsed) ? parsed : null;
    private static bool SchemaUnavailable(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        return exception is InvalidOperationException or DbUpdateException ||
               message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Cannot find the object", StringComparison.OrdinalIgnoreCase);
    }
}

internal static class TachoDriverIdentityRules
{
    public static bool MemberMatches(string? left, string? right)
    {
        var a = NormaliseIdentifier(left);
        var b = NormaliseIdentifier(right);
        return a.Length > 0 && b.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    public static bool CardsMatch(string? left, string? right)
    {
        var a = NormaliseIdentifier(left);
        var b = NormaliseIdentifier(right);
        if (a.Length < 8 || b.Length < 8) return false;
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ||
               a.EndsWith(b, StringComparison.OrdinalIgnoreCase) ||
               b.EndsWith(a, StringComparison.OrdinalIgnoreCase);
    }

    public static string NormaliseIdentifier(string? value) => new((value ?? string.Empty)
        .Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    public static string NormalisePerson(string? value) => string.Join(' ', (value ?? string.Empty)
        .Replace(',', ' ')
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .Select(word => new string(word.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray()))
        .Where(word => word.Length > 0)
        .OrderBy(word => word, StringComparer.Ordinal));
}

public sealed record TachoLiveWorker(
    int MemberCode,
    string DisplayName,
    string? CardNumber,
    string? EmployeeNumber,
    string? WorkerType,
    string? AgencyName,
    string? Email,
    string? Started,
    string? CardLastRead,
    string? DriverCardExpiry,
    string? LicencePassDate,
    string? DrivingLicenceExpiry,
    string? LicenceCheckDue,
    string? LicencePhotoExpiry,
    string? CpcExpiry,
    string? DqcExpiry,
    string RawSourceJson);

internal sealed class TachoLiveWorkerDirectory(HttpClient httpClient, TachoMasterOptions options, ILogger logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    public async Task<IReadOnlyList<TachoLiveWorker>> GetLiveWorkersAsync(CancellationToken ct)
    {
        httpClient.Timeout = TimeSpan.FromSeconds(30);
        httpClient.BaseAddress = new Uri(NormaliseBaseUrl(options.BaseUrl));
        var sid = await LoginAsync(ct);
        var result = new List<TachoLiveWorker>();
        var offset = 0;
        for (var page = 0; page < Math.Max(1, options.MaxPages); page++)
        {
            using var response = await SendWithRetryAsync(() =>
            {
                var request = CreateRequest(HttpMethod.Post, "Member/GetMembersLong", sid);
                request.Content = JsonContent.Create(new { Offset = offset, OnlyLiveMembers = true });
                return request;
            }, "Member/GetMembersLong", ct);
            await EnsureSuccessAsync(response, "Member/GetMembersLong", ct);
            var json = await response.Content.ReadAsStringAsync(ct);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var data = Property(root, "Data");
            if (data is not JsonElement rows || rows.ValueKind != JsonValueKind.Array) break;
            var count = 0;
            foreach (var item in rows.EnumerateArray())
            {
                var memberCode = Int(item, "MemCode", "MemberCode");
                if (memberCode <= 0) continue;
                var given = Text(item, "GivenNames", "CName", "Forename", "FirstName");
                var surname = Text(item, "Surname", "SName", "LastName");
                var sourceName = Text(item, "WorkerName", "MemberName", "Name");
                var displayName = string.Join(' ', new[] { given, surname }.Where(value => !string.IsNullOrWhiteSpace(value))).Trim();
                if (displayName.Length == 0) displayName = DisplayName(sourceName);
                if (displayName.Length == 0) continue;

                result.Add(new TachoLiveWorker(
                    memberCode,
                    displayName,
                    Text(item, "CardNoShort", "DriverCardNo", "DriverCardNumber", "CardNumber"),
                    Text(item, "EmployeeNumber", "PayrollNumber"),
                    Text(item, "Type", "WorkerType", "MemberType", "MemType"),
                    Text(item, "Agency", "AgencyName"),
                    Text(item, "Email", "EmailAddress"),
                    Text(item, "Started", "StartDate", "DateStarted"),
                    Text(item, "CardLastRead", "DriverCardLastRead"),
                    Text(item, "DriverCardExp", "DriverCardExpiry", "CardExpiry"),
                    Text(item, "LicencePassDate", "DrivingLicencePassDate"),
                    Text(item, "DrivingLicenceExp", "DrivingLicenceExpiry", "LicenceExpiry"),
                    Text(item, "LicenceCheckDue", "DrivingLicenceCheckDue"),
                    Text(item, "LicencePhotoExp", "LicencePhotoExpiry"),
                    Text(item, "CPCExpiry", "CpcExpiry"),
                    Text(item, "DQCExpiry", "DqcExpiry"),
                    item.GetRawText()));
                count++;
            }

            var moreData = Bool(root, "MoreData");
            var recordCount = Int(root, "RecordCount");
            if (!moreData || recordCount <= 0 || count == 0) break;
            offset += recordCount;
        }

        return result
            .GroupBy(worker => worker.MemberCode)
            .Select(group => group.First())
            .OrderBy(worker => worker.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<string> LoginAsync(CancellationToken ct)
    {
        string? lastFailure = null;
        foreach (var password in PasswordAttempts(options.Password))
        {
            using var response = await SendWithRetryAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, "auth/login");
                request.Headers.Add("APIKEY", options.ApiKey);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Content = JsonContent.Create(new
                {
                    User = options.Username,
                    Pass = password,
                    OsVersion = "1.0",
                    OsName = "Azure Container App",
                    PcName = Environment.MachineName,
                    AuthType = "password"
                });
                return request;
            }, "login", ct);
            if (response.IsSuccessStatusCode)
            {
                var payload = await response.Content.ReadAsStringAsync(ct);
                return ExtractSessionId(payload);
            }
            lastFailure = await FailureDetailAsync(response, "login", ct);
            if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)) break;
        }
        throw new HttpRequestException($"TachoMaster login failed. {lastFailure ?? "No response was received."}");
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, string sid)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("APIKEY", options.ApiKey);
        request.Headers.Add("SID", sid);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(Func<HttpRequestMessage> factory, string operation, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var request = factory();
                var response = await httpClient.SendAsync(request, ct);
                if (!IsTransient(response.StatusCode) || attempt == 3) return response;
                response.Dispose();
            }
            catch (HttpRequestException) when (attempt < 3) { }
            await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), ct);
        }
        throw new InvalidOperationException($"TachoMaster {operation} failed without a response.");
    }

    private static bool IsTransient(HttpStatusCode statusCode) => statusCode == HttpStatusCode.RequestTimeout || statusCode == (HttpStatusCode)429 || (int)statusCode >= 500;
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        throw new HttpRequestException(await FailureDetailAsync(response, operation, ct), null, response.StatusCode);
    }
    private static async Task<string> FailureDetailAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        var detail = await response.Content.ReadAsStringAsync(ct);
        if (detail.Length > 300) detail = detail[..300];
        return $"TachoMaster {operation} returned {(int)response.StatusCode} ({response.ReasonPhrase}). {detail}";
    }
    private static IReadOnlyList<string> PasswordAttempts(string password)
    {
        var trimmed = password.Trim();
        if (trimmed.All(Uri.IsHexDigit) && trimmed.Length is 32 or 40 or 64) return [trimmed];
        return [trimmed, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(trimmed))).ToLowerInvariant()];
    }
    private static string ExtractSessionId(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (root.ValueKind is JsonValueKind.Number or JsonValueKind.String) return root.ToString();
        foreach (var name in new[] { "sid", "SID", "token", "Token" })
            if (root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number) return value.ToString();
        throw new InvalidOperationException("TachoMaster login response did not contain a SID.");
    }
    private static string NormaliseBaseUrl(string value)
    {
        var trimmed = value.Trim().TrimEnd('/');
        return trimmed.EndsWith("/api", StringComparison.OrdinalIgnoreCase) ? $"{trimmed}/" : $"{trimmed}/api/";
    }
    private static string DisplayName(string? source)
    {
        var value = (source ?? string.Empty).Trim();
        var comma = value.IndexOf(',');
        return comma > 0 ? $"{value[(comma + 1)..].Trim()} {value[..comma].Trim()}".Trim() : value;
    }
    private static JsonElement? Property(JsonElement element, params string[] names)
    {
        foreach (var property in element.EnumerateObject())
            if (names.Any(name => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))) return property.Value;
        return null;
    }
    private static string? Text(JsonElement element, params string[] names)
    {
        var value = Property(element, names);
        if (value is not JsonElement item) return null;
        return item.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(item.GetString()) ? null : item.GetString()!.Trim(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => item.ToString(),
            _ => null
        };
    }
    private static int Int(JsonElement element, params string[] names) => int.TryParse(Text(element, names), out var value) ? value : 0;
    private static bool Bool(JsonElement element, params string[] names) => bool.TryParse(Text(element, names), out var value) && value;
}

public sealed class TachoDriverMasterBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<TachoDriverMasterBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(90), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var sync = scope.ServiceProvider.GetRequiredService<TachoDriverMasterSyncService>();
                var result = await sync.SyncAsync("system:tachomaster-canonical-driver-master", stoppingToken);
                if (result.Success) logger.LogInformation("{Message}", result.Message);
                else logger.LogWarning("{Message}", result.Message);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError(ex, "Scheduled TachoMaster canonical Driver Master sync failed."); }

            try { await Task.Delay(TimeSpan.FromHours(4), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
