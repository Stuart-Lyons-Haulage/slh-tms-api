using Slh.Tms.Api.Controllers;
using Slh.Tms.Api.Models;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class DailyComplianceControllerTests
{
    [Fact]
    public void Vehicle_aliases_include_registration_suffixes_and_fleet_number()
    {
        var vehicle = new Vehicle
        {
            Registration = "AX19 NFH",
            FleetNumber = "32",
            Abbreviation = "AX19"
        };

        var aliases = DailyComplianceController.VehicleAliases(vehicle);

        Assert.Contains("AX19NFH", aliases);
        Assert.Contains("19NFH", aliases);
        Assert.Contains("32", aliases);
        Assert.Contains("AX19", aliases);
    }
}
