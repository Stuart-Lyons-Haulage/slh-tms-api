using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public static class RunStopDwellProjection
{
    public static IReadOnlyList<RunStopDwellState> Build(
        Load load,
        IReadOnlyCollection<DerivedVisit> visits,
        IReadOnlyCollection<DerivedVisit> activeVisits,
        DateTimeOffset now)
    {
        var activeIds = activeVisits.Select(x => x.Id).ToHashSet();
        return (load.Stops ?? [])
            .OrderBy(stop => stop.Sequence)
            .Select(stop =>
            {
                var visit = visits
                    .Where(candidate => candidate.LoadId == load.Id && candidate.LoadStopId == stop.Id)
                    .OrderByDescending(candidate => candidate.EnteredAtUtc)
                    .FirstOrDefault()
                    ?? visits
                        .Where(candidate => candidate.LoadId == load.Id && GeofencePlanningMatch.SamePhysicalSite(stop, candidate.Fence))
                        .OrderByDescending(candidate => candidate.EnteredAtUtc)
                        .FirstOrDefault();

                if (visit is null)
                    return new RunStopDwellState(stop.Id, stop.Sequence, stop.Name, "EnRoute", null, null, null, null, null, null, null, null, null);

                var isOnSite = visit.ExitedAtUtc is null && activeIds.Contains(visit.Id);
                var liveSeconds = isOnSite ? SecondsBetween(visit.EnteredAtUtc, now) : (int?)null;
                var finalSeconds = visit.ExitedAtUtc is null ? (int?)null : SecondsBetween(visit.EnteredAtUtc, visit.ExitedAtUtc.Value);
                var dwellSeconds = finalSeconds ?? liveSeconds ?? Math.Max(0, visit.DwellMinutes * 60);
                var state = visit.ExitedAtUtc is not null ? "Departed" : "OnSite";

                return new RunStopDwellState(
                    stop.Id,
                    stop.Sequence,
                    stop.Name,
                    state,
                    visit.Fence.Id,
                    visit.Fence.Name,
                    visit.EnteredAtUtc,
                    visit.ExitedAtUtc,
                    liveSeconds,
                    liveSeconds is null ? null : Minutes(liveSeconds.Value),
                    finalSeconds,
                    finalSeconds is null ? null : Minutes(finalSeconds.Value),
                    dwellSeconds);
            })
            .ToList();
    }

    public static RunGeofenceLinkException? LinkExceptionFor(Load load, EmbeddedGeofenceSnapshot snapshot)
    {
        if (load.VehicleId is not Guid vehicleId) return null;
        var unlinked = snapshot.ActiveVisits
            .Where(visit => visit.VehicleId == vehicleId && visit.LoadId is null)
            .OrderByDescending(visit => visit.EnteredAtUtc)
            .FirstOrDefault();

        return unlinked is null
            ? null
            : new RunGeofenceLinkException(
                "Unlinked",
                unlinked.Fence.Id,
                unlinked.Fence.Name,
                unlinked.EnteredAtUtc,
                "Vehicle is inside a recognised geofence, but it could not be safely linked to the current run stop. Time on site has not been started for this run.");
    }

    public static async Task<int> TryPersistAsync(TmsDbContext db, EmbeddedGeofenceSnapshot snapshot, CancellationToken ct)
    {
        try
        {
            var persisted = 0;
            foreach (var visit in snapshot.Visits.Where(visit => visit.LoadId is not null && visit.LoadStopId is not null))
            {
                var existing = await db.GeofenceVisits.SingleOrDefaultAsync(row => row.Id == visit.Id, ct);
                if (existing is null)
                {
                    existing = new GeofenceVisit
                    {
                        Id = visit.Id,
                        GeofenceId = visit.Fence.Id,
                        VehicleIdentifier = visit.VehicleIdentifier,
                        EnteredAtUtc = visit.EnteredAtUtc,
                        LastInsideAtUtc = visit.LastInsideAtUtc,
                        Status = VisitStatus(visit)
                    };
                    db.GeofenceVisits.Add(existing);
                    persisted++;
                }

                existing.GeofenceId = visit.Fence.Id;
                existing.LoadId = visit.LoadId;
                existing.LoadStopId = visit.LoadStopId;
                existing.VehicleId = visit.VehicleId;
                existing.VehicleIdentifier = visit.VehicleIdentifier;
                if (visit.EnteredAtUtc < existing.EnteredAtUtc)
                    existing.EnteredAtUtc = visit.EnteredAtUtc;
                existing.ConfirmedAtUtc = Earlier(existing.ConfirmedAtUtc, visit.ConfirmedAtUtc);
                existing.ExitedAtUtc = Later(existing.ExitedAtUtc, visit.ExitedAtUtc);
                existing.LastInsideAtUtc = Later(existing.LastInsideAtUtc, visit.LastInsideAtUtc) ?? visit.LastInsideAtUtc;
                existing.DwellMinutes = visit.ExitedAtUtc is null
                    ? Math.Max(existing.DwellMinutes, visit.DwellMinutes)
                    : Math.Max(0, (int)Math.Floor((visit.ExitedAtUtc.Value - existing.EnteredAtUtc).TotalMinutes));
                existing.Status = VisitStatus(visit);
                existing.StatusReason = VisitReason(visit);
                existing.UpdatedAtUtc = DateTimeOffset.UtcNow;
            }

            if (db.ChangeTracker.HasChanges())
                await db.SaveChangesAsync(ct);

            return persisted;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            return 0;
        }
    }

    private static int SecondsBetween(DateTimeOffset start, DateTimeOffset end) =>
        Math.Max(0, (int)Math.Floor((end - start).TotalSeconds));

    private static int Minutes(int seconds) => Math.Max(0, (int)Math.Floor(seconds / 60m));

    private static DateTimeOffset? Earlier(DateTimeOffset? left, DateTimeOffset? right) =>
        left is null ? right : right is null ? left : left <= right ? left : right;

    private static DateTimeOffset? Later(DateTimeOffset? left, DateTimeOffset? right) =>
        left is null ? right : right is null ? left : left >= right ? left : right;

    private static string VisitStatus(DerivedVisit visit)
    {
        if (visit.ExitedAtUtc is not null) return visit.ConfirmedAtUtc is null ? "PassThrough" : "Departed";
        return visit.ConfirmedAtUtc is null ? "Arrived" : "OnSite";
    }

    private static string VisitReason(DerivedVisit visit) =>
        visit.ExitedAtUtc is not null
            ? $"Geofence departure from {visit.Fence.Name}; final dwell {Math.Max(0, (int)Math.Floor((visit.ExitedAtUtc.Value - visit.EnteredAtUtc).TotalMinutes))} minutes."
            : $"Geofence arrival at {visit.Fence.Name}; live time on site derives from this entry event.";
}

public sealed record RunStopDwellState(
    Guid StopId,
    int Sequence,
    string StopName,
    string State,
    Guid? GeofenceId,
    string? GeofenceName,
    DateTimeOffset? SiteArrivalUtc,
    DateTimeOffset? SiteDepartureUtc,
    int? LiveDwellSeconds,
    int? LiveDwellMinutes,
    int? FinalDwellSeconds,
    int? FinalDwellMinutes,
    int? DwellSeconds);

public sealed record RunGeofenceLinkException(
    string State,
    Guid GeofenceId,
    string GeofenceName,
    DateTimeOffset EnteredAtUtc,
    string Message);
