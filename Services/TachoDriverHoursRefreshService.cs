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
            return new(
                "lease_busy",
                false,
                driverId,
                string.Empty,
                null,
                null,
                null,
                null,
                null,
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
            return new(
                "lease_lost",
                false,
                driverId,
                string.Empty,
                null,
                null,
                null,
                null,
                null,
                "TachoMaster driver-hours refresh stopped because the distributed lease was lost.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "TachoMaster single-driver hours refresh failed for {DriverId}. Actor: {Actor}", driverId, actor);
            return new(
                "failed",
                false,
                driverId,
                string.Empty,
                null,
                null,
                null,
                null,
                null,
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
            return new("not_found", false, driverId, string.Empty, null, null, null, null, null,
                "Driver was not found in Driver Master.");
        }

        if (!driver.Active)
        {
            return Result("inactive", false, driver,
                "Driver is inactive, so TachoMaster hours were not refreshed.");
        }

        var memberCode = TachoDriverIdentityRules.NormaliseIdentifier(driver.TachoMasterDriverId);
        if (memberCode.Length == 0)
        {
            return Result("missing_member_code", false, driver,
                "Driver has no TachoMaster Member Code, so TachoMaster hours were not refreshed.");
        }

        if (!options.IsConfigured)
        {
            return Result("not_configured", false, driver,
                "TachoMaster is not configured, so driver hours were not refreshed.");
        }

        var profiles = await tachoMaster.GetDriverProfilesAsync(ct);
        var profile = profiles.FirstOrDefault(item =>
            string.Equals(
                TachoDriverIdentityRules.NormaliseIdentifier(item.MemberCode.ToString(CultureInfo.InvariantCulture)),
                memberCode,
                StringComparison.OrdinalIgnoreCase));

        if (profile is null)
        {
            return Result("profile_not_found", false, driver,
                $"TachoMaster did not return a profile for Member Code {driver.TachoMasterDriverId}.");
        }

        var now = DateTimeOffset.UtcNow;
        ApplyProfile(driver, profile, now);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "TachoMaster single-driver hours refresh updated {DriverId} / {DriverName} by Member Code {MemberCode}. Actor: {Actor}",
            driver.Id,
            driver.DisplayName,
            driver.TachoMasterDriverId,
            actor);

        return Result("updated", true, driver,
            "TachoMaster driver hours refreshed from the latest profile metrics.");
    }

    private static void ApplyProfile(Models.Driver driver, TachoDriverProfile profile, DateTimeOffset now)
    {
        driver.TachoDriveAvailableTodayMinutes = profile.DriveAvailableTodayMinutes;
        driver.TachoDriveAvailableWeekMinutes = profile.DriveAvailableWeekMinutes;
        driver.TachoWorkAvailableWeekMinutes = profile.WorkAvailableWeekMinutes;
        driver.LastTachoSyncUtc = now;
    }

    private static TachoDriverHoursRefreshResult Result(string status, bool updated, Models.Driver driver, string message) => new(
        status,
        updated,
        driver.Id,
        driver.DisplayName,
        driver.TachoMasterDriverId,
        driver.TachoDriveAvailableTodayMinutes,
        driver.TachoDriveAvailableWeekMinutes,
        driver.TachoWorkAvailableWeekMinutes,
        driver.LastTachoSyncUtc,
        message);
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
