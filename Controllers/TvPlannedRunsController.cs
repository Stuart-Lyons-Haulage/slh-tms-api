using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Canonical run-membership feed shared by the signed-in operations wallboard and the office TV.
/// It deliberately reads through PlanningResilience so SQL Loads, Planning Register and audit
/// recovery copies cannot appear as separate real-world runs. Planner operating dates can also
/// contain an evening carry-in from the previous calendar day; those times are normalised here
/// before the wallboard sorts or labels the journey.
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
        // Older Hisense browser/proxy paths can drop custom request headers. The paired key is
        // also present on the dedicated TV URL, so accept that same read-only key from ?key= as
        // a transport fallback. This does not widen access: it is validated against the same
        // SQL-backed TV display key.
        var suppliedPairedKey = !string.IsNullOrWhiteSpace(displayKey)
            ? displayKey
            : Request.Query.TryGetValue("key", out var queryKey) ? queryKey.FirstOrDefault() : null;
        var pairedKeyAllowed = await TvDisplayKeyStore.ValidateAsync(db, suppliedPairedKey, ct);
        var legacyKeyAllowed = TvWallboardAccess.IsAllowed(HttpContext, configuration);
        if (!signedInAllowed && !pairedKeyAllowed && !legacyKeyAllowed)
            return Unauthorized(new { message = "This wallboard request is not authorised." });

        var day = date ?? UkOperatingDate(DateTimeOffset.UtcNow);
        var loads = (await PlanningResilience.ReadLoadsAsync(db, day, ct))
            .Where(load => load.PlanningDate == day && load.Status != LoadStatus.Cancelled)
            .OrderBy(load => load.Stops.Where(stop => stop.PlannedArrivalUtc is not null)
                .Select(stop => stop.PlannedArrivalUtc)
                .Min() ?? DateTimeOffset.MaxValue)
            .ThenBy(load => load.Reference)
            .ToList();

        await RunOperationalStore.EnrichAsync(db, loads, ct);
        WallboardPhysicalStops.Apply(loads);
        foreach (var load in loads)
        {
            OvernightRunContinuity.Apply(load);
            load.Stops = OperationalStopOrdering.Order(load.Stops)
                .Select((stop, index) =>
                {
                    stop.Sequence = index + 1;
                    return stop;
                })
                .ToList();
        }
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
