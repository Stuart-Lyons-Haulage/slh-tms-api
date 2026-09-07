using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class DriverWeeklyRestComplianceServiceTests
{
    [Fact]
    public void Six_by_twenty_four_window_is_still_available_before_deadline()
    {
        var driver = TestDriver();
        var duties = new[]
        {
            Duty("2026-08-20T05:00:00Z", "2026-08-20T15:00:00Z"),
            Duty("2026-08-22T15:00:00Z", "2026-08-22T23:00:00Z"),
            Duty("2026-08-23T05:00:00Z", "2026-08-23T15:00:00Z"),
            Duty("2026-08-24T05:00:00Z", "2026-08-24T15:00:00Z"),
            Duty("2026-08-25T05:00:00Z", "2026-08-25T15:00:00Z"),
            Duty("2026-08-26T05:00:00Z", "2026-08-26T15:00:00Z"),
            Duty("2026-08-27T05:00:00Z", "2026-08-27T15:00:00Z")
        };

        var result = DriverWeeklyRestComplianceService.Evaluate(driver, DateTimeOffset.Parse("2026-08-28T14:00:00Z"), duties);

        Assert.NotEqual("Overdue", result.Status);
        Assert.Equal(DateTimeOffset.Parse("2026-08-28T15:00:00Z"), result.WeeklyRestDueUtc);
    }

    [Fact]
    public void Six_by_twenty_four_deadline_blocks_a_seventh_period_after_the_deadline()
    {
        var driver = TestDriver();
        var duties = new[]
        {
            Duty("2026-08-20T05:00:00Z", "2026-08-20T15:00:00Z"),
            Duty("2026-08-22T15:00:00Z", "2026-08-22T23:00:00Z"),
            Duty("2026-08-23T05:00:00Z", "2026-08-23T15:00:00Z"),
            Duty("2026-08-24T05:00:00Z", "2026-08-24T15:00:00Z"),
            Duty("2026-08-25T05:00:00Z", "2026-08-25T15:00:00Z"),
            Duty("2026-08-26T05:00:00Z", "2026-08-26T15:00:00Z"),
            Duty("2026-08-27T05:00:00Z", "2026-08-27T15:00:00Z"),
            Duty("2026-08-28T16:00:00Z", "2026-08-28T18:00:00Z")
        };

        var result = DriverWeeklyRestComplianceService.Evaluate(driver, DateTimeOffset.Parse("2026-08-28T16:00:00Z"), duties);

        Assert.Equal("Overdue", result.Status);
        Assert.Equal(DateTimeOffset.Parse("2026-08-28T15:00:00Z"), result.WeeklyRestDueUtc);
    }

    [Fact]
    public void Twenty_four_hour_gap_is_treated_as_a_reduced_weekly_rest_reset()
    {
        var driver = TestDriver();
        var duties = new[]
        {
            Duty("2026-08-20T05:00:00Z", "2026-08-20T15:00:00Z"),
            Duty("2026-08-21T15:00:00Z", "2026-08-21T23:00:00Z"),
            Duty("2026-08-22T23:00:00Z", "2026-08-23T07:00:00Z")
        };

        var result = DriverWeeklyRestComplianceService.Evaluate(driver, DateTimeOffset.Parse("2026-08-23T07:00:00Z"), duties);

        Assert.Equal(DateTimeOffset.Parse("2026-08-22T23:00:00Z"), result.LastWeeklyRestEndUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-08-28T23:00:00Z"), result.WeeklyRestDueUtc);
    }

    [Fact]
    public void Missing_weekly_rest_in_recent_history_becomes_overdue_after_144_hours()
    {
        var driver = TestDriver();
        var duties = new[]
        {
            Duty("2026-08-20T05:00:00Z", "2026-08-20T15:00:00Z"),
            Duty("2026-08-21T05:00:00Z", "2026-08-21T15:00:00Z"),
            Duty("2026-08-22T05:00:00Z", "2026-08-22T15:00:00Z"),
            Duty("2026-08-23T05:00:00Z", "2026-08-23T15:00:00Z"),
            Duty("2026-08-24T05:00:00Z", "2026-08-24T15:00:00Z"),
            Duty("2026-08-25T05:00:00Z", "2026-08-25T15:00:00Z"),
            Duty("2026-08-26T05:00:00Z", "2026-08-26T15:00:00Z")
        };

        var result = DriverWeeklyRestComplianceService.Evaluate(driver, DateTimeOffset.Parse("2026-08-26T05:00:00Z"), duties);

        Assert.Equal("Overdue", result.Status);
    }

    private static Driver TestDriver() => new()
    {
        Id = Guid.NewGuid(),
        EmployeeNumber = "SLH001",
        DisplayName = "Test Driver",
        TachoMasterDriverId = "101"
    };

    private static TachoDriverDutyStatus Duty(string startUtc, string? endUtc) => new(
        "AB12CDE",
        101,
        "Test Driver",
        "1234567890123456",
        "SLH001",
        DateTimeOffset.Parse(startUtc),
        string.IsNullOrWhiteSpace(endUtc) ? null : DateTimeOffset.Parse(endUtc),
        0,
        0,
        0,
        0,
        0,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null);
}
