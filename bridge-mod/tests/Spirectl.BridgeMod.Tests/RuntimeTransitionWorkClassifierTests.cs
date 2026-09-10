using Spirectl.Sts2.Core.SceneInspection;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

public sealed class RuntimeTransitionWorkClassifierTests
{
    [Theory]
    [InlineData("MegaCrit.Sts2.Core.Nodes.Combat.NPlayerTurnBanner", true)]
    [InlineData("MegaCrit.Sts2.Core.Nodes.NAncientNameBanner", true)]
    [InlineData("NPlayerTurnBanner", true)]
    [InlineData("MegaCrit.Sts2.Core.Nodes.Vfx.NRestSiteFireVfx", false)]
    [InlineData("Godot.AnimationPlayer", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IntroAnimationNodeTypesAreRecognized(string? typeName, bool expected)
    {
        Assert.Equal(expected, RuntimeTransitionWorkClassifier.IsIntroAnimationNodeType(typeName));
    }

    [Fact]
    public void VisibleIntroAnimationBlocks()
    {
        var result = RuntimeTransitionWorkClassifier.ClassifyIntroAnimation(visibleInTree: true, modulateAlpha: 1.0);

        Assert.True(result.Blocks);
        Assert.False(result.IgnoredInfinite);
        Assert.Equal("intro-animation-showing", result.Reason);
    }

    [Fact]
    public void FadedOutIntroAnimationIsIgnored()
    {
        var result = RuntimeTransitionWorkClassifier.ClassifyIntroAnimation(visibleInTree: true, modulateAlpha: 0.0);

        Assert.False(result.Blocks);
        Assert.Equal("intro-animation-idle", result.Reason);
    }

    [Fact]
    public void HiddenIntroAnimationIsIgnored()
    {
        var result = RuntimeTransitionWorkClassifier.ClassifyIntroAnimation(visibleInTree: false, modulateAlpha: 1.0);

        Assert.False(result.Blocks);
        Assert.Equal("intro-animation-idle", result.Reason);
    }

    [Fact]
    public void RunningIntroTweenBlocksRegardlessOfAlpha()
    {
        // A persistent banner (e.g. NAncientNameBanner) stays at full alpha but is still
        // playing its intro: the running tween is the authoritative signal.
        var result = RuntimeTransitionWorkClassifier.ClassifyIntroAnimation(
            visibleInTree: true,
            modulateAlpha: 1.0,
            introTweenRunning: true);

        Assert.True(result.Blocks);
        Assert.Equal("intro-animation-showing", result.Reason);
    }

    [Fact]
    public void StoppedIntroTweenIsIgnoredEvenWhenStillVisibleAtFullAlpha()
    {
        // The NAncientNameBanner regression: its own modulate alpha never returns to 0 and
        // the node never frees itself, so the alpha heuristic would block forever. Once the
        // intro tween stops, the banner is settled and must not block quiescence.
        var result = RuntimeTransitionWorkClassifier.ClassifyIntroAnimation(
            visibleInTree: true,
            modulateAlpha: 1.0,
            introTweenRunning: false);

        Assert.False(result.Blocks);
        Assert.Equal("intro-animation-idle", result.Reason);
    }

    [Fact]
    public void HiddenIntroAnimationIsIgnoredEvenWhenTweenRunning()
    {
        var result = RuntimeTransitionWorkClassifier.ClassifyIntroAnimation(
            visibleInTree: false,
            modulateAlpha: 1.0,
            introTweenRunning: true);

        Assert.False(result.Blocks);
        Assert.Equal("intro-animation-idle", result.Reason);
    }

    [Fact]
    public void ActiveGameTransitionBlocks()
    {
        var result = RuntimeTransitionWorkClassifier.ClassifyGameTransition(inTransition: true);

        Assert.True(result.Blocks);
        Assert.False(result.IgnoredInfinite);
        Assert.False(result.Infinite);
        Assert.Equal("game-transition-active", result.Reason);
    }

    [Fact]
    public void InactiveGameTransitionIsIgnored()
    {
        var result = RuntimeTransitionWorkClassifier.ClassifyGameTransition(inTransition: false);

        Assert.False(result.Blocks);
        Assert.Equal("game-transition-inactive", result.Reason);
    }
}
