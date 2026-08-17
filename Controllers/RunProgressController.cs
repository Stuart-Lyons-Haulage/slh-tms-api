using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/run-progress")]
[Authorize]
public sealed class RunProgressController(TmsDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] DateOnly? date, CancellationToken ct)
    {
        await GeofenceRunProgression.EnsureSchemaAsync(db, ct);
        var planningDate = date ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var now = DateTimeOffset.UtcNow;

        var loads = await db.Loads.AsNoTracking().Include(x => x.Stops)
            .Where(x => x.PlanningDate == planningDate && x.Status != LoadStatus.Cancelled)
            .OrderBy(x => x.Reference)
            .Take(500)
            .ToListAsync(ct);

        var loadIds = loads.Select(x => x.Id).ToList();
        var visits = loadIds.Count == 0
            ? []
            : await db.GeofenceVisits.AsNoTracking()
                .Where(x => x.LoadId != null && loadIds.Contains(x.LoadId.Value))
                .OrderBy(x => x.EnteredAtUtc)
                .ToListAsync(ct);

        var geofenceIds = visits.Select(x => x.GeofenceId).Distinct().ToList();
        var geofences = geofenceIds.Count == 0
            ? new Dictionary<Guid, SiteGeofence>()
            : await db.SiteGeofences.AsNoTracking().Where(x => geofenceIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);

        var records = loads.Select(load =>
        {
            var orderedStops = load.Stops.OrderBy(x => x.Sequence).ToList();
            var loadVisits = visits.Where(x => x.LoadId == load.Id).ToList();
            var completedStopIds = loadVisits
                .Where(x => x.LoadStopId != null && x.ConfirmedAtUtc != null && x.ExitedAtUtc != null && x.Status == "Departed")
                .Select(x => x.LoadStopId!.Value)
                .ToHashSet();

            var activeVisit = loadVisits.Where(x => x.ExitedAtUtc == null).OrderByDescending(x => x.EnteredAtUtc).FirstOrDefault();
            var nextStop = orderedStops.FirstOrDefault(x => !completedStopIds.Contains(x.Id));
            var lastDeparture = loadVisits.Where(x => x.ExitedAtUtc != null && x.ConfirmedAtUtc != null)
                .OrderByDescending(x => x.ExitedAtUtc).FirstOrDefault();

            SiteGeofence? activeFence = null;
            if (activeVisit is not null) geofences.TryGetValue(activeVisit.GeofenceId, out activeFence);
            var currentDwellMinutes = activeVisit is null
                ? (int?)null
                : Math.Max(activeVisit.DwellMinutes, Math.Max(0, (int)Math.Floor((now - activeVisit.EnteredAtUtc).TotalMinutes)));
            var waitLimit = activeFence?.MaxWaitMinutes ?? activeFence?.CategoryMaxWaitMinutes;
            var totalStops = orderedStops.Count;
            var completedStops = completedStopIds.Count;
            var progressPercent = totalStops == 0 ? 0m : Math.Round((decimal)completedStops / totalStops * 100m, 1);
            var runState = totalStops > 0 && completedStops >= totalStops
                ? "Completed"
                : activeVisit?.Status ?? (completedStops > 0 ? "BetweenStops" : load.Status.ToString());

            return new
            {
                loadId = load.Id,
                loadReference = load.Reference,
                loadStatus = load.Status.ToString(),
                runState,
                totalStops,
                completedStops,
                progressPercent,
                nextStop = nextStop is null ? null : new { nextStop.Id, nextStop.Sequence, nextStop.Name, nextStop.Address, nextStop.PlannedArrivalUtc },
                currentVisit = activeVisit is null ? null : new
                {
                    activeVisit.Id,
                    geofenceId = activeVisit.GeofenceId,
                    geofenceName = activeFence?.Name,
                    category = activeFence?.Category,
                    activeVisit.LoadStopId,
                    activeVisit.EnteredAtUtc,
                    activeVisit.ConfirmedAtUtc,
                    dwellMinutes = currentDwellMinutes,
                    waitLimitMinutes = waitLimit,
                    isDelayed = activeVisit.Status == "SiteDelay" || waitLimit is int limit && currentDwellMinutes > limit,
                    activeVisit.Status,
                    activeVisit.StatusReason
                },
                lastDeparture = lastDeparture is null ? null : new { lastDeparture.LoadStopId, lastDeparture.ExitedAtUtc, lastDeparture.DwellMinutes },
                calculatedAtUtc = now
            };
        }).ToList();

        return Ok(new { planningDate, calculatedAtUtc = now, count = records.Count, records });
    }
}
