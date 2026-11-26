#if ENABLE_STS2_LIVE_HOST
extern alias Sts2Live;

using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2.Live;
using Xunit;
using Reward = Sts2Live::MegaCrit.Sts2.Core.Rewards.Reward;

namespace Spirectl.BridgeMod.Tests;

public sealed partial class Sts2HostTests
{
    [RequiresHarmonyRuntimeFact]
    [Trait("Category", RequiresHarmonyRuntimeFactAttribute.Category)]
    public void GameApiRewardsCaptureHookInstallsAgainstTheInstalledGameAssemblies()
    {
        var report = new Sts2CompatibilityReport();
        var log = new InMemoryLogStream();

        new Sts2CompatibilityHookInstaller(report, log).Install(
            nameof(Sts2RewardsCaptureHooks), Sts2RewardsCaptureHooks.Install);

        Assert.Empty(report.Snapshot());
        Assert.Contains(log.Read(new LogQuery(100, null, null, null)).Entries,
            entry => entry.Message == "Installed reward-capture hook.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GameApiRewardsCapturePostfixMatchesBothSupportedTaskShapes(bool returnsRewards)
    {
        var returnType = returnsRewards ? typeof(Task<List<Reward>>) : typeof(Task);

        var postfix = Sts2RewardsCaptureHooks.ResolvePostfix(returnType);

        Assert.NotNull(postfix);
        Assert.Equal(returnType, postfix.GetParameters()[1].ParameterType);
    }

    [Theory]
    [InlineData(typeof(void))]
    [InlineData(typeof(Task<int>))]
    public void GameApiRewardsCaptureRejectsUnsupportedReturnShapes(Type returnType)
    {
        Assert.Null(Sts2RewardsCaptureHooks.ResolvePostfix(returnType));
    }
}
#endif
