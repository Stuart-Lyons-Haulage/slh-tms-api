using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class GeofenceHistoryReplayServiceTests
{
    [Fact]
    public void Priority_site_names_are_exposed_by_replay_result_shape()
    {
        var result = new GeofenceHistoryReplayResult(
            new DateOnly(2026, 8, 25),
            1250,
            40,
            36,
            30,
            [new("NWF Selsey", 10, 9, 8, DateTimeOffset.Parse("2026-08-25T12:00:00Z"))],
            DateTimeOffset.Parse("2026-08-25T12:01:00Z"));

        Assert.Equal(1250, result.HistoricalTrackingRecords);
        Assert.Equal("NWF Selsey", Assert.Single(result.PrioritySites).Site);
        Assert.Equal(8, result.PrioritySites[0].Departures);
    }
}
