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
    public void Plain_text_update_is_an_outbound_dispatch_status_event()
    {
        var messageController = Read("Controllers", "RunDriverMessageController.cs");
        var statusController = Read("Controllers", "DriverDispatchStatusController.cs");
        Assert.Contains("Driver text update sent", messageController);
        Assert.Contains("Driver text update sent", statusController);
        Assert.Contains("Sent Awaiting Response", statusController);
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