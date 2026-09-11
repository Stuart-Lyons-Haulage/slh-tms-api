using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public sealed record MasterComplianceResult(bool Allowed, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);

public sealed class MasterAssignmentComplianceService(TmsDbContext db)
{
    private static readonly TimeSpan WarningWindow = TimeSpan.FromDays(30);

    public async Task<MasterComplianceResult> CheckAsync(Guid? driverId, Guid? vehicleId, CancellationToken ct)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var today = DateTime.UtcNow.Date;

        if (driverId is Guid selectedDriverId)
        {
            var driver = await db.Drivers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == selectedDriverId, ct);
            if (driver is not null)
            {
                var master = await db.MasterDrivers.AsNoTracking().FirstOrDefaultAsync(x =>
                    x.DriverId == driver.EmployeeNumber ||
                    x.TachoMasterDriverId == driver.TachoMasterDriverId ||
                    x.FullName == driver.DisplayName, ct);
                if (master is not null)
                {
                    CheckDate(master.LicenceExpiry, "driver licence", today, errors, warnings);
                    CheckDate(master.CPCExpiry, "driver CPC", today, errors, warnings);
                    CheckDate(master.DigitalTachoCardExpiry, "driver digital tacho card", today, errors, warnings);
                    CheckDate(master.MedicalExpiry, "driver medical", today, errors, warnings);
                }
            }
        }

        if (vehicleId is Guid selectedVehicleId)
        {
            var vehicle = await db.Vehicles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == selectedVehicleId, ct);
            if (vehicle is not null)
            {
                var master = await db.MasterVehicles.AsNoTracking().FirstOrDefaultAsync(x =>
                    x.Registration == vehicle.Registration ||
                    x.VehicleId == selectedVehicleId.ToString(), ct);
                if (master is not null)
                {
                    CheckDate(master.MOTExpiry, "vehicle MOT", today, errors, warnings);
                    CheckDate(master.TachoCalibrationExpiry, "vehicle tacho calibration", today, errors, warnings);
                }
            }
        }

        return new MasterComplianceResult(errors.Count == 0, errors, warnings);
    }

    private static void CheckDate(DateTime? expiry, string label, DateTime today, List<string> errors, List<string> warnings)
    {
        if (expiry is null) return;
        var date = expiry.Value.Date;
        if (date < today)
        {
            errors.Add($"{label} expired on {date:dd/MM/yyyy}.");
            return;
        }
        if (date <= today.Add(WarningWindow))
            warnings.Add($"{label} expires on {date:dd/MM/yyyy}.");
    }
}
