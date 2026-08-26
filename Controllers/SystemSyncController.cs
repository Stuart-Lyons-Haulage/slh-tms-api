using System.Text.Json;
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
        var tachoUtc = await db.Drivers.AsNoTracking().MaxAsync(item => (DateTimeOffset?)item.LastTachoSyncUtc, ct);
        var fleetioUtc = await db.Vehicles.AsNoTracking().MaxAsync(item => (DateTimeOffset?)item.FleetioLastSyncedUtc, ct);
        var sageUtc = await db.StagedImports.AsNoTracking()
            .Where(item => item.EntityType == "sagehrsync" && item.Status == StagingStatus.Promoted)
            .MaxAsync(item => item.ReviewedAtUtc ?? item.ReceivedAtUtc, ct);
        var heartbeat = await db.StagedImports.AsNoTracking()
            .Where(item => item.EntityType == "infomailboxheartbeat" && item.Status == StagingStatus.Promoted)
            .OrderByDescending(item => item.ReviewedAtUtc ?? item.ReceivedAtUtc)
            .FirstOrDefaultAsync(ct);
        var heartbeatUtc = heartbeat?.ReviewedAtUtc ?? heartbeat?.ReceivedAtUtc;
        var latestOrderReceivedUtc = await db.StagedImports.AsNoTracking()
            .Where(item => item.EntityType == "order" && item.Source != null && item.Source.Contains("Info mailbox"))
            .MaxAsync(item => (DateTimeOffset?)item.ReceivedAtUtc, ct);
        var latestInboxReceivedAtUtc = ReadPayloadDate(heartbeat?.PayloadJson, "latestInboxReceivedAtUtc");
        var heartbeatFlowName = ReadPayloadText(heartbeat?.PayloadJson, "flowName");
        var heartbeatFlowRunId = ReadPayloadText(heartbeat?.PayloadJson, "flowRunId");

        var providers = new[]
        {
            Provider("DOT / Falcon", dot.IsConfigured, trackingUtc, TimeSpan.FromMinutes(10), now),
            Provider("TachoMaster", tacho.IsConfigured, tachoUtc, TimeSpan.FromMinutes(15), now),
            Provider("Sage HR", sage.IsConfigured, sageUtc, TimeSpan.FromHours(30), now),
            Provider("Fleetio", fleetio.IsConfigured, fleetioUtc, TimeSpan.FromMinutes(75), now),
            Provider("Info mailbox", true, heartbeatUtc, TimeSpan.FromMinutes(10), now, TimeSpan.FromMinutes(20))
        };
        var configured = providers.Where(item => item.Configured).ToArray();
        var status = configured.Any(item => item.State == "stale") ? "attention" : configured.Any(item => item.State == "pending") ? "pending" : "current";
        var lastPlatformUpdateUtc = providers.Where(item => item.LastUpdatedUtc is not null).Max(item => item.LastUpdatedUtc);
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
                fleetio = "every hour",
                infoMailbox = "every 5 minutes heartbeat"
            },
            providers,
            mailbox = new
            {
                mailbox = "info@lyonshaulage.com",
                lastHeartbeatUtc = heartbeatUtc,
                heartbeatAgeMinutes = heartbeatUtc is null ? (double?)null : Math.Round((now - heartbeatUtc.Value).TotalMinutes, 1),
                latestInboxReceivedAtUtc,
                lastOrderReceivedUtc = latestOrderReceivedUtc,
                heartbeatFlowName,
                heartbeatFlowRunId,
                probe = "shared Outlook mailbox read + TMS API write"
            }
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

    private static ProviderSnapshot Provider(
        string name,
        bool configured,
        DateTimeOffset? lastUpdatedUtc,
        TimeSpan currentThreshold,
        DateTimeOffset now,
        TimeSpan? staleThreshold = null)
    {
        var age = lastUpdatedUtc is null ? (TimeSpan?)null : now - lastUpdatedUtc.Value;
        var staleAfter = staleThreshold ?? currentThreshold;
        var state = !configured
            ? "not-configured"
            : age is null
                ? "pending"
                : age > staleAfter
                    ? "stale"
                    : age > currentThreshold
                        ? "pending"
                        : "current";
        return new ProviderSnapshot(name, configured, state, lastUpdatedUtc, age is null ? null : Math.Round(age.Value.TotalMinutes, 1));
    }

    private static DateTimeOffset? ReadPayloadDate(string? payloadJson, string property)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && value.TryGetDateTimeOffset(out var parsed)
                ? parsed
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadPayloadText(string? payloadJson, string property)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record ProviderSnapshot(string Name, bool Configured, string State, DateTimeOffset? LastUpdatedUtc, double? AgeMinutes);
}
