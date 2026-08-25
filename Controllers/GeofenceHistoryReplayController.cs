using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/geofence-history")]
[Authorize]
public sealed class GeofenceHistoryReplayController(GeofenceHistoryReplayService replay) : ControllerBase
{
    [HttpPost("rebuild-today")]
    public async Task<ActionResult<GeofenceHistoryReplayResult>> RebuildToday(CancellationToken ct)
    {
        var today = UkDate(DateTimeOffset.UtcNow);
        return Ok(await replay.ReplayAsync(today, ct));
    }

    [HttpPost("rebuild")]
    public async Task<ActionResult<GeofenceHistoryReplayResult>> Rebuild([FromQuery] DateOnly date, CancellationToken ct) =>
        Ok(await replay.ReplayAsync(date, ct));

    private static DateOnly UkDate(DateTimeOffset utcNow)
    {
        try
        {
            var local = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(utcNow, "Europe/London");
            return DateOnly.FromDateTime(local.DateTime);
        }
        catch (TimeZoneNotFoundException)
        {
            return DateOnly.FromDateTime(utcNow.UtcDateTime);
        }
    }
}
