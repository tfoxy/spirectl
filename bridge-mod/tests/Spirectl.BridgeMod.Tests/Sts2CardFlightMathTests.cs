using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// WS-3 discard→draw shuffle CARD FLIGHT — the pure algebra the producer and the browser client both replay
// (Sts2CardFlightMath). Every expectation here is written INDEPENDENTLY of the implementation: the reference curve
// and integrator below are spelled out from the animation's own definition, so a change to either side that breaks
// parity fails.
public sealed class Sts2CardFlightMathTests
{
    private static Sts2CardFlightMath.Point2 P(double x, double y) => new(x, y);

    // The quadratic Bezier, written out longhand: (1-t)^2*v0 + 2*(1-t)*t*c0 + t^2*v1.
    private static Sts2CardFlightMath.Point2 ReferenceBezier(
        Sts2CardFlightMath.Point2 v0,
        Sts2CardFlightMath.Point2 v1,
        Sts2CardFlightMath.Point2 c0,
        double t)
        => P(
            (Math.Pow(1 - t, 2) * v0.X) + (2 * (1 - t) * t * c0.X) + (Math.Pow(t, 2) * v1.X),
            (Math.Pow(1 - t, 2) * v0.Y) + (2 * (1 - t) * t * c0.Y) + (Math.Pow(t, 2) * v1.Y));

    [Fact]
    public void Bezier_MatchesTheReferenceQuadraticAcrossTheCurve()
    {
        var v0 = P(240, 900);
        var v1 = P(1680, 180);
        var c0 = P(960, 40);
        for (var i = 0; i <= 40; i++)
        {
            var t = i / 40.0;
            var expected = ReferenceBezier(v0, v1, c0, t);
            var actual = Sts2CardFlightMath.Bezier(v0, v1, c0, t);
            Assert.Equal(expected.X, actual.X, 9);
            Assert.Equal(expected.Y, actual.Y, 9);
        }
    }

    [Fact]
    public void Bezier_EndpointsAreExact()
    {
        var v0 = P(-17.5, 3.25);
        var v1 = P(881, -4);
        var c0 = P(400, 400);
        Assert.Equal(v0, Sts2CardFlightMath.Bezier(v0, v1, c0, 0));
        Assert.Equal(v1.X, Sts2CardFlightMath.Bezier(v0, v1, c0, 1).X, 9);
        Assert.Equal(v1.Y, Sts2CardFlightMath.Bezier(v0, v1, c0, 1).Y, 9);
    }

    [Fact]
    public void Bezier_IsNotClamped_LikeTheHost()
    {
        // The host's arc can step slightly past t=1 before its exit test, and does not clamp there. A clamped
        // implementation would return v1 here; the real curve overshoots.
        var v0 = P(0, 0);
        var v1 = P(100, 0);
        var c0 = P(50, 0);
        Assert.True(Sts2CardFlightMath.Bezier(v0, v1, c0, 1.1).X > 100);
    }

    // The arc's up/down decision: the drawn control-point offset is folded in ONLY on the downward branch, which is
    // the detail most likely to be mis-stated.
    [Fact]
    public void ArcDir_OffsetOnlyAppliesBelowTheHalfwayLine()
    {
        Assert.Equal(-500.0, Sts2CardFlightMath.ArcDir(endY: 100, controlPointOffset: 321));
        Assert.Equal(-500.0, Sts2CardFlightMath.ArcDir(endY: 539.9, controlPointOffset: -300));
        Assert.Equal(500.0 + 321, Sts2CardFlightMath.ArcDir(endY: 540, controlPointOffset: 321));
        Assert.Equal(500.0 - 300, Sts2CardFlightMath.ArcDir(endY: 900, controlPointOffset: -300));
    }

    // The control point is the anchors' midpoint, lifted off the straight line by the arc.
    [Fact]
    public void ControlPoint_IsTheMidpointLiftedByArcDir()
    {
        var c = Sts2CardFlightMath.ControlPoint(P(100, 800), P(300, 400), arcDir: 250);
        Assert.Equal(200, c.X, 9);
        Assert.Equal(600 - 250, c.Y, 9);
    }

    [Fact]
    public void ControlPoint_NegativeArcDirPushesTheCurveDown()
    {
        var c = Sts2CardFlightMath.ControlPoint(P(0, 0), P(0, 0), arcDir: -500);
        Assert.Equal(500, c.Y, 9);
    }

    // The two integrator steps: the arc phase accelerates, the landing phase holds its speed.
    [Fact]
    public void Integrator_Phase1Accelerates_Phase2HoldsSpeed()
    {
        var (t1, s1) = Sts2CardFlightMath.StepAccelerating(time: 0, speed: 1.2, accel: 2.25, dt: 1 / 60.0);
        Assert.Equal(1.2 / 60.0, t1, 12);
        Assert.Equal(1.2 + (2.25 / 60.0), s1, 12);

        var t2 = Sts2CardFlightMath.StepConstant(time: t1, speed: s1, dt: 1 / 60.0);
        Assert.Equal(t1 + (s1 / 60.0), t2, 12);
    }

    // The whole reason the animation is reproducible: `time` is driven by dt, not by frame count, so a 9.4fps client
    // (the traced shuffle) and a 60fps client fly the SAME arc — the coarse one just samples it less often and
    // arrives at each parameter at very nearly the same wall clock.
    //
    // NOT bit-identical, and deliberately not asserted as such: this is forward Euler over an accelerating speed, so
    // a coarse step under-integrates `time` by ~accel*dt*T/2 and finishes marginally late. Measured here: 0.733s at
    // 60fps vs 0.851s at 9.4fps — i.e. ~1.1 coarse frames of drift over a 0.75s phase, invisible next to the
    // ~10x latency win. The bound is TWO coarse frames (one for that drift, one for quantization of the exit test).
    // The game itself has the same property: its own dt is whatever the game frame took.
    [Fact]
    public void Integrator_PhaseOneLengthIsNearlyFrameRateIndependent()
    {
        const double speed0 = 1.15;
        const double accel = 2.2;
        const double duration = 1.4;

        static double ElapsedForPhaseOne(double dt, double speed0, double accel, double duration)
        {
            var time = 0.0;
            var speed = speed0;
            var elapsed = 0.0;
            while (time / duration <= 1.0 && elapsed < 30.0)
            {
                (time, speed) = Sts2CardFlightMath.StepAccelerating(time, speed, accel, dt);
                elapsed += dt;
            }

            return elapsed;
        }

        var at60 = ElapsedForPhaseOne(1 / 60.0, speed0, accel, duration);
        var at9 = ElapsedForPhaseOne(1 / 9.4, speed0, accel, duration);
        Assert.True(Math.Abs(at60 - at9) <= 2 / 9.4, $"60fps={at60:F4}s vs 9.4fps={at9:F4}s");
        // And the coarse one is never EARLY (forward Euler under-integrates an accelerating speed).
        Assert.True(at9 >= at60 - (1 / 60.0), $"60fps={at60:F4}s vs 9.4fps={at9:F4}s");
    }

    // `Rotation = (lookAhead - here).Angle() + PI/2`, and `Vector2.Angle()` is `Mathf.Atan2(Y, X)`.
    [Fact]
    public void Rotation_IsTheTangentPlusAQuarterTurn()
    {
        // Moving straight right (+x): atan2(0, 1) = 0 → PI/2.
        Assert.Equal(Math.PI / 2, Sts2CardFlightMath.RotationFrom(P(0, 0), P(10, 0), fallback: 0), 12);
        // Moving straight down (+y in Godot screen space): atan2(1, 0) = PI/2 → PI.
        Assert.Equal(Math.PI, Sts2CardFlightMath.RotationFrom(P(0, 0), P(0, 10), fallback: 0), 12);
    }

    [Fact]
    public void Rotation_DegenerateTangentUsesTheFallback_NeverNaN()
    {
        var r = Sts2CardFlightMath.RotationFrom(P(5, 5), P(5, 5), fallback: 1.25);
        Assert.Equal(1.25, r, 12);
        Assert.False(double.IsNaN(r));
    }

    // The landing pop's scale channel is an UNCLAMPED lerp 0.1 → -0.1 floored at 0, so past progress 0.5 it is
    // pinned at 0 and the card is gone.
    [Fact]
    public void PopScale_StartsAtATenth_HitsZeroAtHalfway_AndStaysThere()
    {
        Assert.Equal(0.1, Sts2CardFlightMath.PopScale(0), 12);
        Assert.Equal(0.05, Sts2CardFlightMath.PopScale(0.25), 12);
        Assert.Equal(0.0, Sts2CardFlightMath.PopScale(0.5), 12);
        Assert.Equal(0.0, Sts2CardFlightMath.PopScale(0.9), 12);
        Assert.Equal(0.0, Sts2CardFlightMath.PopScale(5), 12);
    }

    // The window bound must actually COVER the animation for every parameter draw that can occur, or a flight
    // would resume streaming (and snap) before it finished. This brute-forces that whole parameter box.
    [Fact]
    public void SuppressWindow_CoversTheRealLifetimeAcrossTheWholeRngBox()
    {
        foreach (var duration in new[] { 1.0, 1.25, 1.5, 1.75 })
        foreach (var speed0 in new[] { 1.1, 1.175, 1.25 })
        foreach (var accel in new[] { 2.0, 2.25, 2.5 })
        {
            const double dt = 1 / 60.0;
            var time = 0.0;
            var speed = speed0;
            var elapsed = 0.0;
            while (time / duration <= 1.0)
            {
                (time, speed) = Sts2CardFlightMath.StepAccelerating(time, speed, accel, dt);
                elapsed += dt;
            }

            time = 0.0;
            while (time / duration <= 1.0)
            {
                time = Sts2CardFlightMath.StepConstant(time, speed, dt);
                elapsed += dt;
            }

            var lifetimeMs = (elapsed + Sts2CardFlightMath.FadeOutSeconds) * 1000.0;
            var windowMs = Sts2CardFlightMath.SuppressWindowMs(duration);
            Assert.True(
                windowMs >= lifetimeMs,
                $"duration={duration} speed0={speed0} accel={accel}: window {windowMs}ms < lifetime {lifetimeMs:F1}ms");
        }
    }

    [Fact]
    public void SuppressWindow_IsTwiceDurationPlusTheFade()
    {
        Assert.Equal(2800, Sts2CardFlightMath.SuppressWindowMs(1.0));
        Assert.Equal(4300, Sts2CardFlightMath.SuppressWindowMs(1.75));
    }

    [Fact]
    public void SuppressWindow_IsCappedByTheStuckWindowBackstop()
    {
        // A bogus captured duration must not freeze a subtree indefinitely.
        Assert.Equal(Sts2CardFlightMath.SuppressCapMs, Sts2CardFlightMath.SuppressWindowMs(1_000_000));
        Assert.Equal(Sts2CardFlightMath.SuppressCapMs, Sts2CardFlightMath.SuppressWindowMs(double.MaxValue));
        // The slowest real flight is still comfortably UNDER the cap, so the cap never clips one.
        Assert.True(Sts2CardFlightMath.SuppressWindowMs(1.75) < Sts2CardFlightMath.SuppressCapMs);
    }

    [Fact]
    public void SuppressWindow_OpensNoWindowForADegenerateDuration()
    {
        Assert.Equal(0, Sts2CardFlightMath.SuppressWindowMs(0));
        Assert.Equal(0, Sts2CardFlightMath.SuppressWindowMs(-1));
        Assert.Equal(0, Sts2CardFlightMath.SuppressWindowMs(double.NaN));
        Assert.Equal(0, Sts2CardFlightMath.SuppressWindowMs(double.PositiveInfinity));
    }

    [Fact]
    public void IsReplayable_RejectsEveryDegenerateDraw()
    {
        Assert.True(Sts2CardFlightMath.IsReplayable(speed0: 1.1, accel: 2.0, duration: 1.0, scale0: 1.0));
        Assert.False(Sts2CardFlightMath.IsReplayable(0, 2.0, 1.0, 1.0)); // speed 0 ⇒ `time` never advances
        Assert.False(Sts2CardFlightMath.IsReplayable(1.1, 2.0, 0, 1.0)); // duration 0 ⇒ divide by zero
        Assert.False(Sts2CardFlightMath.IsReplayable(1.1, 2.0, 1.0, 0)); // scale0 0 ⇒ phase-2 divide by zero
        Assert.False(Sts2CardFlightMath.IsReplayable(double.NaN, 2.0, 1.0, 1.0));
        Assert.False(Sts2CardFlightMath.IsReplayable(1.1, double.NaN, 1.0, 1.0));
        Assert.False(Sts2CardFlightMath.IsReplayable(1.1, 2.0, double.PositiveInfinity, 1.0));
    }

    // End-to-end shape check: the flight starts AT the start pile, ends AT the end pile, and the arc actually bows
    // to the control side (a straight lerp would not).
    [Fact]
    public void FullFlight_StartsAtTheStartPile_EndsAtTheEndPile_AndBows()
    {
        var start = P(300, 880);
        var end = P(1620, 880);
        var arcDir = Sts2CardFlightMath.ArcDir(end.Y, controlPointOffset: 100); // 880 >= 540 ⇒ 600
        var control = Sts2CardFlightMath.ControlPoint(start, end, arcDir);
        Assert.Equal(880 - 600, control.Y, 9);

        Assert.Equal(start, Sts2CardFlightMath.Bezier(start, end, control, 0));
        var mid = Sts2CardFlightMath.Bezier(start, end, control, 0.5);
        Assert.True(mid.Y < start.Y, "a positive arcDir must bow the flight UPWARD (smaller Y)");
        Assert.Equal(960, mid.X, 9);
        var landed = Sts2CardFlightMath.Bezier(start, end, control, 1);
        Assert.Equal(end.X, landed.X, 6);
        Assert.Equal(end.Y, landed.Y, 6);
    }
}
