using Spirectl.Sts2;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

public sealed class BridgeBuildInfoTests
{
    [Fact]
    public void BridgeVersionUsesInformationalVersionFormat()
    {
        Assert.Equal("0.1.0", BridgeBuildInfo.BridgeSemVer);
        Assert.Equal("spirectl-bridge/0.1.0", BridgeBuildInfo.BridgeVersion);
        Assert.StartsWith("0.1.0", BridgeBuildInfo.AssemblyInformationalVersion);
        Assert.False(string.IsNullOrWhiteSpace(BridgeBuildInfo.AssemblyInformationalVersion));
        Assert.Equal(BridgeBuildInfo.BridgeSemVer, BridgeBuildInfo.BuildIdentity.BridgeSemVer);
        Assert.Equal(BridgeBuildInfo.BridgeVersion, BridgeBuildInfo.BuildIdentity.BridgeVersion);
        Assert.Equal(
            BridgeBuildInfo.AssemblyInformationalVersion,
            BridgeBuildInfo.BuildIdentity.AssemblyInformationalVersion);
        Assert.False(string.IsNullOrWhiteSpace(BridgeBuildInfo.BuildIdentity.BuiltAtUtc));
    }
}
