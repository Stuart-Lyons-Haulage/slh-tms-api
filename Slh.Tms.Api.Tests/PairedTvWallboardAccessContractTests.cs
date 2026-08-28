using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Controllers;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class PairedTvWallboardAccessContractTests
{
    [Theory]
    [InlineData(typeof(OperationsController), nameof(OperationsController.DeliveryEtas))]
    [InlineData(typeof(RunProgressController), nameof(RunProgressController.Get))]
    [InlineData(typeof(DriverPlanningController), nameof(DriverPlanningController.Assignments))]
    [InlineData(typeof(RunGeofenceLinkageController), nameof(RunGeofenceLinkageController.Get))]
    [InlineData(typeof(RunTimingController), nameof(RunTimingController.Get))]
    public void Shared_wallboard_feed_accepts_the_paired_tv_display_key(Type controller, string methodName)
    {
        var method = controller.GetMethod(methodName);

        Assert.NotNull(method);
        Assert.NotNull(method!.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.Contains(method.GetParameters(), parameter =>
            string.Equals(
                parameter.GetCustomAttribute<FromHeaderAttribute>()?.Name,
                "X-TV-Display-Key",
                StringComparison.Ordinal));
    }
}
