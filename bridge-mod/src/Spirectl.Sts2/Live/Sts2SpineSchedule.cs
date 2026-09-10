namespace Spirectl.Sts2.Live;

// Godot-free core of the mirror's spine-anim SCHEDULE replay: always compiled (no Godot dependency) so it's
// offline-unit-testable, like Sts2TweenEndpointTuples. The Godot-typed producer/hook (Sts2SpineInspector,
// Sts2SpineAnimationHooks) stay in the live-host-only Live/ glob and delegate here.
//
// Under a permanently-frozen SpineSprite (ProcessMode.Disabled) the game's native track queue no longer
// auto-advances a one-shot (attack) to its pre-queued loop (idle_loop), so the producer replays that queue
// itself off wall-clock from the sequence the CreatureAnimator.SetNextState hook recorded.
internal static class Sts2SpineSchedule
{
    // Pure wall-clock replay of a recorded schedule. A looping head — or a one-shot head still inside its
    // duration window — plays the current anim, its clock = elapsed since the schedule started (the client seeds
    // frame 0 at each name change, then free-runs, so the growing value is ignored between changes). Once a
    // one-shot head with a queued NextAnim elapses, it hands off to that return anim with the clock re-based to
    // the handoff (≈0 at the switch → the client seeds the return clip from frame 0).
    //
    // A LONE one-shot (no NextAnim) keeps reporting itself, and past its end it now reports itself PAUSED at the
    // clip's duration. WHY: nothing ever retires such a schedule. NRegentVfx.Attack() fires
    // SetAnimation("attack", loop:false) on each weapon sprite and queues nothing behind it, so once the swing is
    // over the producer still streamed ("attack", ever-growing t, looping:false) forever. The animated lane
    // clamps on the last frame, but the product default is a server-baked STILL, and a still with no `&t=`
    // samples the MIDDLE of the clip (Sts2SpineStillFrame.ChooseSampleTime) — so the browser kept a dagger frozen
    // mid-swing long after the Regent had lowered it. Reporting the node as paused at the clip's end reuses the
    // paused-still plumbing whole, with no client change: spineStillTime pins `&t=<duration>` and ChooseSampleTime
    // honours the request, yielding the clip's LAST frame — the pose the game rests in.
    //
    // The pinned time being CONSTANT is load-bearing, not incidental: `&t=` is part of the clip cache key on both
    // sides, so a wall-clock value would mint a fresh bake (and a fresh fetch) every tick. Clamping to the
    // duration gives one stable URL for the whole rest.
    //
    // A duration of 0 means the hook could not resolve one (see ResolveOneShotDurationMsec) — there is no end to
    // clamp to, and pinning t=0 would ask for the clip's FIRST frame, which for a death animation is the creature
    // still standing. That case keeps the pre-fix behaviour: no pause, and the host's own heuristic (mid frame,
    // or the last frame for a terminal die/defeat name) picks.
    public static (string? Anim, double TrackTime, bool Looping, bool Paused) ResolveScheduledAnim(
        string currentAnim,
        bool currentLooping,
        double currentDurMsec,
        string? nextAnim,
        bool nextLooping,
        double elapsedMsec,
        bool restFinishedOneShot = true)
    {
        if (currentLooping || elapsedMsec < currentDurMsec)
        {
            return (currentAnim, elapsedMsec / 1000.0, currentLooping, false);
        }

        if (nextAnim is not null)
        {
            return (nextAnim, (elapsedMsec - currentDurMsec) / 1000.0, nextLooping, false);
        }

        if (restFinishedOneShot && currentDurMsec > 0)
        {
            return (currentAnim, currentDurMsec / 1000.0, false, true);
        }

        return (currentAnim, elapsedMsec / 1000.0, currentLooping, false);
    }

    // Resolve the one-shot HEAD's duration (msec) for a recorded schedule, from two second-valued reads the hook
    // takes at SetNextState time: the LIVE track entry's end (GetAnimationState().GetCurrent(0).GetAnimationEnd() —
    // the exact call the game makes) and, as a fallback, the clip's STATIC duration from the loaded skeleton data
    // (find_animation(name).get_duration()).
    //
    // Why the fallback matters: a one-shot head with a queued return whose duration comes back 0 makes
    // ResolveScheduledAnim hand off to the return loop IMMEDIATELY (elapsed >= 0), so the attack clip is never
    // streamed — the mirror snaps straight to idle_loop and the per-card attack visibly does not play. The live
    // track-entry read can come back 0 on a couch-frozen SpineSprite (ProcessMode.Disabled: get_current(0) may not
    // be materialised the way the game's own looping-branch read relies on), but the skeleton-data duration is
    // STATIC and always available — and SetNextState only plays a clip it already gated on HasAnimation(id), so the
    // clip is guaranteed present in the skeleton, so this fallback always yields the real (non-zero) duration for a
    // real attack. Prefer the live end (what the game uses); fall back to the skeleton duration; 0 only if BOTH are
    // non-positive (never for a real clip). Pure/Godot-free so the decision is offline-unit-testable.
    public static double ResolveOneShotDurationMsec(double trackEndSeconds, double skeletonDurationSeconds)
    {
        if (trackEndSeconds > 0)
        {
            return trackEndSeconds * 1000.0;
        }

        if (skeletonDurationSeconds > 0)
        {
            return skeletonDurationSeconds * 1000.0;
        }

        return 0;
    }
}
