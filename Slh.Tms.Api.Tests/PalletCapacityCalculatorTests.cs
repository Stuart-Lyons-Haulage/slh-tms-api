using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class PalletCapacityCalculatorTests
{
    [Fact]
    public void StandardFullLoad_IsOneHundredPercent()
    {
        var result = PalletCapacityCalculator.Calculate(26, 0);
        Assert.Equal(100m, result.UtilisationPercent);
        Assert.Equal("Green", result.Status);
    }

    [Fact]
    public void EuroFullLoad_IsOneHundredPercent()
    {
        var result = PalletCapacityCalculator.Calculate(0, 33);
        Assert.Equal(100m, result.UtilisationPercent);
        Assert.Equal("Green", result.Status);
    }

    [Fact]
    public void MixedLoad_UsesProportionalFootprint()
    {
        var result = PalletCapacityCalculator.Calculate(10, 20);
        Assert.Equal(99.1m, result.UtilisationPercent);
        Assert.Equal("Green", result.Status);
    }

    [Fact]
    public void MixedOverCapacity_IsRed()
    {
        var result = PalletCapacityCalculator.Calculate(13, 17);
        Assert.Equal(101.5m, result.UtilisationPercent);
        Assert.Equal("Red", result.Status);
    }

    [Fact]
    public void UnknownPalletType_IsAmber()
    {
        var result = PalletCapacityCalculator.Calculate(10, 10, 2);
        Assert.Equal("Amber", result.Status);
    }
}
