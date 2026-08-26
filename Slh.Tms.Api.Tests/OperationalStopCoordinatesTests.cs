using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class OperationalStopCoordinatesTests
{
    [Theory]
    [InlineData("NWF-Selsey")]
    [InlineData("Collect · NWF-Selsey")]
    [InlineData("Collection · NWF-Selsey")]
    public void Missing_site_coordinates_fall_back_to_unique_approved_geofence(string stopName)
    {
        var stop = new LoadStop
        {
            Id = Guid.NewGuid(),
            Sequence = 1,
            Name = stopName
        };

        var coordinate = OperationalStopCoordinates.Resolve(stop);

        Assert.NotNull(coordinate);
        var fence = Assert.Single(EmbeddedGeofenceEngine.ApprovedFences
            .Where(candidate => GeofencePlanningMatch.SamePhysicalSite(stop, candidate)));
        var expected = OperationalRunOrigin.FenceCentre(fence);
        Assert.Equal(expected, coordinate);
    }

    [Fact]
    public void Site_master_coordinates_remain_authoritative()
    {
        var stop = new LoadStop
        {
            Id = Guid.NewGuid(),
            Sequence = 1,
            Name = "NWF-Selsey",
            Longitude = -0.12345m,
            Latitude = 50.98765m
        };

        Assert.Equal((-0.12345m, 50.98765m), OperationalStopCoordinates.Resolve(stop));
    }

    [Fact]
    public void Unknown_unmapped_stop_fails_closed()
    {
        var stop = new LoadStop
        {
            Id = Guid.NewGuid(),
            Sequence = 1,
            Name = "Definitely not an SLH approved geofence 999999"
        };

        Assert.Null(OperationalStopCoordinates.Resolve(stop));
    }
}
