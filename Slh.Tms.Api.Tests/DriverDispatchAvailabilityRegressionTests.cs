using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class DriverDispatchAvailabilityRegressionTests
{
    [Fact]
    public void Dispatch_status_marks_proven_zero_tacho_capacity_unavailable()
    {
        var source = Read("Controllers", "DriverDispatchStatusController.cs");
        Assert.Contains("driveAvailablePlanningDayMinutes is <= 0", source);
        Assert.Contains("workAvailableWeekMinutes is <= 0", source);
        Assert.Contains("return new(\"Unavailable\", weekly.Message);", source);
        Assert.Contains("DriveAvailablePlanningDayMinutes", source);
        Assert.Contains("AvailabilityStatus", source);
    }

    [Fact]
    public void Assistant_keeps_live_linked_vehicle_ahead_of_learned_and_yesterday_pairings()
    {
        var source = Read("Services", "DriverDispatchAssistantService.cs");
        Assert.Contains("liveLinkedVehicle ?? preferred?.Vehicle ?? previousVehicle", source);
        Assert.Contains("LiveMatchesDriver(driver, pair.Live)", source);
        Assert.Contains("currently linked to {context.SuggestedVehicle.Registration} · keep vehicle", source);
        Assert.Contains("in yesterday · keep {context.SuggestedVehicle.Registration}", source);
    }

    [Fact]
    public void Operational_status_is_separate_from_driver_confirmation()
    {
        var source = Read("Controllers", "DriverDispatchStatusController.cs");
        Assert.Contains("var latestDispatch = loadLogs.FirstOrDefault(item => item.Status == \"Driver dispatched\")", source);
        Assert.Contains("var operationalStatus = load is null", source);
        Assert.Contains("? \"Completed\"", source);
        Assert.Contains("? \"Working\"", source);
        Assert.Contains("? \"Dispatched\"", source);
        Assert.Contains("bool DriverConfirmed", source);
        Assert.Contains("DateTimeOffset? DriverConfirmationAtUtc", source);
    }

    [Fact]
    public void Working_requires_live_movement_and_card_or_open_tacho_duty()
    {
        var source = Read("Controllers", "DriverDispatchStatusController.cs");
        Assert.Contains("live.IsMoving != true && live.SpeedKph.GetValueOrDefault() <= 3", source);
        Assert.Contains("CardsMatch(driver.TachoCardNumber, live.CurrentDriverCardNumber)", source);
        Assert.Contains("duty.DutyEndUtc is null", source);
        Assert.Contains("ExecutionIdentityResolver.MatchesVehicleIdentifier(aliases, duty.VehicleCode)", source);
    }

    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { FindRepositoryRoot() }.Concat(parts).ToArray()));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Controllers"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Repository root was not found.");
    }
}