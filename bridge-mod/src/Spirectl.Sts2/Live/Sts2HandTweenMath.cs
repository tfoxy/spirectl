namespace Spirectl.Sts2.Live;

// WS-4 HAND-LAYOUT LERP → DECLARATIVE HINT — the Godot-free half.
//
// PURE and Godot-free (like Sts2CardFlightMath / Sts2CancelledTweenResync / Sts2TweenEndpointTuples) so the whole
// settle-time algebra is unit-testable without the live host; the Godot-typed Harmony hook that reads the live
// holder (Sts2HandHolderHooks) stays in the live-host glob.
//
// THE MOTION THIS MODELS. A hand card holder is not driven by a Godot `Tween`: each of its three channels
// (position, angle, scale) is an independent per-process-frame EXPONENTIAL APPROACH toward a target, with a fixed
// rate and a fixed "close enough, stop" threshold. So there is no tween for Sts2TweenRecorderHooks to capture, no
// declared duration to forward, and the mirror streamed every intermediate frame of every holder: a 3s
// start-of-turn draw window measured 369.3 KB / 594 upserts (21.4% of the window) in `Hand/CardHolderContainer`,
// peaking at 1.03 MB / 2477 upserts in one second. Everything the producer ships has to be DERIVED, which is what
// this file does. (Behavioural detail — the exact update expressions, the entry points, which of them teleport
// rather than approach — is in `.sts2/research/hand-holder-animation.md`; see AGENTS.md.)
//
// WHY THIS IS PUBLISHABLE AS AN ORDINARY TWEEN HINT. An exponential approach toward a fixed target and a CSS
// `transition` with an expo-out timing function are the SAME curve; only the rate constant differs. So the producer
// does not need a new wire type — it publishes `Embedding.TweenAnimationHint` with the resolved end transform,
// `trans = "Expo"`, `ease = "Out"`, and the duration this file computes.

// The three independently animated channels of one hand card holder, used as a per-frame BATCH ACCUMULATOR: which
// channels the ONE endpoint published for that holder is allowed to speak for. See the Retarget/Teleport rules at
// the bottom of Sts2HandTweenMath.
[Flags]
internal enum HandChannels
{
    None = 0,
    Position = 1,
    Angle = 2,
    Scale = 4,
}

internal static class Sts2HandTweenMath
{
    // The three per-channel rates. These are the host's exact constants, not values fitted to a recording.
    internal const double PositionRate = 7.0;
    internal const double AngleRate = 10.0;
    internal const double ScaleRate = 8.0;

    // The three per-channel SETTLE thresholds, on the same footing. Position is a DISTANCE of 1 (the game tests the
    // squared distance against 1); angle is in DEGREES; scale compares the X component only.
    internal const double PositionEpsilon = 1.0;
    internal const double AngleEpsilonDegrees = 0.1;
    internal const double ScaleEpsilon = 0.002;

    // THE DURATION MODEL, and why it is the DISCRETE one.
    //
    // The continuous model of `v += (target - v) * k * dt` is `d(t) = d0 * e^(-k*t)`, giving `t = ln(d0/eps) / k`.
    // That model is what a plan would write down, and it OVERESTIMATES: the real loop takes finite steps, each
    // multiplying the remaining distance by `(1 - k*dt)`, and `(1 - k*dt) < e^(-k*dt)`, so the discrete loop
    // converges FASTER than the continuous one. Over n = t/dt frames,
    //
    //     d(t) = d0 * (1 - k*dt)^(t/dt) = d0 * e^(-k_eff * t)   with   k_eff = -ln(1 - k*dt) / dt
    //
    // and `t = ln(d0/eps) / k_eff`. Worked check for the measured start-of-turn draw (d0 = 722 px, k = 7):
    // continuous gives 940 ms; discrete gives 826 ms at 30 fps, 884 ms at 60 fps, 903 ms at 90 fps, 927 ms at
    // 240 fps — against an OBSERVED settle of ~800 ms. The discrete model is the one that matches, and it is the
    // one implemented here.
    //
    // The host's real frame delta is used rather than a hard-coded 60 Hz because the settle time is genuinely
    // frame-rate dependent (a FASTER host settles SLOWER in wall clock, k_eff decreasing toward k from above) and
    // the headless mirror host does not run at a fixed rate — it has a shipped idle frame cap.
    //
    // dt IS CLAMPED to [1/240, 1/30] s, for two independent reasons:
    //   * VALIDITY. The game's `Lerp` weight is NOT clamped, so a frame longer than 1/k seconds (~143 ms at k = 7,
    //     ~100 ms at k = 10) makes the weight exceed 1 and the value OVERSHOOTS its target — the exponential-decay
    //     model does not describe that regime at all. Capping dt at 1/30 keeps the largest weight (angle) at 0.333,
    //     comfortably inside the regime where the model holds.
    //   * STABILITY. Below ~30 fps the game's own wall-clock settle becomes erratic; we would rather publish a
    //     duration in the band the animation was designed to look like than track a degenerate host frame rate.
    //     The endpoint is unaffected either way, so the worst case is that the mirror eases to the right place
    //     slightly slower than the host did.
    // For d0 = 722 that clamp bounds the whole model output to [826, 927] ms, always under the continuous 940.
    internal const double MinFrameSeconds = 1.0 / 240.0;
    internal const double MaxFrameSeconds = 1.0 / 30.0;

    // Mirrors Sts2RuntimeSceneWatcher.SuppressCapMs. The hint's duration IS the streaming-suppression window the
    // resolver opens (one number drives both, exactly as for a real tween), so clamping here keeps the two equal
    // instead of letting the watcher silently clamp only its half.
    internal const long MaxHintMs = 2000;

    // Below this, publish NOTHING and let the holder stream. A sub-50 ms approach is over before a hint can cross
    // the wire and be armed, so pinning the node for it is pure loss: the client would hold a stale transform for a
    // frame or two, and the producer would withhold deltas that were already cheap. ~3 frames at 60 Hz.
    internal const double MinHintMs = 48.0;

    // Clamp a host process delta into the band the model is valid over (see MaxFrameSeconds). A non-finite or
    // non-positive delta (a first frame, a torn read) falls back to the 60 Hz mid-band value rather than poisoning
    // the whole computation.
    internal static double ClampFrameSeconds(double frameSeconds)
    {
        if (!double.IsFinite(frameSeconds) || frameSeconds <= 0.0)
        {
            return 1.0 / 60.0;
        }

        if (frameSeconds < MinFrameSeconds)
        {
            return MinFrameSeconds;
        }

        return frameSeconds > MaxFrameSeconds ? MaxFrameSeconds : frameSeconds;
    }

    // The DISCRETE effective decay rate `k_eff = -ln(1 - k*dt) / dt` (see the model note above). `frameSeconds` is
    // clamped first, which also guarantees `k*dt < 1` for every rate this file declares, so the log argument is
    // always positive.
    internal static double EffectiveRate(double rate, double frameSeconds)
    {
        var dt = ClampFrameSeconds(frameSeconds);
        var decay = 1.0 - (rate * dt);
        if (decay <= 0.0)
        {
            // Unreachable for the three declared rates at a clamped dt; kept so a future rate can't produce a NaN.
            return double.PositiveInfinity;
        }

        return -Math.Log(decay) / dt;
    }

    // The CONTINUOUS model `t = ln(d0/eps) / k`, kept as a documented reference point (and asserted against in the
    // unit tests) so the discrete/continuous relationship is a checked property rather than a claim in a comment.
    internal static double ContinuousSettleSeconds(double rate, double distance, double epsilon)
    {
        if (!IsUsableApproach(distance, epsilon) || rate <= 0.0)
        {
            return 0.0;
        }

        return Math.Log(distance / epsilon) / rate;
    }

    // Wall-clock seconds for one channel to reach its settle threshold from `distance` away, under the discrete
    // model at the host's frame delta. Returns 0 when the channel is already inside the game's own snap threshold
    // — the loop then settles on its FIRST iteration, so there is nothing to animate and nothing to publish.
    internal static double SettleSeconds(double rate, double distance, double epsilon, double frameSeconds)
    {
        if (!IsUsableApproach(distance, epsilon))
        {
            return 0.0;
        }

        var kEff = EffectiveRate(rate, frameSeconds);
        if (!double.IsFinite(kEff) || kEff <= 0.0)
        {
            return 0.0;
        }

        return Math.Log(distance / epsilon) / kEff;
    }

    internal static double PositionSettleMs(double distance, double frameSeconds)
        => SettleSeconds(PositionRate, distance, PositionEpsilon, frameSeconds) * 1000.0;

    internal static double AngleSettleMs(double degrees, double frameSeconds)
        => SettleSeconds(AngleRate, degrees, AngleEpsilonDegrees, frameSeconds) * 1000.0;

    internal static double ScaleSettleMs(double delta, double frameSeconds)
        => SettleSeconds(ScaleRate, delta, ScaleEpsilon, frameSeconds) * 1000.0;

    // The ONE duration a coalesced hint carries: the SLOWEST of the three channels, so the hint outlives every
    // channel the batch restarted (a relayout retargets all three of a holder's channels back-to-back), clamped to
    // the watcher's suppression cap and floored at MinHintMs. Returns 0 for
    // "nothing to publish for its own sake" — but that is NOT the caller's decision on its own; route it through
    // CorrectiveDurationMs below, which is what keeps an already-pinned consumer from being stranded.
    internal static long HintDurationMs(double positionMs, double angleMs, double scaleMs)
    {
        var longest = positionMs;
        if (angleMs > longest)
        {
            longest = angleMs;
        }

        if (scaleMs > longest)
        {
            longest = scaleMs;
        }

        if (!double.IsFinite(longest) || longest < MinHintMs)
        {
            return 0;
        }

        return longest >= MaxHintMs ? MaxHintMs : (long)longest;
    }

    // The duration to actually PUBLISH, given whether this holder still has an OPEN streaming-suppression window —
    // i.e. whether a client is (or may still be) pinned to a hint from an earlier batch.
    //
    // WHY THE FLOOR CANNOT SIMPLY DROP A BATCH. A tween hint is one-way: once a client has armed one, the producer
    // has no wire-level way to say "abandon it". Collapsing the suppression window un-pins the PRODUCER — it resumes
    // streaming — but the client keeps replaying the dead endpoint until its own deadline and, until the pin
    // catch-up landed on the consumer, threw away every streamed pose that arrived meanwhile. So "publish nothing
    // and cancel" is only safe for a holder NOBODY is pinned to. For a holder mid-approach it strands the card at an
    // endpoint the game has abandoned, which is exactly the two hand defects this pairs with:
    //   * refocusing a card mid-unfocus left it unfocused — the game writes a focus pose INSTANTLY (angle and scale
    //     are set outright and the lift is a direct position write), so the batch has ~no travel left, every channel
    //     lands inside its own snap threshold, and the natural duration is 0;
    //   * a start-of-turn draw re-fans the whole hand once per drawn card, so an already-near-target holder produces
    //     a sub-floor batch on each of the later cards and its correction was dropped every time.
    // In both, the right answer is not "no hint" but "a SHORT hint": MinHintMs is long enough to cross the wire and
    // re-arm the client onto the pose the game actually wants, and short enough that a snap still reads as a snap.
    //
    // With NO open window the floor keeps its original meaning: a sub-50ms approach on an unpinned holder is over
    // before a hint could be armed, so pinning for it is pure loss and streaming is strictly better.
    internal static long CorrectiveDurationMs(double positionMs, double angleMs, double scaleMs, bool windowOpen)
    {
        var natural = HintDurationMs(positionMs, angleMs, scaleMs);
        if (natural > 0)
        {
            return natural;
        }

        return windowOpen ? (long)MinHintMs : 0;
    }

    // A channel is only worth modelling when it starts OUTSIDE its own settle threshold and both numbers are sane.
    private static bool IsUsableApproach(double distance, double epsilon)
        => double.IsFinite(distance) && double.IsFinite(epsilon) && epsilon > 0.0 && distance > epsilon;

    // ---- batch channel accumulation: which channels one coalesced endpoint may speak for -----------
    //
    // A frame's layout instructions for a holder coalesce into ONE published endpoint, and each channel of that
    // endpoint is read from the channel's own TARGET field at drain time. So the accumulator answers exactly one
    // question per channel — "is that field the instruction this frame gave?" — and PER CHANNEL, THE LAST
    // INSTRUCTION OF THE FRAME WINS.
    //
    //   * A RE-TARGET means "publish this channel from its target field": the field IS the instruction. Set the bit.
    //   * A TELEPORT means "the pose is already written, and this channel's target field is NOT the instruction it
    //     carries". Clearing the bit is what makes the endpoint fall back to the holder's LIVE value for that
    //     channel, which is where the teleport just put it. Crucially the clear must SUPERSEDE any earlier
    //     re-target of the same channel in the same batch: declining to set the bit is not enough, because an
    //     earlier instruction in the frame may already have set it, and the stale target field would then be
    //     published as the endpoint.
    //   * A LATER re-target of the same channel is a genuine instruction again and re-sets the bit. Order is
    //     preserved for free, because callers apply these in call order.
    //
    // Both rules are pure functions of (accumulated, channel) and touch no other channel's bit, so one channel's
    // teleport can never silence another channel's re-target in the same batch.
    internal static HandChannels Retarget(HandChannels touched, HandChannels channel) => touched | channel;

    internal static HandChannels Teleport(HandChannels touched, HandChannels channel) => touched & ~channel;
}
