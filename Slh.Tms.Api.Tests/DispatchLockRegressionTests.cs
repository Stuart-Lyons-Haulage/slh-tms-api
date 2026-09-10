using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class DispatchLockRegressionTests
{
    [Fact]
    public void Dispatch_service_does_not_parallelise_queries_on_same_db_context()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "Services", "DispatchService.cs"));
        Assert.DoesNotContain("Task.WhenAll(profilesTask, dutiesTask, runsTask, vehiclesTask, trailersTask)", source);
    }

    [Fact]
    public void Dispatch_controller_maps_unexpected_lock_failure_to_structured_problem()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "Controllers", "DispatchController.cs"));
        Assert.Contains("catch (Exception exception)", source);
        Assert.Contains("Dispatch lock failed safely", source);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Slh.Tms.Api.csproj"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
