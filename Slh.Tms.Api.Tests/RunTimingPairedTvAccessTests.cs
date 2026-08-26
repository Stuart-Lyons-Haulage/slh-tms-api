using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class RunTimingPairedTvAccessTests
{
    [Fact]
    public void Run_timing_controller_declares_the_paired_tv_header_contract()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Controllers", "RunTimingController.cs"));
        Assert.Contains("X-TV-Display-Key", source);
        Assert.Contains("TvDisplayKeyStore.ValidateAsync", source);
    }
}
