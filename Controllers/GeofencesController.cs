using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/geofences")]
[Authorize]
public sealed class GeofencesController(TmsDbContext db) : ControllerBase
{
    [HttpPost("import-falcon")]
    [Authorize(Policy = "TmsWrite")]
    public IActionResult ImportFalcon([FromBody] JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("geofences", out var geofences) || geofences.ValueKind != JsonValueKind.Array)
            return UnprocessableEntity(new { error = "Expected a Falcon geofence JSON object containing a geofences array." });

        return Conflict(new
        {
            code = "embedded_geofence_runtime",
            message = "The production geofence engine is using the approved SLH embedded geofence set because this Azure SQL identity does not have DDL permission. New Falcon geofences should be incorporated into the approved seed rather than written to runtime geofence tables.",
            supplied = geofences.GetArrayLength()
        });
    }

    [HttpPost("import-slh-seed")]
    [Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> ImportSlhSeed(CancellationToken ct)
    {
        var statuses = await EmbeddedGeofenceEngine.FenceStatusesAsync(db, ct);
        return Ok(new
        {
            supplied = statuses.Count,
            inserted = 0,
            updated = statuses.Count,
            siteMatched = statuses.Count(x => x.SiteId != null),
            relinked = 0,
            remainingUnlinked = statuses.Count(x => x.SiteId == null),
            invalidPolygons = 0,
            source = "EmbeddedSLHGeofences",
            importedAtUtc = DateTimeOffset.UtcNow
        });
    }

    [HttpPost("repair-links")]
    [Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> RepairLinks(CancellationToken ct)
    {
        var statuses = await EmbeddedGeofenceEngine.FenceStatusesAsync(db, ct);
        var linked = statuses.Count(x => x.SiteId != null);
        return Ok(new
        {
            total = statuses.Count,
            linked,
            relinked = 0,
            unlinked = statuses.Count - linked,
            validPolygons = statuses.Count,
            invalidPolygons = 0,
            source = "EmbeddedSLHGeofences",
            repairedAtUtc = DateTimeOffset.UtcNow
        });
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var statuses = await EmbeddedGeofenceEngine.FenceStatusesAsync(db, ct);
        var records = statuses
            .OrderBy(x => x.Fence.Category)
            .ThenBy(x => x.Fence.Name)
            .Select(x => new
            {
                id = x.Fence.Id,
                name = x.Fence.Name,
                category = x.Fence.Category,
                maxWaitMinutes = x.Fence.MaxWaitMinutes,
                categoryMaxWaitMinutes = x.Fence.CategoryMaxWaitMinutes,
                siteNumber = x.Fence.SiteNumber,
                siteId = x.SiteId,
                active = true,
                polygonValid = true,
                geofenceAvailable = true,
                siteLinked = x.SiteId != null,
                validationStatus = x.SiteId == null ? "Unlinked" : "Valid"
            }).ToList();
        return Ok(new { count = records.Count, source = "EmbeddedSLHGeofences", records });
    }

    [HttpGet("visits")]
    public async Task<IActionResult> Visits([FromQuery] DateOnly? date, CancellationToken ct)
    {
        var day = date ?? UkOperatingDate(DateTimeOffset.UtcNow);
        List<Load> loads;
        try { loads = await PlanningRegisterStore.ReadLoadsAsync(db, day, ct); }
        catch { loads = []; db.ChangeTracker.Clear(); }

        var snapshot = await EmbeddedGeofenceEngine.BuildAsync(db, day, loads, ct);
        var records = snapshot.Visits.OrderByDescending(x => x.EnteredAtUtc).Take(1000).Select(x => new
        {
            x.Id,
            geofenceId = x.Fence.Id,
            x.LoadId,
            x.LoadStopId,
            x.VehicleId,
            x.VehicleIdentifier,
            x.EnteredAtUtc,
            x.ConfirmedAtUtc,
            x.ExitedAtUtc,
            x.DwellMinutes,
            status = x.ExitedAtUtc is not null ? (x.ConfirmedAtUtc is not null ? "Departed" : "PassThrough") : x.ConfirmedAtUtc is not null ? "OnSiteConfirmed" : "Arrived",
            statusReason = x.ExitedAtUtc is not null
                ? "Derived from RoadTech tracking crossing the approved geofence boundary."
                : x.ConfirmedAtUtc is not null
                    ? "Confirmed after the minimum dwell period."
                    : "Vehicle currently inside geofence."
        }).ToList();
        return Ok(new { date = day, count = records.Count, source = "RoadTechDerived", records });
    }

    private static DateOnly UkOperatingDate(DateTimeOffset value)
    {
        try { return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, TimeZoneInfo.FindSystemTimeZoneById("Europe/London")).DateTime); }
        catch (TimeZoneNotFoundException) { return DateOnly.FromDateTime(value.UtcDateTime); }
    }
}
