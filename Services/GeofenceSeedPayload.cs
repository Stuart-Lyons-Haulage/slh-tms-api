namespace Slh.Tms.Api.Services;

internal static class GeofenceSeedPayload
{
    // Canonical operational geofence payload rebuilt from the 15 Falcon category
    // exports supplied by SLH on 19 August 2026. The payload is checksum validated
    // by OperationalGeofencePayload before the engine can consume it.
    internal static string Json => OperationalGeofencePayload.Json;
}
