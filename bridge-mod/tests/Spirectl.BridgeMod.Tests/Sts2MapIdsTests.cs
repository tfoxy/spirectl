using Spirectl.Sts2;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

public sealed class Sts2MapIdsTests
{
    [Theory]
    [InlineData("map-node:3:1", 3, 1)]
    [InlineData("map-point:3:1", 3, 1)]
    [InlineData("map-node:-1:0", -1, 0)]
    [InlineData("map-point:-1:0", -1, 0)]
    public void ParsesNodeAndStatePointIds(string id, int expectedRow, int expectedCol)
    {
        Assert.True(Sts2MapIds.TryParseNodeId(id, out var row, out var col));
        Assert.Equal(expectedRow, row);
        Assert.Equal(expectedCol, col);
        Assert.Equal($"map-node:{expectedRow}:{expectedCol}", Sts2MapIds.NodeId(row, col));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown:3:1")]
    [InlineData("map-point:3")]
    [InlineData("map-node:3:1:0")]
    [InlineData("map-point:three:1")]
    [InlineData("map-node:3:one")]
    [InlineData("map-point:2147483648:1")]
    public void RejectsMalformedIds(string? id)
    {
        Assert.False(Sts2MapIds.TryParseNodeId(id, out var row, out var col));
        Assert.Equal(0, row);
        Assert.Equal(0, col);
    }
}
