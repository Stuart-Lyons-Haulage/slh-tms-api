using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/geofences")]
[Authorize]
public sealed class GeofencesController(TmsDbContext db) : ControllerBase
{
    [HttpPost("import-falcon")]
    [Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> ImportFalcon([FromBody] JsonElement payload, CancellationToken ct)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return UnprocessableEntity(new { error = "Expected a Falcon geofence JSON object." });
        try
        {
            var result = await GeofenceRunProgression.ImportFalconAsync(db, payload, ct);
            return Ok(new { result.Inserted, result.Updated, result.SiteMatched, importedAtUtc = DateTimeOffset.UtcNow });
        }
        catch (ArgumentException exception)
        {
            return UnprocessableEntity(new { error = exception.Message });
        }
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        await GeofenceRunProgression.EnsureSchemaAsync(db, ct);
        var rows = await db.SiteGeofences.AsNoTracking().OrderBy(x => x.Category).ThenBy(x => x.Name)
            .Select(x => new { x.Id, x.Name, x.Category, x.MaxWaitMinutes, x.CategoryMaxWaitMinutes, x.SiteId, x.Active })
            .ToListAsync(ct);
        return Ok(new { count = rows.Count, records = rows });
    }

    [HttpGet("visits")]
    public async Task<IActionResult> Visits([FromQuery] DateOnly? date, CancellationToken ct)
    {
        await GeofenceRunProgression.EnsureSchemaAsync(db, ct);
        var day = date ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var start = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var end = start.AddDays(1);
        var rows = await db.GeofenceVisits.AsNoTracking()
            .Where(x => x.EnteredAtUtc >= start && x.EnteredAtUtc < end)
            .OrderByDescending(x => x.EnteredAtUtc).Take(1000)
            .Select(x => new { x.Id, x.GeofenceId, x.LoadId, x.LoadStopId, x.VehicleId, x.VehicleIdentifier, x.EnteredAtUtc, x.ConfirmedAtUtc, x.ExitedAtUtc, x.DwellMinutes, x.Status, x.StatusReason })
            .ToListAsync(ct);
        return Ok(new { date = day, count = rows.Count, records = rows });
    }
}
