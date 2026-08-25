using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class NaturesWayPlanningMatchTests
{
    [Theory]
    [InlineData("Drayton")]
    [InlineData("Runcton")]
    [InlineData("Merston")]
    [InlineData("Selsey")]
    public void Nwf_planner_locality_resolves_to_one_approved_physical_fence(string locality)
    {
        var plannerLabel = $"NWF-{locality}";
        var stop = new LoadStop { Id = Guid.NewGuid(), Sequence = 1, Name = plannerLabel };

        var matchingFences = EmbeddedGeofenceEngine.ApprovedFences
            .Where(fence => GeofencePlanningMatch.SamePhysicalSite(stop, fence))
            .ToList();

        var fence = Assert.Single(matchingFences);
        Assert.Equal(fence.Name, GeofencePlanningMatch.MatchText(plannerLabel));
    }
}
