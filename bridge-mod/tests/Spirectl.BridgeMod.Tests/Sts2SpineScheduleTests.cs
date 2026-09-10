using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// Pure, Godot-free core of the mirror's spine-anim schedule replay: given the sequence the game queued via
// CreatureAnimator.SetNextState (a one-shot head + its pre-queued return loop, with the one-shot's duration), the
// producer replays it off wall-clock so SpineCurrentAnim flips at the right moments while the SpineSprite node is
// permanently frozen (ProcessMode.Disabled). This exercises Sts2SpineSchedule.ResolveScheduledAnim directly.
public sealed class Sts2SpineScheduleTests
{
    // The dominant combat case: attack (500ms one-shot) → pre-queued idle_loop.
    [Fact]
    public void OneShotThenLoop_PlaysOneShotThenHandsOffToLoop()
    {
        // Inside the one-shot window: reports the one-shot (not looping), clock = elapsed, running.
        var mid = Sts2SpineSchedule.ResolveScheduledAnim(
            "attack", currentLooping: false, currentDurMsec: 500,
            nextAnim: "idle_loop", nextLooping: true, elapsedMsec: 200);
        Assert.Equal(("attack", 0.2, false, false), mid);

        // After the one-shot elapses: hands off to the queued loop (looping), clock re-based to the handoff.
        var after = Sts2SpineSchedule.ResolveScheduledAnim(
            "attack", currentLooping: false, currentDurMsec: 500,
            nextAnim: "idle_loop", nextLooping: true, elapsedMsec: 800);
        Assert.Equal(("idle_loop", 0.3, true, false), after);
    }

    // The switch is exact at the boundary, and the return clip seeds at frame 0 (clock ≈ 0).
    [Fact]
    public void OneShotThenLoop_SwitchesAtBoundaryWithZeroTrackTime()
    {
        var atBoundary = Sts2SpineSchedule.ResolveScheduledAnim(
            "attack", currentLooping: false, currentDurMsec: 500,
            nextAnim: "idle_loop", nextLooping: true, elapsedMsec: 500);
        Assert.Equal(("idle_loop", 0.0, true, false), atBoundary);

        var justBefore = Sts2SpineSchedule.ResolveScheduledAnim(
            "attack", currentLooping: false, currentDurMsec: 500,
            nextAnim: "idle_loop", nextLooping: true, elapsedMsec: 499);
        Assert.Equal("attack", justBefore.Anim);
        Assert.False(justBefore.Looping);
    }

    // A lone looping head (the initial idle, no queued next) always reports itself as looping, forever.
    [Fact]
    public void LoopingHead_AlwaysReportsItselfLooping()
    {
        var early = Sts2SpineSchedule.ResolveScheduledAnim(
            "idle_loop", currentLooping: true, currentDurMsec: 0,
            nextAnim: null, nextLooping: false, elapsedMsec: 10);
        Assert.Equal(("idle_loop", 0.01, true, false), early);

        var late = Sts2SpineSchedule.ResolveScheduledAnim(
            "idle_loop", currentLooping: true, currentDurMsec: 0,
            nextAnim: null, nextLooping: false, elapsedMsec: 100_000);
        Assert.Equal(("idle_loop", 100.0, true, false), late);
    }

    // A lone one-shot with no queued return (e.g. a terminal "die") keeps reporting itself past its end, so the
    // client freezes on its last frame (SpineLooping false) rather than snapping to a default.
    [Fact]
    public void LoneOneShot_KeepsReportingItselfPastItsEnd()
    {
        var past = Sts2SpineSchedule.ResolveScheduledAnim(
            "die", currentLooping: false, currentDurMsec: 400,
            nextAnim: null, nextLooping: false, elapsedMsec: 5_000);
        Assert.Equal("die", past.Anim);
        Assert.False(past.Looping);
    }

    // ── A FINISHED lone one-shot RESTS at its end (the Regent's daggers) ───────────────────────────────────────

    // NRegentVfx.Attack() plays SetAnimation("attack", loop:false) on a weapon sprite and queues nothing behind
    // it, so this schedule is never retired. While the swing runs the node reports the real elapsed time; once it
    // is over it reports the clip's END, PAUSED — which is what makes the still lane bake the last frame (the
    // weapon lowered) instead of the mid-swing frame the host's default heuristic picks.
    [Fact]
    public void FinishedLoneOneShot_RestsPausedAtTheClipEnd()
    {
        var duringSwing = Sts2SpineSchedule.ResolveScheduledAnim(
            "attack", currentLooping: false, currentDurMsec: 900,
            nextAnim: null, nextLooping: false, elapsedMsec: 300);
        Assert.Equal(("attack", 0.3, false, false), duringSwing);

        var rested = Sts2SpineSchedule.ResolveScheduledAnim(
            "attack", currentLooping: false, currentDurMsec: 900,
            nextAnim: null, nextLooping: false, elapsedMsec: 4_000);
        Assert.Equal(("attack", 0.9, false, true), rested);
    }

    // The rest time must be CONSTANT: it lands in the `&t=` clip cache key on both sides, so a wall-clock value
    // would mint a fresh host bake (and a fresh client fetch) every single tick.
    [Fact]
    public void FinishedLoneOneShot_RestTimeIsStableAcrossTicks()
    {
        var times = new List<double>();
        foreach (var elapsed in new double[] { 900, 901, 2_000, 60_000, 3_600_000 })
        {
            var rested = Sts2SpineSchedule.ResolveScheduledAnim(
                "attack", currentLooping: false, currentDurMsec: 900,
                nextAnim: null, nextLooping: false, elapsedMsec: elapsed);
            Assert.True(rested.Paused);
            times.Add(rested.TrackTime);
        }

        Assert.Single(times.Distinct());
    }

    // A one-shot WITH a queued return is untouched: it still hands off at the boundary. This is the regression the
    // ResolveOneShotDurationMsec comment warns about (an early handoff means the attack clip is never streamed at
    // all) — resting must never pre-empt a real handoff.
    [Fact]
    public void FinishedOneShotWithQueuedNext_StillHandsOffAndNeverRests()
    {
        foreach (var elapsed in new double[] { 500, 501, 900, 10_000 })
        {
            var resolved = Sts2SpineSchedule.ResolveScheduledAnim(
                "attack", currentLooping: false, currentDurMsec: 500,
                nextAnim: "idle_loop", nextLooping: true, elapsedMsec: elapsed);

            Assert.Equal("idle_loop", resolved.Anim);
            Assert.True(resolved.Looping);
            Assert.False(resolved.Paused);
        }
    }

    // A looping head never rests — it has no end to rest at, and pausing an idle would freeze every creature in
    // combat on one frame.
    [Fact]
    public void LoopingHead_NeverRests()
    {
        var late = Sts2SpineSchedule.ResolveScheduledAnim(
            "idle_loop", currentLooping: true, currentDurMsec: 700,
            nextAnim: null, nextLooping: false, elapsedMsec: 500_000);

        Assert.Equal(("idle_loop", 500.0, true, false), late);
    }

    // An UNRESOLVED duration (both reads non-positive — ResolveOneShotDurationMsec's last resort) has no end to
    // clamp to. Pinning t=0 would ask for the clip's FIRST frame, which for "die" is the creature still standing,
    // so this keeps the pre-fix behaviour and lets the host's own mid/terminal heuristic pick.
    [Fact]
    public void FinishedLoneOneShot_UnknownDuration_DoesNotRest()
    {
        var unresolved = Sts2SpineSchedule.ResolveScheduledAnim(
            "die", currentLooping: false, currentDurMsec: 0,
            nextAnim: null, nextLooping: false, elapsedMsec: 5_000);

        Assert.Equal(("die", 5.0, false, false), unresolved);
    }

    // SPIRECTL_SPINE_FINISHED_REST=0 (the producer passes the flag through): back to the ever-growing wall-clock
    // track time, unpaused — i.e. the host keeps baking the clip's MID frame.
    [Fact]
    public void FinishedLoneOneShot_KillSwitchOff_KeepsTheGrowingWallClock()
    {
        var off = Sts2SpineSchedule.ResolveScheduledAnim(
            "attack", currentLooping: false, currentDurMsec: 900,
            nextAnim: null, nextLooping: false, elapsedMsec: 4_000, restFinishedOneShot: false);

        Assert.Equal(("attack", 4.0, false, false), off);
    }

    // ── One-shot head duration resolution (the fix for the dropped per-card attack) ────────────────────────────

    // The live track-entry end is authoritative (it is what the game itself reads) — used verbatim, converted to
    // msec, whatever the skeleton fallback would say.
    [Fact]
    public void OneShotDuration_PrefersLiveTrackEnd()
    {
        Assert.Equal(800.0, Sts2SpineSchedule.ResolveOneShotDurationMsec(trackEndSeconds: 0.8, skeletonDurationSeconds: 0.5));
    }

    // THE FIX: a couch-frozen SpineSprite can hand back 0 for the live track-entry end; the static skeleton-data
    // duration is then used so the schedule gets a real (non-zero) duration and does NOT skip the attack.
    [Fact]
    public void OneShotDuration_FallsBackToSkeletonWhenLiveEndIsZero()
    {
        Assert.Equal(500.0, Sts2SpineSchedule.ResolveOneShotDurationMsec(trackEndSeconds: 0, skeletonDurationSeconds: 0.5));
        // Negative/garbage live reads are treated the same as 0.
        Assert.Equal(500.0, Sts2SpineSchedule.ResolveOneShotDurationMsec(trackEndSeconds: -1, skeletonDurationSeconds: 0.5));
    }

    // Only when BOTH reads are non-positive (never for a real clip — SetNextState gated on HasAnimation) does it
    // resolve to 0, preserving the pre-fix behavior for the genuinely-unknown case.
    [Fact]
    public void OneShotDuration_ZeroOnlyWhenBothUnknown()
    {
        Assert.Equal(0.0, Sts2SpineSchedule.ResolveOneShotDurationMsec(trackEndSeconds: 0, skeletonDurationSeconds: 0));
        Assert.Equal(0.0, Sts2SpineSchedule.ResolveOneShotDurationMsec(trackEndSeconds: -3, skeletonDurationSeconds: -2));
    }

    // End-to-end of the fix: with the live end reading 0 (frozen node), the OLD path recorded currentDurMsec == 0
    // and ResolveScheduledAnim skipped STRAIGHT to the idle loop on the very first tick — the attack never played.
    // With the skeleton fallback supplying the real duration, the same first tick now reports the ATTACK.
    [Fact]
    public void FrozenNodeAttack_WithSkeletonFallback_PlaysAttackInsteadOfSkippingToIdle()
    {
        // Pre-fix reproduction: live end 0 and no fallback ⇒ duration 0 ⇒ idle_loop on the first tick (bug).
        var buggyDur = Sts2SpineSchedule.ResolveOneShotDurationMsec(trackEndSeconds: 0, skeletonDurationSeconds: 0);
        var buggy = Sts2SpineSchedule.ResolveScheduledAnim(
            "attack", currentLooping: false, currentDurMsec: buggyDur,
            nextAnim: "idle_loop", nextLooping: true, elapsedMsec: 16);
        Assert.Equal("idle_loop", buggy.Anim);
        Assert.False(buggy.Paused);

        // Fixed: live end 0 but skeleton says 0.5s ⇒ duration 500ms ⇒ the attack plays through its window.
        var fixedDur = Sts2SpineSchedule.ResolveOneShotDurationMsec(trackEndSeconds: 0, skeletonDurationSeconds: 0.5);
        var early = Sts2SpineSchedule.ResolveScheduledAnim(
            "attack", currentLooping: false, currentDurMsec: fixedDur,
            nextAnim: "idle_loop", nextLooping: true, elapsedMsec: 16);
        Assert.Equal("attack", early.Anim);
        Assert.False(early.Looping);

        // …and still hands off to idle_loop once the (now real) window elapses.
        var after = Sts2SpineSchedule.ResolveScheduledAnim(
            "attack", currentLooping: false, currentDurMsec: fixedDur,
            nextAnim: "idle_loop", nextLooping: true, elapsedMsec: 700);
        Assert.Equal("idle_loop", after.Anim);
        Assert.True(after.Looping);
    }
}
