using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// One-time production maintenance requested before email-attachment order intake goes live.
/// Clears test/legacy operational planning data while deliberately preserving master data,
/// integrations, RoadTech tracking history/live status, geofences and audit history.
/// </summary>
public static class PreEmailCleanSlateMaintenance
{
    internal const string MarkerKey = "maintenance:pre-email-clean-slate:20260822";
    private const string MarkerType = "maintenance";
    internal static readonly DateTimeOffset DriverActivityCutoffUtc = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly HashSet<string> OperationalStagingTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "planningload",
        "order",
        "register:order",
        "plannerplanrun",
        "plannerplansourcerun",
        "planningpalletallocation",
        "runoperational",
        "loadcommercial",
        "planbaseline",
        "planchangeevent"
    };

    public static async Task<PreEmailCleanSlateResult?> ApplyOnceAsync(
        TmsDbContext db,
        TachoMasterClient tachoMaster,
        ILogger logger,
        CancellationToken ct)
    {
        if (await db.StagedImports.AsNoTracking().AnyAsync(row => row.IdempotencyKey == MarkerKey, ct))
            return null;

        var startedAtUtc = DateTimeOffset.UtcNow;
        var drivers = await db.Drivers.Where(driver => driver.Active).OrderBy(driver => driver.DisplayName).ToListAsync(ct);
        await MasterDetailStore.EnrichDriversAsync(db, drivers, ct);

        var archivedDrivers = new List<ArchivedDriver>();
        var driverArchiveSkipped = false;
        if (tachoMaster.IsConfigured)
        {
            try
            {
                var profiles = await tachoMaster.GetDriverProfilesAsync(ct);
                foreach (var driver in drivers)
                {
                    var profile = MatchProfile(driver, profiles);
                    if (profile?.MetricsValidAtUtc is not DateTimeOffset lastEvidence || lastEvidence >= DriverActivityCutoffUtc)
                        continue;

                    driver.Active = false;
                    archivedDrivers.Add(new ArchivedDriver(driver.Id, driver.EmployeeNumber, driver.DisplayName, profile.MemberCode, profile.CardNumber, lastEvidence));
                    db.MasterDataAudits.Add(new MasterDataAudit
                    {
                        EntityType = "Driver",
                        EntityId = driver.Id,
                        Action = "Archived",
                        ChangedBy = "system:pre-email-clean-slate",
                        ChangesJson = JsonSerializer.Serialize(new
                        {
                            reason = "No TachoMaster card/driver metric evidence in 2026",
                            cutoffUtc = DriverActivityCutoffUtc,
                            lastTachoEvidenceUtc = lastEvidence,
                            profile.MemberCode,
                            profile.CardNumber
                        })
                    });
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                driverArchiveSkipped = true;
                logger.LogWarning(exception, "Pre-email clean slate could not safely evaluate TachoMaster driver activity; no drivers will be archived by this maintenance run.");
            }
        }
        else
        {
            driverArchiveSkipped = true;
            logger.LogWarning("Pre-email clean slate skipped driver archiving because TachoMaster is not configured.");
        }

        // Keep geofence/tracking evidence, but detach it from jobs that are being removed.
        var detachedGeofenceVisits = await SafeExecuteAsync(
            () => db.GeofenceVisits.Where(visit => visit.LoadId != null || visit.LoadStopId != null)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(visit => visit.LoadId, (Guid?)null)
                    .SetProperty(visit => visit.LoadStopId, (Guid?)null), ct),
            logger,
            "detach historic geofence visits");

        var etaSnapshotsDeleted = await SafeExecuteAsync(
            () => db.EtaSnapshots.ExecuteDeleteAsync(ct), logger, "clear ETA snapshots");
        var driverStatusLogsDeleted = await SafeExecuteAsync(
            () => db.DriverStatusLogs.ExecuteDeleteAsync(ct), logger, "clear historic driver status logs");
        var loadStopsDeleted = await SafeExecuteAsync(
            () => db.LoadStops.ExecuteDeleteAsync(ct), logger, "clear load stops");
        var loadsDeleted = await SafeExecuteAsync(
            () => db.Loads.ExecuteDeleteAsync(ct), logger, "clear loads");
        var ordersDeleted = await SafeExecuteAsync(
            () => db.TransportOrders.ExecuteDeleteAsync(ct), logger, "clear transport orders");

        var stagingRows = await db.StagedImports
            .Where(row =>
                OperationalStagingTypes.Contains(row.EntityType) ||
                row.IdempotencyKey.StartsWith("planimport:") ||
                row.IdempotencyKey.StartsWith("planimport-source:") ||
                row.IdempotencyKey.StartsWith("planningload:") ||
                row.IdempotencyKey.StartsWith("palletallocation:") ||
                row.IdempotencyKey.StartsWith("planbaseline:") ||
                row.IdempotencyKey.StartsWith("planchange:"))
            .ToListAsync(ct);
        var stagingRowsDeleted = stagingRows.Count;
        if (stagingRowsDeleted > 0) db.StagedImports.RemoveRange(stagingRows);

        var completedAtUtc = DateTimeOffset.UtcNow;
        var result = new PreEmailCleanSlateResult(
            startedAtUtc,
            completedAtUtc,
            loadStopsDeleted,
            loadsDeleted,
            ordersDeleted,
            etaSnapshotsDeleted,
            driverStatusLogsDeleted,
            detachedGeofenceVisits,
            stagingRowsDeleted,
            archivedDrivers,
            driverArchiveSkipped);

        db.StagedImports.Add(new StagedImport
        {
            EntityType = MarkerType,
            IdempotencyKey = MarkerKey,
            PayloadJson = JsonSerializer.Serialize(result),
            Source = "SLH pre-email clean slate",
            Status = StagingStatus.Promoted,
            ReceivedAtUtc = completedAtUtc,
            ReviewedAtUtc = completedAtUtc,
            ReviewedBy = "system:pre-email-clean-slate",
            ReviewNote = "One-time operational reset before email attachment order intake. Master/integration/tracking configuration preserved."
        });

        await db.SaveChangesAsync(ct);
        logger.LogWarning(
            "Pre-email clean slate completed: {Orders} orders, {Loads} loads, {Stops} stops, {Staging} operational staging rows cleared; {Drivers} stale drivers archived; driver archive skipped={DriverArchiveSkipped}.",
            ordersDeleted, loadsDeleted, loadStopsDeleted, stagingRowsDeleted, archivedDrivers.Count, driverArchiveSkipped);
        return result;
    }

    internal static TachoDriverProfile? MatchProfile(Driver driver, IReadOnlyList<TachoDriverProfile> profiles)
    {
        if (int.TryParse(driver.TachoMasterDriverId, out var memberCode) && memberCode > 0)
        {
            var byMember = profiles.FirstOrDefault(profile => profile.MemberCode == memberCode);
            if (byMember is not null) return byMember;
        }

        if (!string.IsNullOrWhiteSpace(driver.TachoCardNumber))
        {
            var byCard = profiles.Where(profile => CardsMatch(driver.TachoCardNumber, profile.CardNumber)).Take(2).ToList();
            if (byCard.Count == 1) return byCard[0];
        }

        if (!string.IsNullOrWhiteSpace(driver.EmployeeNumber))
        {
            var employee = Normalise(driver.EmployeeNumber);
            var byEmployee = profiles.Where(profile => !string.IsNullOrWhiteSpace(profile.EmployeeNumber) && Normalise(profile.EmployeeNumber) == employee).Take(2).ToList();
            if (byEmployee.Count == 1) return byEmployee[0];
        }

        var names = new[] { driver.TachoName, driver.DisplayName }
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => NormalisePerson(name!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var byName = profiles.Where(profile => names.Contains(NormalisePerson(profile.DriverName))).Take(2).ToList();
        return byName.Count == 1 ? byName[0] : null;
    }

    internal static bool CardsMatch(string? left, string? right)
    {
        var a = Normalise(left ?? string.Empty);
        var b = Normalise(right ?? string.Empty);
        if (a.Length < 8 || b.Length < 8) return false;
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ||
               a.EndsWith(b, StringComparison.OrdinalIgnoreCase) ||
               b.EndsWith(a, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<int> SafeExecuteAsync(Func<Task<int>> action, ILogger logger, string operation)
    {
        try { return await action(); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Pre-email clean slate could not {Operation}; continuing with the remaining cleanup so diagnostics stay available.", operation);
            return 0;
        }
    }

    private static string Normalise(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static string NormalisePerson(string value) => string.Join(' ', value
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .Select(word => new string(word.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray()))
        .Where(word => word.Length > 0)
        .OrderBy(word => word, StringComparer.Ordinal));
}

public sealed record ArchivedDriver(Guid DriverId, string EmployeeNumber, string DisplayName, int TachoMemberCode, string? TachoCardNumber, DateTimeOffset LastTachoEvidenceUtc);
public sealed record PreEmailCleanSlateResult(
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    int LoadStopsDeleted,
    int LoadsDeleted,
    int OrdersDeleted,
    int EtaSnapshotsDeleted,
    int DriverStatusLogsDeleted,
    int GeofenceVisitsDetached,
    int OperationalStagingRowsDeleted,
    IReadOnlyList<ArchivedDriver> ArchivedDrivers,
    bool DriverArchiveSkipped);
