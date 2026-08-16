using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

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
        await MasterDetailStore.SaveAsync(db, "driver", "D-17", """{"employeeNumber":"D-17","coding":"FT","agencyName":"SLH","northEligible":true,"preloadEligible":false,"drivingLicenceNumber":"LIC17","licenceExpiry":"2027-03-04","licenceStatus":"Valid","tachoMasterDriverId":"TM17","tachoCardNumber":"CARD17","tachoDriveAvailableTodayMinutes":238,"tachoDriveAvailableWeekMinutes":1260,"tachoWorkAvailableWeekMinutes":1800,"lastTachoSyncUtc":"2026-08-16T20:00:00Z","notes":"Master note"}""", "test", "tester", CancellationToken.None);

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
        Assert.Equal("CARD17", enriched.TachoCardNumber);
        Assert.Equal(238, enriched.TachoDriveAvailableTodayMinutes);
        Assert.Equal(1260, enriched.TachoDriveAvailableWeekMinutes);
        Assert.Equal(1800, enriched.TachoWorkAvailableWeekMinutes);
        Assert.Equal(DateTimeOffset.Parse("2026-08-16T20:00:00Z"), enriched.LastTachoSyncUtc);
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

    [Fact]
    public async Task SiteMapPointsAreRetainedAndEnriched()
    {
        await using var db = CreateDb();
        db.Sites.Add(new Site { ExternalCode = "SITE-17", Name = "Test Depot" });
        await db.SaveChangesAsync();
        await MasterDetailStore.SaveAsync(db, "site", "SITE-17", """{"externalCode":"SITE-17","aliases":"Test Yard","latitude":51.507351,"longitude":-0.127758}""", "test", "tester", CancellationToken.None);

        db.ChangeTracker.Clear();
        var rows = await db.Sites.AsNoTracking().ToListAsync();
        await MasterDetailStore.EnrichSitesAsync(db, rows, CancellationToken.None);

        var enriched = Assert.Single(rows);
        Assert.Equal("Test Yard", enriched.Aliases);
        Assert.Equal(51.507351m, enriched.Latitude);
        Assert.Equal(-0.127758m, enriched.Longitude);
    }

    private static TmsDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>().UseInMemoryDatabase($"master-detail-{Guid.NewGuid()}").Options;
        return new TmsDbContext(options);
    }
}
