using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/planning/geofence-linkage")]
[Authorize(Policy = "TmsAccess")]
public sealed class RunGeofenceLinkageController(TmsDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] DateOnly date, CancellationToken ct)
    {
        var loads = await ReadLoadsAsync(date, ct);
        var resolver = await PlannerSourceMasterDataResolver.CreateAsync(db, ct);
        var loadIds = loads.Select(load => load.Id).ToHashSet();
        var stopIds = loads.SelectMany(load => load.Stops ?? []).Select(stop => stop.Id).ToHashSet();

        List<GeofenceVisit> visits;
        try
        {
            visits = await db.GeofenceVisits.AsNoTracking()
                .Where(visit =>
                    (visit.LoadId != null && loadIds.Contains(visit.LoadId.Value)) ||
                    (visit.LoadStopId != null && stopIds.Contains(visit.LoadStopId.Value)))
                .OrderBy(visit => visit.EnteredAtUtc)
                .ToListAsync(ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            visits = [];
        }

        var rows = loads
            .Where(load => load.Status != LoadStatus.Cancelled)
            .OrderBy(load => load.Reference)
            .SelectMany(load =>
            {
                var stops = (load.Stops ?? []).OrderBy(stop => stop.Sequence).ToList();
                var finalSequence = stops.Count == 0 ? 0 : stops.Max(stop => stop.Sequence);
                return stops.Select(stop =>
                {
                    var resolution = resolver.Resolve(stop.Name);
                    var stopVisits = visits.Where(visit => visit.LoadStopId == stop.Id).OrderBy(visit => visit.EnteredAtUtc).ToList();
                    var issue = !resolution.SiteMatched
                        ? "SiteNameNotResolved"
                        : !resolution.GeofenceLinked
                            ? "SiteMatchedGeofenceUnlinked"
                            : null;

                    return new
                    {
                        loadId = load.Id,
                        run = load.Reference,
                        vehicleId = load.VehicleId,
                        stopId = stop.Id,
                        stop.Sequence,
                        stopName = stop.Name,
                        finalDelivery = stop.Sequence == finalSequence,
                        siteMatched = resolution.SiteMatched,
                        siteCode = resolution.SiteNumber,
                        siteName = resolution.SiteName,
                        geofenceLinked = resolution.GeofenceLinked,
                        geofenceName = resolution.GeofenceName,
                        issue,
                        visitRecorded = stopVisits.Count > 0,
                        latestEnterUtc = stopVisits.LastOrDefault()?.EnteredAtUtc,
                        latestExitUtc = stopVisits.LastOrDefault()?.ExitedAtUtc,
                        evidence = resolution.EvidenceNote
                    };
                });
            })
            .ToList();

        var issues = rows.Where(row => row.issue is not null).ToList();
        return Ok(new
        {
            planningDate = date,
            runs = loads.Count(load => load.Status != LoadStatus.Cancelled),
            stops = rows.Count,
            siteNameUnresolved = issues.Count(row => row.issue == "SiteNameNotResolved"),
            siteMatchedButGeofenceUnlinked = issues.Count(row => row.issue == "SiteMatchedGeofenceUnlinked"),
            linkedStops = rows.Count(row => row.siteMatched && row.geofenceLinked),
            stopsWithVisitEvidence = rows.Count(row => row.visitRecorded),
            issues,
            records = rows,
            checkedAtUtc = DateTimeOffset.UtcNow
        });
    }

    private async Task<List<Load>> ReadLoadsAsync(DateOnly date, CancellationToken ct)
    {
        var merged = new Dictionary<Guid, Load>();
        try
        {
            foreach (var load in await PlanningRegisterStore.ReadLoadsAsync(db, date, ct))
                merged[load.Id] = load;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
        }

        try
        {
            var sqlLoads = await db.Loads.AsNoTracking()
                .Include(load => load.Stops)
                .Where(load => load.PlanningDate == date)
                .ToListAsync(ct);
            foreach (var load in sqlLoads) merged[load.Id] = load;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
        }

        return merged.Values.ToList();
    }
}
