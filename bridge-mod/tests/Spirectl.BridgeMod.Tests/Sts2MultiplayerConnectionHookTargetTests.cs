#if ENABLE_STS2_LIVE_HOST
using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

public sealed class Sts2MultiplayerConnectionHookTargetTests
{
    [Fact]
    public void ConnectionTargetsResolveAgainstTheSelectedGameApiLane()
    {
        var targets = Sts2MultiplayerConnectionHooks.ResolveTargets();

        Assert.Equal("MegaCrit.Sts2.Core.Multiplayer.NetClientGameService", targets.Connecting.DeclaringType!.FullName);
        Assert.Equal("MegaCrit.Sts2.Core.Multiplayer.NetClientGameService", targets.Disconnected.DeclaringType!.FullName);
        Assert.Equal("OnDisconnectedFromHost", targets.Disconnected.Name);
        Assert.Equal("MegaCrit.Sts2.Core.Nodes.CommonUi.NErrorPopup", targets.NetworkError.DeclaringType!.FullName);
        Assert.Equal("Create", targets.NetworkError.Name);
        Assert.Equal([ExpectedNetworkErrorType], targets.NetworkError.GetParameters().Select(parameter => parameter.ParameterType.FullName));
    }

    private const string ExpectedNetworkErrorType = "MegaCrit.Sts2.Core.Entities.Multiplayer.NetErrorInfo";
}
#endif
