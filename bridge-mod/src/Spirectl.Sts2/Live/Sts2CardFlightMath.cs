namespace Spirectl.Sts2.Live;

// WS-3 DISCARD→DRAW SHUFFLE CARD FLIGHT — the Godot-free half.
//
// PURE and Godot-free (like Sts2ReparentEmit / Sts2CancelledTweenResync / Sts2TweenEndpointTuples) so the whole
// curve/integrator/window algebra is unit-testable without the live host; the Godot-typed Harmony hook that reads
// the live instance (Sts2CardFlightHooks) and the watcher method that maps it into streamed space
// (Sts2RuntimeSceneWatcher.ResolveCardFlight) stay in the live-host glob.
//
// ON SCREEN. Shuffling the discard pile back into the draw pile spawns one short-lived "card soul" node per
// shuffled card, each flying its own arc from the discard anchor to the draw anchor: it accelerates along a
// quadratic curve with its long axis pointed along the flight, lands on the pile and pops down to a tenth of its
// size, then fades out over 0.8s and disappears, dragging a comet trail the whole way. Every soul draws its own
// speed / acceleration / duration / arc height when it spawns, so one shuffle is a spray of independently timed
// arcs, and the two endpoints are the piles' fixed screen anchors.
//
// WHY THIS IS REPLAYED CLIENT-SIDE RATHER THAN STREAMED. The animation is not driven by a Godot Tween, so the
// tween recorder (Sts2TweenRecorderHooks, a Tween-only hook) captures nothing from it, and it advances once per
// RENDERED frame — the mirror therefore saw it as a torrent of per-frame transform deltas: a 34.8s wire recording
// attributed 45% of the shuffle's upsert bytes to this one subtree. Because each soul's parameters are fixed at
// spawn and never change afterwards, the whole ~3s flight is exactly reproducible on the client from a handful of
// captured scalars plus the two anchors, and the host can suppress the subtree for the flight's duration instead.
//
// UNITS. The integrator's `time` is NOT wall clock: each step adds `speed * dt` with `speed` starting near 1.1 and
// growing, so it runs ~15-100% fast, and the captured `duration` is expressed in that same pseudo-time unit. This
// file keeps those units end to end so the client integrator matches the host step for step; only
// <see cref="SuppressWindowMs"/> converts to wall clock.
internal static class Sts2CardFlightMath
{
    // The final fade, once the soul is sitting on the pile: alpha to zero over 0.8s, then the node is gone.
    internal const double FadeOutSeconds = 0.8;

    // The line that decides whether the arc bows up or down: half of the game's 1080-tall design space.
    internal const double ArcDirThresholdY = 540.0;

    // The two arc magnitudes, in design-space pixels of bow (see ArcDir for which branch takes the drawn offset).
    internal const double ArcUp = -500.0;
    internal const double ArcDownBase = 500.0;

    // How far ahead along the curve the rotation samples, in pseudo-time, to get the flight's tangent.
    internal const double RotationLookAhead = 0.05;

    // The landing pop's scale endpoints. NOTE the start (0.1) is an ABSOLUTE scale and the soul's scene authors no
    // scale of its own, i.e. 1.0 — so the soul really does POP from full size to a tenth on the first frame of the
    // landing phase before shrinking away. The client reproduces that by dividing by the node's own resting scale
    // (see Sts2CardFlightHooks / Scale0).
    internal const double PopScaleFrom = 0.1;
    internal const double PopScaleTo = -0.1;

    // STUCK-WINDOW BACKSTOP, the Sts2RuntimeSceneWatcher.SuppressCapMs idea sized for THIS animation. The watcher's
    // tween cap (2000ms) is far too small here: a legal flight lasts arc + landing + 0.8s of fade, and the window
    // we open is the analytic upper bound 2*duration + 0.8 (proof below), which at the largest duration observed
    // (1.75) is 4.3s. This cap therefore only ever fires on a BOGUS captured duration (a torn/garbage field read),
    // which is exactly what a backstop is for.
    //
    // WHY 2*duration + 0.8 BOUNDS THE REAL WALL-CLOCK LIFETIME. The arc ends when the integral of `speed` reaches
    // `duration`; since `speed` starts at >=1.1 and only grows, T1 <= duration / 1.1 < duration. The landing phase
    // runs at the CONSTANT end-of-arc speed (> 1.1), so T2 = duration / speed_end < duration. The fade is exactly
    // 0.8s. Hence T1 + T2 + 0.8 < 2*duration + 0.8 for every parameter draw that can occur.
    internal const long SuppressCapMs = 5000;

    // A 2D point in whatever space the caller is working in (Godot global on the hook side, the watcher's streamed
    // space on the wire side). Deliberately NOT Godot's Vector2 so this file stays compilable without the game.
    internal readonly record struct Point2(double X, double Y)
    {
        internal static Point2 Lerp(Point2 a, Point2 b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
    }

    // The arc's up/down decision. Note the drawn offset is folded in ONLY on the downward branch — a detail easy to
    // lose when this is described as "arcDir = ±500 + ctrlOffset".
    internal static double ArcDir(double endY, double controlPointOffset)
        => endY < ArcDirThresholdY ? ArcUp : ArcDownBase + controlPointOffset;

    // The quadratic's control point: the midpoint of the two anchors, pushed off the straight line by the arc.
    internal static Point2 ControlPoint(Point2 start, Point2 end, double arcDir)
        => new(start.X + (end.X - start.X) * 0.5, start.Y + (end.Y - start.Y) * 0.5 - arcDir);

    // The quadratic Bezier the arc follows: (1-t)^2*v0 + 2(1-t)t*c0 + t^2*v1. Deliberately NOT clamped, to match
    // the host: the arc's last step may pass t=1 by a fraction of a frame before the exact end anchor is assigned.
    internal static Point2 Bezier(Point2 v0, Point2 v1, Point2 c0, double t)
    {
        var omt = 1.0 - t;
        var a = omt * omt;
        var b = 2.0 * omt * t;
        var c = t * t;
        return new Point2((a * v0.X) + (b * c0.X) + (c * v1.X), (a * v0.Y) + (b * c0.Y) + (c * v1.Y));
    }

    // One accelerating step of the arc: time advances by the current speed, and the speed itself grows. Returned as
    // a pair so the client's per-rAF integrator can be diffed against this in a test rather than asserted by eye.
    internal static (double Time, double Speed) StepAccelerating(double time, double speed, double accel, double dt)
        => (time + (speed * dt), speed + (accel * dt));

    // One step of the landing phase: time advances by the speed, which now HOLDS whatever value the arc left it at
    // and does not accelerate further.
    internal static double StepConstant(double time, double speed, double dt) => time + (speed * dt);

    // The arc's tangent, rotated a quarter turn so the card soul's long axis lies ALONG the flight. Returns
    // `fallback` for a degenerate (zero-length) tangent so a duration-0 / start==end flight can't produce NaN.
    internal static double RotationFrom(Point2 here, Point2 lookAhead, double fallback)
    {
        var dx = lookAhead.X - here.X;
        var dy = lookAhead.Y - here.Y;
        if (dx == 0.0 && dy == 0.0)
        {
            return fallback;
        }

        return Math.Atan2(dy, dx) + (Math.PI / 2.0);
    }

    // The landing pop's scale channel. The lerp is UNCLAMPED, so a progress past 1 goes negative and the max pins
    // it at 0 — i.e. the soul is fully gone before the phase's nominal end, which is what the screen shows.
    internal static double PopScale(double progress)
        => Math.Max(PopScaleFrom + ((PopScaleTo - PopScaleFrom) * progress), 0.0);

    // The streaming-suppression window this flight needs, in ms of wall clock: the analytic upper bound on the
    // node's whole visible lifetime (see SuppressCapMs for the proof), clamped by the stuck-window backstop and at
    // zero (a non-finite / negative captured duration opens NO window, so we simply keep streaming — the
    // fail-open discipline every other producer suppression uses).
    internal static long SuppressWindowMs(double durationSeconds)
    {
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0.0)
        {
            return 0;
        }

        var ms = ((2.0 * durationSeconds) + FadeOutSeconds) * 1000.0;
        return ms >= SuppressCapMs ? SuppressCapMs : (long)ms;
    }

    // Is this a flight we can replay at all? A degenerate draw (non-finite scalars, a non-positive duration, a
    // non-positive speed that would never advance `time`, or a zero resting scale we would divide by in phase 2)
    // must fall back to today's streamed behaviour rather than ship a hint the client cannot integrate.
    internal static bool IsReplayable(double speed0, double accel, double duration, double scale0)
        => double.IsFinite(speed0) && speed0 > 0.0
            && double.IsFinite(accel)
            && double.IsFinite(duration) && duration > 0.0
            && double.IsFinite(scale0) && Math.Abs(scale0) > 1e-6;

    // ---------------------------------------------------------------------------------------------------------
    // R13 HAND→DISCARD FLY (`vfx/vfx_card_fly`). On screen: the card the player just played leaves the hand, arcs
    // to the discard pile darkening and shrinking to nothing, and drags the same comet behind it. It is the SAME
    // curve and the SAME two-phase integrator as the shuffle sweep above — Bezier, ControlPoint, StepAccelerating,
    // StepConstant, RotationFrom and SuppressWindowMs are all reused unchanged — so only what actually differs
    // lives below: the arc branch's threshold, and the two extra channels the card face carries.
    //
    // What it moves is the difference that matters everywhere else: the shuffle flies a throwaway stand-in, this
    // flies the REAL card node, the one the mirror was already streaming and painting a frame ago.
    // ---------------------------------------------------------------------------------------------------------

    // The arc's up/down decision, taken against the LIVE viewport height instead of the shuffle's design-space
    // literal — pass the viewport the flight is playing in. Above the half-way line the card arcs UP by a flat
    // amount; below it, DOWN by that amount plus the drawn offset (the offset is folded into the low branch only).
    internal static double DiscardArcDir(double endY, double controlPointOffset, double viewportHeight)
        => endY < viewportHeight * 0.5 ? ArcUp : ArcDownBase + controlPointOffset;

    // Both extra channels the card face carries during the arc — it darkens to black and shrinks to a tenth — run
    // on ONE shared weight that completes at a THIRD of the arc's pseudo-time. So the card is already a small dark
    // chip for the last two thirds of the flight, and only the trail still reads as bright.
    internal const double DiscardBodyRampRate = 3.0;

    internal static double DiscardBodyRampWeight(double time, double duration)
    {
        var weight = time * DiscardBodyRampRate / duration;
        // Written as a negated comparison so a NaN (duration 0 at time 0) lands at 0 rather than propagating.
        return !(weight > 0.0) ? 0.0 : (weight < 1.0 ? weight : 1.0);
    }

    // The shrink endpoints for that ramp: full size → a tenth.
    internal const double DiscardArcScaleFrom = 1.0;
    internal const double DiscardArcScaleTo = 0.1;

    internal static double DiscardArcScale(double weight)
        => DiscardArcScaleFrom + ((DiscardArcScaleTo - DiscardArcScaleFrom) * weight);

    // The landing shrink, once the card is sitting on the pile: it continues off the bottom and is pinned at zero,
    // so the card is GONE at 0.4 of the second phase and the rest of that phase is empty screen while the trail
    // fades out behind it.
    internal const double DiscardPopScaleFrom = 0.1;
    internal const double DiscardPopScaleTo = -0.15;

    internal static double DiscardPopScale(double progress)
        => Math.Max(DiscardPopScaleFrom + ((DiscardPopScaleTo - DiscardPopScaleFrom) * progress), 0.0);

    // Everything <see cref="IsReplayable"/> demands, plus a finite seed angle: the discard's rotation channel eases
    // OUT of the pose the card was already resting at in the hand, so a non-finite seed would poison every replayed
    // frame instead of just the first.
    internal static bool IsDiscardReplayable(double speed0, double accel, double duration, double scale0, double rot0)
        => IsReplayable(speed0, accel, duration, scale0) && double.IsFinite(rot0);
}
