namespace Slh.Tms.Api.Services;

/// <summary>
/// Marker service for explicit operational replay of today's RoadTech history after
/// geofence/site linkage changes. The replay itself is performed by
/// GeofenceHistoryReplayService; this type exists only to keep the recovery feature
/// discoverable alongside the existing background ingestion pipeline.
/// </summary>
public static class GeofenceHistoryReplayTrigger
{
}
