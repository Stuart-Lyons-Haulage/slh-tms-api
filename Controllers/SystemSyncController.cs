using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Integrations;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/system-sync")]
[Authorize]
public sealed class SystemSyncController(
    TmsDbContext db,
    IntegrationSyncCoordinator coordinator,
    DotTrackingOptions dot,
    TachoMasterOptions tacho,
    SageHrClient sage,
    FleetioOptions fleetio) : ControllerBase
{
    [HttpGet("state")]
    public async Task<IActionResult> State(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var trackingUtc = await db.VehicleLiveStatuses.AsNoTracking().MaxAsync(item => (DateTimeOffset?)item.LastEventTimeUtc, ct);

        // Driver.LastTachoSyncUtc is runtime-only / NotMapped and cannot be translated by EF.
        // Use persisted successful Tacho receipts instead so the dashboard feed-health endpoint
        // cannot fail with HTTP 500 while still reporting the most recent platform evidence.
        var tachoUtc = await db.StagedImports.AsNoTracking()
            .Where(item => item.Status == StagingStatus.Promoted &&
                (item.EntityType == "tachodrivermastersync" ||
                 item.EntityType == "tachomastersync" ||
                 item.EntityType == "tachodriverprofile"))
            .MaxAsync(item => (DateTimeOffset?)(item.ReviewedAtUtc ?? item.ReceivedAtUtc), ct);

        // FleetioLastSyncedUtc is runtime-only / NotMapped. Use the latest
        // persisted Fleetio mapping update as the receipt instead of asking EF
        // to translate a non-persisted property.
        var fleetioUtc = await db.IntegrationMappings.AsNoTracking()
            .Where(item => item.Provider == "Fleetio" && item.Active)
            .MaxAsync(item => (DateTimeOffset?)item.UpdatedAtUtc, ct);
        var sageUtc = await db.StagedImports.AsNoTracking()
            .Where(item => item.EntityType == "sagehrsync" && item.Status == StagingStatus.Promoted)
            .MaxAsync(item => (DateTimeOffset?)(item.ReviewedAtUtc ?? item.ReceivedAtUtc), ct);

        var providers = new[]
        {
            Provider("DOT / Falcon", dot.IsConfigured, trackingUtc, TimeSpan.FromMinutes(10), now),
            Provider("TachoMaster", tacho.IsConfigured, tachoUtc, TimeSpan.FromMinutes(15), now),
            Provider("Sage HR", sage.IsConfigured, sageUtc, TimeSpan.FromHours(30), now),
            Provider("Fleetio", fleetio.IsConfigured, fleetioUtc, TimeSpan.FromMinutes(75), now)
        };
        var configured = providers.Where(item => item.Configured).ToArray();
        var status = configured.Any(item => item.State == "stale") ? "attention" : configured.Any(item => item.State == "pending") ? "pending" : "current";
        var lastPlatformUpdateUtc = providers.Select(item => item.LastUpdatedUtc).Max();
        return Ok(new
        {
            status,
            generatedAtUtc = now,
            lastPlatformUpdateUtc,
            displaySource = "TMS platform state",
            schedules = new
            {
                dot = "continuous ingestion",
                tachoMaster = "every 5 minutes",
                sageHr = "05:30 Europe/London daily",
                fleetio = "every hour"
            },
            providers
        });
    }

    [HttpPost("force/{provider}"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Force(string provider, CancellationToken ct)
    {
        var actor = User.Identity?.Name ?? "admin:manual";
        return provider.Trim().ToLowerInvariant() switch
        {
            "tacho" or "tachomaster" => Ok(await coordinator.SyncTachoMasterAsync(actor, ct)),
            "sage" or "sagehr" or "sage-hr" => Ok(await coordinator.SyncSageHrAsync(actor, ct)),
            "fleetio" => Ok(await coordinator.SyncFleetioAsync(actor, ct)),
            "all" => Ok(await coordinator.ForceAllAsync(actor, ct)),
            _ => BadRequest(new { message = "Provider must be tacho, sage, fleetio or all." })
        };
    }

    private static ProviderSnapshot Provider(string name, bool configured, DateTimeOffset? lastUpdatedUtc, TimeSpan threshold, DateTimeOffset now)
    {
        var state = !configured ? "not-configured" : lastUpdatedUtc is null ? "pending" : now - lastUpdatedUtc > threshold ? "stale" : "current";
        return new ProviderSnapshot(name, configured, state, lastUpdatedUtc, lastUpdatedUtc is null ? null : Math.Round((now - lastUpdatedUtc.Value).TotalMinutes, 1));
    }

    private sealed record ProviderSnapshot(string Name, bool Configured, string State, DateTimeOffset? LastUpdatedUtc, double? AgeMinutes);
}