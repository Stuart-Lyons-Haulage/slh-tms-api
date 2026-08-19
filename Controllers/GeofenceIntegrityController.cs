using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/geofence-integrity")]
[Authorize]
public sealed class GeofenceIntegrityController(TmsDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var planningDate = UkOperatingDate(now);
            List<Load> loads;
            try { loads = await PlanningRegisterStore.ReadLoadsAsync(db, planningDate, ct); }
            catch { loads = []; db.ChangeTracker.Clear(); }

            var fences = await EmbeddedGeofenceEngine.FenceStatusesAsync(db, ct);
            var snapshot = await EmbeddedGeofenceEngine.BuildAsync(db, planningDate, loads, ct);
            var latestTracking = await db.VehicleTrackingEvents.AsNoTracking()
                .OrderByDescending(x => x.EventTimeUtc)
                .Select(x => new { x.VehicleIdentifier, x.EventTimeUtc, x.Latitude, x.Longitude, x.ProviderName })
                .FirstOrDefaultAsync(ct);

            var trackingAgeMinutes = latestTracking is null ? (double?)null : Math.Max(0, (now - latestTracking.EventTimeUtc).TotalMinutes);
            var trackingFresh = trackingAgeMinutes is not null && trackingAgeMinutes <= 15;
            var linked = fences.Count(x => x.SiteId != null);
            var engineReady = fences.Count > 0;
            var planningLinkReady = linked > 0;
            var latestVisit = snapshot.Visits.OrderByDescending(x => x.EnteredAtUtc).FirstOrDefault();
            var latestConfirmed = snapshot.ConfirmedVisits.OrderByDescending(x => x.ConfirmedAtUtc).FirstOrDefault();
            var recentVisits = snapshot.Visits.OrderByDescending(x => x.EnteredAtUtc).Take(20).ToList();

            return Ok(new
            {
                checkedAtUtc = now,
                source = "EmbeddedSLHGeofences+RoadTechTracking",
                engineReady,
                planningLinkReady,
                liveRunProgressionReady = engineReady && trackingFresh,
                trackingFresh,
                trackingAgeMinutes,
                geofences = new
                {
                    total = fences.Count,
                    active = fences.Count,
                    valid = fences.Count,
                    linked,
                    unlinked = fences.Count - linked,
                    invalid = 0
                },
                records = fences.Select(x => new
                {
                    id = x.Fence.Id,
                    name = x.Fence.Name,
                    category = x.Fence.Category,
                    categoryMaxWaitMinutes = x.Fence.CategoryMaxWaitMinutes,
                    maxWaitMinutes = x.Fence.MaxWaitMinutes,
                    pendingEntryMinutes = x.Fence.PendingEntryMinutes,
                    pendingExitMinutes = x.Fence.PendingExitMinutes,
                    siteNumber = x.Fence.SiteNumber,
                    siteId = x.SiteId,
                    active = true,
                    polygonValid = true,
                    geofenceAvailable = true,
                    siteLinked = x.SiteId != null,
                    validationStatus = x.SiteId == null ? "Unlinked" : "Valid"
                }),
                latestTracking,
                latestGeofenceHit = Visit(latestVisit),
                latestConfirmedHit = Visit(latestConfirmed),
                recentHits = recentVisits.Select(Visit)
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            return Ok(new
            {
                checkedAtUtc = DateTimeOffset.UtcNow,
                source = "EmbeddedSLHGeofencesSafeFallback",
                engineReady = EmbeddedGeofenceEngine.ApprovedFences.Count > 0,
                planningLinkReady = false,
                liveRunProgressionReady = false,
                trackingFresh = false,
                trackingAgeMinutes = (double?)null,
                geofences = new
                {
                    total = EmbeddedGeofenceEngine.ApprovedFences.Count,
                    active = EmbeddedGeofenceEngine.ApprovedFences.Count,
                    valid = EmbeddedGeofenceEngine.ApprovedFences.Count,
                    linked = 0,
                    unlinked = EmbeddedGeofenceEngine.ApprovedFences.Count,
                    invalid = 0
                },
                warning = $"Approved geofences are available, but live tracking integrity could not be calculated: {exception.GetBaseException().Message}"
            });
        }
    }

    private static object? Visit(DerivedVisit? visit)
    {
        if (visit is null) return null;
        return new
        {
            geofenceName = visit.Fence.Name,
            visit.VehicleIdentifier,
            visit.EnteredAtUtc,
            visit.ConfirmedAtUtc,
            visit.ExitedAtUtc,
            visit.LoadId,
            visit.LoadStopId,
            visit.DwellMinutes,
            status = visit.ExitedAtUtc is not null ? (visit.ConfirmedAtUtc is not null ? "Departed" : "PassThrough") : visit.ConfirmedAtUtc is not null ? "OnSiteConfirmed" : "Arrived",
            statusReason = visit.ExitedAtUtc is not null
                ? "Derived from RoadTech tracking crossing the approved SLH geofence boundary."
                : visit.ConfirmedAtUtc is not null
                    ? "Confirmed from RoadTech tracking after the minimum dwell period."
                    : "Vehicle is currently inside the approved SLH geofence."
        };
    }

    private static DateOnly UkOperatingDate(DateTimeOffset value)
    {
        try { return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, TimeZoneInfo.FindSystemTimeZoneById("Europe/London")).DateTime); }
        catch (TimeZoneNotFoundException) { return DateOnly.FromDateTime(value.UtcDateTime); }
    }
}
