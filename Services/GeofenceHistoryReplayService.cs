using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;

namespace Slh.Tms.Api.Services;

public sealed class GeofenceHistoryReplayService(
    DotTrackingClient client,
    DotTrackingTelemetryStore store,
    TmsDbContext db,
    ILogger<GeofenceHistoryReplayService> logger)
{
    public async Task<GeofenceHistoryReplayResult> ReplayAsync(DateOnly planningDate, CancellationToken ct)
    {
        var historical = (await client.GetHistoricalVehicleEventsAsync(planningDate, ct))
            .Select(DotTelemetryRecord.FromProvider)
            .ToList();

        await store.PersistAsync(historical, ct, markAsLiveReceipt: false);

        var loads = await ReadLoadsAsync(planningDate, ct);
        if (loads.Count == 0)
            return new GeofenceHistoryReplayResult(planningDate, historical.Count, 0, 0, 0, [], DateTimeOffset.UtcNow);

        var snapshot = await EmbeddedGeofenceEngine.BuildAsync(db, planningDate, loads, ct);
        await EmbeddedGeofenceSqlProjection.PersistAsync(db, snapshot, ct);

        var siteDiagnostics = snapshot.Visits
            .Where(x => IsPriorityFence(x.Fence.Name))
            .GroupBy(x => CanonicalFenceName(x.Fence.Name), StringComparer.OrdinalIgnoreCase)
            .Select(group => new GeofenceHistoryReplaySiteDiagnostic(
                group.Key,
                group.Count(),
                group.Count(x => x.LoadStopId is not null),
                group.Count(x => x.ExitedAtUtc is not null),
                group.Max(x => x.ExitedAtUtc ?? x.LastInsideAtUtc)))
            .OrderBy(x => x.Site)
            .ToList();

        logger.LogInformation(
            "Replayed {HistoryCount} RoadTech records for {PlanningDate}; reconstructed {VisitCount} visits, {LinkedCount} linked visits and {DepartureCount} departures.",
            historical.Count,
            planningDate,
            snapshot.Visits.Count,
            snapshot.Visits.Count(x => x.LoadStopId is not null),
            snapshot.Visits.Count(x => x.ExitedAtUtc is not null));

        return new GeofenceHistoryReplayResult(
            planningDate,
            historical.Count,
            snapshot.Visits.Count,
            snapshot.Visits.Count(x => x.LoadStopId is not null),
            snapshot.Visits.Count(x => x.ExitedAtUtc is not null),
            siteDiagnostics,
            DateTimeOffset.UtcNow);
    }

    private async Task<List<Load>> ReadLoadsAsync(DateOnly planningDate, CancellationToken ct)
    {
        var merged = new Dictionary<Guid, Load>();
        foreach (var load in await PlanningRegisterStore.ReadLoadsAsync(db, planningDate, ct))
            merged[load.Id] = load;

        var live = await db.Loads.AsNoTracking()
            .Include(load => load.Stops)
            .Where(load => load.PlanningDate == planningDate)
            .ToListAsync(ct);
        foreach (var load in live) merged[load.Id] = load;

        return merged.Values.Where(load => load.Status != LoadStatus.Cancelled).ToList();
    }

    private static bool IsPriorityFence(string value)
    {
        var normalized = value.ToUpperInvariant();
        return normalized.Contains("LAKE LANE") ||
               normalized.Contains("SELSEY") ||
               normalized.Contains("RUNCTON") ||
               normalized.Contains("MERSTON") ||
               normalized.Contains("DRAYTON");
    }

    private static string CanonicalFenceName(string value)
    {
        var normalized = value.ToUpperInvariant();
        if (normalized.Contains("LAKE LANE")) return "Lake Lane";
        if (normalized.Contains("SELSEY")) return "NWF Selsey";
        if (normalized.Contains("RUNCTON")) return "NWF Runcton";
        if (normalized.Contains("MERSTON")) return "NWF Merston";
        if (normalized.Contains("DRAYTON")) return "NWF Drayton";
        return value;
    }
}

public sealed record GeofenceHistoryReplayResult(
    DateOnly PlanningDate,
    int HistoricalTrackingRecords,
    int ReconstructedVisits,
    int LinkedVisits,
    int Departures,
    IReadOnlyList<GeofenceHistoryReplaySiteDiagnostic> PrioritySites,
    DateTimeOffset CompletedAtUtc);

public sealed record GeofenceHistoryReplaySiteDiagnostic(
    string Site,
    int Visits,
    int LinkedVisits,
    int Departures,
    DateTimeOffset LatestEvidenceUtc);
