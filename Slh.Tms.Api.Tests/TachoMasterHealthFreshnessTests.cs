using Slh.Tms.Api.Controllers;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class TachoMasterHealthFreshnessTests
{
    [Theory]
    [InlineData(null, "unknown")]
    [InlineData(0, "live")]
    [InlineData(15, "live")]
    [InlineData(15.1, "delayed")]
    [InlineData(30, "delayed")]
    [InlineData(30.1, "stale")]
    [InlineData(1320, "stale")]
    public void JobFreshness_ClassifiesFiveMinuteSchedulerLag(double? ageMinutes, string expected)
    {
        Assert.Equal(expected, TachoMasterHealthController.JobFreshness(ageMinutes));
    }
}
