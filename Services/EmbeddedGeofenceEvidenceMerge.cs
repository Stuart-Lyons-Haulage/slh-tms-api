using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Presents durable geofence evidence to the live operational consumers. Active SQL
/// geofences remain the authoritative geometry, while current RoadTech reconstruction
/// is retained as resilience evidence until the durable projection has caught up.
/// </summary>
public static class EmbeddedGeofenceEvidenceMerge
{
    public static async Task<EmbeddedGeofenceSnapshot> MergeDurableProjectionAsync(
        TmsDbContext db,
        EmbeddedGeofenceSnapshot snapshot,
        IReadOnlyCollection<Load> loads,
        CancellationToken ct,
        ILogger? logger = null)
    {
        var loadById = loads
            .Where(load => load.Status != LoadStatus.Cancelled)
            .GroupBy(load => load.Id)
            .ToDictionary(group => group.Key, group => group.First());
        if (loadById.Count == 0) return snapshot;

        try
        {
            var activeSqlRows = await db.SiteGeofences.AsNoTracking()
                .Where(fence => fence.Active)
                .OrderBy(fence => fence.Name)
                .ToListAsync(ct);

            if (activeSqlRows.Count == 0)
                return await MergeEmbeddedFallbackAsync(db, snapshot, loadById, ct);

            var sqlFences = activeSqlRows
                .Select(ToEmbeddedFence)
                .Where(fence => fence is not null)
                .Select(fence => fence!)
                .ToList();
            var fenceById = sqlFences.ToDictionary(fence => fence.Id);
            var fenceByName = sqlFences
                .GroupBy(fence => Normalise(fence.Name), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            // BuildAsync already reconstructed today's RoadTech evidence using the active
            // SQL geofence catalogue. Keep those linked visits visible while the durable
            // GeofenceVisits projection catches up. The previous implementation discarded
            // every reconstructed visit whenever any SQL geofence existed, which could turn
            // real completed stops back into zero progress if durable LoadId linkage lagged.
            var combined = new Dictionary<Guid, DerivedVisit>();
            foreach (var visit in snapshot.Visits)
            {
                if (visit.LoadId is not Guid loadId || !loadById.ContainsKey(loadId)) continue;
                var activeFence = fenceById.GetValueOrDefault(visit.Fence.Id)
                    ?? fenceByName.GetValueOrDefault(Normalise(visit.Fence.Name));
                if (activeFence is null) continue;

                combined[visit.Id] = new DerivedVisit
                {
                    Id = visit.Id,
                    VehicleId = visit.VehicleId,
                    VehicleIdentifier = visit.VehicleIdentifier,
                    Fence = activeFence,
                    LoadId = visit.LoadId,
                    LoadStopId = visit.LoadStopId,
                    EnteredAtUtc = visit.EnteredAtUtc,
                    ConfirmedAtUtc = visit.ConfirmedAtUtc,
                    ExitedAtUtc = visit.ExitedAtUtc,
                    LastInsideAtUtc = visit.LastInsideAtUtc,
                    DwellMinutes = visit.DwellMinutes
                };
            }

            var loadIds = loadById.Keys.ToList();
            var durableRows = await db.GeofenceVisits.AsNoTracking()
                .Where(visit => visit.LoadId != null && loadIds.Contains(visit.LoadId.Value))
                .OrderBy(visit => visit.EnteredAtUtc)
                .ToListAsync(ct);

            foreach (var row in durableRows)
            {
                if (!fenceById.TryGetValue(row.GeofenceId, out var fence)) continue;
                if (row.LoadId is not Guid loadId || !loadById.TryGetValue(loadId, out var load)) continue;
                var vehicleId = row.VehicleId ?? load.VehicleId;
                if (vehicleId is null) continue;

                if (combined.TryGetValue(row.Id, out var current))
                {
                    // Current reconstruction may have repaired run/stop linkage that an
                    // older durable row does not yet contain, so only fill missing identity
                    // from durable storage while retaining the strongest timing evidence.
                    current.LoadId ??= row.LoadId;
                    current.LoadStopId ??= row.LoadStopId;
                    if (current.VehicleId == Guid.Empty) current.VehicleId = vehicleId.Value;
                    if (string.IsNullOrWhiteSpace(current.VehicleIdentifier)) current.VehicleIdentifier = row.VehicleIdentifier;
                    current.Fence = fence;
                    if (row.EnteredAtUtc < current.EnteredAtUtc) current.EnteredAtUtc = row.EnteredAtUtc;
                    if (current.ConfirmedAtUtc is null && row.ConfirmedAtUtc is not null) current.ConfirmedAtUtc = row.ConfirmedAtUtc;
                    if (current.ExitedAtUtc is null && row.ExitedAtUtc is not null) current.ExitedAtUtc = row.ExitedAtUtc;
                    if (row.LastInsideAtUtc > current.LastInsideAtUtc) current.LastInsideAtUtc = row.LastInsideAtUtc;
                    current.DwellMinutes = Math.Max(current.DwellMinutes, row.DwellMinutes);
                    continue;
                }

                combined[row.Id] = new DerivedVisit
                {
                    Id = row.Id,
                    VehicleId = vehicleId.Value,
                    VehicleIdentifier = row.VehicleIdentifier,
                    Fence = fence,
                    LoadId = row.LoadId,
                    LoadStopId = row.LoadStopId,
                    EnteredAtUtc = row.EnteredAtUtc,
                    ConfirmedAtUtc = row.ConfirmedAtUtc,
                    ExitedAtUtc = row.ExitedAtUtc,
                    LastInsideAtUtc = row.LastInsideAtUtc,
                    DwellMinutes = row.DwellMinutes
                };
            }

            return Snapshot(snapshot, sqlFences, combined.Values.OrderBy(visit => visit.EnteredAtUtc).ToList());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            logger?.LogWarning(exception,
                "Durable geofence evidence merge failed; returning the in-memory RoadTech snapshot as a safe fallback. " +
                "Completed stops may be temporarily invisible until the next poll cycle resolves the exception.");
            return snapshot;
        }
    }

    private static async Task<EmbeddedGeofenceSnapshot> MergeEmbeddedFallbackAsync(
        TmsDbContext db,
        EmbeddedGeofenceSnapshot snapshot,
        IReadOnlyDictionary<Guid, Load> loadById,
        CancellationToken ct)
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

            if (row.LoadId is not Guid loadId || !loadById.TryGetValue(loadId, out var load)) continue;
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

        return Snapshot(snapshot, snapshot.Fences, combined.Values.OrderBy(visit => visit.EnteredAtUtc).ToList());
    }

    private static EmbeddedGeofenceSnapshot Snapshot(
        EmbeddedGeofenceSnapshot source,
        IReadOnlyList<EmbeddedFence> fences,
        IReadOnlyList<DerivedVisit> visits)
    {
        var now = DateTimeOffset.UtcNow;
        var activeVisits = visits
            .Where(visit => visit.ExitedAtUtc is null && now - visit.LastInsideAtUtc <= TimeSpan.FromMinutes(15))
            .ToList();
        var confirmedVisits = visits.Where(visit => visit.ConfirmedAtUtc is not null).ToList();

        return new EmbeddedGeofenceSnapshot(
            fences,
            visits,
            activeVisits,
            confirmedVisits,
            source.TrackingEventCount,
            source.LatestTrackingUtc);
    }

    private static EmbeddedFence? ToEmbeddedFence(SiteGeofence row)
    {
        var points = ParsePoints(row.PolygonJson);
        if (points.Count < 3) return null;
        return new EmbeddedFence(
            row.Id,
            row.Name,
            row.Category,
            row.CategoryMaxWaitMinutes,
            row.MaxWaitMinutes,
            row.PendingEntryMinutes,
            row.PendingExitMinutes,
            row.SiteNumber,
            points);
    }

    private static IReadOnlyList<GeoPoint> ParsePoints(string? polygonJson)
    {
        if (string.IsNullOrWhiteSpace(polygonJson)) return [];
        try
        {
            using var document = JsonDocument.Parse(polygonJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
            var points = new List<GeoPoint>();
            foreach (var point in document.RootElement.EnumerateArray())
            {
                if (point.ValueKind == JsonValueKind.Array && point.GetArrayLength() >= 2 &&
                    point[0].TryGetDouble(out var x) && point[1].TryGetDouble(out var y))
                {
                    points.Add(new GeoPoint(x, y));
                    continue;
                }
                if (point.ValueKind != JsonValueKind.Object) continue;
                var longitude = Double(point, "longitude") ?? Double(point, "lng") ?? Double(point, "lon") ?? Double(point, "x");
                var latitude = Double(point, "latitude") ?? Double(point, "lat") ?? Double(point, "y");
                if (longitude is not null && latitude is not null)
                    points.Add(new GeoPoint(longitude.Value, latitude.Value));
            }
            return points;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static double? Double(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number
            : null;

    private static string Normalise(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
