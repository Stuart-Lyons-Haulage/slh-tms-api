using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class DotTrackingBaseUrlTests
{
    [Theory]
    [InlineData("api-v1.roadtech.co.uk", "https://api-v1.roadtech.co.uk/api/")]
    [InlineData("https://api-v1.roadtech.co.uk", "https://api-v1.roadtech.co.uk/api/")]
    [InlineData("https://api-v1.roadtech.co.uk/api", "https://api-v1.roadtech.co.uk/api/")]
    [InlineData("https://api-v1.roadtech.co.uk/api/", "https://api-v1.roadtech.co.uk/api/")]
    public void NormaliseBaseUrl_accepts_supported_runtime_formats(string input, string expected)
    {
        Assert.Equal(expected, DotTrackingClient.NormaliseBaseUrl(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://api-v1.roadtech.co.uk")]
    public void NormaliseBaseUrl_rejects_invalid_runtime_formats(string input)
    {
        Assert.Throws<ArgumentException>(() => DotTrackingClient.NormaliseBaseUrl(input));
    }
}
