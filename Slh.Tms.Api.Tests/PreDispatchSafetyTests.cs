using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class PreDispatchSafetyTests
{
    private static readonly DateTimeOffset EvidenceAt = new(2026, 8, 28, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Unverified_evidence_requires_explicit_acknowledgement_before_dispatch()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new TmsDbContext(options);
        var driver = Driver("D1");
        var vehicle = Vehicle("SLH1");
        var load = LoadFor(new DateOnly(2026, 8, 28), driver.Id, vehicle.Id, trailerId: null);
        db.Drivers.Add(driver);
        db.Vehicles.Add(vehicle);
        db.Loads.Add(load);
        await db.SaveChangesAsync();
        var service = new PreDispatchSafetyService(db, new FixedTimeProvider(EvidenceAt));

        var readiness = await service.EvaluateAsync(load.Id, CancellationToken.None);

        Assert.Equal("Unverified", readiness.Classification);
        Assert.True(readiness.RequiresAcknowledgement);
        Assert.Contains(readiness.Checks, item => item.Code == "TrailerAllocated" && !item.Passed);
        Assert.Contains(readiness.Checks, item => item.Code == "TachoEvidenceMissing" && !item.Passed);

        var missingAck = await Assert.ThrowsAsync<PreDispatchException>(() =>
            service.DispatchAsync(load.Id, new ControlledDispatchRequest(false), "planner", CancellationToken.None));
        Assert.Equal("UnverifiedAcknowledgementRequired", missingAck.Code);
        Assert.Equal(LoadStatus.Planned, (await db.Loads.SingleAsync()).Status);

        var dispatched = await service.DispatchAsync(load.Id, new ControlledDispatchRequest(true), "planner", CancellationToken.None);
        Assert.Equal("Dispatched", dispatched.Status);
        Assert.Equal(LoadStatus.Dispatched, (await db.Loads.SingleAsync()).Status);
    }

    [Fact]
    public async Task Known_insufficient_drive_time_blocks_dispatch_without_mutation()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new TmsDbContext(options);
        var driver = Driver("D2");
        var vehicle = Vehicle("SLH2");
        var trailer = new Trailer { TrailerNumber = "SLH20", Active = true };
        var load = LoadFor(new DateOnly(2026, 8, 28), driver.Id, vehicle.Id, trailer.Id,
            first: (51.5074m, -0.1278m), last: (53.4808m, -2.2426m));
        db.Drivers.Add(driver);
        db.Vehicles.Add(vehicle);
        db.Trailers.Add(trailer);
        db.Loads.Add(load);
        AddDriverDetail(db, driver, driveAvailableTodayMinutes: 30, lastTachoSyncUtc: EvidenceAt.AddMinutes(-10));
        await db.SaveChangesAsync();
        var service = new PreDispatchSafetyService(db, new FixedTimeProvider(EvidenceAt));

        var readiness = await service.EvaluateAsync(load.Id, CancellationToken.None);

        Assert.Equal("Blocked", readiness.Classification);
        Assert.NotNull(readiness.EstimatedDriveMinutes);
        Assert.True(readiness.EstimatedDriveMinutes > 30);
        Assert.Contains(readiness.Checks, item => item.Code == "DriveTimeAvailable" && !item.Passed && item.Severity == "Critical");

        var blocked = await Assert.ThrowsAsync<PreDispatchException>(() =>
            service.DispatchAsync(load.Id, new ControlledDispatchRequest(true), "planner", CancellationToken.None));
        Assert.Equal("DispatchBlocked", blocked.Code);
        Assert.Equal(LoadStatus.Planned, (await db.Loads.SingleAsync()).Status);
    }

    [Fact]
    public async Task Overlapping_same_driver_assignment_blocks_dispatch()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new TmsDbContext(options);
        var date = new DateOnly(2026, 8, 28);
        var driver = Driver("D3");
        var vehicle = Vehicle("SLH3");
        var otherVehicle = Vehicle("SLH4");
        var trailer = new Trailer { TrailerNumber = "SLH21", Active = true };
        var otherTrailer = new Trailer { TrailerNumber = "SLH22", Active = true };
        var target = LoadFor(date, driver.Id, vehicle.Id, trailer.Id, startUtc: EvidenceAt.AddHours(2), endUtc: EvidenceAt.AddHours(6));
        var existing = LoadFor(date, driver.Id, otherVehicle.Id, otherTrailer.Id, startUtc: EvidenceAt.AddHours(4), endUtc: EvidenceAt.AddHours(8));
        existing.Reference = "EXISTING-02";
        db.Drivers.Add(driver);
        db.Vehicles.AddRange(vehicle, otherVehicle);
        db.Trailers.AddRange(trailer, otherTrailer);
        db.Loads.AddRange(target, existing);
        AddDriverDetail(db, driver, driveAvailableTodayMinutes: 500, lastTachoSyncUtc: EvidenceAt.AddMinutes(-5));
        await db.SaveChangesAsync();
        var service = new PreDispatchSafetyService(db, new FixedTimeProvider(EvidenceAt));

        var readiness = await service.EvaluateAsync(target.Id, CancellationToken.None);

        Assert.Equal("Blocked", readiness.Classification);
        Assert.Contains(readiness.Checks, item => item.Code == "DriverConflict" && !item.Passed && item.Severity == "Critical");
        await Assert.ThrowsAsync<PreDispatchException>(() =>
            service.DispatchAsync(target.Id, new ControlledDispatchRequest(true), "planner", CancellationToken.None));
        Assert.Equal(LoadStatus.Planned, (await db.Loads.SingleAsync(item => item.Id == target.Id)).Status);
    }

    private static Driver Driver(string employee) => new()
    {
        EmployeeNumber = employee,
        DisplayName = $"Driver {employee}",
        Active = true
    };

    private static Vehicle Vehicle(string registration) => new()
    {
        Registration = registration,
        Active = true
    };

    private static Load LoadFor(
        DateOnly date,
        Guid driverId,
        Guid vehicleId,
        Guid? trailerId,
        (decimal Lat, decimal Lon)? first = null,
        (decimal Lat, decimal Lon)? last = null,
        DateTimeOffset? startUtc = null,
        DateTimeOffset? endUtc = null)
    {
        first ??= (52.0m, -1.0m);
        last ??= (52.1m, -1.1m);
        return new Load
        {
            Reference = $"RUN-{Guid.NewGuid():N}"[..20],
            PlanningDate = date,
            Status = LoadStatus.Planned,
            DriverId = driverId,
            VehicleId = vehicleId,
            TrailerId = trailerId,
            Stops =
            [
                new LoadStop { Sequence = 1, Name = "Collection", Latitude = first.Value.Lat, Longitude = first.Value.Lon, PlannedArrivalUtc = startUtc },
                new LoadStop { Sequence = 2, Name = "Delivery", Latitude = last.Value.Lat, Longitude = last.Value.Lon, PlannedArrivalUtc = endUtc }
            ]
        };
    }

    private static void AddDriverDetail(TmsDbContext db, Driver driver, int driveAvailableTodayMinutes, DateTimeOffset lastTachoSyncUtc)
    {
        db.StagedImports.Add(new StagedImport
        {
            EntityType = "masterdetail:driver",
            IdempotencyKey = $"masterdetail:driver:{driver.EmployeeNumber.ToLowerInvariant()}",
            Status = StagingStatus.Promoted,
            PayloadJson = JsonSerializer.Serialize(new
            {
                driver.EmployeeNumber,
                tachoDriveAvailableTodayMinutes = driveAvailableTodayMinutes,
                lastTachoSyncUtc
            })
        });
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
