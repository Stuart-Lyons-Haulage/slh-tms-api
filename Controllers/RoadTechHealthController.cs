using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/health/tracking")]
public sealed class RoadTechHealthController(TmsDbContext db, DotTrackingClient client, DotTrackingOptions options, ILogger<RoadTechHealthController> logger) : ControllerBase
{
    [HttpGet, AllowAnonymous]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        if (!options.IsConfigured) return StatusCode(503, new { status = "unconfigured", configured = false, gpsRequested = (options.DataMask & 1) == 1 });
        try
        {
            var now = DateTimeOffset.UtcNow;
            var provider = await client.GetLatestVehicleEventsAsync(ct);
            var records = provider.Select(DotTelemetryRecord.FromProvider).ToList();
            var gps = records.Where(x => x.Latitude is not null && x.Longitude is not null).ToList();
            var newestSource = gps.Select(x => (DateTimeOffset?)x.EventTimeUtc).Max();
            var newestReceipt = await db.VehicleLiveStatuses.AsNoTracking().Select(x => (DateTimeOffset?)x.LastReceivedAtUtc).MaxAsync(ct);
            var receiptAge = newestReceipt is null ? (double?)null : Math.Max(0, Math.Round((now - newestReceipt.Value).TotalMinutes, 1));
            var healthy = gps.Count > 0 && receiptAge is not null && receiptAge <= Math.Max(10, options.StaleAfterMinutes);
            var body = new { status = healthy ? "healthy" : "stale", configured = true, gpsRequested = (options.DataMask & 1) == 1, vehiclesReturned = records.Count, vehiclesWithGps = gps.Count, newestSourceTimestampUtc = newestSource, newestReceiptUtc = newestReceipt, receiptAgeMinutes = receiptAge, checkedAtUtc = now };
            return healthy ? Ok(body) : StatusCode(503, body);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "RoadTech tracking health check failed.");
            return StatusCode(503, new { status = "upstream-failure", configured = true, error = ex.GetType().Name, checkedAtUtc = DateTimeOffset.UtcNow });
        }
    }
}
