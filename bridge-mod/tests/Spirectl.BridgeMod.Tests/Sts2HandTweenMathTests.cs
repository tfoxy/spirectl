using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// WS-4 HAND-LAYOUT LERP — the duration model the producer publishes as a tween hint (Sts2HandTweenMath).
//
// Every expectation below is derived INDEPENDENTLY of the implementation: the reference loop below reproduces how
// the hand actually settles — each of position, angle and scale eases a fixed FRACTION OF THE REMAINING distance
// per frame and snaps once it is close enough — stepped a frame at a time. So the analytic model is checked against
// a SIMULATION of the on-screen settle rather than against itself, and a drift on either side fails here.
public sealed class Sts2HandTweenMathTests
{
    // One frame: move `dt * rate` of the way toward the target, then exit once the remainder is inside `epsilon`.
    // Returns the wall-clock SECONDS the settle took. Ordering matters and is reproduced exactly: the step happens
    // FIRST and only then the test, so a frame is consumed even when the very first step lands inside the threshold.
    private static double SimulateSeconds(double rate, double distance, double epsilon, double dt, int maxFrames = 100000)
    {
        var remaining = distance;
        for (var frame = 1; frame <= maxFrames; frame++)
        {
            remaining -= remaining * (rate * dt); // Lerp(v, target, w) moves w of the way there
            if (Math.Abs(remaining) < epsilon)
            {
                return frame * dt;
            }
        }

        return maxFrames * dt;
    }

    [Theory]
    [InlineData(1.0 / 30.0)]
    [InlineData(1.0 / 60.0)]
    [InlineData(1.0 / 90.0)]
    [InlineData(1.0 / 144.0)]
    public void PositionSettle_MatchesASimulationOfTheSettleLoop(double dt)
    {
        // The measured start-of-turn draw: 722 px of travel, k = 7, snap at DistanceSquaredTo < 1 (i.e. distance 1).
        var simulated = SimulateSeconds(Sts2HandTweenMath.PositionRate, 722, Sts2HandTweenMath.PositionEpsilon, dt);
        var modelled = Sts2HandTweenMath.PositionSettleMs(722, dt) / 1000.0;

        // Within one frame: the analytic model is continuous in t, the loop can only exit on a frame boundary.
        Assert.InRange(modelled, simulated - dt, simulated + dt);
    }

    [Theory]
    [InlineData(1.0 / 30.0)]
    [InlineData(1.0 / 60.0)]
    [InlineData(1.0 / 90.0)]
    public void AngleAndScaleSettle_MatchASimulationOfTheirOwnLoops(double dt)
    {
        // A ten-card fan's outermost angle is 15 degrees; the snap threshold is 0.1 degrees and k = 10.
        var angleSim = SimulateSeconds(Sts2HandTweenMath.AngleRate, 15, Sts2HandTweenMath.AngleEpsilonDegrees, dt);
        Assert.InRange(Sts2HandTweenMath.AngleSettleMs(15, dt) / 1000.0, angleSim - dt, angleSim + dt);

        // The hand's scale drop from 1.0 to 0.85 at ten cards, against the 0.002 snap threshold and k = 8.
        var scaleSim = SimulateSeconds(Sts2HandTweenMath.ScaleRate, 0.15, Sts2HandTweenMath.ScaleEpsilon, dt);
        Assert.InRange(Sts2HandTweenMath.ScaleSettleMs(0.15, dt) / 1000.0, scaleSim - dt, scaleSim + dt);
    }

    [Fact]
    public void DiscreteModel_IsFasterThanTheContinuousOne_AtEveryFrameRate()
    {
        // The whole reason the plan's `t = ln(d)/k` was corrected: the loop steps by a factor (1 - k*dt) per frame
        // and (1 - k*dt) < e^(-k*dt), so the real loop converges FASTER than the continuous model predicts. This is
        // a property of the model, not a tuned constant, so it must hold across the clamp band.
        var continuous = Sts2HandTweenMath.ContinuousSettleSeconds(Sts2HandTweenMath.PositionRate, 722, 1.0);
        foreach (var dt in new[] { 1.0 / 30.0, 1.0 / 60.0, 1.0 / 90.0, 1.0 / 240.0 })
        {
            var discrete = Sts2HandTweenMath.SettleSeconds(Sts2HandTweenMath.PositionRate, 722, 1.0, dt);
            Assert.True(discrete < continuous, $"dt={dt}: discrete {discrete} should be under continuous {continuous}");
        }

        // And it approaches the continuous model from below as dt shrinks (k_eff -> k from above).
        var fast = Sts2HandTweenMath.SettleSeconds(Sts2HandTweenMath.PositionRate, 722, 1.0, 1.0 / 240.0);
        var slow = Sts2HandTweenMath.SettleSeconds(Sts2HandTweenMath.PositionRate, 722, 1.0, 1.0 / 30.0);
        Assert.True(slow < fast);
        Assert.True(continuous - fast < 0.02);
    }

    [Fact]
    public void PositionSettle_SitsInTheDocumentedBand_ForTheMeasuredDraw()
    {
        // The band the clamp guarantees for the measured d0 = 722 px: 826 ms at the 30 fps floor, 927 ms at the
        // 240 fps ceiling. This is the claim the model note makes, pinned so it cannot silently drift.
        Assert.InRange(Sts2HandTweenMath.PositionSettleMs(722, 1.0 / 30.0), 820, 832);
        Assert.InRange(Sts2HandTweenMath.PositionSettleMs(722, 1.0 / 60.0), 878, 890);
        Assert.InRange(Sts2HandTweenMath.PositionSettleMs(722, 1.0 / 90.0), 897, 909);
        Assert.InRange(Sts2HandTweenMath.PositionSettleMs(722, 1.0 / 240.0), 921, 933);

        // The clamp keeps every reachable duration under the continuous model's 940 ms, which is the plan's
        // (overestimating) formula and the upper bound of this whole family.
        Assert.True(
            Sts2HandTweenMath.PositionSettleMs(722, 1.0 / 240.0)
            < Sts2HandTweenMath.ContinuousSettleSeconds(Sts2HandTweenMath.PositionRate, 722, 1.0) * 1000.0);
    }

    [Fact]
    public void FrameDelta_IsClampedIntoTheModelsValidBand()
    {
        Assert.Equal(Sts2HandTweenMath.MaxFrameSeconds, Sts2HandTweenMath.ClampFrameSeconds(0.5), 12);
        Assert.Equal(Sts2HandTweenMath.MinFrameSeconds, Sts2HandTweenMath.ClampFrameSeconds(0.0001), 12);
        Assert.Equal(1.0 / 60.0, Sts2HandTweenMath.ClampFrameSeconds(0.0), 12);
        Assert.Equal(1.0 / 60.0, Sts2HandTweenMath.ClampFrameSeconds(double.NaN), 12);
        Assert.Equal(1.0 / 60.0, Sts2HandTweenMath.ClampFrameSeconds(-1.0), 12);

        // The clamp exists to keep the game's own UNCLAMPED Lerp weight (dt * k) safely under 1 for every rate;
        // above 1 the game overshoots and the exponential-decay model stops describing it at all.
        var maxRate = Math.Max(
            Sts2HandTweenMath.AngleRate,
            Math.Max(Sts2HandTweenMath.PositionRate, Sts2HandTweenMath.ScaleRate));
        Assert.True(maxRate * Sts2HandTweenMath.MaxFrameSeconds < 0.5);
    }

    [Fact]
    public void ChannelsAlreadyInsideTheirSnapThreshold_ProduceNoDuration()
    {
        // "d0 < eps means the loop settles on its first iteration — publish nothing rather than a 0 ms hint."
        Assert.Equal(0.0, Sts2HandTweenMath.PositionSettleMs(0.4, 1.0 / 60.0));
        Assert.Equal(0.0, Sts2HandTweenMath.AngleSettleMs(0.05, 1.0 / 60.0));
        Assert.Equal(0.0, Sts2HandTweenMath.ScaleSettleMs(0.001, 1.0 / 60.0));
        Assert.Equal(0.0, Sts2HandTweenMath.PositionSettleMs(double.NaN, 1.0 / 60.0));
    }

    [Fact]
    public void HintDuration_TakesTheSlowestChannel()
    {
        Assert.Equal(900, Sts2HandTweenMath.HintDurationMs(900, 400, 300));
        Assert.Equal(900, Sts2HandTweenMath.HintDurationMs(300, 900, 400));
        Assert.Equal(900, Sts2HandTweenMath.HintDurationMs(300, 400, 900));
    }

    [Fact]
    public void HintDuration_FloorsShortApproachesToNothing()
    {
        // Below the floor there is no point pinning the node: the approach is over before a hint can cross the wire.
        Assert.Equal(0, Sts2HandTweenMath.HintDurationMs(0, 0, 0));
        Assert.Equal(0, Sts2HandTweenMath.HintDurationMs(Sts2HandTweenMath.MinHintMs - 1, 0, 0));
        Assert.Equal(
            (long)Sts2HandTweenMath.MinHintMs,
            Sts2HandTweenMath.HintDurationMs(Sts2HandTweenMath.MinHintMs, 0, 0));
    }

    [Fact]
    public void HintDuration_ClampsToTheWatchersSuppressionCap()
    {
        // The hint's duration IS the suppression window the resolver opens, so it must not exceed the watcher's own
        // cap — otherwise the published duration and the real window would disagree.
        Assert.Equal(Sts2HandTweenMath.MaxHintMs, Sts2HandTweenMath.HintDurationMs(999999, 0, 0));
        Assert.Equal(0, Sts2HandTweenMath.HintDurationMs(double.PositiveInfinity, 0, 0));
        Assert.Equal(0, Sts2HandTweenMath.HintDurationMs(double.NaN, 0, 0));
    }

    [Fact]
    public void MidFlightRetarget_ShortensTheDuration_BecauseItMeasuresTheRemainingTravel()
    {
        // The mid-flight re-target contract: a fresh hint is computed from the holder's LIVE pose, so re-targeting a
        // holder that has already covered most of its travel publishes a SHORTER hint (and therefore a shorter
        // suppression window), never a fresh full-length one.
        const double Dt = 1.0 / 60.0;
        var full = Sts2HandTweenMath.PositionSettleMs(722, Dt);
        var partway = Sts2HandTweenMath.PositionSettleMs(80, Dt);
        // 1.3 px is still OUTSIDE the game's 1 px snap threshold, so the loop does run — but it is ~35 ms of
        // travel, under the publish floor, which is precisely the case that must CANCEL rather than re-pin.
        var nearlyThere = Sts2HandTweenMath.PositionSettleMs(1.3, Dt);

        Assert.True(partway < full);
        Assert.True(nearlyThere > 0 && nearlyThere < partway);
        Assert.Equal(0, Sts2HandTweenMath.HintDurationMs(nearlyThere, 0, 0)); // under the floor → cancel, don't pin
        Assert.True(Sts2HandTweenMath.HintDurationMs(partway, 0, 0) > 0);
    }

    [Fact]
    public void CorrectiveDuration_KeepsTheFloor_WhenNothingIsPinned()
    {
        // No open window ⇒ no consumer is replaying a hint for this holder, so the floor keeps its original meaning:
        // a sub-50 ms approach is over before a hint could be armed, and streaming it is strictly cheaper.
        Assert.Equal(0, Sts2HandTweenMath.CorrectiveDurationMs(0, 0, 0, windowOpen: false));
        Assert.Equal(0, Sts2HandTweenMath.CorrectiveDurationMs(Sts2HandTweenMath.MinHintMs - 1, 0, 0, windowOpen: false));
    }

    [Fact]
    public void CorrectiveDuration_PublishesAShortHint_WhenTheConsumerIsStillPinned()
    {
        // The refocus/redraw defect in one assertion. A cancel un-pins only the PRODUCER; a consumer that already
        // armed an earlier hint keeps replaying that endpoint. So a batch that changes the pose while a window is
        // open MUST still say something, even when every channel is inside the game's own snap threshold — which is
        // exactly what a focus layout produces, because it TELEPORTS the angle and scale and leaves ~no travel.
        Assert.Equal(
            (long)Sts2HandTweenMath.MinHintMs,
            Sts2HandTweenMath.CorrectiveDurationMs(0, 0, 0, windowOpen: true));
        Assert.Equal(
            (long)Sts2HandTweenMath.MinHintMs,
            Sts2HandTweenMath.CorrectiveDurationMs(Sts2HandTweenMath.MinHintMs - 1, 0, 0, windowOpen: true));
        // A non-finite reading is still nothing to publish naturally, but it is also still a pose change on a pinned
        // holder — the corrective hint is what keeps the consumer from being stranded by a bad sample.
        Assert.Equal(
            (long)Sts2HandTweenMath.MinHintMs,
            Sts2HandTweenMath.CorrectiveDurationMs(double.NaN, 0, 0, windowOpen: true));
    }

    [Fact]
    public void CorrectiveDuration_NeverShortensAnApproachThatEarnedItsOwnDuration()
    {
        // The override is a FLOOR replacement, not a clamp: a batch with real travel keeps the duration the model
        // computed for it whether or not a window is open, so an open window can never make a long approach snap.
        const double Dt = 1.0 / 60.0;
        var partway = Sts2HandTweenMath.PositionSettleMs(80, Dt);
        var natural = Sts2HandTweenMath.HintDurationMs(partway, 0, 0);
        Assert.True(natural > Sts2HandTweenMath.MinHintMs);
        Assert.Equal(natural, Sts2HandTweenMath.CorrectiveDurationMs(partway, 0, 0, windowOpen: true));
        Assert.Equal(natural, Sts2HandTweenMath.CorrectiveDurationMs(partway, 0, 0, windowOpen: false));
        // …and the watcher's cap still wins over both.
        Assert.Equal(
            Sts2HandTweenMath.MaxHintMs,
            Sts2HandTweenMath.CorrectiveDurationMs(999999, 0, 0, windowOpen: true));
    }

    // ---- batch channel accumulation (Retarget / Teleport) ----------------------------------------
    //
    // Same discipline as the duration tests above: the sequences below are the SHAPES a hand layout can hand the
    // producer, written as data, so the accumulation rule is checked against the instruction order rather than
    // against itself.

    [Fact]
    public void FocusBatch_PublishesPositionOnly_BecauseTheTeleportsSupersedeTheEarlierRetargets()
    {
        // The bug this rule exists for, written as the producer observes it: a card-focus frame delivers a full
        // three-channel re-target for the holder and THEN teleports two of those channels, all inside ONE coalesced
        // batch, with only position re-stated afterwards. The angle and scale re-targets belong to the pose the
        // frame went on to abandon. Per channel the last instruction wins, so the batch must end up speaking for
        // POSITION alone — angle and scale then fall back to the holder's live (post-teleport) pose.
        var touched = HandChannels.None;
        touched = Sts2HandTweenMath.Retarget(touched, HandChannels.Position);
        touched = Sts2HandTweenMath.Retarget(touched, HandChannels.Scale);
        touched = Sts2HandTweenMath.Retarget(touched, HandChannels.Angle);
        touched = Sts2HandTweenMath.Teleport(touched, HandChannels.Angle);
        touched = Sts2HandTweenMath.Teleport(touched, HandChannels.Scale);
        touched = Sts2HandTweenMath.Retarget(touched, HandChannels.Position);

        Assert.Equal(HandChannels.Position, touched);

        // Before the supersede rule the same sequence left all three standing — which is what published an endpoint
        // carrying the stale unfocused rotation/scale next to the focused position.
        var orAccumulated = HandChannels.Position | HandChannels.Scale | HandChannels.Angle;
        Assert.NotEqual(orAccumulated, touched);
    }

    [Fact]
    public void ATeleport_ClearsOnlyItsOwnChannel()
    {
        var all = HandChannels.Position | HandChannels.Angle | HandChannels.Scale;

        // Angle's teleport must not silence scale's re-target, and vice versa — the two instant setters are
        // independent, and either can appear without the other.
        Assert.Equal(HandChannels.Position | HandChannels.Scale, Sts2HandTweenMath.Teleport(all, HandChannels.Angle));
        Assert.Equal(HandChannels.Position | HandChannels.Angle, Sts2HandTweenMath.Teleport(all, HandChannels.Scale));

        // Teleporting a channel that was never re-targeted is a no-op, and repeating one is idempotent.
        Assert.Equal(HandChannels.None, Sts2HandTweenMath.Teleport(HandChannels.None, HandChannels.Angle));
        var once = Sts2HandTweenMath.Teleport(all, HandChannels.Angle);
        Assert.Equal(once, Sts2HandTweenMath.Teleport(once, HandChannels.Angle));

        // Position has no instant setter of its own, but the rule is per-channel arithmetic with no special cases.
        Assert.Equal(HandChannels.Angle | HandChannels.Scale, Sts2HandTweenMath.Teleport(all, HandChannels.Position));
    }

    [Fact]
    public void ARetargetAfterATeleport_PublishesThatChannelAgain()
    {
        // The clear is "this channel's target field is not the instruction it carries" — it is NOT a latch that
        // mutes the channel for the rest of the batch. A later re-target is a real instruction and must count.
        var touched = Sts2HandTweenMath.Retarget(HandChannels.None, HandChannels.Angle);
        touched = Sts2HandTweenMath.Teleport(touched, HandChannels.Angle);
        Assert.Equal(HandChannels.None, touched);

        touched = Sts2HandTweenMath.Retarget(touched, HandChannels.Angle);
        Assert.Equal(HandChannels.Angle, touched);

        // …and it stays a per-channel rule when the two interleave across channels.
        touched = Sts2HandTweenMath.Retarget(touched, HandChannels.Scale);
        touched = Sts2HandTweenMath.Teleport(touched, HandChannels.Scale);
        touched = Sts2HandTweenMath.Retarget(touched, HandChannels.Position);
        Assert.Equal(HandChannels.Angle | HandChannels.Position, touched);
    }

    [Fact]
    public void ABatchWithoutTeleports_KeepsAllThreeChannels()
    {
        // The ordinary (unfocus / draw / re-fan) path: three re-targets per holder and nothing else. It must be
        // untouched by the supersede rule — that path was never the defect.
        var touched = HandChannels.None;
        touched = Sts2HandTweenMath.Retarget(touched, HandChannels.Position);
        touched = Sts2HandTweenMath.Retarget(touched, HandChannels.Angle);
        touched = Sts2HandTweenMath.Retarget(touched, HandChannels.Scale);
        Assert.Equal(HandChannels.Position | HandChannels.Angle | HandChannels.Scale, touched);

        // A re-fan re-states the same channels; coalescing must stay idempotent rather than accumulating anything.
        touched = Sts2HandTweenMath.Retarget(touched, HandChannels.Position);
        touched = Sts2HandTweenMath.Retarget(touched, HandChannels.Angle);
        touched = Sts2HandTweenMath.Retarget(touched, HandChannels.Scale);
        Assert.Equal(HandChannels.Position | HandChannels.Angle | HandChannels.Scale, touched);
    }

    [Fact]
    public void DroppedChannels_DropOutOfTheDuration_AndAPinnedHolderStillGetsTheFloor()
    {
        // The accumulator and the duration model are coupled: a channel the batch no longer speaks for contributes
        // NO settle time, so a focus batch's duration is the position channel's alone and never the (longer)
        // unfocus rotation/scale travel it superseded.
        const double Dt = 1.0 / 60.0;
        var positionMs = Sts2HandTweenMath.PositionSettleMs(20, Dt);
        var angleMs = Sts2HandTweenMath.AngleSettleMs(15, Dt);       // the unfocused fan angle it superseded
        var scaleMs = Sts2HandTweenMath.ScaleSettleMs(0.15, Dt);     // and the unfocused fan scale

        var positionOnly = Sts2HandTweenMath.HintDurationMs(positionMs, 0, 0);
        var allThree = Sts2HandTweenMath.HintDurationMs(positionMs, angleMs, scaleMs);
        Assert.True(positionOnly > 0);
        Assert.Equal((long)positionMs, positionOnly);   // slowest-of-one is that one channel
        Assert.True(positionOnly <= allThree);
        Assert.True(positionOnly < allThree);           // here the superseded channels were the slower ones

        // And the degenerate focus case — a position residue under the floor, on a holder a consumer is still
        // pinned to — keeps publishing the short corrective hint rather than stranding it.
        var residueMs = Sts2HandTweenMath.PositionSettleMs(1.3, Dt);
        Assert.True(residueMs > 0 && residueMs < Sts2HandTweenMath.MinHintMs);
        Assert.Equal(0, Sts2HandTweenMath.CorrectiveDurationMs(residueMs, 0, 0, windowOpen: false));
        Assert.Equal(
            (long)Sts2HandTweenMath.MinHintMs,
            Sts2HandTweenMath.CorrectiveDurationMs(residueMs, 0, 0, windowOpen: true));
    }

    [Fact]
    public void SettleTime_ScalesLogarithmicallyWithDistance_LikeAnExponentialApproach()
    {
        // The shape claim that justifies publishing this as an Expo/Out CSS transition: each e-folding of distance
        // costs the SAME additional time, so the curve is an exponential approach and not, say, a linear ramp.
        const double Dt = 1.0 / 60.0;
        var e1 = Sts2HandTweenMath.SettleSeconds(Sts2HandTweenMath.PositionRate, Math.E * 1.0, 1.0, Dt);
        var e2 = Sts2HandTweenMath.SettleSeconds(Sts2HandTweenMath.PositionRate, Math.E * Math.E, 1.0, Dt);
        var e3 = Sts2HandTweenMath.SettleSeconds(Sts2HandTweenMath.PositionRate, Math.Pow(Math.E, 3), 1.0, Dt);

        Assert.Equal(e2 - e1, e3 - e2, 9);
        Assert.Equal(e1, e2 - e1, 9);
    }
}
