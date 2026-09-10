using System;
using Spirectl.Sts2.Core.SceneInspection;

namespace Spirectl.Sts2.Live;

// R13 producer PROCEED-GLOW FOLD — PURE and Godot-free (like Sts2TopBarFold / Sts2MapPointPulse) so the gate algebra
// and the pin are unit-testable without the Godot-coupled Sts2RuntimeSceneWatcher.
//
// WHY. The shared `ui/proceed_button` Outline is an additive #ffcc00 glow whose SELF-MODULATE ALPHA pulses every
// single frame, forever, on every screen that shows Proceed. Measured off the recorded mirror wire it is the SOLE
// churner on a treasure room (60.1 deltas/s) and 91% of a card-reward screen's — a phone mirroring a still screen
// paying a full delta pipeline for a ±25% alpha shimmer.
//
// WS-D silenced it by pinning the alpha to the node's first-emitted sample. That worked for the wire but deleted the
// shimmer, and — like the top-bar pin it shipped with — leaned on a first-seen sample being a resting value. Here it
// is provably NOT: the button starts out DISABLED and its stop path fades the alpha to **0**, so the first-seen
// rest is 0 while the loop's own rest is 0.75. This is the same
// REAL FOLD the map point and the top bar use instead: pin the channel ANALYTICALLY and ship a declarative
// `RuntimeSceneNodeDelta.PinnedLoopAnim = "proceedGlow"` token that changes only when the loop starts or stops, so
// the client replays the shimmer on its own clock and the steady state is ZERO deltas.
//
// ON SCREEN. The glow breathes: an infinite 0.75 <-> 0.25 alpha sweep, two 0.5s LINEAR legs, period 1000ms. Each
// cycle starts and ends at 0.75, which is the analytic rest this fold pins to.
//
// THE GATE. The shimmer runs exactly while the button is ENABLED, marked as wanting to pulse, and NOT focused —
// three registered script variables the watcher can read directly. Everything that stops the shimmer flips one of
// them: focusing it flashes to alpha 1 in 0.05s, pressing or clearing the pulse flag fades to 0 over 0.5s, and
// disabling it kills the loop outright. So `IsLooping` below is exact, and every non-loop state — the focus flash,
// the fade-to-0, the disabled hold — streams live, untouched, at its true value. Alpha 0 is MEANINGFUL here (it is
// how the glow goes away), which is the other reason the pin can never be "whatever we saw first".
//
// KNOWN COST, accepted (kill switches: SPIRECTL_PROCEED_FOLD=0, or the master SPIRECTL_DECOR_EMIT_SUPPRESS=0): a
// client that does NOT consume `PinnedLoopAnim` (today: the native Godot client) renders a steady glow at 0.75
// instead of a shimmer — the loop's own resting brightness, i.e. what the button looks like at the top of every
// cycle. Its hook points are `MirrorNode.PinnedLoopAnim` + `CosmeticAnimator.BindingFor`, the same pair
// Sts2MapPointPulse and Sts2TopBarFold need.
internal static class Sts2ProceedGlow
{
    /// <summary>The shared instanced scene of the Proceed button.</summary>
    internal const string SceneFile = "res://scenes/ui/proceed_button.tscn";

    /// <summary>Scene-relative path of the pulsing glow outline.</summary>
    internal const string OutlineRelPath = "Image/Outline";

    /// <summary>
    /// The `PinnedLoopAnim` token shipped while the glow loop runs. The client owns the vocabulary (period, easing,
    /// alpha range) exactly like Sts2MapPointPulse / Sts2TopBarFold — the producer only names the loop.
    /// </summary>
    internal const string LoopAnimName = "proceedGlow";

    // The loop's own constants, single-sourced here so a client-side replay can be checked against ONE place:
    // two LINEAR legs sweeping self-modulate alpha 0.75 -> 0.25 -> 0.75.
    internal const double LoopMinAlpha = 0.25;
    internal const double LoopMaxAlpha = 0.75;
    internal const double LoopLegMs = 500.0;
    internal const double LoopPeriodMs = 2.0 * LoopLegMs;

    /// <summary>
    /// The analytic value the pin substitutes while the loop runs: the alpha each loop iteration starts and ends at
    /// (the second leg's endpoint). NOT a first-seen sample — see the type comment.
    /// </summary>
    internal const double PinnedAlpha = LoopMaxAlpha;

    /// <summary>The focus flash: self-modulate alpha to 1 in 0.05s. Streamed live (the loop is dead while focused).</summary>
    internal const double FocusAlpha = 1.0;
    internal const double FocusMs = 50.0;

    /// <summary>The stop fade: self-modulate alpha to 0 in 0.5s. Streamed live.</summary>
    internal const double StoppedAlpha = 0.0;
    internal const double StopMs = 500.0;

    /// <summary>True when this scene identity is the pulsing proceed-button glow.</summary>
    internal static bool IsOutline(string? sceneFilePath, string? relPath)
        => string.Equals(sceneFilePath, SceneFile, StringComparison.Ordinal)
           && string.Equals(relPath, OutlineRelPath, StringComparison.Ordinal);

    /// <summary>
    /// True when the infinite glow loop is running RIGHT NOW: enabled, wanting to pulse, and not focused. The
    /// watcher reads the three values off the scene root (all registered Godot script members, so no
    /// System.Reflection); keeping the boolean algebra here keeps the gate testable and single-sourced.
    /// </summary>
    internal static bool IsLooping(bool isEnabled, bool shouldPulse, bool isFocused)
        => isEnabled && shouldPulse && !isFocused;

    /// <summary>
    /// The self-modulate value to emit while the loop runs: this tick's LIVE rgb with the ANALYTIC loop alpha, so a
    /// real colour/state change still ships while the per-frame alpha sweep is invisible to the change test. Returns
    /// <paramref name="live"/> unchanged (no allocation) when it is null or its alpha already sits at the pin. Html
    /// is left null — the watcher formats it only for emitted nodes.
    /// </summary>
    internal static RuntimeSceneColorSnapshot? PinAlpha(RuntimeSceneColorSnapshot? live)
    {
        if (live is null || live.A.Equals(PinnedAlpha))
        {
            return live;
        }

        return new RuntimeSceneColorSnapshot(live.R, live.G, live.B, PinnedAlpha, null);
    }
}
