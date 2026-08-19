using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Register-first run timeline used by the operational Live Runs board.
/// This remains available when the legacy Loads/LoadStops schema is unavailable.
/// </summary>
[ApiController, Route("api/v1/intelligence/run-timeline")]
[Authorize]
public sealed class RunTimelineResilienceController(TmsDbContext db) : ControllerBase
{
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var load = await PlanningResilience.ReadLoadAsync(db, id, ct);
        if (load is null) return NotFound();

        // Planner notes carry the human-facing run name for SQL-backed rows.
        // Failure here must never take the operational timeline down.
        try { await LoadCommercialStore.EnrichAsync(db, [load], ct); }
        catch (Exception exception) when (exception is not OperationCanceledException) { db.ChangeTracker.Clear(); }

        var displayReference = RunDisplayLabel.For(load);
        var timelineStatus = load.Status.ToString();
        var events = new List<TimelineEvent>
        {
            new(load.CreatedAtUtc, "Run created", displayReference, "Planning", null)
        };

        try
        {
            var logs = await db.DriverStatusLogs.AsNoTracking()
                .Where(x => x.LoadId == id)
                .OrderBy(x => x.CapturedAtUtc)
                .ToListAsync(ct);
            events.AddRange(logs.Select(x => new TimelineEvent(
                x.CapturedAtUtc,
                x.Status,
                x.Notes ?? "Operational status updated",
                "Operations",
                x.CapturedBy)));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
        }

        var derivedTrackingAdded = false;
        try
        {
            // Live progression is intentionally table-free. Rebuild the same physical
            // visit truth used by /run-progress rather than relying on GeofenceVisits,
            // which may not exist or may contain no rows in production.
            var geofenceLoad = GeofencePlanningMatch.PrepareLoad(load);
            var snapshot = await EmbeddedGeofenceEngine.BuildAsync(db, load.PlanningDate, [geofenceLoad], ct);
            var visits = snapshot.Visits.Where(x => x.LoadId == id).OrderBy(x => x.EnteredAtUtc).ToList();
            derivedTrackingAdded = visits.Count > 0;

            foreach (var visit in visits)
            {
                events.Add(new TimelineEvent(
                    visit.EnteredAtUtc,
                    "Geofence arrival",
                    $"{visit.Fence.Name} · {visit.VehicleIdentifier}",
                    "Tracking",
                    null));
                if (visit.ConfirmedAtUtc is not null)
                    events.Add(new TimelineEvent(
                        visit.ConfirmedAtUtc.Value,
                        "Site visit confirmed",
                        $"{visit.Fence.Name} · dwell threshold confirmed · {visit.DwellMinutes} min",
                        "Tracking",
                        null));
                if (visit.ExitedAtUtc is not null)
                    events.Add(new TimelineEvent(
                        visit.ExitedAtUtc.Value,
                        "Geofence departure",
                        $"{visit.Fence.Name} · {visit.DwellMinutes} min dwell",
                        "Tracking",
                        null));
            }

            var completedStopIds = GeofencePlanningMatch.CompletedStopIds(load, visits);
            var totalStops = (load.Stops ?? []).Count;
            var active = snapshot.ActiveVisits.Any(x => x.LoadId == id);
            timelineStatus = totalStops > 0 && completedStopIds.Count >= totalStops
                ? "Completed"
                : active
                    ? "On site"
                    : completedStopIds.Count > 0
                        ? "In progress"
                        : timelineStatus;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
        }

        // Preserve legacy visit history as a fallback only. Using it in addition to the
        // derived engine would duplicate events on environments where both happen to exist.
        if (!derivedTrackingAdded)
        {
            try
            {
                var visits = await db.GeofenceVisits.AsNoTracking()
                    .Where(x => x.LoadId == id)
                    .ToListAsync(ct);
                foreach (var visit in visits)
                {
                    events.Add(new TimelineEvent(
                        visit.EnteredAtUtc,
                        "Geofence arrival",
                        visit.StatusReason ?? visit.VehicleIdentifier,
                        "Tracking",
                        null));
                    if (visit.ConfirmedAtUtc is not null)
                        events.Add(new TimelineEvent(
                            visit.ConfirmedAtUtc.Value,
                            "Site visit confirmed",
                            $"Dwell threshold confirmed · {visit.DwellMinutes} min",
                            "Tracking",
                            null));
                    if (visit.ExitedAtUtc is not null)
                        events.Add(new TimelineEvent(
                            visit.ExitedAtUtc.Value,
                            "Geofence departure",
                            visit.Status,
                            "Tracking",
                            null));
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                db.ChangeTracker.Clear();
            }
        }

        try
        {
            var changes = await PlanLockStore.ChangesAsync(db, load.PlanningDate, load.PlanningDate, ct);
            events.AddRange(changes
                .Where(x => x.LoadId == id)
                .Select(x => new TimelineEvent(
                    x.ChangedAtUtc,
                    x.ChangeType,
                    x.Reason,
                    "Plan change",
                    x.ChangedBy)));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
        }

        return Ok(new
        {
            entityType = "Run",
            id = load.Id,
            reference = displayReference,
            planningDate = load.PlanningDate,
            status = timelineStatus,
            events = events.OrderBy(x => x.AtUtc)
        });
    }

    private sealed record TimelineEvent(DateTimeOffset AtUtc, string Title, string Detail, string Source, string? By);
}
