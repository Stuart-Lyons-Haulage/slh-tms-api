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
    private static readonly string[] PrioritySites = ["Lake Lane", "NWF Selsey", "NWF Runcton", "NWF Merston", "NWF Drayton"];

    public async Task<GeofenceHistoryReplayResult> ReplayAsync(DateOnly planningDate, CancellationToken ct)
    {
        var historical = (await client.GetHistoricalVehicleEventsAsync(planningDate, ct))
            .Select(DotTelemetryRecord.FromProvider)
            .ToList();

        await store.PersistAsync(historical, ct, markAsLiveReceipt: false);

        var loads = await ReadLoadsAsync(planningDate, ct);
        if (loads.Count == 0)
            return new GeofenceHistoryReplayResult(planningDate, historical.Count, 0, 0, 0, EmptySiteDiagnostics(), DateTimeOffset.UtcNow);

        var geofenceLoads = GeofencePlanningMatch.PrepareLoads(loads);
        var snapshot = await EmbeddedGeofenceEngine.BuildAsync(db, planningDate, geofenceLoads, ct);
        await EmbeddedGeofenceSqlProjection.PersistAsync(db, snapshot, ct);

        var siteDiagnostics = PrioritySites
            .Select(site =>
            {
                var visits = snapshot.Visits.Where(x => CanonicalFenceName(x.Fence.Name) == site).ToList();
                return new GeofenceHistoryReplaySiteDiagnostic(
                    site,
                    PlannedStops(loads, site),
                    visits.Count,
                    visits.Count(x => x.LoadStopId is not null),
                    visits.Count(x => x.ExitedAtUtc is not null),
                    visits.Select(x => (DateTimeOffset?)(x.ExitedAtUtc ?? x.LastInsideAtUtc)).OrderByDescending(x => x).FirstOrDefault());
            })
            .ToList();

        logger.LogInformation(
            "Replayed {HistoryCount} RoadTech records for {PlanningDate}; reconstructed {VisitCount} visits, {LinkedCount} linked visits and {DepartureCount} departures. Priority sites: {PrioritySites}.",
            historical.Count,
            planningDate,
            snapshot.Visits.Count,
            snapshot.Visits.Count(x => x.LoadStopId is not null),
            snapshot.Visits.Count(x => x.ExitedAtUtc is not null),
            string.Join(", ", siteDiagnostics.Select(x => $"{x.Site}={x.Visits}/{x.LinkedVisits}/{x.Departures}")));

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
        try
        {
            foreach (var load in await PlanningRegisterStore.ReadLoadsAsync(db, planningDate, ct))
                merged[load.Id] = load;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            logger.LogWarning(exception, "Planning register could not be read during geofence history replay for {PlanningDate}; live Loads will still be tried.", planningDate);
        }

        try
        {
            var live = await db.Loads.AsNoTracking()
                .Include(load => load.Stops)
                .Where(load => load.PlanningDate == planningDate)
                .ToListAsync(ct);
            foreach (var load in live) merged[load.Id] = load;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            logger.LogWarning(exception, "Live Loads could not be read during geofence history replay for {PlanningDate}; planning-register loads will still be used.", planningDate);
        }

        return merged.Values.Where(load => load.Status != LoadStatus.Cancelled).ToList();
    }

    private static IReadOnlyList<GeofenceHistoryReplaySiteDiagnostic> EmptySiteDiagnostics() =>
        PrioritySites.Select(site => new GeofenceHistoryReplaySiteDiagnostic(site, 0, 0, 0, 0, null)).ToList();

    private static int PlannedStops(IEnumerable<Load> loads, string site)
    {
        if (site == "Lake Lane") return 0;
        var locality = site.Replace("NWF ", string.Empty, StringComparison.OrdinalIgnoreCase);
        return loads.SelectMany(load => load.Stops ?? [])
            .Count(stop => stop.Name.Contains(locality, StringComparison.OrdinalIgnoreCase));
    }

    private static string? CanonicalFenceName(string value)
    {
        var normalized = value.ToUpperInvariant();
        if (normalized.Contains("LAKE LANE")) return "Lake Lane";
        if (normalized.Contains("SELSEY")) return "NWF Selsey";
        if (normalized.Contains("RUNCTON")) return "NWF Runcton";
        if (normalized.Contains("MERSTON")) return "NWF Merston";
        if (normalized.Contains("DRAYTON")) return "NWF Drayton";
        return null;
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
    int PlannedStops,
    int Visits,
    int LinkedVisits,
    int Departures,
    DateTimeOffset? LatestEvidenceUtc);
