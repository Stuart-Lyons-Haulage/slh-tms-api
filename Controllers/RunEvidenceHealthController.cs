using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Cross-system operational evidence for today's planned runs. This endpoint is
/// deliberately evidence-focused: a green configuration check is not enough to
/// prove that RoadTech geofences or Tacho/Falcon driver identities are actually
/// matching the runs shown on the wallboards.
/// </summary>
[ApiController, Route("api/v1/health/run-evidence")]
[Authorize]
public sealed class RunEvidenceHealthController(
    TmsDbContext db,
    TachoMasterClient tachoMaster,
    ILogger<RunEvidenceHealthController> logger,
    IConfiguration configuration) : ControllerBase
{
    [HttpGet, AllowAnonymous]
    public async Task<IActionResult> Get([FromQuery] DateOnly? date, CancellationToken ct)
    {
        if (!TvWallboardAccess.IsAllowed(HttpContext, configuration)) return Unauthorized();

        var planningDate = date ?? UkOperatingDate(DateTimeOffset.UtcNow);
        var loads = (await PlanningRegisterStore.ReadLoadsAsync(db, planningDate, ct))
            .Where(load => load.Status != LoadStatus.Cancelled)
            .OrderBy(load => load.Reference)
            .ToList();
        var geofenceLoads = GeofencePlanningMatch.PrepareLoads(loads);
        var snapshot = await EmbeddedGeofenceEngine.BuildAsync(db, planningDate, geofenceLoads, ct);
        var tacho = await RunTachoEvidenceResolver.ResolveAsync(db, tachoMaster, loads, planningDate, logger, ct);

        var runEvidence = loads.Select(load =>
        {
            var stops = (load.Stops ?? []).OrderBy(stop => stop.Sequence).ToList();
            var visits = snapshot.Visits
                .Where(visit => visit.LoadId == load.Id)
                .OrderBy(visit => visit.EnteredAtUtc)
                .ToList();
            var departures = visits.Where(visit => visit.ExitedAtUtc is not null).ToList();
            var finalStop = stops.LastOrDefault();
            var finalVisit = finalStop is null
                ? null
                : visits.Where(visit => visit.LoadStopId == finalStop.Id)
                    .OrderByDescending(visit => visit.EnteredAtUtc)
                    .FirstOrDefault();
            var tachoEvidence = tacho.ByLoadId.GetValueOrDefault(load.Id);
            var completionEvidence = finalVisit is null ? "None" : "FinalGeofenceArrival";
            var progressEvidence = visits.Count == 0
                ? "None"
                : departures.Count > 0
                    ? "GeofenceDeparture"
                    : "GeofenceArrival";

            return new
            {
                loadId = load.Id,
                loadReference = load.Reference,
                totalStops = stops.Count,
                geofenceVisits = visits.Count,
                geofenceDepartures = departures.Count,
                lastGeofenceEvidenceUtc = visits
                    .Select(visit => visit.ExitedAtUtc ?? visit.LastInsideAtUtc)
                    .OrderByDescending(value => value)
                    .FirstOrDefault(),
                finalStopName = finalStop?.Name,
                finalStopArrivalUtc = finalVisit?.EnteredAtUtc,
                finalStopDepartureUtc = finalVisit?.ExitedAtUtc,
                completionEvidence,
                progressEvidence,
                tachoStatus = tachoEvidence?.Status ?? "Unknown",
                tachoSource = tachoEvidence?.EvidenceSource,
                tachoExplanation = tachoEvidence?.Explanation
            };
        }).ToList();

        var prioritySites = new[] { "Lake Lane", "NWF Selsey", "NWF Runcton", "NWF Merston", "NWF Drayton" }
            .Select(site =>
            {
                var visits = snapshot.Visits.Where(visit => CanonicalFenceName(visit.Fence.Name) == site).ToList();
                return new
                {
                    site,
                    plannedStops = PlannedStops(loads, site),
                    visits = visits.Count,
                    linkedVisits = visits.Count(visit => visit.LoadStopId is not null),
                    departures = visits.Count(visit => visit.ExitedAtUtc is not null),
                    latestEvidenceUtc = visits
                        .Select(visit => (DateTimeOffset?)(visit.ExitedAtUtc ?? visit.LastInsideAtUtc))
                        .OrderByDescending(value => value)
                        .FirstOrDefault()
                };
            })
            .ToList();

        var statusCounts = tacho.StatusCounts ?? new Dictionary<string, int>();
        var completedRuns = runEvidence.Count(run => run.completionEvidence == "FinalGeofenceArrival");
        var runsWithProgress = runEvidence.Count(run => run.geofenceVisits > 0);
        var runsWithDeparture = runEvidence.Count(run => run.geofenceDepartures > 0);
        var evidenceGapRuns = runEvidence
            .Where(run => run.geofenceVisits == 0 || run.tachoStatus is "NoTachoDuty" or "Mismatch" or "Unavailable")
            .Select(run => new
            {
                run.loadReference,
                run.geofenceVisits,
                run.geofenceDepartures,
                run.progressEvidence,
                run.tachoStatus,
                run.tachoSource,
                run.tachoExplanation
            })
            .ToList();

        return Ok(new
        {
            planningDate,
            checkedAtUtc = DateTimeOffset.UtcNow,
            source = "PlanningRegister+RoadTechFalcon+EmbeddedGeofences+TachoMaster",
            tracking = new
            {
                observationCount = snapshot.TrackingEventCount,
                snapshot.LatestTrackingUtc
            },
            geofences = new
            {
                approvedFenceCount = snapshot.Fences.Count,
                visitCount = snapshot.Visits.Count,
                departureCount = snapshot.Visits.Count(visit => visit.ExitedAtUtc is not null),
                linkedRunCount = snapshot.Visits.Where(visit => visit.LoadId is not null).Select(visit => visit.LoadId!.Value).Distinct().Count(),
                runsWithProgress,
                runsWithDeparture,
                completedRuns,
                plannedRuns = loads.Count
            },
            tacho = new
            {
                configured = tachoMaster.IsConfigured,
                tacho.Available,
                tacho.Warning,
                providerVehicles = tacho.ProviderVehicles,
                providerEvidenceRecords = tacho.ProviderEvidenceRecords,
                tachoDutyRecords = tacho.TachoDutyRecords,
                falconCardRecords = tacho.FalconCardRecords,
                statusCounts
            },
            prioritySites,
            evidenceGapCount = evidenceGapRuns.Count,
            evidenceGaps = evidenceGapRuns
        });
    }

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

    private static DateOnly UkOperatingDate(DateTimeOffset value)
    {
        try
        {
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, TimeZoneInfo.FindSystemTimeZoneById("Europe/London")).DateTime);
        }
        catch (TimeZoneNotFoundException)
        {
            return DateOnly.FromDateTime(value.UtcDateTime);
        }
    }
}
