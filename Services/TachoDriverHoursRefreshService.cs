using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models.Tracking;

namespace Slh.Tms.Api.Services;

public sealed record TachoDriverHoursRefreshResult(
    string Status,
    bool Updated,
    Guid DriverId,
    string DisplayName,
    string? TachoMasterDriverId,
    string? TachoCardNumber,
    string? TachoVehicleCode,
    DateTimeOffset? CardInsertedUtc,
    DateTimeOffset? CurrentDutyEndUtc,
    bool TachoDutyOpen,
    int? TachoWorkTodayMinutes,
    int? TachoDriveTodayMinutes,
    int? TachoAvailableTodayMinutes,
    int? TachoRestTodayMinutes,
    int? TachoBreakCount,
    int? TachoBreakMinutes,
    int? TachoDriveAvailableTodayMinutes,
    int? TachoDriveAvailableWeekMinutes,
    int? TachoWorkAvailableWeekMinutes,
    DateTimeOffset? LastTachoSyncUtc,
    string Message);

public sealed class TachoDriverHoursRefreshService(
    TmsDbContext db,
    TachoMasterClient tachoMaster,
    TachoMasterOptions options,
    DistributedLeaseManager leases,
    ILogger<TachoDriverHoursRefreshService> logger)
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    public async Task<int> RefreshDriverHoursOnlyAsync(string actor, CancellationToken ct)
    {
        await using var lease = await leases.TryAcquireAsync(
            IntegrationLeaseNames.TachoMaster,
            TimeSpan.FromSeconds(30),
            ct);

        if (lease is null)
        {
            logger.LogInformation(
                "TachoMaster lightweight driver-hours refresh skipped because another distributed writer currently holds the integration lease.");
            return 0;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.LostToken);

        try
        {
            return await RefreshDriverHoursOnlyCoreAsync(actor, linked.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("TachoMaster lightweight driver-hours refresh stopped because the distributed lease was lost.");
            return 0;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "TachoMaster lightweight driver-hours refresh failed. The next scheduled pass will still run.");
            return 0;
        }
    }

    public async Task<TachoDriverHoursRefreshResult> RefreshDriverHoursOnlyAsync(Guid driverId, string actor, CancellationToken ct)
    {
        await using var lease = await leases.TryAcquireAsync(
            IntegrationLeaseNames.TachoMaster,
            TimeSpan.FromSeconds(30),
            ct);

        if (lease is null)
        {
            return Empty(
                "lease_busy",
                false,
                driverId,
                "TachoMaster driver-hours refresh skipped because another distributed writer currently holds the integration lease.");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.LostToken);

        try
        {
            return await RefreshSingleDriverHoursOnlyCoreAsync(driverId, actor, linked.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("TachoMaster single-driver hours refresh for {DriverId} stopped because the distributed lease was lost.", driverId);
            return Empty(
                "lease_lost",
                false,
                driverId,
                "TachoMaster driver-hours refresh stopped because the distributed lease was lost.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "TachoMaster single-driver hours refresh failed for {DriverId}. Actor: {Actor}", driverId, actor);
            return Empty(
                "failed",
                false,
                driverId,
                $"TachoMaster driver-hours refresh failed: {ex.GetBaseException().Message}");
        }
    }

    private async Task<int> RefreshDriverHoursOnlyCoreAsync(string actor, CancellationToken ct)
    {
        if (!options.IsConfigured)
        {
            logger.LogInformation("TachoMaster lightweight driver-hours refresh skipped because TachoMaster is not configured.");
            return 0;
        }

        var now = DateTimeOffset.UtcNow;
        var profiles = await tachoMaster.GetDriverProfilesAsync(ct);
        var profilesByMemberCode = profiles
            .Where(profile => profile.MemberCode > 0)
            .GroupBy(profile => TachoDriverIdentityRules.NormaliseIdentifier(
                profile.MemberCode.ToString(CultureInfo.InvariantCulture)),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        if (profilesByMemberCode.Count == 0)
        {
            logger.LogWarning("TachoMaster lightweight driver-hours refresh returned no driver profiles.");
            return 0;
        }

        var drivers = await db.Drivers
            .Where(driver => driver.Active && driver.TachoMasterDriverId != null && driver.TachoMasterDriverId != string.Empty)
            .OrderBy(driver => driver.DisplayName)
            .ToListAsync(ct);

        var updated = 0;
        foreach (var driver in drivers)
        {
            var memberCode = TachoDriverIdentityRules.NormaliseIdentifier(driver.TachoMasterDriverId);
            if (memberCode.Length == 0) continue;
            if (!profilesByMemberCode.TryGetValue(memberCode, out var profile)) continue;

            ApplyProfile(driver, profile, now);
            updated++;
        }

        if (updated == 0)
        {
            logger.LogInformation(
                "TachoMaster lightweight driver-hours refresh found profiles, but none matched active TMS drivers by Member Code.");
            return 0;
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "TachoMaster lightweight driver-hours refresh updated {UpdatedDrivers} active driver(s) by Member Code. Actor: {Actor}",
            updated,
            actor);
        return updated;
    }

    private async Task<TachoDriverHoursRefreshResult> RefreshSingleDriverHoursOnlyCoreAsync(Guid driverId, string actor, CancellationToken ct)
    {
        var driver = await db.Drivers.FirstOrDefaultAsync(item => item.Id == driverId, ct);
        if (driver is null)
        {
            return Empty("not_found", false, driverId, "Driver was not found in Driver Master.");
        }

        if (!driver.Active)
        {
            return Result("inactive", false, driver, null,
                "Driver is inactive, so TachoMaster hours were not refreshed.");
        }

        var memberCode = TachoDriverIdentityRules.NormaliseIdentifier(driver.TachoMasterDriverId);
        if (memberCode.Length == 0)
        {
            return Result("missing_member_code", false, driver, null,
                "Driver has no TachoMaster Member Code, so TachoMaster hours were not refreshed.");
        }

        if (!options.IsConfigured)
        {
            return Result("not_configured", false, driver, null,
                "TachoMaster is not configured, so driver hours were not refreshed.");
        }

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, London).DateTime);
        var profilesTask = tachoMaster.GetDriverProfilesAsync(ct);
        var dutiesTask = tachoMaster.GetDriverDutyStatusesAsync(today, ct);
        await Task.WhenAll(profilesTask, dutiesTask);

        var profile = (await profilesTask).FirstOrDefault(item =>
            string.Equals(
                TachoDriverIdentityRules.NormaliseIdentifier(item.MemberCode.ToString(CultureInfo.InvariantCulture)),
                memberCode,
                StringComparison.OrdinalIgnoreCase));

        if (profile is null)
        {
            return Result("profile_not_found", false, driver, null,
                $"TachoMaster did not return a profile for Member Code {driver.TachoMasterDriverId}.");
        }

        var todayDuties = (await dutiesTask)
            .Where(item => string.Equals(
                TachoDriverIdentityRules.NormaliseIdentifier(item.MemberCode.ToString(CultureInfo.InvariantCulture)),
                memberCode,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.DutyStartUtc)
            .ToList();
        var dutyEvidence = BuildDutyEvidence(todayDuties);

        var now = DateTimeOffset.UtcNow;
        ApplyProfile(driver, profile, now);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "TachoMaster single-driver refresh updated hours and duty evidence for {DriverId} / {DriverName} by Member Code {MemberCode}. Actor: {Actor}",
            driver.Id,
            driver.DisplayName,
            driver.TachoMasterDriverId,
            actor);

        return Result("updated", true, driver, dutyEvidence,
            todayDuties.Count == 0
                ? "TachoMaster driver hours refreshed, but no card/duty insertion was found for today."
                : "TachoMaster driver hours refreshed with card inserted / first sign-on evidence for ETA and compliance use.");
    }

    private static TachoDutyEvidence? BuildDutyEvidence(IReadOnlyList<TachoDriverDutyStatus> duties)
    {
        if (duties.Count == 0) return null;
        var first = duties.OrderBy(item => item.DutyStartUtc).First();
        var current = duties
            .OrderByDescending(item => item.DutyEndUtc is null)
            .ThenByDescending(item => item.DutyStartUtc)
            .First();

        return new(
            first.CardNumber,
            current.VehicleCode,
            first.DutyStartUtc,
            current.DutyEndUtc,
            current.DutyEndUtc is null,
            duties.Sum(item => item.WorkMinutes),
            duties.Sum(item => item.DriveMinutes),
            duties.Sum(item => item.AvailableMinutes),
            duties.Sum(item => item.RestMinutes),
            duties.Sum(item => item.BreakCount),
            duties.Any(item => item.BreakMinutes is not null) ? duties.Sum(item => item.BreakMinutes ?? 0) : null);
    }

    private static void ApplyProfile(Models.Driver driver, TachoDriverProfile profile, DateTimeOffset now)
    {
        driver.TachoCardNumber = string.IsNullOrWhiteSpace(profile.CardNumber) ? driver.TachoCardNumber : profile.CardNumber;
        driver.TachoDriveAvailableTodayMinutes = profile.DriveAvailableTodayMinutes;
        driver.TachoDriveAvailableWeekMinutes = profile.DriveAvailableWeekMinutes;
        driver.TachoWorkAvailableWeekMinutes = profile.WorkAvailableWeekMinutes;
        driver.LastTachoSyncUtc = now;
    }

    private static TachoDriverHoursRefreshResult Empty(string status, bool updated, Guid driverId, string message) => new(
        status,
        updated,
        driverId,
        string.Empty,
        null,
        null,
        null,
        null,
        null,
        false,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        message);

    private static TachoDriverHoursRefreshResult Result(string status, bool updated, Models.Driver driver, TachoDutyEvidence? duty, string message) => new(
        status,
        updated,
        driver.Id,
        driver.DisplayName,
        driver.TachoMasterDriverId,
        duty?.CardNumber ?? driver.TachoCardNumber,
        duty?.VehicleCode,
        duty?.CardInsertedUtc,
        duty?.CurrentDutyEndUtc,
        duty?.DutyOpen ?? false,
        duty?.WorkTodayMinutes,
        duty?.DriveTodayMinutes,
        duty?.AvailableTodayMinutes,
        duty?.RestTodayMinutes,
        duty?.BreakCount,
        duty?.BreakMinutes,
        driver.TachoDriveAvailableTodayMinutes,
        driver.TachoDriveAvailableWeekMinutes,
        driver.TachoWorkAvailableWeekMinutes,
        driver.LastTachoSyncUtc,
        message);

    private sealed record TachoDutyEvidence(
        string? CardNumber,
        string? VehicleCode,
        DateTimeOffset? CardInsertedUtc,
        DateTimeOffset? CurrentDutyEndUtc,
        bool DutyOpen,
        int? WorkTodayMinutes,
        int? DriveTodayMinutes,
        int? AvailableTodayMinutes,
        int? RestTodayMinutes,
        int? BreakCount,
        int? BreakMinutes);
}

public sealed class TachoDriverHoursRefreshWorker(
    IServiceScopeFactory scopeFactory,
    IHostEnvironment environment,
    ILogger<TachoDriverHoursRefreshWorker> logger) : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (environment.IsEnvironment("Testing")) return;

        var nextRefresh = DateTimeOffset.UtcNow + RefreshInterval;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var delay = nextRefresh - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, stoppingToken);

                await using var scope = scopeFactory.CreateAsyncScope();
                var refresh = scope.ServiceProvider.GetRequiredService<TachoDriverHoursRefreshService>();
                await refresh.RefreshDriverHoursOnlyAsync("system:tachomaster-driver-hours-refresh", stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "TachoMaster lightweight driver-hours refresh pass failed; the next 15-minute pass will still run.");
            }
            finally
            {
                nextRefresh = DateTimeOffset.UtcNow + RefreshInterval;
            }
        }
    }
}
