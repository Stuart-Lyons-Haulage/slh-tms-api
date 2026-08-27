using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/run-progress")]
[Authorize]
public sealed class RunProgressController(
    TmsDbContext db,
    DotTrackingClient trackingClient,
    DotTrackingTelemetryStore telemetryStore,
    TachoMasterClient tachoMaster,
    ILogger<RunProgressController> logger,
    IConfiguration configuration) : ControllerBase
{
    private static readonly TimeSpan LiveRefreshBudget = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan LiveTrackingThreshold = TimeSpan.FromMinutes(5);

    [HttpGet, AllowAnonymous]
    public async Task<IActionResult> Get([FromQuery] DateOnly? date, CancellationToken ct)
    {
        if (!TvWallboardAccess.IsAllowed(HttpContext, configuration)) return Unauthorized();

        var planningDate = date ?? UkOperatingDate(DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow;

        try
        {
            string? refreshWarning = null;
            try
            {
                // Keep Operations progression on the same current Falcon evidence as
                // the Hisense route board. SQL remains the resilience fallback when
                // RoadTech is temporarily unavailable.
                using var liveRefresh = CancellationTokenSource.CreateLinkedTokenSource(ct);
                liveRefresh.CancelAfter(LiveRefreshBudget);
                var trackingRecords = (await trackingClient.GetLatestVehicleEventsAsync(liveRefresh.Token))
                    .Select(DotTelemetryRecord.FromProvider)
                    .Where(record => record.Latitude is not null && record.Longitude is not null)
                    .ToList();

                if (trackingRecords.Count > 0)
                    await telemetryStore.PersistAsync(trackingRecords, ct, markAsLiveReceipt: true);
            }
            catch (OperationCanceledException exception) when (!ct.IsCancellationRequested)
            {
                db.ChangeTracker.Clear();
                logger.LogWarning(
                    exception,
                    "RoadTech live refresh timed out for Operations progression on {PlanningDate}; using stored tracking evidence.",
                    planningDate);
                refreshWarning = "Live Falcon refresh timed out; progression is using the latest stored tracking evidence.";
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                db.ChangeTracker.Clear();
                logger.LogWarning(
                    exception,
                    "RoadTech live refresh failed for Operations progression on {PlanningDate}; using stored tracking evidence.",
                    planningDate);
                refreshWarning = "Live Falcon refresh was unavailable; progression is using the latest stored tracking evidence.";
            }

            // Production planning is register-backed. Do not probe the absent legacy
            // Loads table here: doing so only generates false operational warnings.
            var loads = (await PlanningRegisterStore.ReadLoadsAsync(db, planningDate, ct))
                .Where(x => x.Status != LoadStatus.Cancelled)
                .OrderBy(x => x.Reference)
                .ToList();

            // The planner intentionally uses concise operational labels such as
            // NWF-Merston, while Falcon calls the same fence Merston (Natures Way).
            // Match on a cloned view so the planner-facing labels remain unchanged.
            var geofenceLoads = GeofencePlanningMatch.PrepareLoads(loads);
            var snapshot = await EmbeddedGeofenceEngine.BuildAsync(db, planningDate, geofenceLoads, ct);

            // A temporary RoadTech/history gap must never erase an ENTER/EXIT that was
            // already proved on an earlier refresh. Merge the durable SQL projection
            // before calculating wallboard status and stop completion.
            snapshot = await EmbeddedGeofenceEvidenceMerge.MergeDurableProjectionAsync(db, snapshot, loads, ct);

            // The same refresh that reads fresh RoadTech GPS now also publishes the
            // reconstructed ENTER/EXIT state immediately. This keeps every consumer
            // (Operations wallboard, TV wallboard and geofence health) on the same
            // current evidence instead of waiting for the background projection cycle.
            try
            {
                await EmbeddedGeofenceSqlProjection.PersistAsync(db, snapshot, ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                db.ChangeTracker.Clear();
                logger.LogWarning(
                    exception,
                    "Fresh RoadTech geofence projection failed for Operations progression on {PlanningDate}; returning the live reconstructed snapshot.",
                    planningDate);
                refreshWarning = string.Join(" ", new[]
                {
                    refreshWarning,
                    "Fresh geofence progress could not be written to the shared SQL projection on this refresh."
                }.Where(value => !string.IsNullOrWhiteSpace(value)));
            }

            // Movement/ignition/card evidence is deliberately included on run-progress
            // itself. The wallboard must not depend on the separate TV route-progress
            // request to know that a truck is moving or parked.
            var liveByLoad = await ResolveLiveByLoadAsync(db, loads, ct);
            var tachoEvidence = await RunTachoEvidenceResolver.ResolveAsync(db, tachoMaster, loads, planningDate, logger, ct);
            var records = loads.Select(load => BuildRecord(
                load,
                snapshot,
                now,
                tachoEvidence.ByLoadId.GetValueOrDefault(load.Id),
                liveByLoad.GetValueOrDefault(load.Id))).ToList();

            return Ok(new
            {
                planningDate,
                calculatedAtUtc = now,
                count = records.Count,
                source = "RoadTechCurrent+StoredLive+DurableGeofenceProjection+PlanningRegister",
                geofenceAvailable = snapshot.Fences.Count > 0,
                geofenceCount = snapshot.Fences.Count,
                geofenceVisitCount = snapshot.Visits.Count,
                geofenceLinkedRuns = snapshot.Visits.Where(x => x.LoadId != null).Select(x => x.LoadId!.Value).Distinct().Count(),
                trackingEventCount = snapshot.TrackingEventCount,
                latestTrackingUtc = LatestTracking(snapshot.LatestTrackingUtc, liveByLoad.Values),
                tachoAvailable = tachoEvidence.Available,
                tachoWarning = tachoEvidence.Warning,
                warning = string.Join(" ", new[] { refreshWarning, tachoEvidence.Warning }.Where(value => !string.IsNullOrWhiteSpace(value))),
                records
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Embedded geofence run progression failed for {PlanningDate}.", planningDate);
            db.ChangeTracker.Clear();

            List<Load> loads;
            try { loads = await PlanningRegisterStore.ReadLoadsAsync(db, planningDate, ct); }
            catch { loads = []; db.ChangeTracker.Clear(); }
            var liveByLoad = await ResolveLiveByLoadAsync(db, loads, ct);
            var tachoEvidence = await RunTachoEvidenceResolver.ResolveAsync(db, tachoMaster, loads, planningDate, logger, ct);

            var records = loads.OrderBy(x => x.Reference).Select(load =>
            {
                var stops = (load.Stops ?? []).OrderBy(x => x.Sequence).ToList();
                var next = stops.FirstOrDefault();
                var tacho = tachoEvidence.ByLoadId.GetValueOrDefault(load.Id);
                var live = liveByLoad.GetValueOrDefault(load.Id);
                var trackingObservedAtUtc = live?.LastReceivedAtUtc;
                var trackingAge = trackingObservedAtUtc is null ? (TimeSpan?)null : now - trackingObservedAtUtc.Value;
                var trackingFresh = trackingAge is not null && trackingAge.Value >= TimeSpan.Zero && trackingAge.Value <= LiveTrackingThreshold;
                var trackingMoving = trackingFresh && live is not null && (live.IsMoving == true || (live.SpeedKph ?? 0) > 2);
                return new
                {
                    loadId = load.Id,
                    loadReference = load.Reference,
                    loadStatus = load.Status.ToString(),
                    runState = trackingMoving ? "InProgress" : InferredRunState(load, stops, now),
                    totalStops = stops.Count,
                    completedStops = 0,
                    progressPercent = 0m,
                    nextStop = next is null ? null : new { next.Id, next.Sequence, next.Name, next.Address, next.PlannedArrivalUtc },
                    phase = trackingMoving ? "Heading to" : "Next job",
                    focusStop = next?.Name,
                    geofenceOnSite = false,
                    trackingFresh,
                    trackingMoving,
                    ignitionOn = trackingFresh ? live?.IgnitionOn : null,
                    driverCardPresent = trackingFresh ? !string.IsNullOrWhiteSpace(live?.CurrentDriverCardNumber) : (bool?)null,
                    trackingObservedAtUtc,
                    trackingAgeSeconds = trackingAge is null ? (int?)null : (int)Math.Max(0, Math.Floor(trackingAge.Value.TotalSeconds)),
                    speedKph = trackingFresh ? live?.SpeedKph : null,
                    stopDwell = Array.Empty<object>(),
                    linkageException = (object?)null,
                    currentVisit = (object?)null,
                    lastDeparture = (object?)null,
                    tacho,
                    calculatedAtUtc = now
                };
            }).ToList();

            var geofenceCount = 0;
            try { geofenceCount = EmbeddedGeofenceEngine.ApprovedFences.Count; }
            catch (Exception geofenceException) when (geofenceException is not OperationCanceledException)
            {
                logger.LogError(geofenceException, "Approved geofence payload could not be initialised while building safe run-progress fallback.");
            }

            return Ok(new
            {
                planningDate,
                calculatedAtUtc = now,
                count = records.Count,
                source = "PlanningRegister+StoredLiveSafeFallback",
                geofenceAvailable = geofenceCount > 0,
                geofenceCount,
                geofenceVisitCount = 0,
                geofenceLinkedRuns = 0,
                latestTrackingUtc = LatestTracking(null, liveByLoad.Values),
                tachoAvailable = tachoEvidence.Available,
                tachoWarning = tachoEvidence.Warning,
                warning = string.Join(" ", new[]
                {
                    geofenceCount > 0
                    ? "Approved SLH geofences are loaded, but live progression could not be calculated from tracking on this refresh. Stored live movement remains active."
                    : "Live run progression could not be calculated and the approved geofence payload was unavailable on this refresh. Stored live movement remains active.",
                    tachoEvidence.Warning
                }.Where(value => !string.IsNullOrWhiteSpace(value))),
                records
            });
        }
    }

    private static object BuildRecord(
        Load load,
        EmbeddedGeofenceSnapshot snapshot,
        DateTimeOffset now,
        RunTachoEvidence? tacho,
        VehicleLiveStatus? live)
    {
        var orderedStops = (load.Stops ?? []).OrderBy(x => x.Sequence).ToList();
        var visits = snapshot.Visits.Where(x => x.LoadId == load.Id).OrderBy(x => x.EnteredAtUtc).ToList();
        var completedStopIds = GeofencePlanningMatch.CompletedStopIds(load, visits);
        var stopDwell = RunStopDwellProjection.Build(load, visits, snapshot.ActiveVisits, now);
        var linkageException = RunStopDwellProjection.LinkExceptionFor(load, snapshot);

        var activeVisit = snapshot.ActiveVisits
            .Where(x => x.LoadId == load.Id)
            .OrderByDescending(x => x.EnteredAtUtc)
            .FirstOrDefault();
        var nextStop = RunProgressionFrontier.NextOperationalStop(orderedStops, completedStopIds, activeVisit?.LoadStopId);
        var progressionFrontierSequence = RunProgressionFrontier.Sequence(orderedStops, completedStopIds, activeVisit?.LoadStopId);
        var evidenceGaps = RunProgressionFrontier.EvidenceGapsBeforeFrontier(orderedStops, completedStopIds, activeVisit?.LoadStopId);
        var lastDeparture = visits
            .Where(x => x.ExitedAtUtc != null && x.ConfirmedAtUtc != null)
            .OrderByDescending(x => x.ExitedAtUtc)
            .FirstOrDefault();

        var trackingObservedAtUtc = live?.LastReceivedAtUtc;
        var trackingAge = trackingObservedAtUtc is null ? (TimeSpan?)null : now - trackingObservedAtUtc.Value;
        var trackingFresh = trackingAge is not null && trackingAge.Value >= TimeSpan.Zero && trackingAge.Value <= LiveTrackingThreshold;
        var trackingMoving = trackingFresh && live is not null && (live.IsMoving == true || (live.SpeedKph ?? 0) > 2);

        var totalStops = orderedStops.Count;
        var completedStops = completedStopIds.Count;
        var progressPercent = totalStops == 0 ? 0m : Math.Round((decimal)completedStops / totalStops * 100m, 1);
        var waitLimit = activeVisit?.Fence.MaxWaitMinutes ?? activeVisit?.Fence.CategoryMaxWaitMinutes;
        var dwell = activeVisit?.DwellMinutes;
        if (activeVisit is not null && now - activeVisit.LastInsideAtUtc <= TimeSpan.FromMinutes(5))
            dwell = Math.Max(activeVisit.DwellMinutes, Math.Max(0, (int)Math.Floor((now - activeVisit.EnteredAtUtc).TotalMinutes)));
        var delayed = activeVisit is not null && waitLimit is int limit && dwell.GetValueOrDefault() > limit;
        var activeStatus = activeVisit is null ? null : delayed ? "SiteDelay" : activeVisit.ConfirmedAtUtc is not null ? "OnSiteConfirmed" : "Arrived";
        var complete = RunProgressionFrontier.FinalStopCompleted(orderedStops, completedStopIds);
        var runState = complete
            ? "Completed"
            : activeStatus ?? (progressionFrontierSequence > 0 || trackingMoving ? "BetweenStops" : InferredRunState(load, orderedStops, now));
        var phase = complete
            ? "Complete"
            : activeVisit is not null
                ? "On site"
                : progressionFrontierSequence > 0 || trackingMoving
                    ? "Heading to"
                    : "Next job";

        return new
        {
            loadId = load.Id,
            loadReference = load.Reference,
            loadStatus = load.Status.ToString(),
            runState,
            totalStops,
            completedStops,
            progressionFrontierSequence,
            evidenceGaps = evidenceGaps.Select(stop => new { stop.Id, stop.Sequence, stop.Name }).ToList(),
            progressPercent,
            nextStop = nextStop is null ? null : new { nextStop.Id, nextStop.Sequence, nextStop.Name, nextStop.Address, nextStop.PlannedArrivalUtc },
            phase,
            focusStop = activeVisit?.Fence.Name ?? nextStop?.Name,
            geofenceOnSite = activeVisit is not null,
            trackingFresh,
            trackingMoving,
            ignitionOn = trackingFresh ? live?.IgnitionOn : null,
            driverCardPresent = trackingFresh ? !string.IsNullOrWhiteSpace(live?.CurrentDriverCardNumber) : (bool?)null,
            trackingObservedAtUtc,
            trackingAgeSeconds = trackingAge is null ? (int?)null : (int)Math.Max(0, Math.Floor(trackingAge.Value.TotalSeconds)),
            speedKph = trackingFresh ? live?.SpeedKph : null,
            currentVisit = activeVisit is null ? null : new
            {
                activeVisit.Id,
                geofenceId = activeVisit.Fence.Id,
                geofenceName = activeVisit.Fence.Name,
                category = activeVisit.Fence.Category,
                activeVisit.LoadStopId,
                activeVisit.EnteredAtUtc,
                siteArrivalUtc = activeVisit.EnteredAtUtc,
                activeVisit.ConfirmedAtUtc,
                siteDepartureUtc = (DateTimeOffset?)null,
                state = "OnSite",
                dwellMinutes = dwell,
                liveDwellMinutes = dwell,
                liveDwellSeconds = Math.Max(0, (int)Math.Floor((now - activeVisit.EnteredAtUtc).TotalSeconds)),
                finalDwellMinutes = (int?)null,
                finalDwellSeconds = (int?)null,
                waitLimitMinutes = waitLimit,
                isDelayed = delayed,
                status = activeStatus,
                statusReason = delayed
                    ? $"Dwell is {dwell.GetValueOrDefault()} minutes; site threshold is {waitLimit} minutes."
                    : activeVisit.ConfirmedAtUtc is not null
                        ? $"Confirmed after {dwell.GetValueOrDefault()} minutes in {activeVisit.Fence.Name}."
                        : $"Inside {activeVisit.Fence.Name}; awaiting 10-minute confirmation."
            },
            lastDeparture = lastDeparture is null ? null : new
            {
                lastDeparture.LoadStopId,
                siteArrivalUtc = lastDeparture.EnteredAtUtc,
                siteDepartureUtc = lastDeparture.ExitedAtUtc,
                lastDeparture.ExitedAtUtc,
                lastDeparture.DwellMinutes,
                finalDwellMinutes = lastDeparture.DwellMinutes,
                finalDwellSeconds = lastDeparture.ExitedAtUtc is null ? (int?)null : Math.Max(0, (int)Math.Floor((lastDeparture.ExitedAtUtc.Value - lastDeparture.EnteredAtUtc).TotalSeconds)),
                state = "Departed"
            },
            stopDwell = stopDwell.Select(stop => new
            {
                stop.StopId,
                stop.Sequence,
                stop.StopName,
                stop.State,
                stop.GeofenceId,
                stop.GeofenceName,
                stop.SiteArrivalUtc,
                stop.SiteDepartureUtc,
                stop.LiveDwellSeconds,
                stop.LiveDwellMinutes,
                stop.FinalDwellSeconds,
                stop.FinalDwellMinutes,
                stop.DwellSeconds
            }),
            linkageException,
            tacho,
            calculatedAtUtc = now
        };
    }

    private static async Task<Dictionary<Guid, VehicleLiveStatus?>> ResolveLiveByLoadAsync(
        TmsDbContext db,
        IReadOnlyCollection<Load> loads,
        CancellationToken ct)
    {
        try
        {
            var vehicleIds = loads.Where(load => load.VehicleId is not null).Select(load => load.VehicleId!.Value).Distinct().ToList();
            if (vehicleIds.Count == 0) return [];

            var vehicles = await db.Vehicles.AsNoTracking()
                .Where(vehicle => vehicleIds.Contains(vehicle.Id))
                .ToListAsync(ct);
            var aliasesByVehicle = await ExecutionIdentityResolver.VehicleAliasesAsync(db, vehicles, ct);
            var liveStatuses = await db.VehicleLiveStatuses.AsNoTracking().ToListAsync(ct);

            var result = new Dictionary<Guid, VehicleLiveStatus?>();
            foreach (var load in loads)
            {
                VehicleLiveStatus? live = null;
                if (load.VehicleId is Guid vehicleId && aliasesByVehicle.TryGetValue(vehicleId, out var aliases))
                    live = ExecutionIdentityResolver.MatchLive(aliases, liveStatuses);
                result[load.Id] = live;
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            db.ChangeTracker.Clear();
            return [];
        }
    }

    private static DateTimeOffset? LatestTracking(
        DateTimeOffset? snapshotLatest,
        IEnumerable<VehicleLiveStatus?> liveStatuses)
    {
        var liveLatest = liveStatuses
            .Where(status => status is not null)
            .Select(status => (DateTimeOffset?)status!.LastReceivedAtUtc)
            .OrderByDescending(value => value)
            .FirstOrDefault();
        if (snapshotLatest is null) return liveLatest;
        if (liveLatest is null) return snapshotLatest;
        return snapshotLatest > liveLatest ? snapshotLatest : liveLatest;
    }

    internal static string InferredRunState(Load load, IReadOnlyList<LoadStop> orderedStops, DateTimeOffset now)
    {
        if (load.Status is LoadStatus.Completed or LoadStatus.Cancelled)
            return load.Status.ToString();
        if (load.Status is LoadStatus.Dispatched or LoadStatus.InProgress)
            return "InProgress";
        if (load.Status != LoadStatus.Planned)
            return load.Status.ToString();
        if (load.VehicleId is null && load.DriverId is null)
            return load.Status.ToString();

        return load.Status.ToString();
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
