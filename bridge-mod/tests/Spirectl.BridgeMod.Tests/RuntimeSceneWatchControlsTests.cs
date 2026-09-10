using Spirectl.Sts2;
using Spirectl.Sts2.Embedding;
using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

public sealed class RuntimeSceneWatchControlsTests
{
    [Fact]
    public void TweenReplayChangesBothSuppressionChannelsThroughNarrowPort()
    {
        var transform = Sts2SceneWatchRuntimeSettings.SuppressTweenedTransforms;
        var opacity = Sts2SceneWatchRuntimeSettings.SuppressTweenedOpacity;
        try
        {
            IRuntimeSceneWatchControls controls = Sts2RuntimeSceneWatchControls.Instance;
            controls.SetTweenReplayEnabled(false);
            Assert.False(Sts2SceneWatchRuntimeSettings.SuppressTweenedTransforms);
            Assert.False(Sts2SceneWatchRuntimeSettings.SuppressTweenedOpacity);
            controls.SetTweenReplayEnabled(true);
            Assert.True(Sts2SceneWatchRuntimeSettings.SuppressTweenedTransforms);
            Assert.True(Sts2SceneWatchRuntimeSettings.SuppressTweenedOpacity);
        }
        finally
        {
            Sts2SceneWatchRuntimeSettings.SuppressTweenedTransforms = transform;
            Sts2SceneWatchRuntimeSettings.SuppressTweenedOpacity = opacity;
        }
    }

    [Fact]
    public void ExplicitUnsupportedProviderIsReportedAndDoesNotMutateSettings()
    {
        var before = Sts2SceneWatchRuntimeSettings.SuppressTweenedTransforms;
        using var runtime = EmbeddedRuntimeTestFactory.Create(
            sceneWatchControls: UnsupportedRuntimeSceneWatchControls.Instance);
        var capability = Assert.Single(runtime.GetCapabilities().Capabilities,
            item => item.Id == EmbeddableCapabilityIds.SceneWatchControls);
        Assert.False(capability.Supported);
        Assert.NotNull(capability.UnsupportedReason);
        Assert.Throws<NotSupportedException>(() => runtime.SceneWatchControls.SetTweenReplayEnabled(!before));
        Assert.Equal(before, Sts2SceneWatchRuntimeSettings.SuppressTweenedTransforms);
    }
}
