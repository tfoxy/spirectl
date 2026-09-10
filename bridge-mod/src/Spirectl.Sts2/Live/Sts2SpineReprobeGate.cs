namespace Spirectl.Sts2.Live;

// Godot-free decision core of the late-static SPINE RE-PROBE (round-8 item 13). Always compiled (no Godot
// dependency) so the budget/re-arm truth table is offline-unit-testable, like Sts2SpineDefaults /
// Sts2SpineMaterialKey. The Godot-typed caller (Sts2RuntimeSceneWatcher.MaybeReprobeSpineAnimations) reads the
// node's `skeleton_data_res` instance id and applies the decision.
//
// WHY: a spine node whose skeleton the game injects at RUNTIME (`MegaSprite.SetSkeletonDataRes` from `_Ready` —
// the treasure chest, the boss map point) reports an EMPTY animation list and NO `skelResPath` when the watcher
// first inspects it. Both clients need an animation name to fetch a clip, and the `&skel=` bake fallback needs
// the skeleton path, so until a re-probe upgrades the snapshot the node renders NOTHING.
//
// The original budget was spent one attempt per TICK while the list was empty, so a node the watcher tracked
// more than 30 ticks before its `_Ready` ran exhausted the budget against a still-null skeleton and then stayed
// blank FOREVER (the chest "only appears once it opens" — opening it changes the animation, which is a separate
// re-request path). Spending the budget per SKELETON instead makes the wait unbounded in time and ~free: an
// unchanged, still-absent skeleton skips without consuming anything, and any `skeleton_data_res` TRANSITION
// re-arms the full budget so the bounded retries still cover a resource that is assigned but not yet queryable.
internal static class Sts2SpineReprobeGate
{
    /// <summary>The outcome of one re-probe gate evaluation.</summary>
    /// <param name="ShouldInspect">Run the (expensive) InspectStatic this tick.</param>
    /// <param name="SkeletonProbeId">The skeleton instance id to remember for the next evaluation.</param>
    /// <param name="AttemptsLeft">The remaining attempt budget to store back.</param>
    /// <param name="ReArmed">The skeleton changed, so the budget was refilled (debug marker only).</param>
    internal readonly record struct Decision(
        bool ShouldInspect,
        ulong SkeletonProbeId,
        int AttemptsLeft,
        bool ReArmed);

    /// <summary>
    /// Decide whether a spine root with an empty animation list should be re-inspected this tick.
    /// </summary>
    /// <param name="skeletonInstanceId">The node's CURRENT `skeleton_data_res` instance id; 0 = none assigned.</param>
    /// <param name="lastSkeletonInstanceId">The id seen at the previous evaluation (0 initially).</param>
    /// <param name="attemptsLeft">The remaining attempt budget.</param>
    /// <param name="maxAttempts">The budget to refill to on a skeleton transition.</param>
    public static Decision Decide(
        ulong skeletonInstanceId,
        ulong lastSkeletonInstanceId,
        int attemptsLeft,
        int maxAttempts)
    {
        var reArmed = skeletonInstanceId != lastSkeletonInstanceId;
        if (reArmed)
        {
            attemptsLeft = maxAttempts;
        }
        else if (skeletonInstanceId == 0)
        {
            // Still waiting for the runtime injection: nothing new to learn, and no attempt spent — this is the
            // state a node can legitimately sit in for thousands of ticks.
            return new Decision(false, skeletonInstanceId, attemptsLeft, ReArmed: false);
        }

        if (attemptsLeft <= 0)
        {
            return new Decision(false, skeletonInstanceId, attemptsLeft, reArmed);
        }

        return new Decision(true, skeletonInstanceId, attemptsLeft - 1, reArmed);
    }
}
