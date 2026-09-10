using System;
using Spirectl.Sts2.Core.SceneInspection;
using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// R13 producer TOP-BAR ICON FOLD (Sts2TopBarFold) — a pure, Godot-free exercise of the three things the watcher
// leans on: the scene→loop-token map, the open-state gate, and the pivot-anchored rotation-undo.
//
// The rotation-undo is the load-bearing part and is checked against Godot's own transform algebra: a Godot
// Control with pivot p, rotation θ and uniform scale k has transform `M(θ) = T(pos + p) · R(θ) · S(k) · T(-p)`, so
// the tests BUILD M(θ) exactly the way Godot does, pin it, and assert the result equals M(0) built the same way.
// That pins the property that actually matters — and, with a NON-ZERO position, it is the direct regression test for
// the WS-D bug this fold replaces: pinning the whole first-emitted transform froze the deck + settings icons at a
// PRE-LAYOUT pose (mispositioned in the browser forever), where dividing out only the rotation cannot move a node.
public sealed class Sts2TopBarFoldTests
{
    private const double Tolerance = 1e-9;

    // Godot's Control transform: rotate + uniformly scale about the pivot, then place at `pos`.
    private static RuntimeSceneTransform2DSnapshot ControlTransform(
        double rotationRad,
        double scale,
        double pivotX,
        double pivotY,
        double posX = 0,
        double posY = 0)
    {
        var cos = Math.Cos(rotationRad);
        var sin = Math.Sin(rotationRad);
        // basis = R · S(k)
        var ax = cos * scale;
        var ay = sin * scale;
        var bx = -sin * scale;
        var by = cos * scale;
        // origin = pos + p - basis·p
        var ox = posX + pivotX - (ax * pivotX + bx * pivotY);
        var oy = posY + pivotY - (ay * pivotX + by * pivotY);
        return new RuntimeSceneTransform2DSnapshot(
            new RuntimeSceneVector2Snapshot(ax, ay),
            new RuntimeSceneVector2Snapshot(bx, by),
            new RuntimeSceneVector2Snapshot(ox, oy));
    }

    private static void AssertTransformEqual(
        RuntimeSceneTransform2DSnapshot expected,
        RuntimeSceneTransform2DSnapshot? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.XAxis.X, actual!.XAxis.X, Tolerance);
        Assert.Equal(expected.XAxis.Y, actual.XAxis.Y, Tolerance);
        Assert.Equal(expected.YAxis.X, actual.YAxis.X, Tolerance);
        Assert.Equal(expected.YAxis.Y, actual.YAxis.Y, Tolerance);
        Assert.Equal(expected.Origin.X, actual.Origin.X, Tolerance);
        Assert.Equal(expected.Origin.Y, actual.Origin.Y, Tolerance);
    }

    // ---- scene identity + the loop vocabulary --------------------------------------------------------------

    [Theory]
    // The tokens are a WIRE CONTRACT: the client's replay keys off these exact strings, so pin them verbatim.
    [InlineData(Sts2TopBarFold.DeckButtonScene, "topBarDeckRock")]
    [InlineData(Sts2TopBarFold.MapButtonScene, "topBarMapRock")]
    [InlineData(Sts2TopBarFold.SettingsButtonScene, "topBarSpin")]
    public void LoopAnimFor_NamesTheButtonsLoop(string scene, string expectedToken)
    {
        Assert.Equal(expectedToken, Sts2TopBarFold.LoopAnimFor(scene));
        // All three buttons share the scene-relative path `Control/Icon` while running three DIFFERENT loops — the
        // whole reason the PRODUCER has to name the loop instead of a path-keyed client binding guessing it.
        Assert.True(Sts2TopBarFold.IsIcon(scene, "Control/Icon"));
        Assert.Equal("Control/Icon", Sts2TopBarFold.IconRelPath);
    }

    [Theory]
    // Siblings inside a watched button scene, and the same path in an unwatched scene.
    [InlineData(Sts2TopBarFold.DeckButtonScene, ".")]
    [InlineData(Sts2TopBarFold.DeckButtonScene, "Control")]
    [InlineData(Sts2TopBarFold.DeckButtonScene, "DeckCardCount")]
    [InlineData(Sts2TopBarFold.SettingsButtonScene, "Control/Icon/Deeper")]
    [InlineData("res://scenes/ui/top_bar.tscn", "Control/Icon")]
    [InlineData(null, "Control/Icon")]
    [InlineData(Sts2TopBarFold.MapButtonScene, null)]
    public void IsIcon_MatchesTheAnimatedNodeOnly(string? scene, string? relPath)
        => Assert.False(Sts2TopBarFold.IsIcon(scene, relPath));

    [Fact]
    public void LoopAnimFor_UnwatchedScene_IsNull()
    {
        Assert.Null(Sts2TopBarFold.LoopAnimFor("res://scenes/ui/top_bar.tscn"));
        Assert.Null(Sts2TopBarFold.LoopAnimFor(null));
    }

    [Fact]
    public void SuppressionTable_RoutesEachIconToTheFoldChannel()
    {
        // The fold rides Sts2DecorEmitSuppress's table only to reuse the scene-identity scope resolution; the channel
        // it resolves to is its own, so no other fold's pin can ever touch these nodes.
        foreach (var scene in new[]
                 {
                     Sts2TopBarFold.DeckButtonScene,
                     Sts2TopBarFold.MapButtonScene,
                     Sts2TopBarFold.SettingsButtonScene,
                 })
        {
            var channels = Sts2DecorEmitSuppress.Lookup(scene, Sts2TopBarFold.IconRelPath);
            Assert.Equal(Sts2DecorEmitSuppress.Channels.TopBarRotation, channels);
            Assert.Equal(Sts2DecorEmitSuppress.Channels.None, channels & Sts2DecorEmitSuppress.Channels.ProceedGlowAlpha);
            Assert.Equal(Sts2DecorEmitSuppress.Channels.None, channels & Sts2DecorEmitSuppress.Channels.MapPointPulseScale);
            Assert.True(Sts2DecorEmitSuppress.IsWatchedScene(scene));
        }
    }

    // ---- the open-state gate -------------------------------------------------------------------------------

    [Theory]
    // Each loop runs exactly while its button's screen is open. Closed ⇒ nothing is pinned and nothing is named,
    // so the hover/press/unhover one-shots stream live.
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void IsAnimating_MatchesTheOpenStateGate(bool isScreenOpen, bool expected)
        => Assert.Equal(expected, Sts2TopBarFold.IsAnimating(isScreenOpen));

    [Theory]
    // WHICH member yields the open-state is per-button (live-validated 2026-08-05): on the deck and settings
    // buttons the `IsScreenOpen` script VARIABLE tracks the screen exactly. On the MAP button it is never updated
    // and reads false even while the map is open, so its truthful gate is the registered `IsOpen()` METHOD. A wrong
    // row here silently freezes that icon at rest with no token — exactly the live bug this table was added for.
    [InlineData(Sts2TopBarFold.DeckButtonScene, false)]
    [InlineData(Sts2TopBarFold.SettingsButtonScene, false)]
    [InlineData(Sts2TopBarFold.MapButtonScene, true)]
    [InlineData("res://scenes/ui/other.tscn", false)]
    [InlineData(null, false)]
    public void GateIsOpenMethod_SelectsTheLiveGatePerButton(string? scene, bool expected)
        => Assert.Equal(expected, Sts2TopBarFold.GateIsOpenMethod(scene));

    // ---- the rotation undo ---------------------------------------------------------------------------------

    [Theory]
    // The deck/map rock's real extremes (±0.12 rad) plus a mid-sweep sample and the hover angle (-π/15) the
    // press-down one-shot rotates away from.
    [InlineData(0.12)]
    [InlineData(-0.12)]
    [InlineData(0.0731)]
    [InlineData(-0.20943951023931953)]
    public void PinRestRotation_RestoresTheRestingTransform(double theta)
    {
        const double pivot = 32.0; // the icon's 64x64 box, pivot at its centre

        var live = ControlTransform(theta, 1.0, pivot, pivot);
        var pinned = Sts2TopBarFold.PinRestRotation(live, theta, pivot, pivot);

        AssertTransformEqual(ControlTransform(Sts2TopBarFold.RestRotationRad, 1.0, pivot, pivot), pinned);
    }

    [Fact]
    public void PinRestRotation_KeepsPositionAndScale_TheMispositioningRegression()
    {
        // THE regression case. WS-D pinned the whole first-emitted transform, so a Control whose deferred layout had
        // not settled (the CENTER-anchored deck icon, the GROW_BOTH settings icon at its min-size clamp) was frozen
        // at a pre-layout POSITION forever. Dividing out only the rotation must leave a NON-ZERO position and a
        // hover scale-up completely untouched — whatever the icon's current angle.
        const double theta = 0.11734;
        const double hoverScale = 1.1; // the icon's hover scale-up
        const double posX = 1662.5;    // a laid-out top-bar slot, i.e. not the pre-layout origin
        const double posY = 41.25;
        const double pivot = 32.0;

        var pinned = Sts2TopBarFold.PinRestRotation(
            ControlTransform(theta, hoverScale, pivot, pivot, posX, posY), theta, pivot, pivot);

        AssertTransformEqual(ControlTransform(0.0, hoverScale, pivot, pivot, posX, posY), pinned);
        // Position survived exactly: at rest the origin is `pos + p - S(k)·p`, i.e. the layout position is recoverable.
        Assert.Equal(posX + pivot - hoverScale * pivot, pinned!.Origin.X, Tolerance);
        Assert.Equal(posY + pivot - hoverScale * pivot, pinned.Origin.Y, Tolerance);
        // Scale survived: the pinned basis is a pure uniform scale (no residual rotation).
        Assert.Equal(hoverScale, pinned.XAxis.X, Tolerance);
        Assert.Equal(0.0, pinned.XAxis.Y, Tolerance);
        Assert.Equal(0.0, pinned.YAxis.X, Tolerance);
        Assert.Equal(hoverScale, pinned.YAxis.Y, Tolerance);
    }

    [Theory]
    // The settings spin is an UNBOUNDED accumulator (`_icon.Rotation += (float)delta`, 1 rad/s), so after ten
    // minutes of an open settings screen θ is ~600 rad and after an hour ~3600. IEEERemainder folds it back into
    // [-π, π] before the sin/cos, so the undo stays exact instead of losing precision to a huge Cos argument.
    [InlineData(600.0)]
    [InlineData(3600.5)]
    [InlineData(-1234.567)]
    [InlineData(2.0 * Math.PI * 100.0 + 0.4)]
    public void PinRestRotation_LargeAngles_StillLandOnRest(double theta)
    {
        const double pivot = 32.0;

        var pinned = Sts2TopBarFold.PinRestRotation(
            ControlTransform(theta, 1.0, pivot, pivot, 1662.5, 41.25), theta, pivot, pivot);

        AssertTransformEqual(ControlTransform(0.0, 1.0, pivot, pivot, 1662.5, 41.25), pinned);
    }

    [Fact]
    public void PinRestRotation_IsARightMultiplication_SoAGlobalTransformPinsToo()
    {
        // The undo is `live · T(p)·R(-θ)·T(-p)`, which is independent of anything to the LEFT — so pinning a GLOBAL
        // transform (ancestor · M(θ)) yields ancestor · M(0). This is why the formula needs no separate global-mode
        // branch (the watcher still gates the fold to local mode, for a client-composition reason).
        const double theta = 0.12;
        const double pivot = 32.0;
        // An arbitrary non-trivial ancestor: scale 0.8, rotate 0.3, translate (120, -40).
        var (ac, as_) = (Math.Cos(0.3) * 0.8, Math.Sin(0.3) * 0.8);
        RuntimeSceneTransform2DSnapshot Compose(RuntimeSceneTransform2DSnapshot m)
        {
            double AX(double x, double y) => ac * x + -as_ * y;
            double AY(double x, double y) => as_ * x + ac * y;
            return new RuntimeSceneTransform2DSnapshot(
                new RuntimeSceneVector2Snapshot(AX(m.XAxis.X, m.XAxis.Y), AY(m.XAxis.X, m.XAxis.Y)),
                new RuntimeSceneVector2Snapshot(AX(m.YAxis.X, m.YAxis.Y), AY(m.YAxis.X, m.YAxis.Y)),
                new RuntimeSceneVector2Snapshot(AX(m.Origin.X, m.Origin.Y) + 120, AY(m.Origin.X, m.Origin.Y) - 40));
        }

        var pinnedGlobal = Sts2TopBarFold.PinRestRotation(
            Compose(ControlTransform(theta, 1.1, pivot, pivot, 1662.5, 41.25)), theta, pivot, pivot);

        AssertTransformEqual(Compose(ControlTransform(0.0, 1.1, pivot, pivot, 1662.5, 41.25)), pinnedGlobal);
    }

    [Fact]
    public void PinRestRotation_AtRest_ReturnsTheSameInstance()
    {
        // The steady state whenever no top-bar screen is open: nothing to undo, no allocation, byte-identical wire.
        var live = ControlTransform(0.0, 1.0, 32, 32, 1662.5, 41.25);
        Assert.Same(live, Sts2TopBarFold.PinRestRotation(live, 0.0, 32, 32));
        // A whole number of revolutions is also rest (IEEERemainder folds it to 0).
        Assert.Same(live, Sts2TopBarFold.PinRestRotation(live, 2.0 * Math.PI * 7.0, 32, 32));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void PinRestRotation_DegenerateRotation_LeavesTheTransformAlone(double theta)
    {
        var live = ControlTransform(0.0, 1.0, 32, 32);
        Assert.Same(live, Sts2TopBarFold.PinRestRotation(live, theta, 32, 32));
    }

    [Fact]
    public void PinRestRotation_NullTransform_StaysNull()
        => Assert.Null(Sts2TopBarFold.PinRestRotation(null, 0.12, 32, 32));

    [Fact]
    public void LoopConstants_MatchTheMotionsOnScreen()
    {
        // These constants are what the CLIENT replays, so pin them here: a drift between the producer's documented
        // loops and the client's keyframes is otherwise invisible.

        // The deck rock: +/-0.12 rad on a sine whose argument advances at 4 rad/s.
        Assert.Equal(0.12, Sts2TopBarFold.DeckRockAmplitudeRad, Tolerance);
        Assert.Equal(4.0, Sts2TopBarFold.DeckRockRateRadPerSec, Tolerance);
        Assert.Equal(1570.7963267948966, Sts2TopBarFold.DeckRockPeriodMs, 1e-9);

        // The map rock: two 0.8s Sine/InOut legs between -/+0.12 rad, looping forever.
        Assert.Equal(0.12, Sts2TopBarFold.MapRockAmplitudeRad, Tolerance);
        Assert.Equal(800.0, Sts2TopBarFold.MapRockLegMs, Tolerance);
        Assert.Equal(1600.0, Sts2TopBarFold.MapRockPeriodMs, Tolerance);

        // The settings spin: a constant 1 rad/s, i.e. a 2*pi-second revolution.
        Assert.Equal(1.0, Sts2TopBarFold.SpinRateRadPerSec, Tolerance);
        Assert.Equal(6283.185307179586, Sts2TopBarFold.SpinPeriodMs, 1e-9);

        // The rest the pin lands on is the icons' authored resting angle (unhovering and closing the screen both
        // end at exactly 0), NOT a sampled value.
        Assert.Equal(0.0, Sts2TopBarFold.RestRotationRad, Tolerance);
    }
}
