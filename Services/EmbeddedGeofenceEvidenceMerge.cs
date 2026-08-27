using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Merges previously projected geofence evidence back into a freshly reconstructed
/// embedded snapshot. A temporary RoadTech/history gap must never erase an ENTER/EXIT
/// that has already been proven and written to GeofenceVisits.
/// </summary>
public static class EmbeddedGeofenceEvidenceMerge
{
    public static async Task<EmbeddedGeofenceSnapshot> MergeDurableProjectionAsync(
        TmsDbContext db,
        EmbeddedGeofenceSnapshot snapshot,
        IReadOnlyCollection<Load> loads,
        CancellationToken ct)
    {
        var loadById = loads
            .Where(load => load.Status != LoadStatus.Cancelled)
            .GroupBy(load => load.Id)
            .ToDictionary(group => group.Key, group => group.First());
        if (loadById.Count == 0) return snapshot;

        try
        {
            var loadIds = loadById.Keys.ToList();
            var durableRows = await db.GeofenceVisits.AsNoTracking()
                .Where(visit => visit.LoadId != null && loadIds.Contains(visit.LoadId.Value))
                .OrderBy(visit => visit.EnteredAtUtc)
                .ToListAsync(ct);
            if (durableRows.Count == 0) return snapshot;

            var geofenceIds = durableRows.Select(row => row.GeofenceId).Distinct().ToList();
            var geofenceNames = await db.SiteGeofences.AsNoTracking()
                .Where(fence => geofenceIds.Contains(fence.Id))
                .Select(fence => new { fence.Id, fence.Name })
                .ToListAsync(ct);
            var nameById = geofenceNames.ToDictionary(item => item.Id, item => item.Name);
            var embeddedByName = snapshot.Fences
                .GroupBy(fence => Normalise(fence.Name), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            var combined = snapshot.Visits
                .GroupBy(visit => visit.Id)
                .ToDictionary(group => group.Key, group => group.OrderByDescending(visit => visit.LastInsideAtUtc).First());

            foreach (var row in durableRows)
            {
                if (!nameById.TryGetValue(row.GeofenceId, out var geofenceName) ||
                    !embeddedByName.TryGetValue(Normalise(geofenceName), out var embeddedFence))
                    continue;

                if (combined.TryGetValue(row.Id, out var current))
                {
                    current.LoadId ??= row.LoadId;
                    current.LoadStopId ??= row.LoadStopId;
                    if (current.ConfirmedAtUtc is null && row.ConfirmedAtUtc is not null)
                        current.ConfirmedAtUtc = row.ConfirmedAtUtc;
                    if (current.ExitedAtUtc is null && row.ExitedAtUtc is not null)
                        current.ExitedAtUtc = row.ExitedAtUtc;
                    if (row.LastInsideAtUtc > current.LastInsideAtUtc)
                        current.LastInsideAtUtc = row.LastInsideAtUtc;
                    current.DwellMinutes = Math.Max(current.DwellMinutes, row.DwellMinutes);
                    continue;
                }

                if (row.LoadId is not Guid loadId || !loadById.TryGetValue(loadId, out var load))
                    continue;
                var vehicleId = row.VehicleId ?? load.VehicleId;
                if (vehicleId is null) continue;

                combined[row.Id] = new DerivedVisit
                {
                    Id = row.Id,
                    VehicleId = vehicleId.Value,
                    VehicleIdentifier = row.VehicleIdentifier,
                    Fence = embeddedFence,
                    LoadId = row.LoadId,
                    LoadStopId = row.LoadStopId,
                    EnteredAtUtc = row.EnteredAtUtc,
                    ConfirmedAtUtc = row.ConfirmedAtUtc,
                    ExitedAtUtc = row.ExitedAtUtc,
                    LastInsideAtUtc = row.LastInsideAtUtc,
                    DwellMinutes = row.DwellMinutes
                };
            }

            var visits = combined.Values.OrderBy(visit => visit.EnteredAtUtc).ToList();
            var now = DateTimeOffset.UtcNow;
            var activeVisits = visits
                .Where(visit => visit.ExitedAtUtc is null && now - visit.LastInsideAtUtc <= TimeSpan.FromMinutes(15))
                .ToList();
            var confirmedVisits = visits.Where(visit => visit.ConfirmedAtUtc is not null).ToList();

            return new EmbeddedGeofenceSnapshot(
                snapshot.Fences,
                visits,
                activeVisits,
                confirmedVisits,
                snapshot.TrackingEventCount,
                snapshot.LatestTrackingUtc);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            db.ChangeTracker.Clear();
            return snapshot;
        }
    }

    private static string Normalise(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
