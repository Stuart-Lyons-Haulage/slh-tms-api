using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class ExecutionIdentityResolverTests
{
    [Fact]
    public void Planned_driver_must_match_tacho_duty_for_customer_evidence()
    {
        var now = DateTimeOffset.UtcNow;
        var driver = new Driver { EmployeeNumber = "123", DisplayName = "Ben Joshua Madge", TachoName = "MADGE BEN JOSHUA" };
        var tacho = Tacho("KY71CVP", "Ben Joshua Madge", "123", now.AddHours(-8));

        Assert.True(ExecutionIdentityResolver.DriverMatches(driver, tacho));
        Assert.Equal("Matched", ExecutionIdentityResolver.DriverEvidenceStatus(driver, tacho));

        var other = new Driver { EmployeeNumber = "999", DisplayName = "Another Driver" };
        Assert.False(ExecutionIdentityResolver.DriverMatches(other, tacho));
        Assert.Equal("Mismatch", ExecutionIdentityResolver.DriverEvidenceStatus(other, tacho));
    }

    [Fact]
    public void First_movement_is_taken_after_tacho_sign_on_not_from_an_earlier_vehicle_event()
    {
        var signOn = DateTimeOffset.UtcNow.AddHours(-5);
        var events = new[]
        {
            Tracking("old", signOn.AddHours(-2), 35),
            Tracking("parked", signOn.AddMinutes(2), 0),
            Tracking("first-duty-move", signOn.AddMinutes(14), 22),
            Tracking("later", signOn.AddMinutes(30), 40)
        };

        var first = ExecutionIdentityResolver.FirstMovement(new[] { "KY71CVP" }, events, signOn);
        Assert.Equal(signOn.AddMinutes(14), first);
    }

    [Fact]
    public async Task Explicit_dot_and_tacho_vehicle_mappings_join_the_same_tms_vehicle_identity()
    {
        var vehicle = new Vehicle { Id = Guid.NewGuid(), Registration = "KY71CVP", Active = true };
        var options = new DbContextOptionsBuilder<TmsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new TmsDbContext(options);
        db.Vehicles.Add(vehicle);
        db.IntegrationMappings.AddRange(
            new IntegrationMapping { Provider = "DotTracking", ExternalKey = "FALCON-001", TmsEntityType = "Vehicle", TmsEntityId = vehicle.Id },
            new IntegrationMapping { Provider = "TachoMaster", ExternalKey = "TM-71", TmsEntityType = "Vehicle", TmsEntityId = vehicle.Id });
        await db.SaveChangesAsync();

        var aliases = await ExecutionIdentityResolver.VehicleAliasesAsync(db, new[] { vehicle }, CancellationToken.None);
        Assert.Contains("KY71CVP", aliases[vehicle.Id]);
        Assert.Contains("FALCON001", aliases[vehicle.Id]);
        Assert.Contains("TM71", aliases[vehicle.Id]);
    }

    private static VehicleTrackingEvent Tracking(string id, DateTimeOffset at, decimal speed) => new()
    {
        ProviderName = "RoadTech Falcon",
        ProviderEventId = id,
        VehicleIdentifier = "KY71CVP",
        EventTimeUtc = at,
        Latitude = 50.8m,
        Longitude = -1.1m,
        SpeedKph = speed,
        IsMoving = speed > 2,
        RawPayload = "{}",
        MatchStatus = "Received"
    };

    private static TachoVehicleDriverStatus Tacho(string vehicle, string driver, string employee, DateTimeOffset dutyStart) =>
        new(vehicle, 123, driver, null, employee, dutyStart, null,
            0, 0, 0, 0, 0, null, null, null, 180, null, null, null, null, null, null);
}
