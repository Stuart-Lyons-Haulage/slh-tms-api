using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class PreEmailCleanSlateMaintenanceTests
{
    [Fact]
    public void Card_matching_accepts_full_and_short_tachograph_card_variants()
    {
        Assert.True(PreEmailCleanSlateMaintenance.CardsMatch("UK-DRIVER-1234567890", "1234567890"));
        Assert.True(PreEmailCleanSlateMaintenance.CardsMatch("1234567890", "UK DRIVER 1234567890"));
        Assert.False(PreEmailCleanSlateMaintenance.CardsMatch("1234567890", "9876543210"));
    }

    [Fact]
    public void Profile_match_prefers_stable_tachomaster_member_code()
    {
        var driver = new Driver
        {
            EmployeeNumber = "SLH-100",
            DisplayName = "Current Name",
            TachoName = "Old Name",
            TachoMasterDriverId = "42",
            TachoCardNumber = "OLD-CARD-00000000"
        };
        var profiles = new[]
        {
            Profile(42, "Different Provider Name", "NEW-CARD-12345678", "OTHER", new DateTimeOffset(2026, 8, 1, 8, 0, 0, TimeSpan.Zero)),
            Profile(99, "Current Name", "OLD-CARD-00000000", "SLH-100", new DateTimeOffset(2025, 12, 1, 8, 0, 0, TimeSpan.Zero))
        };

        var matched = PreEmailCleanSlateMaintenance.MatchProfile(driver, profiles);

        Assert.NotNull(matched);
        Assert.Equal(42, matched!.MemberCode);
    }

    [Fact]
    public void Profile_match_uses_unique_card_suffix_when_member_code_is_missing()
    {
        var driver = new Driver
        {
            EmployeeNumber = "SLH-200",
            DisplayName = "Driver Two",
            TachoCardNumber = "GB-ABC-1234567890"
        };
        var profiles = new[]
        {
            Profile(7, "Provider Driver", "1234567890", "OTHER", new DateTimeOffset(2026, 2, 1, 8, 0, 0, TimeSpan.Zero))
        };

        var matched = PreEmailCleanSlateMaintenance.MatchProfile(driver, profiles);

        Assert.NotNull(matched);
        Assert.Equal(7, matched!.MemberCode);
    }

    [Theory]
    [InlineData("2025-12-31T23:59:59+00:00", true)]
    [InlineData("2026-01-01T00:00:00+00:00", false)]
    [InlineData("2026-08-21T12:00:00+00:00", false)]
    public void Driver_activity_cutoff_archives_only_evidence_before_2026(string timestamp, bool shouldArchive)
    {
        var evidence = DateTimeOffset.Parse(timestamp, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(shouldArchive, evidence < PreEmailCleanSlateMaintenance.DriverActivityCutoffUtc);
    }

    private static TachoDriverProfile Profile(int memberCode, string name, string? card, string? employee, DateTimeOffset? validAt)
        => new(
            memberCode,
            name,
            card,
            employee,
            validAt,
            1,
            480,
            540,
            1800,
            4000,
            0,
            0,
            2000);
}
