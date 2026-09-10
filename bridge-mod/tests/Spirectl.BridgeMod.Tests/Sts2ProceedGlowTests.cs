using Spirectl.Sts2.Core.SceneInspection;
using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// R13 producer PROCEED-GLOW FOLD (Sts2ProceedGlow) — a pure, Godot-free exercise of the scene identity, the
// loop gate (all eight combinations), and the analytic alpha pin.
public sealed class Sts2ProceedGlowTests
{
    private const double Tolerance = 1e-9;

    // ---- scene identity ------------------------------------------------------------------------------------

    [Fact]
    public void Outline_IsTheOnlyMatchedNode()
    {
        Assert.True(Sts2ProceedGlow.IsOutline(Sts2ProceedGlow.SceneFile, "Image/Outline"));
        Assert.Equal("Image/Outline", Sts2ProceedGlow.OutlineRelPath);
        // Siblings/ancestors of the glow are untouched (the button's own move must keep streaming), and so is the
        // same path in another scene.
        Assert.False(Sts2ProceedGlow.IsOutline(Sts2ProceedGlow.SceneFile, "."));
        Assert.False(Sts2ProceedGlow.IsOutline(Sts2ProceedGlow.SceneFile, "Image"));
        Assert.False(Sts2ProceedGlow.IsOutline(Sts2ProceedGlow.SceneFile, "Image/Label"));
        Assert.False(Sts2ProceedGlow.IsOutline("res://scenes/ui/top_bar/top_bar_deck_button.tscn", "Image/Outline"));
        Assert.False(Sts2ProceedGlow.IsOutline(null, "Image/Outline"));
        Assert.False(Sts2ProceedGlow.IsOutline(Sts2ProceedGlow.SceneFile, null));
    }

    [Fact]
    public void SuppressionTable_RoutesTheOutlineToTheFoldChannel()
    {
        var channels = Sts2DecorEmitSuppress.Lookup(Sts2ProceedGlow.SceneFile, Sts2ProceedGlow.OutlineRelPath);
        Assert.Equal(Sts2DecorEmitSuppress.Channels.ProceedGlowAlpha, channels);
        Assert.Equal(Sts2DecorEmitSuppress.Channels.None, channels & Sts2DecorEmitSuppress.Channels.TopBarRotation);
        Assert.Equal(Sts2DecorEmitSuppress.Channels.None, channels & Sts2DecorEmitSuppress.Channels.MapPointPulseScale);
        Assert.True(Sts2DecorEmitSuppress.IsWatchedScene(Sts2ProceedGlow.SceneFile));
    }

    // ---- the loop gate -------------------------------------------------------------------------------------

    [Theory]
    // Enabled AND wanting to pulse AND not focused — the condition under which the shimmer loop runs
    // (OnEnable / OnUnfocus / SetPulseState(true)). The full truth table, so no combination is left to inference.
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, false, false)]  // pulse wanted but the button is hidden/disabled → OnDisable killed it
    [InlineData(false, true, true, false)]
    [InlineData(true, false, false, false)]  // SetPulseState(false) → StopGlowTween fades to 0; that fade streams live
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, true)]    // shown, pulsing, unhovered → the infinite loop is the only writer
    [InlineData(true, true, true, false)]    // hovered/pressed → OnFocus's one-shot to alpha 1 streams live
    public void IsLooping_MatchesTheLoopGates(bool enabled, bool shouldPulse, bool focused, bool expected)
        => Assert.Equal(expected, Sts2ProceedGlow.IsLooping(enabled, shouldPulse, focused));

    // ---- the alpha pin -------------------------------------------------------------------------------------

    [Theory]
    // Anywhere in the loop's sweep, including both endpoints.
    [InlineData(0.25)]
    [InlineData(0.4823)]
    [InlineData(0.686)]
    [InlineData(0.741)]
    public void PinAlpha_FreezesAlphaButLetsColourThrough(double liveAlpha)
    {
        // The authored glow (#ffcc00). The live pulse only moves A → the pinned result is byte-identical every tick,
        // so the change test sees nothing and the node never emits.
        var pulsed = new RuntimeSceneColorSnapshot(1.0, 0.8, 0.0, liveAlpha, null);
        var pinned = Sts2ProceedGlow.PinAlpha(pulsed);

        Assert.NotNull(pinned);
        Assert.Equal(0.75, pinned!.A, Tolerance);
        Assert.Equal(1.0, pinned.R, Tolerance);
        Assert.Equal(0.8, pinned.G, Tolerance);
        Assert.Equal(0.0, pinned.B, Tolerance);
        // Html is left null — the watcher formats it only for emitted nodes.
        Assert.Null(pinned.Html);
    }

    [Fact]
    public void PinAlpha_KeepsARealColourChange()
    {
        // A real STATE change (the glow re-tinted) keeps its RGB — only the alpha is pinned, so the client still
        // learns about it while the loop runs.
        var retinted = new RuntimeSceneColorSnapshot(1.0, 0.0, 0.0, 0.31, "#ff00004f");
        var pinned = Sts2ProceedGlow.PinAlpha(retinted);

        Assert.Equal(1.0, pinned!.R, Tolerance);
        Assert.Equal(0.0, pinned.G, Tolerance);
        Assert.Equal(0.0, pinned.B, Tolerance);
        Assert.Equal(Sts2ProceedGlow.PinnedAlpha, pinned.A, Tolerance);
    }

    [Fact]
    public void PinAlpha_AlreadyAtRest_ReturnsTheSameInstance()
    {
        var atRest = new RuntimeSceneColorSnapshot(1.0, 0.8, 0.0, Sts2ProceedGlow.PinnedAlpha, null);
        Assert.Same(atRest, Sts2ProceedGlow.PinAlpha(atRest));
        Assert.Null(Sts2ProceedGlow.PinAlpha(null));
    }

    [Fact]
    public void LoopConstants_MatchTheShimmerOnScreen()
    {
        // An endless shimmer: self-modulate alpha to 0.25 over 0.5s, back to 0.75 over 0.5s, both legs LINEAR.
        // These are what the CLIENT replays, so pin them.
        Assert.Equal(0.25, Sts2ProceedGlow.LoopMinAlpha, Tolerance);
        Assert.Equal(0.75, Sts2ProceedGlow.LoopMaxAlpha, Tolerance);
        Assert.Equal(500.0, Sts2ProceedGlow.LoopLegMs, Tolerance);
        Assert.Equal(1000.0, Sts2ProceedGlow.LoopPeriodMs, Tolerance);
        // The pin is the value each loop iteration STARTS and ENDS at — analytic, not a first-seen sample. That
        // distinction is load-bearing here: the button starts out disabled and its stop path fades to 0, so the
        // first value the producer ever sees for this node is 0, which is a MEANINGFUL "no glow" state.
        Assert.Equal(Sts2ProceedGlow.LoopMaxAlpha, Sts2ProceedGlow.PinnedAlpha, Tolerance);
        Assert.NotEqual(Sts2ProceedGlow.StoppedAlpha, Sts2ProceedGlow.PinnedAlpha);
        // The two one-shots that are deliberately NOT folded (they stream live).
        Assert.Equal(1.0, Sts2ProceedGlow.FocusAlpha, Tolerance);
        Assert.Equal(50.0, Sts2ProceedGlow.FocusMs, Tolerance);
        Assert.Equal(0.0, Sts2ProceedGlow.StoppedAlpha, Tolerance);
        Assert.Equal(500.0, Sts2ProceedGlow.StopMs, Tolerance);
        // The wire token is a client contract — pin it verbatim.
        Assert.Equal("proceedGlow", Sts2ProceedGlow.LoopAnimName);
    }
}
