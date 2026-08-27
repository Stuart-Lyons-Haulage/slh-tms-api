using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Calculates configuration coverage independently from runtime geofence evidence.
/// A run is configured/linked when at least one of its planned stops resolves through
/// Site Master to an active linked geofence. Runtime hits are deliberately not used in
/// this calculation so a telemetry fault cannot make configured linkage appear to be 0.
/// </summary>
public static class RunGeofenceConfigurationCoverage
{
    public static async Task<RunGeofenceCoverage> CalculateAsync(
        TmsDbContext db,
        IReadOnlyCollection<Load> loads,
        CancellationToken ct)
    {
        var activeGeofenceCount = 0;
        try
        {
            activeGeofenceCount = await db.SiteGeofences.AsNoTracking()
                .CountAsync(fence => fence.Active, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
        }

        PlannerSourceMasterDataResolver? resolver = null;
        try
        {
            resolver = await PlannerSourceMasterDataResolver.CreateAsync(db, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
        }

        var totalStops = loads.Where(load => load.Status != LoadStatus.Cancelled).SelectMany(load => load.Stops ?? []).Count();
        if (resolver is null)
            return new RunGeofenceCoverage(activeGeofenceCount, 0, 0, totalStops, new Dictionary<Guid, RunGeofenceStopCoverage>());

        var linkedRuns = 0;
        var linkedStops = 0;
        var stopCoverage = new Dictionary<Guid, RunGeofenceStopCoverage>();

        foreach (var load in loads.Where(load => load.Status != LoadStatus.Cancelled))
        {
            var runLinked = false;
            foreach (var stop in load.Stops ?? [])
            {
                var resolution = resolver.Resolve(stop.Name);
                stopCoverage[stop.Id] = new RunGeofenceStopCoverage(
                    stop.Id,
                    stop.Sequence,
                    stop.Name,
                    resolution.SiteMatched,
                    resolution.SiteId,
                    resolution.SiteNumber,
                    resolution.SiteName,
                    resolution.GeofenceLinked,
                    resolution.GeofenceId,
                    resolution.GeofenceName,
                    resolution.EvidenceNote);

                if (!resolution.GeofenceLinked) continue;
                linkedStops++;
                runLinked = true;
            }
            if (runLinked) linkedRuns++;
        }

        return new RunGeofenceCoverage(activeGeofenceCount, linkedRuns, linkedStops, totalStops, stopCoverage);
    }
}

public sealed record RunGeofenceCoverage(
    int ActiveGeofenceCount,
    int LinkedRuns,
    int LinkedStops,
    int TotalStops,
    IReadOnlyDictionary<Guid, RunGeofenceStopCoverage> Stops);

public sealed record RunGeofenceStopCoverage(
    Guid StopId,
    int Sequence,
    string StopName,
    bool SiteMatched,
    Guid? SiteId,
    string? SiteNumber,
    string? SiteName,
    bool GeofenceLinked,
    Guid? GeofenceId,
    string? GeofenceName,
    string EvidenceNote);
