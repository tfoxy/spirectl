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

    // Which game build this payload was compiled for. The values depend on what
    // Directory.Build.props could see at build time — a lane alone for a
    // reference-SDK build, all three for a build against a real install, nothing
    // for an offline build with no assemblies — so the assertions are the
    // invariants rather than any one environment's values.
    [Fact]
    public void GameBuildIdentityIsCarriedAndNeverHalfClaimed()
    {
        var identity = BridgeBuildInfo.BuildIdentity;
        Assert.Equal(BridgeBuildInfo.Sts2ApiLane, identity.Sts2ApiLane);
        Assert.Equal(BridgeBuildInfo.BuiltAgainstGameVersion, identity.BuiltAgainstGameVersion);
        Assert.Equal(
            BridgeBuildInfo.BuiltAgainstMainAssemblyHash,
            identity.BuiltAgainstMainAssemblyHash);

        // Empty is a real answer ("this payload cannot claim one") and must never
        // be whitespace, which would read as a claim downstream.
        Assert.DoesNotContain(" ", identity.Sts2ApiLane);
        Assert.DoesNotContain(" ", identity.BuiltAgainstGameVersion);
        Assert.DoesNotContain(" ", identity.BuiltAgainstMainAssemblyHash);

        // A game version is only knowable from a real install's release_info.json,
        // which is also where the lane is detected — so a version without a lane
        // would mean the stamp came from somewhere the lane resolution did not.
        if (!string.IsNullOrEmpty(identity.BuiltAgainstGameVersion))
        {
            Assert.False(string.IsNullOrEmpty(identity.Sts2ApiLane));
        }

        // The hash rides as text so "unknown" stays expressible; when present it
        // must still be the integer the game wrote.
        if (!string.IsNullOrEmpty(identity.BuiltAgainstMainAssemblyHash))
        {
            Assert.True(long.TryParse(identity.BuiltAgainstMainAssemblyHash, out _));
            Assert.False(string.IsNullOrEmpty(identity.BuiltAgainstGameVersion));
        }
    }
}
