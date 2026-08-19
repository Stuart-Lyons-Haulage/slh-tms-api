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
            status = load.Status.ToString(),
            events = events.OrderBy(x => x.AtUtc)
        });
    }

    private sealed record TimelineEvent(DateTimeOffset AtUtc, string Title, string Detail, string Source, string? By);
}
