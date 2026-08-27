using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class TachoDriverMasterIdentityTests
{
    [Fact]
    public void Member_code_is_the_primary_stable_identity()
    {
        Assert.True(TachoDriverIdentityRules.MemberMatches(" 1955725 ", "1955725"));
        Assert.False(TachoDriverIdentityRules.MemberMatches("1631289", "1955725"));
    }

    [Fact]
    public void Card_identity_tolerates_provider_prefix_or_suffix_formatting()
    {
        Assert.True(TachoDriverIdentityRules.CardsMatch("V100000149273000", "V100000149273000"));
        Assert.True(TachoDriverIdentityRules.CardsMatch("GB-V100000149273000", "V100000149273000"));
        Assert.False(TachoDriverIdentityRules.CardsMatch("V100000149273000", "V100000325782000"));
    }

    [Fact]
    public void Same_name_does_not_make_different_member_codes_the_same_driver()
    {
        Assert.Equal(
            TachoDriverIdentityRules.NormalisePerson("Gerika, Donatas"),
            TachoDriverIdentityRules.NormalisePerson("Donatas Gerika"));
        Assert.False(TachoDriverIdentityRules.MemberMatches("1385435", "2056313"));
    }

    [Fact]
    public void Missing_card_does_not_override_a_valid_member_identity()
    {
        Assert.True(TachoDriverIdentityRules.MemberMatches("1955729", "1955729"));
        Assert.False(TachoDriverIdentityRules.CardsMatch(null, null));
    }
}
