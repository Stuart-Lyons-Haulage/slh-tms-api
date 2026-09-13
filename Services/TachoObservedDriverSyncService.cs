using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public sealed record TachoObservedDriverSyncResult(int Observed, int Existing, int Created, int SkippedUnknownVehicle, int SkippedWithoutCard);

/// <summary>
/// Reconciles the live/open TachoMaster duty feed with Driver Master on every scheduled Tacho poll.
/// A physical tachograph card is the primary identity. A previously unseen card is only allowed to
/// create a driver when TachoMaster shows it in a vehicle that exists in the SLH Vehicle Master.
/// The MasterDataAudit generated here is captured by the audit outbox, which mirrors the new driver
/// to the governed Microsoft Lists CRM without making the five-minute Tacho job depend on Graph.
/// </summary>
public sealed class TachoObservedDriverSyncService(
    TmsDbContext db,
    TachoMasterClient tachoMaster,
    ILogger<TachoObservedDriverSyncService> logger)
{
    public async Task<TachoObservedDriverSyncResult> SyncAsync(string actor, CancellationToken ct)
    {
        if (!tachoMaster.IsConfigured)
            return new(0, 0, 0, 0, 0);

        var today = UkOperatingDate(DateTimeOffset.UtcNow);
        var statusesByVehicle = await tachoMaster.GetOpenDriverStatusesByVehicleAsync(today, ct);
        var observed = statusesByVehicle.Values.SelectMany(value => value).ToList();
        if (observed.Count == 0)
            return new(0, 0, 0, 0, 0);

        var vehicles = await db.Vehicles.AsNoTracking().Where(vehicle => vehicle.Active).ToListAsync(ct);
        var knownVehicleKeys = vehicles
            .SelectMany(vehicle => new[] { vehicle.Registration, vehicle.FleetNumber, vehicle.Abbreviation })
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(NormaliseIdentifier)
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var drivers = await db.Drivers.OrderBy(driver => driver.DisplayName).ToListAsync(ct);
        await MasterDetailStore.EnrichDriversAsync(db, drivers, ct);

        var existing = 0;
        var created = 0;
        var skippedUnknownVehicle = 0;
        var skippedWithoutCard = 0;
        var now = DateTimeOffset.UtcNow;

        foreach (var status in observed
            .OrderBy(item => item.VehicleCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.DriverName, StringComparer.OrdinalIgnoreCase))
        {
            var vehicleKey = NormaliseIdentifier(status.VehicleCode);
            if (vehicleKey.Length == 0 || !knownVehicleKeys.Contains(vehicleKey))
            {
                skippedUnknownVehicle++;
                continue;
            }

            var cardKey = NormaliseIdentifier(status.CardNumber);
            if (cardKey.Length == 0)
            {
                skippedWithoutCard++;
                continue;
            }

            var driver = drivers.FirstOrDefault(candidate =>
                NormaliseIdentifier(candidate.TachoCardNumber) == cardKey);

            // A member-code match means this is an existing person whose card detail was not yet
            // persisted locally. Enrich that row instead of creating a duplicate driver.
            if (driver is null && status.MemberCode > 0)
            {
                var member = status.MemberCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
                driver = drivers.FirstOrDefault(candidate =>
                    string.Equals(candidate.TachoMasterDriverId?.Trim(), member, StringComparison.OrdinalIgnoreCase));
            }

            if (driver is not null)
            {
                driver.TachoCardNumber = status.CardNumber;
                driver.TachoMasterDriverId = status.MemberCode > 0
                    ? status.MemberCode.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : driver.TachoMasterDriverId;
                driver.TachoName = string.IsNullOrWhiteSpace(status.DriverName) ? driver.TachoName : status.DriverName.Trim();
                driver.TachoDriveAvailableTodayMinutes = status.DriveAvailableTodayMinutes ?? driver.TachoDriveAvailableTodayMinutes;
                driver.TachoDriveAvailableWeekMinutes = status.DriveAvailableWeekMinutes ?? driver.TachoDriveAvailableWeekMinutes;
                driver.TachoWorkAvailableWeekMinutes = status.WorkAvailableWeekMinutes ?? driver.TachoWorkAvailableWeekMinutes;
                driver.LastTachoSyncUtc = now;
                await MasterDetailStore.SaveAsync(db, "driver", driver.EmployeeNumber, JsonSerializer.Serialize(driver), "TachoMaster live vehicle identity", actor, ct);
                existing++;
                continue;
            }

            var employeeNumber = UniqueReference(cardKey, drivers);
            var displayName = string.IsNullOrWhiteSpace(status.DriverName)
                ? $"Tacho driver {cardKey[^Math.Min(6, cardKey.Length)..]}"
                : status.DriverName.Trim();

            driver = new Driver
            {
                EmployeeNumber = employeeNumber,
                DisplayName = Clip(displayName, 160),
                TachoName = Clip(displayName, 160),
                TachoMasterDriverId = status.MemberCode > 0
                    ? status.MemberCode.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : null,
                TachoCardNumber = status.CardNumber,
                TachoDriveAvailableTodayMinutes = status.DriveAvailableTodayMinutes,
                TachoDriveAvailableWeekMinutes = status.DriveAvailableWeekMinutes,
                TachoWorkAvailableWeekMinutes = status.WorkAvailableWeekMinutes,
                DriverType = "Driver",
                LastTachoSyncUtc = now,
                Active = true
            };

            db.Drivers.Add(driver);
            drivers.Add(driver);
            await MasterDetailStore.SaveAsync(db, "driver", driver.EmployeeNumber, JsonSerializer.Serialize(driver), "Created from live TachoMaster vehicle identity", actor, ct);
            db.MasterDataAudits.Add(new MasterDataAudit
            {
                EntityType = "Driver",
                EntityId = driver.Id,
                Action = "CreatedFromLiveTachoCard",
                ChangedBy = actor,
                ChangesJson = JsonSerializer.Serialize(new
                {
                    source = "TachoMaster open duty",
                    status.VehicleCode,
                    status.DriverName,
                    status.MemberCode,
                    status.CardNumber,
                    employeeNumber
                })
            });
            created++;

            logger.LogWarning(
                "Created Driver Master record {Driver} ({EmployeeNumber}) because previously unseen Tacho card {Card} was observed in SLH vehicle {Vehicle}.",
                driver.DisplayName, driver.EmployeeNumber, status.CardNumber, status.VehicleCode);
        }

        if (existing > 0 || created > 0)
            await db.SaveChangesAsync(ct);

        return new(observed.Count, existing, created, skippedUnknownVehicle, skippedWithoutCard);
    }

    private static string UniqueReference(string cardKey, IReadOnlyCollection<Driver> drivers)
    {
        var suffix = cardKey.Length <= 12 ? cardKey : cardKey[^12..];
        var candidate = Clip($"TACHO-{suffix}", 40);
        var used = drivers.Select(driver => driver.EmployeeNumber).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(candidate)) return candidate;

        for (var index = 2; index < 1000; index++)
        {
            candidate = Clip($"TACHO-{suffix}-{index}", 40);
            if (!used.Contains(candidate)) return candidate;
        }

        return Clip($"TACHO-{Guid.NewGuid():N}", 40);
    }

    private static string NormaliseIdentifier(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : new string(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static string Clip(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];

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
