using System;
using Spirectl.Sts2.Core.SceneInspection;
using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// R12b producer MAP-POINT PULSE FOLD (Sts2MapPointPulse) — a pure, Godot-free exercise of the three things the
// watcher leans on: the scene-identity rule, the pulse's gate algebra, and the pivot-anchored scale-undo.
//
// The scale-undo is the load-bearing part and is checked against Godot's own transform algebra: a Godot Control
// with pivot p, rotation R and uniform scale k has transform `M(k) = T(pos + p) · R · S(k) · T(-p)`, so the tests
// BUILD M(k) exactly the way Godot does, pin it, and assert the result equals M(1) built the same way. That pins
// the property that actually matters — the pin lands on the node's RESTING pose, not merely on "basis / k".
public sealed class Sts2MapPointPulseTests
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

    // ---- scene identity ------------------------------------------------------------------------------------

    [Fact]
    public void IconContainer_IsTheOnlyMatchedNode()
    {
        Assert.True(Sts2MapPointPulse.IsIconContainer(Sts2MapPointPulse.SceneFile, "IconContainer"));
        // Siblings + descendants of the pulsing container are NOT the animated node (the game scales the container;
        // the icons ride it), and neither is the same path in another scene.
        Assert.False(Sts2MapPointPulse.IsIconContainer(Sts2MapPointPulse.SceneFile, "IconContainer/Icon"));
        Assert.False(Sts2MapPointPulse.IsIconContainer(Sts2MapPointPulse.SceneFile, "."));
        Assert.False(Sts2MapPointPulse.IsIconContainer("res://scenes/ui/boss_map_point.tscn", "IconContainer"));
        Assert.False(Sts2MapPointPulse.IsIconContainer(null, "IconContainer"));
        Assert.False(Sts2MapPointPulse.IsIconContainer(Sts2MapPointPulse.SceneFile, null));
    }

    [Fact]
    public void SuppressionTable_RoutesTheIconContainerToTheFoldChannel()
    {
        // The fold rides Sts2DecorEmitSuppress's table ONLY to reuse the scene-identity scope resolution; the
        // channel it resolves to is its own, so no other fold's pin ever touches this node.
        var channels = Sts2DecorEmitSuppress.Lookup(Sts2MapPointPulse.SceneFile, Sts2MapPointPulse.IconContainerRelPath);
        Assert.Equal(Sts2DecorEmitSuppress.Channels.MapPointPulseScale, channels);
        Assert.Equal(Sts2DecorEmitSuppress.Channels.None, channels & Sts2DecorEmitSuppress.Channels.TopBarRotation);
        Assert.Equal(Sts2DecorEmitSuppress.Channels.None, channels & Sts2DecorEmitSuppress.Channels.ProceedGlowAlpha);
        Assert.True(Sts2DecorEmitSuppress.IsWatchedScene(Sts2MapPointPulse.SceneFile));
    }

    // ---- the pulse gate ------------------------------------------------------------------------------------

    [Theory]
    // Enabled AND not focused AND input allowed — the condition under which a map point breathes.
    [InlineData(true, false, true, true)]    // travelable, unhovered, map interactive → pulsing
    [InlineData(false, false, true, false)]  // not travelable (the ~57 of 61 points that just sit there)
    [InlineData(true, true, true, false)]    // hovered/controller-focused → the game lerps it back to rest instead
    [InlineData(true, false, false, false)]  // mid-travel or drawing on the map → input not allowed
    [InlineData(false, true, false, false)]
    public void IsPulsing_MatchesThePulseGates(bool enabled, bool focused, bool inputAllowed, bool expected)
        => Assert.Equal(expected, Sts2MapPointPulse.IsPulsing(enabled, focused, inputAllowed));

    // ---- the scale undo ------------------------------------------------------------------------------------

    [Theory]
    // The pulse's real extremes (Sin·0.25 + 1.2 ∈ [0.95, 1.45]) plus the midpoint, against the per-point random
    // tilt the map hands out (SetAngle(NextGaussianFloat(0, 8)) degrees) and the authored 56x56 centre pivot.
    [InlineData(0.95)]
    [InlineData(1.2)]
    [InlineData(1.45)]
    public void PinRestScale_RestoresTheRestingTransform(double k)
    {
        const double rotation = 0.0065023587; // a real observed IconContainer tilt (~0.37 deg), off the live wire
        const double pivot = 28.0;            // 56x56 container, pivot at its centre

        var live = ControlTransform(rotation, k, pivot, pivot);
        var pinned = Sts2MapPointPulse.PinRestScale(live, k, pivot, pivot);

        AssertTransformEqual(ControlTransform(rotation, Sts2MapPointPulse.RestScale, pivot, pivot), pinned);
    }

    [Fact]
    public void PinRestScale_KeepsRotationAndPosition()
    {
        // The tilt is a one-off random value per map point and the position is layout — neither is recoverable
        // analytically, so both must survive the pin untouched. (Only the uniform scale is ours to undo.)
        const double rotation = 0.21;
        const double k = 1.37;

        var pinned = Sts2MapPointPulse.PinRestScale(ControlTransform(rotation, k, 28, 28, 445.4, 971.8), k, 28, 28);

        AssertTransformEqual(ControlTransform(rotation, 1.0, 28, 28, 445.4, 971.8), pinned);
        // Rotation survived: the pinned basis is a pure rotation of `rotation` radians (unit length, right angle).
        Assert.Equal(Math.Cos(rotation), pinned!.XAxis.X, Tolerance);
        Assert.Equal(Math.Sin(rotation), pinned.XAxis.Y, Tolerance);
    }

    [Fact]
    public void PinRestScale_IsARightMultiplication_SoAGlobalTransformPinsToo()
    {
        // The undo is `live · T(p)·S(1/k)·T(-p)`, which is independent of anything to the LEFT — so pinning a
        // GLOBAL transform (ancestor · M(k)) yields ancestor · M(1). This is why the formula needs no separate
        // global-mode branch (the watcher still gates the fold to local mode, for a client-composition reason).
        const double k = 1.45;
        const double pivot = 28.0;
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

        var pinnedGlobal = Sts2MapPointPulse.PinRestScale(
            Compose(ControlTransform(0.05, k, pivot, pivot)), k, pivot, pivot);

        AssertTransformEqual(Compose(ControlTransform(0.05, 1.0, pivot, pivot)), pinnedGlobal);
    }

    [Fact]
    public void PinRestScale_AtRest_ReturnsTheSameInstance()
    {
        // The steady state for most map points: no pulse, so no allocation and a byte-identical wire.
        var live = ControlTransform(0.01, 1.0, 28, 28);
        Assert.Same(live, Sts2MapPointPulse.PinRestScale(live, 1.0, 28, 28));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void PinRestScale_DegenerateScale_LeavesTheTransformAlone(double k)
    {
        var live = ControlTransform(0.01, 1.0, 28, 28);
        Assert.Same(live, Sts2MapPointPulse.PinRestScale(live, k, 28, 28));
    }

    [Fact]
    public void PinRestScale_NullTransform_StaysNull()
        => Assert.Null(Sts2MapPointPulse.PinRestScale(null, 1.45, 28, 28));

    [Fact]
    public void PulseConstants_MatchThePulseOnScreen()
    {
        // A uniform scale of `Sin(t) * 0.25 + 1.2`, with t advancing at 4 rad/s. These constants are what the
        // CLIENT replays, so pin them here: a drift between the producer's documented loop and the client's
        // keyframes is otherwise invisible.
        Assert.Equal(0.25, Sts2MapPointPulse.ScaleAmount, Tolerance);
        Assert.Equal(1.2, Sts2MapPointPulse.ScaleBase, Tolerance);
        Assert.Equal(4.0, Sts2MapPointPulse.RateRadPerSec, Tolerance);
        Assert.Equal(0.95, Sts2MapPointPulse.ScaleBase - Sts2MapPointPulse.ScaleAmount, Tolerance);
        Assert.Equal(1.45, Sts2MapPointPulse.ScaleBase + Sts2MapPointPulse.ScaleAmount, Tolerance);
        // Period 2*pi / 4 = 1570.796...ms — the number the client's keyframes must use.
        Assert.Equal(1570.7963267948966, 2000.0 * Math.PI / Sts2MapPointPulse.RateRadPerSec, 1e-9);
        // The rest the pin lands on is the authored resting scale, NOT a sampled mid-pulse value.
        Assert.Equal(1.0, Sts2MapPointPulse.RestScale, Tolerance);
    }
}
