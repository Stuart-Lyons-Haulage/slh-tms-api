using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;

namespace Slh.Tms.Api.Services;

public static class GeofenceRunProgression
{
    private const int DefaultConfirmDwellMinutes = 10;

    public static async Task EnsureSchemaAsync(TmsDbContext db, CancellationToken ct)
    {
        const string sql = """
IF OBJECT_ID(N'[dbo].[SiteGeofences]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[SiteGeofences](
        [Id] uniqueidentifier NOT NULL PRIMARY KEY,
        [Name] nvarchar(200) NOT NULL,
        [NormalizedName] nvarchar(200) NOT NULL,
        [Category] nvarchar(80) NULL,
        [CategoryMaxWaitMinutes] int NULL,
        [MaxWaitMinutes] int NULL,
        [PendingEntryMinutes] int NOT NULL CONSTRAINT [DF_SiteGeofences_PendingEntryMinutes] DEFAULT 0,
        [PendingExitMinutes] int NOT NULL CONSTRAINT [DF_SiteGeofences_PendingExitMinutes] DEFAULT 0,
        [SiteNumber] nvarchar(40) NULL,
        [SiteId] uniqueidentifier NULL,
        [PolygonJson] nvarchar(max) NOT NULL,
        [Active] bit NOT NULL CONSTRAINT [DF_SiteGeofences_Active] DEFAULT 1,
        [CreatedAtUtc] datetimeoffset NOT NULL,
        [UpdatedAtUtc] datetimeoffset NOT NULL
    );
    CREATE UNIQUE INDEX [IX_SiteGeofences_NormalizedName] ON [dbo].[SiteGeofences]([NormalizedName]);
    CREATE INDEX [IX_SiteGeofences_SiteId] ON [dbo].[SiteGeofences]([SiteId]);
END;
IF OBJECT_ID(N'[dbo].[GeofenceVisits]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[GeofenceVisits](
        [Id] uniqueidentifier NOT NULL PRIMARY KEY,
        [GeofenceId] uniqueidentifier NOT NULL,
        [LoadId] uniqueidentifier NULL,
        [LoadStopId] uniqueidentifier NULL,
        [VehicleId] uniqueidentifier NULL,
        [VehicleIdentifier] nvarchar(80) NOT NULL,
        [EnteredAtUtc] datetimeoffset NOT NULL,
        [ConfirmedAtUtc] datetimeoffset NULL,
        [ExitedAtUtc] datetimeoffset NULL,
        [LastInsideAtUtc] datetimeoffset NOT NULL,
        [DwellMinutes] int NOT NULL,
        [Status] nvarchar(40) NOT NULL,
        [StatusReason] nvarchar(500) NULL,
        [CreatedAtUtc] datetimeoffset NOT NULL,
        [UpdatedAtUtc] datetimeoffset NOT NULL
    );
    CREATE INDEX [IX_GeofenceVisits_Vehicle_Open] ON [dbo].[GeofenceVisits]([VehicleIdentifier],[ExitedAtUtc]);
    CREATE INDEX [IX_GeofenceVisits_Load_Stop] ON [dbo].[GeofenceVisits]([LoadId],[LoadStopId]);
    CREATE INDEX [IX_GeofenceVisits_Entered] ON [dbo].[GeofenceVisits]([EnteredAtUtc]);
END;
""";
        await db.Database.ExecuteSqlRawAsync(sql, ct);
    }

    public static async Task<GeofenceImportResult> ImportFalconAsync(TmsDbContext db, JsonElement root, CancellationToken ct)
    {
        await EnsureSchemaAsync(db, ct);
        var category = Text(root, "category");
        var categoryMaxWait = Int(root, "category_max_wait_time");
        if (!root.TryGetProperty("geofences", out var geofences) || geofences.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("Expected a Falcon geofence payload containing a geofences array.");

        var sites = await db.Sites.AsNoTracking().Where(x => x.Active).ToListAsync(ct);
        var inserted = 0; var updated = 0; var matched = 0;
        foreach (var item in geofences.EnumerateArray())
        {
            var name = Text(item, "name")?.Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (!item.TryGetProperty("points", out var points) || points.ValueKind != JsonValueKind.Array || points.GetArrayLength() < 3) continue;
            var normalized = Normalize(name);
            var existing = await db.SiteGeofences.SingleOrDefaultAsync(x => x.NormalizedName == normalized, ct);
            var site = MatchSite(name, sites);
            if (existing is null)
            {
                existing = new SiteGeofence { Name = name, NormalizedName = normalized, PolygonJson = points.GetRawText() };
                db.SiteGeofences.Add(existing); inserted++;
            }
            else updated++;
            existing.Name = name;
            existing.Category = category;
            existing.CategoryMaxWaitMinutes = categoryMaxWait;
            existing.MaxWaitMinutes = Int(item, "max_wait_time");
            existing.PendingEntryMinutes = Int(item, "pending_entry_minutes") ?? 0;
            existing.PendingExitMinutes = Int(item, "pending_exit_minutes") ?? 0;
            existing.SiteNumber = Text(item, "site_no");
            existing.PolygonJson = points.GetRawText();
            existing.SiteId = site?.Id;
            existing.Active = true;
            existing.UpdatedAtUtc = DateTimeOffset.UtcNow;
            if (site is not null) matched++;
        }
        await db.SaveChangesAsync(ct);
        return new GeofenceImportResult(inserted, updated, matched);
    }

    public static async Task ProcessTelemetryAsync(TmsDbContext db, IReadOnlyCollection<DotTelemetryRecord> records, CancellationToken ct)
    {
        if (records.Count == 0) return;
        await EnsureSchemaAsync(db, ct);
        var geofences = await db.SiteGeofences.AsNoTracking().Where(x => x.Active).ToListAsync(ct);
        if (geofences.Count == 0) return;
        var vehicles = await db.Vehicles.AsNoTracking().Where(x => x.Active).ToListAsync(ct);

        foreach (var record in records.OrderBy(x => x.EventTimeUtc))
        {
            if (record.Latitude is null || record.Longitude is null) continue;
            var vehicle = vehicles.FirstOrDefault(v => VehicleMatches(v, record.VehicleIdentifier));
            var load = vehicle is null ? null : await db.Loads.Include(x => x.Stops)
                .Where(x => x.VehicleId == vehicle.Id && x.Status != LoadStatus.Completed && x.Status != LoadStatus.Cancelled)
                .OrderByDescending(x => x.PlanningDate).FirstOrDefaultAsync(ct);
            var openVisit = await db.GeofenceVisits.OrderByDescending(x => x.EnteredAtUtc)
                .FirstOrDefaultAsync(x => x.VehicleIdentifier == record.VehicleIdentifier && x.ExitedAtUtc == null, ct);
            var inside = geofences.FirstOrDefault(g => Contains(g.PolygonJson, record.Longitude.Value, record.Latitude.Value));

            if (inside is not null)
            {
                if (openVisit is not null && openVisit.GeofenceId != inside.Id)
                    await CloseVisitAsync(db, openVisit, record.EventTimeUtc, "Departed", "Vehicle entered a different geofence.", ct);

                openVisit = await db.GeofenceVisits.OrderByDescending(x => x.EnteredAtUtc)
                    .FirstOrDefaultAsync(x => x.VehicleIdentifier == record.VehicleIdentifier && x.ExitedAtUtc == null && x.GeofenceId == inside.Id, ct);
                if (openVisit is null)
                {
                    var stop = MatchNextStop(load, inside.Name, await CompletedStopIds(db, load?.Id, ct));
                    openVisit = new GeofenceVisit
                    {
                        GeofenceId = inside.Id, LoadId = load?.Id, LoadStopId = stop?.Id, VehicleId = vehicle?.Id,
                        VehicleIdentifier = record.VehicleIdentifier, EnteredAtUtc = record.EventTimeUtc,
                        LastInsideAtUtc = record.EventTimeUtc, Status = "Arrived", StatusReason = $"Entered {inside.Name}."
                    };
                    db.GeofenceVisits.Add(openVisit);
                }
                else
                {
                    openVisit.LastInsideAtUtc = record.EventTimeUtc;
                    openVisit.DwellMinutes = Math.Max(0, (int)Math.Floor((record.EventTimeUtc - openVisit.EnteredAtUtc).TotalMinutes));
                    var confirmMinutes = Math.Max(DefaultConfirmDwellMinutes, inside.PendingEntryMinutes);
                    if (openVisit.ConfirmedAtUtc is null && openVisit.DwellMinutes >= confirmMinutes)
                    {
                        openVisit.ConfirmedAtUtc = record.EventTimeUtc;
                        openVisit.Status = "OnSiteConfirmed";
                        openVisit.StatusReason = $"Confirmed after {openVisit.DwellMinutes} minutes in {inside.Name}.";
                        if (load is not null && load.Status is LoadStatus.Dispatched or LoadStatus.Planned) load.Status = LoadStatus.InProgress;
                    }
                    var waitLimit = inside.MaxWaitMinutes ?? inside.CategoryMaxWaitMinutes;
                    if (waitLimit is int limit && openVisit.DwellMinutes > limit)
                    {
                        openVisit.Status = "SiteDelay";
                        openVisit.StatusReason = $"Dwell is {openVisit.DwellMinutes} minutes; site threshold is {limit} minutes.";
                    }
                    openVisit.UpdatedAtUtc = DateTimeOffset.UtcNow;
                }
            }
            else if (openVisit is not null)
            {
                var fence = geofences.FirstOrDefault(x => x.Id == openVisit.GeofenceId);
                var exitMinutes = fence?.PendingExitMinutes ?? 0;
                if (record.EventTimeUtc - openVisit.LastInsideAtUtc >= TimeSpan.FromMinutes(exitMinutes))
                {
                    var confirmed = openVisit.ConfirmedAtUtc is not null;
                    await CloseVisitAsync(db, openVisit, record.EventTimeUtc,
                        confirmed ? "Departed" : "PassThrough",
                        confirmed ? "Confirmed site visit completed on geofence exit." : "Vehicle left before the minimum dwell confirmation.", ct);
                    if (confirmed && load is not null)
                    {
                        db.DriverStatusLogs.Add(new DriverStatusLog
                        {
                            LoadId = load.Id, DriverId = load.DriverId, Status = "GeofenceStopCompleted",
                            Notes = $"Stop {openVisit.LoadStopId} completed from geofence exit at {record.EventTimeUtc:u}.", CapturedBy = "RoadTech Geofence Engine"
                        });
                    }
                }
            }
            if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);
        }
    }

    private static async Task<HashSet<Guid>> CompletedStopIds(TmsDbContext db, Guid? loadId, CancellationToken ct)
    {
        if (loadId is null) return [];
        return (await db.GeofenceVisits.AsNoTracking().Where(x => x.LoadId == loadId && x.LoadStopId != null && x.ExitedAtUtc != null && x.ConfirmedAtUtc != null)
            .Select(x => x.LoadStopId!.Value).ToListAsync(ct)).ToHashSet();
    }

    private static LoadStop? MatchNextStop(Load? load, string geofenceName, HashSet<Guid> completed) => load?.Stops
        .Where(x => !completed.Contains(x.Id))
        .OrderBy(x => x.Sequence)
        .FirstOrDefault(x => NamesOverlap(x.Name, geofenceName))
        ?? load?.Stops.Where(x => !completed.Contains(x.Id)).OrderBy(x => x.Sequence).FirstOrDefault();

    private static async Task CloseVisitAsync(TmsDbContext db, GeofenceVisit visit, DateTimeOffset at, string status, string reason, CancellationToken ct)
    {
        visit.ExitedAtUtc = at;
        visit.DwellMinutes = Math.Max(0, (int)Math.Floor((at - visit.EnteredAtUtc).TotalMinutes));
        visit.Status = status; visit.StatusReason = reason; visit.UpdatedAtUtc = DateTimeOffset.UtcNow;
        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);
    }

    private static Site? MatchSite(string geofenceName, IReadOnlyCollection<Site> sites)
    {
        var key = Normalize(geofenceName);
        return sites.FirstOrDefault(x => Normalize(x.Name) == key || Normalize(x.DriverTextName) == key)
            ?? sites.FirstOrDefault(x => key.Contains(Normalize(x.Name), StringComparison.Ordinal) || Normalize(x.Name).Contains(key, StringComparison.Ordinal));
    }

    private static bool VehicleMatches(Vehicle vehicle, string identifier)
    {
        var key = Normalize(identifier);
        return new[] { vehicle.Registration, vehicle.FleetNumber, vehicle.Abbreviation }.Where(x => !string.IsNullOrWhiteSpace(x)).Any(x => Normalize(x) == key);
    }

    private static bool NamesOverlap(string a, string b)
    {
        var left = Normalize(a); var right = Normalize(b);
        return left == right || left.Contains(right, StringComparison.Ordinal) || right.Contains(left, StringComparison.Ordinal);
    }

    private static bool Contains(string polygonJson, decimal longitude, decimal latitude)
    {
        using var doc = JsonDocument.Parse(polygonJson);
        var points = doc.RootElement.EnumerateArray().Select(p => (X: p[0].GetDouble(), Y: p[1].GetDouble())).ToArray();
        var x = (double)longitude; var y = (double)latitude; var inside = false;
        for (int i = 0, j = points.Length - 1; i < points.Length; j = i++)
        {
            var pi = points[i]; var pj = points[j];
            if (((pi.Y > y) != (pj.Y > y)) && x < (pj.X - pi.X) * (y - pi.Y) / (pj.Y - pi.Y) + pi.X) inside = !inside;
        }
        return inside;
    }

    private static string Normalize(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined ? v.ToString() : null;
    private static int? Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : null;
}

public sealed record GeofenceImportResult(int Inserted, int Updated, int SiteMatched);
