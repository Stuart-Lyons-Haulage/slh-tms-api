using Slh.Tms.Api.Controllers;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Tests;

public sealed class PlanningResilienceTests
{
    [Fact]
    public void Active_planning_register_row_wins_over_cancelled_live_tombstone()
    {
        var id = Guid.NewGuid();
        var registered = new Load { Id = id, Status = LoadStatus.Planned };
        var live = new Load { Id = id, Status = LoadStatus.Cancelled };

        Assert.True(PlanningResilience.KeepRegisteredOverLiveTombstone(registered, live));
    }

    [Fact]
    public void Live_runtime_row_still_wins_when_it_is_not_cancelled()
    {
        var id = Guid.NewGuid();
        var registered = new Load { Id = id, Status = LoadStatus.Planned };
        var live = new Load { Id = id, Status = LoadStatus.InProgress };

        Assert.False(PlanningResilience.KeepRegisteredOverLiveTombstone(registered, live));
    }

    [Fact]
    public void Cancelled_register_does_not_revive_from_cancelled_live_row()
    {
        var id = Guid.NewGuid();
        var registered = new Load { Id = id, Status = LoadStatus.Cancelled };
        var live = new Load { Id = id, Status = LoadStatus.Cancelled };

        Assert.False(PlanningResilience.KeepRegisteredOverLiveTombstone(registered, live));
    }
}