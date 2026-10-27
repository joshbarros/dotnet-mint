using Mint.Shared;
using Xunit;

namespace Mint.UnitTests;

public class PoisonRuleTests
{
    [Theory]
    [InlineData("POISON")]
    [InlineData("COL-POISON-7")]
    [InlineData("xxPOISONyy")]
    [InlineData("A-VERY-LONG-TOKEN-THAT-MERELY-CONTAINS-POISON-INSIDE")]
    public void Substring_MatchesInsideLongerTokens(string reference) =>
        Assert.True(PoisonRule.IsPoison(reference));

    [Theory]
    [InlineData("COL-1")]
    [InlineData("POISO")]
    [InlineData("poison")]
    [InlineData("")]
    public void NonPoison_DoesNotMatch(string reference) =>
        Assert.False(PoisonRule.IsPoison(reference));
}
