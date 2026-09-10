using Spirectl.Sts2.Core.SceneInspection;
using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// R14 producer IDLE-WIRE COMBAT FOLDS (Sts2IntentBobFold / Sts2IntentGlyphFold / Sts2OrbSpinFold /
// Sts2EndTurnGlowFold) — pure, Godot-free exercises of the four scene identities, their rest pins, and the one
// gated loop among them. Together these are what takes a visually IDLE combat from ~30 msgs/s to silence, so
// the properties asserted here are the ones a regression would break invisibly: the pins are ANALYTIC (never a
// sample), the undo is exact for an arbitrarily placed/rotated/scaled node, and no fold leaks onto a sibling.
public sealed class Sts2IdleWireFoldTests
{
    private const double Tolerance = 1e-9;

    private static RuntimeSceneTransform2DSnapshot Xform(
        double xx, double xy, double yx, double yy, double ox, double oy)
        => new(
            new RuntimeSceneVector2Snapshot(xx, xy),
            new RuntimeSceneVector2Snapshot(yx, yy),
            new RuntimeSceneVector2Snapshot(ox, oy));

    /// <summary>A Godot Control's transform `T(pos + p) · R(θ) · S(k) · T(-p)`, built the way the engine does.</summary>
    private static RuntimeSceneTransform2DSnapshot ControlXform(
        double posX, double posY, double rotationRad, double scale, double pivotX, double pivotY)
    {
        var cos = System.Math.Cos(rotationRad);
        var sin = System.Math.Sin(rotationRad);
        // basis = R(θ) · S(k) — Godot column convention: XAxis = (cos, sin)·k, YAxis = (-sin, cos)·k.
        var xx = cos * scale;
        var xy = sin * scale;
        var yx = -sin * scale;
        var yy = cos * scale;
        // origin = pos + p - basis · p
        return Xform(xx, xy, yx, yy,
            posX + pivotX - (xx * pivotX + yx * pivotY),
            posY + pivotY - (xy * pivotX + yy * pivotY));
    }

    private static void AssertXformEqual(RuntimeSceneTransform2DSnapshot expected, RuntimeSceneTransform2DSnapshot? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.XAxis.X, actual!.XAxis.X, Tolerance);
        Assert.Equal(expected.XAxis.Y, actual.XAxis.Y, Tolerance);
        Assert.Equal(expected.YAxis.X, actual.YAxis.X, Tolerance);
        Assert.Equal(expected.YAxis.Y, actual.YAxis.Y, Tolerance);
        Assert.Equal(expected.Origin.X, actual.Origin.X, Tolerance);
        Assert.Equal(expected.Origin.Y, actual.Origin.Y, Tolerance);
    }

    // ================= enemy-intent BOB (Sts2IntentBobFold) ==================================================

    [Fact]
    public void IntentBob_MatchesOnlyTheHolderOfTheIntentScene()
    {
        Assert.True(Sts2IntentBobFold.IsHolder(Sts2IntentBobFold.SceneFile, "IntentHolder"));
        // The holder's own children, its scene root, and the same path in the co-op player-intent scene are all
        // untouched — the bob is one node's position, and the children ride it via DOM nesting.
        Assert.False(Sts2IntentBobFold.IsHolder(Sts2IntentBobFold.SceneFile, "."));
        Assert.False(Sts2IntentBobFold.IsHolder(Sts2IntentBobFold.SceneFile, "IntentHolder/Value"));
        Assert.False(Sts2IntentBobFold.IsHolder("res://scenes/combat/multiplayer_player_intent.tscn", "IntentHolder"));
        Assert.False(Sts2IntentBobFold.IsHolder(null, "IntentHolder"));
        Assert.False(Sts2IntentBobFold.IsHolder(Sts2IntentBobFold.SceneFile, null));
    }

    [Fact]
    public void IntentBob_RoutesToItsOwnChannel_AndTheGlyphToTheOther()
    {
        Assert.True(Sts2DecorEmitSuppress.IsWatchedScene(Sts2IntentBobFold.SceneFile));
        Assert.Equal(
            Sts2DecorEmitSuppress.Channels.IntentBobPosition,
            Sts2DecorEmitSuppress.Lookup(Sts2IntentBobFold.SceneFile, Sts2IntentBobFold.HolderRelPath));
        // ONE scene, TWO folds (the intent bobs AND flips its glyph) — and they must not be the same channel.
        Assert.Equal(
            Sts2DecorEmitSuppress.Channels.IntentGlyphTexture,
            Sts2DecorEmitSuppress.Lookup(Sts2IntentGlyphFold.SceneFile, Sts2IntentGlyphFold.GlyphRelPath));
        // Everything else in the intent scene keeps streaming.
        Assert.Equal(Sts2DecorEmitSuppress.Channels.None, Sts2DecorEmitSuppress.Lookup(Sts2IntentBobFold.SceneFile, "."));
        Assert.Equal(
            Sts2DecorEmitSuppress.Channels.None,
            Sts2DecorEmitSuppress.Lookup(Sts2IntentBobFold.SceneFile, "IntentHolder/Value"));
        Assert.Equal(
            Sts2DecorEmitSuppress.Channels.None,
            Sts2DecorEmitSuppress.Lookup(Sts2IntentBobFold.SceneFile, "IntentHolder/IntentParticle"));
    }

    [Theory]
    // The sine's full excursion: `Position = Up * (sin*10 + 8)` → y ∈ [-18, +2]. Both extrema, the baseline, and
    // the two zero crossings of the raw sine.
    [InlineData(-18.0)]
    [InlineData(-8.0)]
    [InlineData(2.0)]
    [InlineData(-13.2)]
    public void IntentBob_PinRemovesExactlyThePositionOffset(double positionY)
    {
        // An arbitrarily placed, rotated and scaled holder — the pin must not touch any of that.
        var pinned = Sts2IntentBobFold.PinRestPosition(
            ControlXform(0.0, positionY, 0.37, 1.15, 32, 32), 0.0, positionY);
        AssertXformEqual(ControlXform(0.0, 0.0, 0.37, 1.15, 32, 32), pinned);
    }

    [Fact]
    public void IntentBob_PinIsIdentityAtRest_AndLeavesTheBasisAlone()
    {
        var live = ControlXform(0, 0, 0.37, 1.15, 32, 32);
        // Already at rest → the SAME instance back (no allocation on the steady state).
        Assert.Same(live, Sts2IntentBobFold.PinRestPosition(live, 0, 0));
        Assert.Same(live, Sts2IntentBobFold.PinRestPosition(live, 1e-9, -1e-9));
        // A non-finite live position can never be undone — leave the stream alone rather than emit NaN.
        Assert.Same(live, Sts2IntentBobFold.PinRestPosition(live, double.NaN, 0));
        Assert.Null(Sts2IntentBobFold.PinRestPosition(null, 0, -12));

        // The basis is bit-identical: a bob undo must never disturb rotation/scale/skew.
        var moved = Sts2IntentBobFold.PinRestPosition(live, 0, -12)!;
        Assert.Equal(live.XAxis.X, moved.XAxis.X);
        Assert.Equal(live.XAxis.Y, moved.XAxis.Y);
        Assert.Equal(live.YAxis.X, moved.YAxis.X);
        Assert.Equal(live.YAxis.Y, moved.YAxis.Y);
    }

    [Fact]
    public void IntentBob_ConstantsMatchTheBobOnScreen()
    {
        // 10px of amplitude about a baseline 8px up, period 2000ms — the numbers the client's `bob` binding replays.
        Assert.Equal(10.0, Sts2IntentBobFold.AmplitudePx);
        Assert.Equal(8.0, Sts2IntentBobFold.BaselineUpPx);
        Assert.Equal(2000.0, Sts2IntentBobFold.PeriodMs);
        Assert.Equal(0.0, Sts2IntentBobFold.RestPositionX);
        Assert.Equal(0.0, Sts2IntentBobFold.RestPositionY);
    }

    // ================= enemy-intent GLYPH flip-book (Sts2IntentGlyphFold) ====================================

    private static RuntimeSceneRect2Snapshot Rect(double x, double y, double w, double h)
        => new(new RuntimeSceneVector2Snapshot(x, y), new RuntimeSceneVector2Snapshot(w, h));

    private static RuntimeSceneIntentFramesSnapshot Frames(params RuntimeSceneIntentFrameSnapshot[] frames)
        => new("attack", Sts2IntentGlyphFold.AnimationFps, frames);

    [Fact]
    public void IntentGlyph_MatchesOnlyTheGlyphSprite()
    {
        Assert.Equal("IntentHolder/Intent", Sts2IntentGlyphFold.GlyphRelPath);
        Assert.True(Sts2IntentGlyphFold.IsGlyph(Sts2IntentGlyphFold.SceneFile, Sts2IntentGlyphFold.GlyphRelPath));
        Assert.False(Sts2IntentGlyphFold.IsGlyph(Sts2IntentGlyphFold.SceneFile, "IntentHolder"));
        Assert.False(Sts2IntentGlyphFold.IsGlyph("res://scenes/combat/end_turn_button.tscn", "IntentHolder/Intent"));
    }

    [Fact]
    public void IntentGlyph_SubstitutesFrameZeroForWhateverCropIsLive()
    {
        var texture = new RuntimeSceneResourceRefSnapshot("texture", "res://images/packed/page_7.png", "AtlasTexture", "page_7");
        RuntimeSceneResourceRefSnapshot? live = texture;
        // The live read is mid-flip-book: frame 3's crop.
        RuntimeSceneRect2Snapshot? region = Rect(300, 40, 64, 64);
        RuntimeSceneRect2Snapshot? margin = Rect(1, 2, 3, 4);

        var frame0Region = Rect(0, 40, 64, 64);
        var frame0Margin = Rect(0, 0, 0, 0);
        Sts2IntentGlyphFold.PinFrameZero(
            Frames(
                new RuntimeSceneIntentFrameSnapshot("res://images/packed/page_7.png", frame0Region, frame0Margin),
                new RuntimeSceneIntentFrameSnapshot("res://images/packed/page_7.png", Rect(64, 40, 64, 64), frame0Margin),
                new RuntimeSceneIntentFrameSnapshot("res://images/packed/page_7.png", Rect(128, 40, 64, 64), frame0Margin)),
            ref live, ref region, ref margin);

        Assert.Same(frame0Region, region);
        Assert.Same(frame0Margin, margin);
        // Same page → the ref instance is untouched (no allocation on the common case).
        Assert.Same(texture, live);
    }

    [Fact]
    public void IntentGlyph_SwapsThePageWhenFrameZeroLivesOnAnother_AndKeepsTheRefsFieldShape()
    {
        RuntimeSceneResourceRefSnapshot? live =
            new("texture", "res://images/packed/page_7.png", "AtlasTexture", "page_7");
        RuntimeSceneRect2Snapshot? region = Rect(300, 40, 64, 64);
        RuntimeSceneRect2Snapshot? margin = null;

        Sts2IntentGlyphFold.PinFrameZero(
            Frames(new RuntimeSceneIntentFrameSnapshot("res://images/packed/page_2.png", Rect(0, 0, 64, 64), null)),
            ref live, ref region, ref margin);

        Assert.Equal("res://images/packed/page_2.png", live!.ResourcePath);
        // Only the path is substituted — field/type/name are whatever ReadPrimaryTexture produced.
        Assert.Equal("texture", live.Field);
        Assert.Equal("AtlasTexture", live.ResourceType);
        Assert.Equal("page_7", live.ResourceName);
    }

    [Fact]
    public void IntentGlyph_LeavesTheStreamAloneWithoutAUsableFrameSet()
    {
        var texture = new RuntimeSceneResourceRefSnapshot("texture", "res://a.png", "AtlasTexture", "a");
        var liveRegion = Rect(300, 40, 64, 64);

        foreach (var frames in new RuntimeSceneIntentFramesSnapshot?[]
                 {
                     null,
                     Frames(),
                     Frames(new RuntimeSceneIntentFrameSnapshot("", Rect(0, 0, 1, 1), null)),
                 })
        {
            RuntimeSceneResourceRefSnapshot? live = texture;
            RuntimeSceneRect2Snapshot? region = liveRegion;
            RuntimeSceneRect2Snapshot? margin = null;
            Sts2IntentGlyphFold.PinFrameZero(frames, ref live, ref region, ref margin);
            Assert.Same(texture, live);
            Assert.Same(liveRegion, region);
        }

        // A node with no texture at all (nothing to substitute into).
        RuntimeSceneResourceRefSnapshot? none = null;
        RuntimeSceneRect2Snapshot? noneRegion = null;
        RuntimeSceneRect2Snapshot? noneMargin = null;
        Sts2IntentGlyphFold.PinFrameZero(
            Frames(new RuntimeSceneIntentFrameSnapshot("res://b.png", Rect(0, 0, 1, 1), null)),
            ref none, ref noneRegion, ref noneMargin);
        Assert.Null(none);
        Assert.Null(noneRegion);
    }

    // ================= energy/star ORB SPIN (Sts2OrbSpinFold) ================================================

    [Theory]
    // The five energy counters' layer sets, read off the recovered scenes. Note necrobinder has ONE rotation layer
    // (its `Layers/Layer3` is a plain SIBLING of RotationLayers and must never be folded).
    [InlineData("res://scenes/combat/energy_counters/ironclad_energy_counter.tscn", "Layers/RotationLayers/Layer2", true)]
    [InlineData("res://scenes/combat/energy_counters/ironclad_energy_counter.tscn", "Layers/RotationLayers/Layer3", true)]
    [InlineData("res://scenes/combat/energy_counters/silent_energy_counter.tscn", "Layers/RotationLayers/Layer3", true)]
    [InlineData("res://scenes/combat/energy_counters/defect_energy_counter.tscn", "Layers/RotationLayers/Layer2", true)]
    [InlineData("res://scenes/combat/energy_counters/regent_energy_counter.tscn", "Layers/RotationLayers/Layer3", true)]
    [InlineData("res://scenes/combat/energy_counters/necrobinder_energy_counter.tscn", "Layers/RotationLayers/Layer2", true)]
    [InlineData("res://scenes/combat/energy_counters/necrobinder_energy_counter.tscn", "Layers/RotationLayers/Layer3", false)]
    [InlineData("res://scenes/combat/energy_counters/necrobinder_energy_counter.tscn", "Layers/Layer3", false)]
    // The star counter nests + numbers its layers differently — the exact ambiguity a `*/Layer2` heuristic got wrong.
    [InlineData("res://scenes/combat/energy_counters/star_counter.tscn", "Icon/RotationLayers/Layer1", true)]
    [InlineData("res://scenes/combat/energy_counters/star_counter.tscn", "Icon/RotationLayers/Layer2", true)]
    [InlineData("res://scenes/combat/energy_counters/star_counter.tscn", "Layers/RotationLayers/Layer2", false)]
    // Non-rotating siblings and the containers themselves keep streaming.
    [InlineData("res://scenes/combat/energy_counters/ironclad_energy_counter.tscn", "Layers/RotationLayers", false)]
    [InlineData("res://scenes/combat/energy_counters/ironclad_energy_counter.tscn", "Layers/Layer1", false)]
    [InlineData("res://scenes/combat/energy_counters/ironclad_energy_counter.tscn", ".", false)]
    [InlineData("res://scenes/combat/intent.tscn", "Layers/RotationLayers/Layer2", false)]
    public void OrbSpin_MatchesExactlyTheAuthoredRotationLayers(string scene, string relPath, bool expected)
    {
        Assert.Equal(expected, Sts2OrbSpinFold.IsSpinLayer(scene, relPath));
        Assert.Equal(
            expected ? Sts2DecorEmitSuppress.Channels.OrbSpinRotation : Sts2DecorEmitSuppress.Channels.None,
            Sts2DecorEmitSuppress.Lookup(scene, relPath) & Sts2DecorEmitSuppress.Channels.OrbSpinRotation);
    }

    [Fact]
    public void OrbSpin_EveryCounterSceneIsWatched()
    {
        Assert.Equal(6, Sts2OrbSpinFold.LayerPathsByScene.Count);
        foreach (var scene in Sts2OrbSpinFold.LayerPathsByScene.Keys)
        {
            Assert.True(Sts2DecorEmitSuppress.IsWatchedScene(scene), scene);
        }
    }

    [Theory]
    // `RotationDegrees += …` is UNBOUNDED, so the pin must hold thousands of radians into a long run — which is
    // exactly where a raw Cos/Sin would lose precision (the reason the shared undo reduces θ first).
    [InlineData(0.7)]
    [InlineData(-2.4)]
    [InlineData(3.14159)]
    [InlineData(4231.87)]
    public void OrbSpin_PinRemovesExactlyTheAccumulatedRotation(double rotationRad)
    {
        // Layers are authored pivot-centred at (64,64) in all six scenes.
        var pinned = Sts2OrbSpinFold.PinRestRotation(
            ControlXform(11, -7, rotationRad, 1.0, 64, 64), rotationRad, 64, 64);
        AssertXformEqual(ControlXform(11, -7, Sts2OrbSpinFold.RestRotationRad, 1.0, 64, 64), pinned);
    }

    [Fact]
    public void OrbSpin_RestIsTheAuthoredAngle_AndTheConstantsMatchTheAnimator()
    {
        Assert.Equal(0.0, Sts2OrbSpinFold.RestRotationRad);
        Assert.Equal(30.0, Sts2OrbSpinFold.SpinDegreesPerSec);
        Assert.Equal(5.0, Sts2OrbSpinFold.SpinDegreesPerSecAtZero);
        Assert.Equal(12000.0, Sts2OrbSpinFold.FirstLayerTurnMs, Tolerance);
        Assert.Null(Sts2OrbSpinFold.PinRestRotation(null, 1.2, 64, 64));
    }

    // ================= END-TURN GLOW (Sts2EndTurnGlowFold) ===================================================

    [Fact]
    public void EndTurnGlow_MatchesOnlyTheGlowVfx()
    {
        Assert.True(Sts2EndTurnGlowFold.IsGlowVfx(Sts2EndTurnGlowFold.SceneFile, "Visuals/GlowVfx"));
        // `Visuals/Glow` is a DIFFERENT node driven by a one-shot enable tween — it must keep streaming.
        Assert.False(Sts2EndTurnGlowFold.IsGlowVfx(Sts2EndTurnGlowFold.SceneFile, "Visuals/Glow"));
        Assert.False(Sts2EndTurnGlowFold.IsGlowVfx(Sts2EndTurnGlowFold.SceneFile, "Visuals"));
        Assert.False(Sts2EndTurnGlowFold.IsGlowVfx(Sts2EndTurnGlowFold.SceneFile, "."));
        Assert.False(Sts2EndTurnGlowFold.IsGlowVfx("res://scenes/combat/intent.tscn", "Visuals/GlowVfx"));

        Assert.True(Sts2DecorEmitSuppress.IsWatchedScene(Sts2EndTurnGlowFold.SceneFile));
        Assert.Equal(
            Sts2DecorEmitSuppress.Channels.EndTurnGlowPulse,
            Sts2DecorEmitSuppress.Lookup(Sts2EndTurnGlowFold.SceneFile, Sts2EndTurnGlowFold.GlowVfxRelPath));
        Assert.Equal(
            Sts2DecorEmitSuppress.Channels.None,
            Sts2DecorEmitSuppress.Lookup(Sts2EndTurnGlowFold.SceneFile, "Visuals/Glow"));
    }

    [Theory]
    // The "shiny" flag is raised immediately before the pulse loop starts and cleared immediately before it is
    // killed, so the flag IS the loop. Everything else — the 0.5s fade-to-0, the authored resting alpha 0 — streams.
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void EndTurnGlow_IsLoopingMirrorsIsShiny(bool isShiny, bool expected)
        => Assert.Equal(expected, Sts2EndTurnGlowFold.IsLooping(isShiny));

    [Theory]
    // Anywhere in the loop's 0.5 → 0.7 scale sweep, including both endpoints.
    [InlineData(0.5)]
    [InlineData(0.6)]
    [InlineData(0.7)]
    public void EndTurnGlow_PinPullsTheScaleBackToTheLoopStart(double liveScale)
    {
        // GlowVfx is authored pivot-CENTRED at (256,128) on a 512×256 box, at an arbitrary button placement.
        var pinned = Sts2EndTurnGlowFold.PinRestScale(
            ControlXform(940, 610, 0, liveScale, 256, 128), liveScale, 256, 128);
        AssertXformEqual(ControlXform(940, 610, 0, Sts2EndTurnGlowFold.PinnedScale, 256, 128), pinned);
    }

    [Fact]
    public void EndTurnGlow_ScalePinIsIdentityAtTheLoopStart_AndSafeOnDegenerateInput()
    {
        var atStart = ControlXform(940, 610, 0, Sts2EndTurnGlowFold.PinnedScale, 256, 128);
        Assert.Same(atStart, Sts2EndTurnGlowFold.PinRestScale(atStart, Sts2EndTurnGlowFold.PinnedScale, 256, 128));
        Assert.Same(atStart, Sts2EndTurnGlowFold.PinRestScale(atStart, 0.0, 256, 128));
        Assert.Same(atStart, Sts2EndTurnGlowFold.PinRestScale(atStart, double.NaN, 256, 128));
        Assert.Null(Sts2EndTurnGlowFold.PinRestScale(null, 0.7, 256, 128));
    }

    [Theory]
    // Anywhere in the loop's 0.4 → 0 alpha sweep BELOW the pin. RGB must survive untouched — a real colour change
    // still ships. (The 0.4 endpoint is the no-op case, asserted separately below.)
    [InlineData(0.21)]
    [InlineData(0.0)]
    [InlineData(0.399)]
    public void EndTurnGlow_AlphaPinSubstitutesTheLoopStartAndKeepsRgb(double liveAlpha)
    {
        var pinned = Sts2EndTurnGlowFold.PinAlpha(new RuntimeSceneColorSnapshot(0.25, 0.875, 1.0, liveAlpha, "#40dfffff"));
        Assert.NotNull(pinned);
        Assert.Equal(0.25, pinned!.R);
        Assert.Equal(0.875, pinned.G);
        Assert.Equal(1.0, pinned.B);
        Assert.Equal(Sts2EndTurnGlowFold.PinnedAlpha, pinned.A);
        // Html is formatted only for emitted nodes; a substituted colour must not carry a stale string forward.
        Assert.Null(pinned.Html);
    }

    [Fact]
    public void EndTurnGlow_AlphaPinIsIdentityAtTheLoopStart_AndOpacityMovesInLockstep()
    {
        var atStart = new RuntimeSceneColorSnapshot(0.25, 0.875, 1.0, Sts2EndTurnGlowFold.PinnedAlpha, null);
        Assert.Same(atStart, Sts2EndTurnGlowFold.PinAlpha(atStart));
        Assert.Null(Sts2EndTurnGlowFold.PinAlpha(null));
        // The watcher reads a node's Opacity as modulate.A, so the two pins MUST agree or the sweep keeps churning
        // through the opacity channel alone and the fold silences nothing.
        Assert.Equal(Sts2EndTurnGlowFold.PinnedAlpha, Sts2EndTurnGlowFold.PinOpacity(0.13));
        Assert.Equal(Sts2EndTurnGlowFold.PinnedAlpha, Sts2EndTurnGlowFold.PinOpacity(0.0));
    }

    [Fact]
    public void EndTurnGlow_ConstantsAndReplayRatiosMatchThePulseOnScreen()
    {
        // scale 0.5 → 0.7 (Quart/Out) in parallel with alpha 0.4 → 0 (linear), 1.5s, looping, both snapping back.
        Assert.Equal(0.5, Sts2EndTurnGlowFold.LoopStartScale);
        Assert.Equal(0.7, Sts2EndTurnGlowFold.LoopMaxScale);
        Assert.Equal(0.4, Sts2EndTurnGlowFold.LoopStartAlpha);
        Assert.Equal(0.0, Sts2EndTurnGlowFold.LoopEndAlpha);
        Assert.Equal(1500.0, Sts2EndTurnGlowFold.LoopPeriodMs);
        Assert.Equal("endTurnGlow", Sts2EndTurnGlowFold.LoopAnimName);
        // The pins are the loop's START values (what every cycle begins and restarts at) — not an average, not a
        // sample: both channels sweep monotonically away and snap back.
        Assert.Equal(Sts2EndTurnGlowFold.LoopStartScale, Sts2EndTurnGlowFold.PinnedScale);
        Assert.Equal(Sts2EndTurnGlowFold.LoopStartAlpha, Sts2EndTurnGlowFold.PinnedAlpha);
        // The client can only MULTIPLY the pins back up, so the replay endpoints are ratios: 1 → 1.4 and 1 → 0.
        Assert.Equal(1.0, Sts2EndTurnGlowFold.ReplayScaleFrom);
        Assert.Equal(1.4, Sts2EndTurnGlowFold.ReplayScaleTo, Tolerance);
        Assert.Equal(1.0, Sts2EndTurnGlowFold.ReplayAlphaFrom);
        Assert.Equal(0.0, Sts2EndTurnGlowFold.ReplayAlphaTo);
    }

    // ================= cross-fold isolation ==================================================================

    [Fact]
    public void TheFourNewChannels_AreDistinctAndDoNotDisturbTheR12R13Rules()
    {
        var all = Sts2DecorEmitSuppress.Channels.IntentBobPosition
                  | Sts2DecorEmitSuppress.Channels.OrbSpinRotation
                  | Sts2DecorEmitSuppress.Channels.IntentGlyphTexture
                  | Sts2DecorEmitSuppress.Channels.EndTurnGlowPulse;
        // Four distinct bits, none of them overlapping the three older folds.
        Assert.Equal(4, System.Numerics.BitOperations.PopCount((uint)all));
        Assert.Equal(Sts2DecorEmitSuppress.Channels.None, all & Sts2DecorEmitSuppress.Channels.TopBarRotation);
        Assert.Equal(Sts2DecorEmitSuppress.Channels.None, all & Sts2DecorEmitSuppress.Channels.ProceedGlowAlpha);
        Assert.Equal(Sts2DecorEmitSuppress.Channels.None, all & Sts2DecorEmitSuppress.Channels.MapPointPulseScale);

        // The R12/R13 rules still resolve exactly as before (the static merge must not have disturbed them).
        Assert.Equal(
            Sts2DecorEmitSuppress.Channels.TopBarRotation,
            Sts2DecorEmitSuppress.Lookup(Sts2TopBarFold.DeckButtonScene, Sts2TopBarFold.IconRelPath));
        Assert.Equal(
            Sts2DecorEmitSuppress.Channels.ProceedGlowAlpha,
            Sts2DecorEmitSuppress.Lookup(Sts2ProceedGlow.SceneFile, Sts2ProceedGlow.OutlineRelPath));
        Assert.Equal(
            Sts2DecorEmitSuppress.Channels.MapPointPulseScale,
            Sts2DecorEmitSuppress.Lookup(Sts2MapPointPulse.SceneFile, Sts2MapPointPulse.IconContainerRelPath));
    }

    [Fact]
    public void TheDeepestNewRule_FitsInsideMaxRelDepth()
    {
        // `Layers/RotationLayers/Layer2` is 3 segments — exactly MaxRelDepth. If the cap ever tightened, the orb
        // fold would silently stop matching (ChildRelPath returns null and the node reads as unwatched), so assert
        // the composer actually reaches it the way the watcher's descent does.
        var layers = Sts2DecorEmitSuppress.ChildRelPath(Sts2DecorEmitSuppress.RootRelPath, "Layers");
        var rotationLayers = Sts2DecorEmitSuppress.ChildRelPath(layers, "RotationLayers");
        var layer2 = Sts2DecorEmitSuppress.ChildRelPath(rotationLayers, "Layer2");
        Assert.Equal("Layers/RotationLayers/Layer2", layer2);
        Assert.Null(Sts2DecorEmitSuppress.ChildRelPath(layer2, "Sprite"));
    }
}
