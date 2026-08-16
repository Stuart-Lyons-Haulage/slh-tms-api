using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Tests;

public sealed class MasterDetailStoreTests
{
    [Fact]
    public async Task DriverWorkbookFieldsAreRetainedAndEnriched()
    {
        await using var db = CreateDb();
        var driver = new Driver { EmployeeNumber = "D-17", DisplayName = "Test Driver" };
        db.Drivers.Add(driver);
        await db.SaveChangesAsync();
        await MasterDetailStore.SaveAsync(db, "driver", "D-17", """{"employeeNumber":"D-17","coding":"FT","agencyName":"SLH","northEligible":true,"preloadEligible":false,"drivingLicenceNumber":"LIC17","licenceExpiry":"2027-03-04","licenceStatus":"Valid","tachoMasterDriverId":"TM17","notes":"Master note"}""", "test", "tester", CancellationToken.None);

        db.ChangeTracker.Clear();
        var rows = await db.Drivers.AsNoTracking().ToListAsync();
        await MasterDetailStore.EnrichDriversAsync(db, rows, CancellationToken.None);

        var enriched = Assert.Single(rows);
        Assert.Equal("FT", enriched.Coding);
        Assert.Equal("SLH", enriched.AgencyName);
        Assert.True(enriched.NorthEligible is true);
        Assert.True(enriched.PreloadEligible is false);
        Assert.Equal("LIC17", enriched.DrivingLicenceNumber);
        Assert.Equal(new DateOnly(2027, 3, 4), enriched.LicenceExpiry);
        Assert.Equal("TM17", enriched.TachoMasterDriverId);
    }

    [Fact]
    public async Task FleetioPlaceholderVehiclesAreQuarantined()
    {
        await using var db = CreateDb();
        db.Vehicles.AddRange(
            new Vehicle { Registration = "C123456", Active = true },
            new Vehicle { Registration = "CU23ABC", Active = true });
        await db.SaveChangesAsync();

        var changed = await MasterDetailStore.QuarantineFleetioPlaceholdersAsync(db, CancellationToken.None);

        Assert.Equal(1, changed);
        Assert.False((await db.Vehicles.SingleAsync(vehicle => vehicle.Registration == "C123456")).Active);
        Assert.True((await db.Vehicles.SingleAsync(vehicle => vehicle.Registration == "CU23ABC")).Active);
    }

    private static TmsDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>().UseInMemoryDatabase($"master-detail-{Guid.NewGuid()}").Options;
        return new TmsDbContext(options);
    }
}
