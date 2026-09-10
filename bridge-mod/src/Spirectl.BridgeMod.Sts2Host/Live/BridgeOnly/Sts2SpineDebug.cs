using Godot;

namespace Spirectl.Sts2.Live;


// SPIRECTL_SPINE_DEBUG=1 diagnostic lever for the spine capture/bake pipeline. When ON it emits `[spine-debug]`
// lines (via GD.Print — sts2 nulls stdio, so GD.Print is the only reliable sink) tracing exactly WHERE a
// creature's live animation comes from and whether the producer ever streams a removal/flip for it:
//   * SetNextState postfix — the game-logic anim transitions the schedule replays (state id / looping /
//     next-chain / one-shot duration). The authoritative source for a couch-frozen creature.
//   * OnAnimationStarted accepted/rejected — the signal-cache path, with the reject reason (nil track-0,
//     empty/`_ignore` name, re-asserted loop) so a missing anim change is attributable.
//   * The FIRST PickDefaultAnimation fallback per node — a creature that reaches this never fired a real
//     transition, so it plays a guessed idle forever (root of "death anim not played", "one frame" symptoms).
//   * Spine node add/evict (+ a 0-animations-at-add marker — the #8 "skeleton assigned after add → empty
//     animation list forever → client never fetches" trap) and Visible/opacity flips + RemovedIds membership
//     for spine roots (so #6 "exploded giant lingers" is decidable: did the producer emit Visible=false / drop
//     the id, or not?).
//
// Read ONCE at type load (env vars are process-stable). OFF = a single field read at each guarded call site and
// zero string work, so it can ship on the hot capture path without cost. A diagnostic must NEVER disrupt the
// host: every emit is wrapped in try/catch. Wave-2 live-repro agents rely on these markers.
internal static class Sts2SpineDebug
{
    public static readonly bool Enabled =
        (System.Environment.GetEnvironmentVariable("SPIRECTL_SPINE_DEBUG") ?? string.Empty)
            .Trim().ToLowerInvariant() is "1" or "true" or "on" or "yes";

    // Emit a diagnostic line. Callers gate the (string-building) call on Sts2SpineDebug.Enabled so nothing is
    // allocated when off; the guard here is belt-and-suspenders. Never throws.
    public static void Log(string message)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            GD.Print($"[spine-debug] {message}");
        }
        catch
        {
            // A diagnostic must never disrupt the game.
        }
    }
}
