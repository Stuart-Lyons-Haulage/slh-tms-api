using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;

namespace Slh.Tms.Api.Services;

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

            driver.TachoDriveAvailableTodayMinutes = profile.DriveAvailableTodayMinutes;
            driver.TachoDriveAvailableWeekMinutes = profile.DriveAvailableWeekMinutes;
            driver.TachoWorkAvailableWeekMinutes = profile.WorkAvailableWeekMinutes;
            driver.LastTachoSyncUtc = now;
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
