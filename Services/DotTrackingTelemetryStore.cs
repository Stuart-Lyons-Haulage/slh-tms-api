using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models.Tracking;

namespace Slh.Tms.Api.Services;

public sealed class DotTrackingTelemetryStore(TmsDbContext db)
{
    public async Task PersistAsync(IEnumerable<DotTelemetryRecord> records, CancellationToken ct)
    {
        foreach (var record in records.Where(record => record.Latitude is not null && record.Longitude is not null))
        {
            var exists = await db.VehicleTrackingEvents.AnyAsync(item => item.ProviderName == "RoadTech Falcon" && item.ProviderEventId == record.ProviderEventId, ct);
            if (!exists) db.VehicleTrackingEvents.Add(new VehicleTrackingEvent
            {
                ProviderName = "RoadTech Falcon", ProviderEventId = record.ProviderEventId, VehicleIdentifier = record.VehicleIdentifier,
                EventTimeUtc = record.EventTimeUtc, Latitude = record.Latitude!.Value, Longitude = record.Longitude!.Value,
                SpeedKph = record.SpeedKph, IgnitionOn = record.IgnitionOn, IsMoving = record.IsMoving, RawPayload = record.RawPayload, MatchStatus = "Received"
            });
            var live = await db.VehicleLiveStatuses.SingleOrDefaultAsync(item => item.VehicleIdentifier == record.VehicleIdentifier, ct);
            if (live is null)
            {
                db.VehicleLiveStatuses.Add(new VehicleLiveStatus
                {
                    VehicleIdentifier = record.VehicleIdentifier, LastEventTimeUtc = record.EventTimeUtc, Latitude = record.Latitude!.Value,
                    Longitude = record.Longitude!.Value, SpeedKph = record.SpeedKph, IgnitionOn = record.IgnitionOn, IsMoving = record.IsMoving, LastKnownStatus = record.Status
                });
            }
            else if (record.EventTimeUtc >= live.LastEventTimeUtc)
            {
                live.LastEventTimeUtc = record.EventTimeUtc; live.LastReceivedAtUtc = DateTimeOffset.UtcNow; live.Latitude = record.Latitude!.Value;
                live.Longitude = record.Longitude!.Value; live.SpeedKph = record.SpeedKph; live.IgnitionOn = record.IgnitionOn; live.IsMoving = record.IsMoving; live.LastKnownStatus = record.Status;
            }
        }
        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);
    }
}
