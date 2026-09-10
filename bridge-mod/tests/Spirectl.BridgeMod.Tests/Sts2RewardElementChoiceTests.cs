using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

public sealed class Sts2RewardElementChoiceTests
{
    private static readonly (ulong InstanceId, string ChoiceId)[] Choices =
    [
        (101, "reward:p:1:visible:0"),
        (202, "reward:p:1:visible:1"),
    ];

    [Theory]
    [InlineData("101", "reward:p:1:visible:0")]
    [InlineData("202", "reward:p:1:visible:1")]
    [InlineData("303", null)]
    [InlineData("not-an-instance", null)]
    [InlineData(null, null)]
    public void ResolvesOnlyTheCurrentExactSceneInstance(string? elementId, string? expected)
        => Assert.Equal(expected, Sts2RewardElementChoice.Resolve(elementId, Choices));
}
