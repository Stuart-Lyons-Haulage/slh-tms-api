using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
        // Use the same merged planning source and the same reconstructed + durable
        // RoadTech evidence as Run Progress. The old diagnostics endpoint only read
        // durable GeofenceVisits by exact LoadStopId, so the progress bar could advance
        // while this panel incorrectly continued to report "LINKED · NO HIT".
        var loads = (await PlanningResilience.ReadLoadsAsync(db, date, ct))
            .Where(load => load.Status != LoadStatus.Cancelled)
            .ToList();
        var resolver = await PlannerSourceMasterDataResolver.CreateAsync(db, ct);
        var geofenceLoads = GeofencePlanningMatch.PrepareLoads(loads);
        var snapshot = await EmbeddedGeofenceEngine.BuildAsync(db, date, geofenceLoads, ct);
        snapshot = await EmbeddedGeofenceEvidenceMerge.MergeDurableProjectionAsync(db, snapshot, loads, ct);
        var visits = snapshot.Visits
            .Where(visit => visit.LoadId is not null)
            .OrderBy(visit => visit.EnteredAtUtc)
            .ToList();

        var rows = loads
            .OrderBy(load => load.Reference)
            .SelectMany(load =>
            {
                var stops = (load.Stops ?? []).OrderBy(stop => stop.Sequence).ToList();
                var finalSequence = stops.Count == 0 ? 0 : stops.Max(stop => stop.Sequence);
                return stops.Select(stop =>
                {
                    var resolution = resolver.Resolve(stop.Name);
                    var stopVisits = visits
                        .Where(visit => visit.LoadId == load.Id && visit.LoadStopId == stop.Id)
                        .OrderBy(visit => visit.EnteredAtUtc)
                        .ToList();
                    var latestVisit = stopVisits.LastOrDefault();
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
                        geofenceName = latestVisit?.Fence.Name ?? resolution.GeofenceName,
                        issue,
                        visitRecorded = latestVisit is not null,
                        latestEnterUtc = latestVisit?.EnteredAtUtc,
                        latestExitUtc = latestVisit?.ExitedAtUtc,
                        confirmedAtUtc = latestVisit?.ConfirmedAtUtc,
                        evidence = resolution.EvidenceNote
                    };
                });
            })
            .ToList();

        var issues = rows.Where(row => row.issue is not null).ToList();
        var hitRuns = rows.Where(row => row.visitRecorded).Select(row => row.loadId).Distinct().Count();
        return Ok(new
        {
            planningDate = date,
            runs = loads.Count,
            stops = rows.Count,
            siteNameUnresolved = issues.Count(row => row.issue == "SiteNameNotResolved"),
            siteMatchedButGeofenceUnlinked = issues.Count(row => row.issue == "SiteMatchedGeofenceUnlinked"),
            linkedStops = rows.Count(row => row.siteMatched && row.geofenceLinked),
            stopsWithVisitEvidence = rows.Count(row => row.visitRecorded),
            runsWithVisitEvidence = hitRuns,
            issues,
            records = rows,
            checkedAtUtc = DateTimeOffset.UtcNow
        });
    }
}
