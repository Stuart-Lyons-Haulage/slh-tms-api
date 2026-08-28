using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Canonical run-membership feed shared by the signed-in operations wallboard and the office TV.
/// It deliberately reads through PlanningResilience so SQL Loads, Planning Register and audit
/// recovery copies cannot appear as separate real-world runs.
/// </summary>
[ApiController]
[Route("api/v1/tv-display/planned-runs")]
public sealed class TvPlannedRunsController(TmsDbContext db, IConfiguration configuration) : ControllerBase
{
    [HttpGet, AllowAnonymous]
    public async Task<IActionResult> Get(
        [FromHeader(Name = "X-TV-Display-Key")] string? displayKey,
        [FromQuery] DateOnly? date,
        CancellationToken ct)
    {
        var signedInAllowed = User.Identity?.IsAuthenticated == true;
        var pairedKeyAllowed = await TvDisplayKeyStore.ValidateAsync(db, displayKey, ct);
        var legacyKeyAllowed = TvWallboardAccess.IsAllowed(HttpContext, configuration);
        if (!signedInAllowed && !pairedKeyAllowed && !legacyKeyAllowed)
            return Unauthorized(new { message = "This wallboard request is not authorised." });

        var day = date ?? UkOperatingDate(DateTimeOffset.UtcNow);
        var loads = (await PlanningResilience.ReadLoadsAsync(db, day, ct))
            .Where(load => load.PlanningDate == day && load.Status != LoadStatus.Cancelled)
            .OrderBy(load => load.Reference)
            .ToList();

        await RunOperationalStore.EnrichAsync(db, loads, ct);
        return Ok(loads);
    }

    private static DateOnly UkOperatingDate(DateTimeOffset value)
    {
        try
        {
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, TimeZoneInfo.FindSystemTimeZoneById("Europe/London")).DateTime);
        }
        catch (TimeZoneNotFoundException)
        {
            return DateOnly.FromDateTime(value.UtcDateTime);
        }
    }
}
