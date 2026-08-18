using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Live operational coverage for Ops Control.
/// DOT/RoadTech is the source of vehicle location and movement.
/// TachoMaster is the source of driver/card identity and legal-hours data.
/// </summary>
[ApiController]
[Route("api/v1/operations")]
[Authorize]
public sealed class OperationsLiveCoverageController(
    TmsDbContext db,
    DotTrackingClient dotTracking,
    TachoMasterClient tachoMaster,
    DotTrackingOptions tracking,
    ILogger<OperationsLiveCoverageController> logger) : ControllerBase
{
    [HttpGet("live-coverage")]
    public async Task<IActionResult> LiveCoverage(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var operatingDate = UkOperatingDate(now);

        var dotRecords = await GetDotRecords(ct);
        var dotProvider = dotRecords.Provider;
        var latestDotEvent = dotRecords.Records.Count == 0
            ? (DateTimeOffset?)null
            : dotRecords.Records.Max(record => record.EventTimeUtc);

        IReadOnlyDictionary<string, TachoVehicleDriverStatus> tachoStatuses = new Dictionary<string, TachoVehicleDriverStatus>();
        string? tachoError = null;
        if (tachoMaster.IsConfigured)
        {
            try
            {
                tachoStatuses = await tachoMaster.GetCurrentDriverStatusesByVehicleAsync(operatingDate, ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                tachoError = exception.GetBaseException().Message;
                logger.LogWarning(exception, "Live operations coverage could not read TachoMaster driver/card identities.");
            }
        }

        var movingRecords = dotRecords.Records
            .Where(record => IsMoving(record))
            .GroupBy(record => NormaliseIdentifier(record.VehicleIdentifier))
            .Select(group => group.OrderByDescending(record => record.EventTimeUtc).First())
            .ToList();

        var tachoAliases = BuildTachoAliasLookup(tachoStatuses);
        var movingWithTacho = 0;
        var movingWithTachoMember = 0;
        var movingWithLiveCardOrName = 0;
        var movingWithoutTacho = new List<UnmatchedMovingVehicle>();
        var movingVehicleRows = new List<LiveVehicleCoverageRow>();

        foreach (var record in movingRecords.OrderBy(record => record.VehicleIdentifier))
        {
            var aliases = IdentifierAliases(record.VehicleIdentifier);
            var status = aliases
                .Select(alias => tachoAliases.GetValueOrDefault(alias))
                .Where(value => value is not null)
                .OrderByDescending(value => value!.VehicleCode.Length)
                .FirstOrDefault();

            if (status is not null)
            {
                movingWithTacho++;
                if (status.MemberCode > 0) movingWithTachoMember++;
            }
            else
            {
                movingWithoutTacho.Add(new UnmatchedMovingVehicle(
                    record.VehicleIdentifier,
                    record.EventTimeUtc,
                    record.SpeedKph,
                    record.DriverName,
                    string.IsNullOrWhiteSpace(record.DriverCardNumber) ? false : true,
                    string.IsNullOrWhiteSpace(record.DriverCardNumber)
                        ? "No TachoMaster duty/card identity matched this moving DOT vehicle."
                        : "DOT reports a driver card, but it did not match a TachoMaster member/vehicle identity."));
            }

            if (!string.IsNullOrWhiteSpace(record.DriverName) || !string.IsNullOrWhiteSpace(record.DriverCardNumber))
                movingWithLiveCardOrName++;

            movingVehicleRows.Add(new LiveVehicleCoverageRow(
                record.VehicleIdentifier,
                record.EventTimeUtc,
                record.SpeedKph,
                record.Latitude,
                record.Longitude,
                status?.DriverName,
                status?.CardNumber,
                status?.MemberCode > 0,
                status is null
                    ? "Attention"
                    : status.MemberCode > 0
                        ? "Matched"
                        : "LiveIdentityOnly"));
        }

        var tachoDutyLike = tachoStatuses.Values.Count(status => status.DriveMinutes > 0 || status.WorkMinutes > 0 || status.AvailableMinutes > 0 || status.RestMinutes > 0);
        var tachoMemberIdentities = tachoStatuses.Values.Count(status => status.MemberCode > 0);
        var liveOnlyIdentities = Math.Max(0, tachoStatuses.Count - tachoMemberIdentities);

        return Ok(new LiveCoverageResponse(
            now,
            operatingDate,
            new DotCoverage(
                tracking.IsConfigured,
                dotProvider,
                dotRecords.Records.Count,
                movingRecords.Count,
                latestDotEvent),
            new TachoCoverage(
                tachoMaster.IsConfigured,
                tachoError is null && tachoMaster.IsConfigured,
                tachoStatuses.Count,
                tachoDutyLike,
                tachoMemberIdentities,
                liveOnlyIdentities,
                tachoError),
            new LiveCoverageSummary(
                movingRecords.Count,
                movingWithTacho,
                movingWithTachoMember,
                movingWithLiveCardOrName,
                Math.Max(0, movingRecords.Count - movingWithTacho),
                movingWithoutTacho.Count),
            movingVehicleRows,
            movingWithoutTacho));
    }

    private async Task<DotCoverageRecords> GetDotRecords(CancellationToken ct)
    {
        try
        {
            var items = await dotTracking.GetLatestVehicleEventsAsync(ct);
            var records = items.Select(DotTelemetryRecord.FromProvider).ToList();
            if (records.Count > 0) return new DotCoverageRecords("RoadTech Falcon live", records);
        }
        catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(exception, "Live DOT/RoadTech coverage check failed; using stored live-status fallback.");
        }

        try
        {
            var stored = await db.VehicleLiveStatuses.AsNoTracking().ToListAsync(ct);
            var records = stored.Select(status => new DotTelemetryRecord(
                $"stored-{status.Id}",
                status.VehicleIdentifier,
                status.LastEventTimeUtc,
                status.Latitude,
                status.Longitude,
                status.SpeedKph,
                status.IgnitionOn,
                status.IsMoving,
                status.LastKnownStatus ?? "Stored DOT position",
                "{}",
                status.CurrentDriverName,
                null)).ToList();
            return new DotCoverageRecords("RoadTech Falcon stored fallback", records);
        }
        catch (Exception exception) when (exception is InvalidOperationException or DbUpdateException)
        {
            logger.LogWarning(exception, "Stored DOT/RoadTech live status is unavailable.");
            return new DotCoverageRecords("RoadTech Falcon unavailable", []);
        }
    }

    private static Dictionary<string, TachoVehicleDriverStatus> BuildTachoAliasLookup(IReadOnlyDictionary<string, TachoVehicleDriverStatus> statuses)
    {
        var lookup = new Dictionary<string, TachoVehicleDriverStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (var status in statuses.Values)
        {
            foreach (var alias in IdentifierAliases(status.VehicleCode))
            {
                if (!lookup.ContainsKey(alias)) lookup[alias] = status;
            }
        }
        return lookup;
    }

    private static bool IsMoving(DotTelemetryRecord record) =>
        record.IsMoving == true || record.SpeedKph.GetValueOrDefault() > 3;

    private static string NormaliseIdentifier(string value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static IReadOnlyList<string> IdentifierAliases(string value)
    {
        var normalised = NormaliseIdentifier(value);
        if (string.IsNullOrWhiteSpace(normalised)) return [];

        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { normalised };
        for (var length = 3; length <= Math.Min(6, normalised.Length); length++)
            aliases.Add(normalised[^length..]);

        if (normalised.Length == 7 && char.IsLetter(normalised[0]) && char.IsLetter(normalised[1]) && char.IsDigit(normalised[2]) && char.IsDigit(normalised[3]))
            aliases.Add(normalised[2..]);

        if (normalised.EndsWith("H", StringComparison.OrdinalIgnoreCase) && normalised.Length > 4)
            aliases.Add(normalised[..^1]);

        return aliases.Where(alias => alias.Length >= 3).ToList();
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

    private sealed record DotCoverageRecords(string Provider, IReadOnlyList<DotTelemetryRecord> Records);
}

public sealed record LiveCoverageResponse(
    DateTimeOffset GeneratedAtUtc,
    DateOnly OperatingDate,
    DotCoverage Dot,
    TachoCoverage TachoMaster,
    LiveCoverageSummary Summary,
    IReadOnlyList<LiveVehicleCoverageRow> Vehicles,
    IReadOnlyList<UnmatchedMovingVehicle> UnmatchedMovingVehicles);

public sealed record DotCoverage(
    bool Configured,
    string Provider,
    int LiveVehicleCount,
    int MovingVehicleCount,
    DateTimeOffset? LatestEventUtc);

public sealed record TachoCoverage(
    bool Configured,
    bool Connected,
    int VehicleIdentityCount,
    int DutyRecordCount,
    int TachoMemberIdentityCount,
    int LiveOnlyIdentityCount,
    string? Error);

public sealed record LiveCoverageSummary(
    int MovingVehicles,
    int MovingWithTachoIdentity,
    int MovingWithTachoMemberMatch,
    int MovingWithLiveCardOrNameFromDot,
    int MovingWithoutTachoIdentity,
    int AttentionCount);

public sealed record LiveVehicleCoverageRow(
    string VehicleIdentifier,
    DateTimeOffset LastEventUtc,
    decimal? SpeedKph,
    decimal? Latitude,
    decimal? Longitude,
    string? TachoDriverName,
    string? TachoCardNumber,
    bool TachoMemberMatched,
    string Status);

public sealed record UnmatchedMovingVehicle(
    string VehicleIdentifier,
    DateTimeOffset LastEventUtc,
    decimal? SpeedKph,
    string? DotDriverName,
    bool DotDriverCardDetected,
    string Reason);
