using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Integrations;

namespace Slh.Tms.Api.Services;

public sealed record IntegrationSyncResult(string Provider, bool Success, DateTimeOffset CompletedAtUtc, string Message, int Changed = 0);

public sealed class IntegrationSyncCoordinator(
    TmsDbContext db,
    TachoMasterClient tachoMaster,
    SageHrClient sageHr,
    SageHrOptions sageOptions,
    FleetioClient fleetioClient,
    ILogger<IntegrationSyncCoordinator> logger)
{
    public async Task<IntegrationSyncResult> SyncTachoMasterAsync(string actor, CancellationToken ct)
    {
        if (!tachoMaster.IsConfigured)
            return new("TachoMaster", false, DateTimeOffset.UtcNow, $"TachoMaster is not configured: {string.Join(", ", tachoMaster.MissingSettings)}.");

        var profiles = await tachoMaster.GetDriverProfilesAsync(ct);
        var drivers = await db.Drivers.Where(driver => driver.Active).OrderBy(driver => driver.DisplayName).ToListAsync(ct);
        await MasterDetailStore.EnrichDriversAsync(db, drivers, ct);
        var byName = profiles.GroupBy(profile => NormalisePersonName(profile.DriverName)).ToDictionary(group => group.Key, group => group.First());
        var byEmployee = profiles.Where(profile => !string.IsNullOrWhiteSpace(profile.EmployeeNumber))
            .GroupBy(profile => NormalisePersonName(profile.EmployeeNumber)).ToDictionary(group => group.Key, group => group.First());
        var matched = 0;
        foreach (var driver in drivers)
        {
            TachoDriverProfile? profile = null;
            if (!string.IsNullOrWhiteSpace(driver.TachoName)) byName.TryGetValue(NormalisePersonName(driver.TachoName), out profile);
            if (profile is null && !string.IsNullOrWhiteSpace(driver.EmployeeNumber)) byEmployee.TryGetValue(NormalisePersonName(driver.EmployeeNumber), out profile);
            if (profile is null) continue;
            driver.TachoMasterDriverId = profile.MemberCode.ToString();
            driver.TachoCardNumber = profile.CardNumber;
            driver.TachoDriveAvailableTodayMinutes = profile.DriveAvailableTodayMinutes;
            driver.TachoDriveAvailableWeekMinutes = profile.DriveAvailableWeekMinutes;
            driver.TachoWorkAvailableWeekMinutes = profile.WorkAvailableWeekMinutes;
            driver.LastTachoSyncUtc = DateTimeOffset.UtcNow;
            await MasterDetailStore.SaveAsync(db, "driver", driver.EmployeeNumber, JsonSerializer.Serialize(driver), "TachoMaster driver directory", actor, ct);
            matched++;
        }
        await db.SaveChangesAsync(ct);
        return new("TachoMaster", true, DateTimeOffset.UtcNow, $"TachoMaster matched {matched} of {drivers.Count} active drivers.", matched);
    }

    public async Task<IntegrationSyncResult> SyncSageHrAsync(string actor, CancellationToken ct)
    {
        if (!sageHr.IsConfigured)
            return new("Sage HR", false, DateTimeOffset.UtcNow, $"Sage HR is not configured: {string.Join(", ", sageHr.MissingSettings)}.");

        var employees = await sageHr.GetActiveEmployeesAsync(ct);
        var rawCandidates = employees.Where(IsDriver).ToList();
        var candidates = rawCandidates
            .GroupBy(employee => string.IsNullOrWhiteSpace(employee.EmployeeNumber) ? $"SAGE-{employee.Id}" : employee.EmployeeNumber.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()).ToList();
        var created = 0;
        var updated = 0;
        var skipped = rawCandidates.Count - candidates.Count;
        var existingNumbers = (await db.Drivers.AsNoTracking().Select(driver => driver.EmployeeNumber).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        foreach (var employee in candidates)
        {
            var employeeNumber = ClipRequired(string.IsNullOrWhiteSpace(employee.EmployeeNumber) ? $"SAGE-{employee.Id}" : employee.EmployeeNumber.Trim(), 40);
            var displayName = ClipRequired($"{employee.FirstName} {employee.LastName}".Trim(), 160);
            if (string.IsNullOrWhiteSpace(displayName)) { skipped++; continue; }
            var mobileNumber = Clip(employee.MobilePhone, 40);
            var driverType = Clip(employee.Position, 80);
            var driverGroup = Clip(employee.Team, 80);
            if (!existingNumbers.Contains(employeeNumber))
            {
                var id = Guid.NewGuid();
                string? tachoName = null;
                string? skills = null;
                await db.Database.ExecuteSqlInterpolatedAsync($@"
                    INSERT INTO dbo.Drivers (Id, EmployeeNumber, DisplayName, TachoName, MobileNumber, DriverType, DriverGroup, Skills, Active)
                    VALUES ({id}, {employeeNumber}, {displayName}, {tachoName}, {mobileNumber}, {driverType}, {driverGroup}, {skills}, {true})", ct);
                existingNumbers.Add(employeeNumber);
                created++;
            }
            else
            {
                await db.Database.ExecuteSqlInterpolatedAsync($@"
                    UPDATE dbo.Drivers SET DisplayName = {displayName}, MobileNumber = {mobileNumber}, DriverType = {driverType}, DriverGroup = {driverGroup}, Active = {true}
                    WHERE EmployeeNumber = {employeeNumber}", ct);
                updated++;
            }
        }
        var now = DateTimeOffset.UtcNow;
        db.StagedImports.Add(new StagedImport
        {
            EntityType = "sagehrsync",
            IdempotencyKey = $"sagehrsync:{Guid.NewGuid():N}",
            PayloadJson = JsonSerializer.Serialize(new { sourceEmployeeCount = employees.Count, driverCandidateCount = candidates.Count, created, updated, skipped }),
            Source = actor.StartsWith("system:", StringComparison.OrdinalIgnoreCase) ? "Sage HR scheduled synchronisation" : "Sage HR manual synchronisation",
            Status = StagingStatus.Promoted,
            ReceivedAtUtc = now,
            ReviewedAtUtc = now,
            ReviewedBy = actor,
            ReviewNote = "Sage HR synchronisation through the shared integration coordinator."
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new("Sage HR", true, now, $"Sage HR synchronised {created + updated} driver records ({created} created, {updated} updated).", created + updated);
    }

    public async Task<IntegrationSyncResult> SyncFleetioAsync(string actor, CancellationToken ct)
    {
        if (!fleetioClient.IsConfigured)
            return new("Fleetio", false, DateTimeOffset.UtcNow, $"Fleetio is not configured: {string.Join(", ", fleetioClient.MissingSettings)}.");

        var fleetioVehicles = await fleetioClient.GetVehiclesAsync(100, ct);
        var tmsVehicles = await db.Vehicles.Where(vehicle => vehicle.Active).ToListAsync(ct);
        var lookup = BuildFleetioLookup(fleetioVehicles);
        var updated = 0;
        foreach (var vehicle in tmsVehicles)
        {
            if (Regex.IsMatch(vehicle.Registration, "^C\\d{5,}$", RegexOptions.IgnoreCase))
            {
                vehicle.Active = false;
                continue;
            }
            var match = VehicleKeys(vehicle.Registration).Select(key => lookup.GetValueOrDefault(key)).FirstOrDefault(item => item is not null);
            if (match is null) continue;
            vehicle.FleetioId = match.Id;
            vehicle.FleetioName = Clip(match.Name, 160);
            vehicle.FleetioStatus = Clip(match.Status, 80);
            vehicle.FleetioVor = match.Vor;
            vehicle.FleetioPmiDueUtc = match.PmiDueUtc;
            vehicle.FleetioMotDueUtc = match.MotDueUtc;
            vehicle.FleetioServiceStatus = Clip(match.ServiceStatus, 160);
            vehicle.FleetioLastSyncedUtc = DateTimeOffset.UtcNow;
            if (string.IsNullOrWhiteSpace(vehicle.FleetNumber)) vehicle.FleetNumber = Clip(match.FleetNumber, 40);
            updated++;
        }
        await db.SaveChangesAsync(ct);
        return new("Fleetio", true, DateTimeOffset.UtcNow, $"Fleetio enriched {updated} active TMS vehicles.", updated);
    }

    public async Task<IReadOnlyList<IntegrationSyncResult>> ForceAllAsync(string actor, CancellationToken ct)
    {
        var results = new List<IntegrationSyncResult>();
        foreach (var sync in new Func<string, CancellationToken, Task<IntegrationSyncResult>>[] { SyncTachoMasterAsync, SyncSageHrAsync, SyncFleetioAsync })
        {
            try { results.Add(await sync(actor, ct)); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Forced integration sync failed.");
                results.Add(new("Integration", false, DateTimeOffset.UtcNow, ex.GetBaseException().Message));
            }
        }
        return results;
    }

    private bool IsDriver(SageHrEmployee employee) =>
        (!string.IsNullOrWhiteSpace(sageOptions.DriverTeamName) && string.Equals(employee.Team, sageOptions.DriverTeamName, StringComparison.OrdinalIgnoreCase)) ||
        (!string.IsNullOrWhiteSpace(sageOptions.DriverPositionKeyword) && employee.Position?.Contains(sageOptions.DriverPositionKeyword, StringComparison.OrdinalIgnoreCase) == true);

    private static Dictionary<string, FleetioVehicle> BuildFleetioLookup(IReadOnlyList<FleetioVehicle> items)
    {
        var lookup = new Dictionary<string, FleetioVehicle>(StringComparer.OrdinalIgnoreCase);
        foreach (var vehicle in items)
            foreach (var key in VehicleKeys(vehicle.Registration)) lookup.TryAdd(key, vehicle);
        return lookup;
    }

    private static IReadOnlyList<string> VehicleKeys(params string?[] values)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values.Where(item => !string.IsNullOrWhiteSpace(item)))
        {
            var key = new string(value!.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
            if (key.Length == 0) continue;
            keys.Add(key);
            if (key.Length > 3) keys.Add(key[^3..]);
            if (key.EndsWith("H", StringComparison.OrdinalIgnoreCase) && key.Length > 4) keys.Add(key[..^1]);
        }
        return keys.ToList();
    }

    private static string NormalisePersonName(string? value) => string.Join(' ', (value ?? string.Empty)
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .Select(word => new string(word.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray()))
        .Where(word => word.Length > 0).OrderBy(word => word, StringComparer.Ordinal));
    private static string? Clip(string? value, int maxLength) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= maxLength ? value.Trim() : value.Trim()[..maxLength];
    private static string ClipRequired(string value, int maxLength) => value.Trim().Length <= maxLength ? value.Trim() : value.Trim()[..maxLength];
}

public sealed class IntegrationBackgroundSyncService(IServiceScopeFactory scopeFactory, ILogger<IntegrationBackgroundSyncService> logger) : BackgroundService
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextTacho = DateTimeOffset.MinValue;
        var nextFleetio = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            try
            {
                if (now >= nextTacho)
                {
                    await Run(scope => scope.SyncTachoMasterAsync("system:scheduler", stoppingToken), stoppingToken);
                    nextTacho = now.AddMinutes(5);
                }
                if (now >= nextFleetio)
                {
                    await Run(scope => scope.SyncFleetioAsync("system:scheduler", stoppingToken), stoppingToken);
                    nextFleetio = now.AddHours(1);
                }
                await RunMorningSageIfDue(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Background integration scheduler iteration failed.");
            }
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    private async Task Run(Func<IntegrationSyncCoordinator, Task<IntegrationSyncResult>> action, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var coordinator = scope.ServiceProvider.GetRequiredService<IntegrationSyncCoordinator>();
        try
        {
            var result = await action(coordinator);
            if (!result.Success) logger.LogWarning("{Provider} scheduled sync did not complete: {Message}", result.Provider, result.Message);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Scheduled integration synchronisation failed.");
        }
    }

    private async Task RunMorningSageIfDue(CancellationToken ct)
    {
        var localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, London);
        if (localNow.TimeOfDay < new TimeSpan(5, 30, 0)) return;
        var localDate = DateOnly.FromDateTime(localNow.DateTime);
        var localStart = new DateTimeOffset(localDate.ToDateTime(TimeOnly.MinValue), London.GetUtcOffset(localDate.ToDateTime(TimeOnly.MinValue))).ToUniversalTime();
        var localEnd = localStart.AddDays(1);
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var alreadyRun = await db.StagedImports.AsNoTracking().AnyAsync(item => item.EntityType == "sagehrsync" && item.Status == StagingStatus.Promoted && item.ReviewedAtUtc >= localStart && item.ReviewedAtUtc < localEnd, ct);
        if (alreadyRun) return;
        var coordinator = scope.ServiceProvider.GetRequiredService<IntegrationSyncCoordinator>();
        try { await coordinator.SyncSageHrAsync("system:scheduler", ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested) { logger.LogWarning(ex, "Scheduled morning Sage HR synchronisation failed."); }
    }
}
