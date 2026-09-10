using System;
using Spirectl.Sts2.Core.SceneInspection;

namespace Spirectl.Sts2.Live;

// R14 producer END-TURN GLOW FOLD — PURE and Godot-free (like Sts2ProceedGlow, whose shape it copies) so the gate
// algebra and both pins are unit-testable without the Godot-coupled Sts2RuntimeSceneWatcher.
//
// WHY THIS ONE IS MANDATORY FOR AN IDLE COMBAT. The other three combat churners (Sts2IntentBobFold,
// Sts2OrbSpinFold, Sts2IntentGlyphFold) run all the time. This one runs EXACTLY when the combat is idle-waiting —
// it is your turn, you have nothing left to play, and you have not ended the turn yet, i.e. precisely the state a
// player sits in while thinking. So without this fold the wire can never go quiet during the one window the idle
// machinery (the mirror client's 200ms DOM hatchery, the headless host's 1000ms MaxFps backoff) exists to exploit.
//
// ON SCREEN. The End Turn button grows a soft halo that swells and fades away, over and over: an infinite 1500ms
// cycle on `Visuals/GlowVfx` running two channels in parallel — a uniform scale 0.5 -> 0.7 (Quart/Out) and an
// alpha 0.4 -> 0 (LINEAR) — both snapping back to their start at every iteration. Same looping shape as the
// proceed-button glow, on two channels instead of one.
//
// THE GATE. The pulse is on exactly while the button's "shiny" flag is set: it is raised immediately before the
// loop starts and cleared immediately before it is killed, so the flag and the loop are the same fact. It is a
// registered Godot script variable (`PropertyUsageFlags.ScriptVariable`), so the watcher reads it off the scene
// root with `Get` — no System.Reflection — and the gate is EXACT. Everything that is not the infinite loop streams
// live and untouched: the 0.5s Expo/Out fade-to-0 that ends a pulse, and the authored resting modulate whose alpha
// is 0 (the glow is simply absent, which is MEANINGFUL and must never be confused with the loop's own alpha).
//
// WHAT WE PIN, AND TO WHAT. The two channels the loop writes, at the values each cycle BEGINS AND RESTARTS at —
// the analytic rest, never a first-seen sample:
//   * uniform scale → 0.5 (`PinnedScale`), divided back out of the transform about the node's own pivot, so the
//     button's placement/anchoring/layout keeps streaming live;
//   * `modulate.a`  → 0.4 (`PinnedAlpha`), substituted into modulate (RGB keeps streaming, so a real colour change
//     still ships) and into the node's `Opacity`, which the watcher reads as `modulate.A`.
// A sampled pin would be catastrophic here in a way it is not for the proceed glow: BOTH of this loop's channels
// sweep monotonically away from their start and snap back, so a sample taken mid-cycle is neither an endpoint nor
// an average — a glow frozen at alpha 0.05 is invisible, and one frozen at 0.7 scale is permanently oversized.
//
// THE CLIENT REPLAY multiplies both pins back up (see `animAttributes.ts`'s `endTurnGlow` token → presentation's
// `pulseScaleFade`): scale 1 → `PinnedScale`→`LoopMaxScale` ratio = 1.4, opacity 1 → `LoopEndAlpha`/`PinnedAlpha`
// = 0, over `LoopPeriodMs`. Both ratios are exported below so the replay can be checked against ONE place.
//
// LOCAL-TRANSFORM MODE ONLY for the scale half (the watcher's guard), for uniformity with the other transform
// folds; `GlowVfx` is a childless TextureRect and the pin is a right-multiplication, so nothing could double-apply
// either way. The alpha half needs no mode guard (opacity is a per-node-local channel).
//
// KNOWN COST, accepted (kill switches: SPIRECTL_ENDTURN_GLOW_FOLD=0, or the master SPIRECTL_DECOR_EMIT_SUPPRESS=0):
// a client that does NOT consume `PinnedLoopAnim` renders a steady glow at the cycle's opening pose (scale 0.5,
// alpha 0.4) instead of a pulse — the brightest, smallest frame of the loop, i.e. what the button looks like at the
// top of every cycle.
internal static class Sts2EndTurnGlowFold
{
    /// <summary>The instanced scene of the End Turn button.</summary>
    internal const string SceneFile = "res://scenes/combat/end_turn_button.tscn";

    /// <summary>Scene-relative path of the pulsing glow.</summary>
    internal const string GlowVfxRelPath = "Visuals/GlowVfx";

    /// <summary>
    /// The `PinnedLoopAnim` token shipped while the pulse loop runs. The client owns the vocabulary (period,
    /// easing, the two ratios) exactly like Sts2ProceedGlow / Sts2TopBarFold — the producer only names the loop.
    /// </summary>
    internal const string LoopAnimName = "endTurnGlow";

    // The loop's own constants, single-sourced here.
    internal const double LoopStartScale = 0.5;
    internal const double LoopMaxScale = 0.7;
    internal const double LoopStartAlpha = 0.4;
    internal const double LoopEndAlpha = 0.0;
    internal const double LoopPeriodMs = 1500.0;

    /// <summary>The analytic value the scale pin substitutes: the scale each loop iteration starts and restarts at.</summary>
    internal const double PinnedScale = LoopStartScale;

    /// <summary>The analytic value the alpha pin substitutes: the alpha each loop iteration starts and restarts at.</summary>
    internal const double PinnedAlpha = LoopStartAlpha;

    /// <summary>
    /// The client-side replay's scale endpoints, RELATIVE to <see cref="PinnedScale"/> (the pin is baked into the
    /// streamed transform, so the replay can only multiply). 1 → 1.4.
    /// </summary>
    internal const double ReplayScaleFrom = 1.0;
    internal const double ReplayScaleTo = LoopMaxScale / LoopStartScale;

    /// <summary>The client-side replay's opacity endpoints, RELATIVE to <see cref="PinnedAlpha"/>. 1 → 0.</summary>
    internal const double ReplayAlphaFrom = 1.0;
    internal const double ReplayAlphaTo = LoopEndAlpha / LoopStartAlpha;

    /// <summary>The stop leg: modulate alpha to 0 in 0.5s (Expo/Out). Streamed live.</summary>
    internal const double StoppedAlpha = 0.0;
    internal const double StopMs = 500.0;

    /// <summary>Below this the live scale is treated as degenerate and the transform is left untouched.</summary>
    private const double MinScale = 1e-4;

    /// <summary>Scales within this of <see cref="PinnedScale"/> need no pin.</summary>
    private const double ScaleEpsilon = 1e-4;

    /// <summary>True when this scene identity is the pulsing end-turn glow VFX.</summary>
    internal static bool IsGlowVfx(string? sceneFilePath, string? relPath)
        => string.Equals(sceneFilePath, SceneFile, StringComparison.Ordinal)
           && string.Equals(relPath, GlowVfxRelPath, StringComparison.Ordinal);

    /// <summary>
    /// True when the infinite pulse loop is running RIGHT NOW. The "shiny" flag is raised immediately before the
    /// loop starts and cleared immediately before it is killed, so the flag and the loop are the same fact. Kept
    /// here so the gate is testable and single-sourced.
    /// </summary>
    internal static bool IsLooping(bool isShiny) => isShiny;

    /// <summary>
    /// Divide the pulse's UNIFORM scale back out of <paramref name="live"/> down to <see cref="PinnedScale"/>,
    /// leaving the node's rotation, skew and placement exactly as streamed. Returns <paramref name="live"/> itself
    /// (no allocation) when the live scale already sits at the pin.
    /// </summary>
    /// <param name="live">The transform as read this tick.</param>
    /// <param name="liveScale">`Control.Scale.X` — the loop's current uniform factor `k`.</param>
    /// <param name="pivotX">`Control.PivotOffset.X` (256 — the authored centre of the 512×256 box).</param>
    /// <param name="pivotY">`Control.PivotOffset.Y` (128).</param>
    /// <remarks>
    /// Same pivot-anchored right-multiplication as <see cref="Sts2MapPointPulse.PinRestScale"/>, except the rest is
    /// the loop's own 0.5 rather than 1: `C = M(k)^-1 · M(r) = T(p) · S(r/k) · T(-p)`, independent of the node's
    /// rotation and position, and (being a right-multiplication) equally valid on a global transform.
    /// </remarks>
    internal static RuntimeSceneTransform2DSnapshot? PinRestScale(
        RuntimeSceneTransform2DSnapshot? live,
        double liveScale,
        double pivotX,
        double pivotY)
    {
        if (live is null
            || !double.IsFinite(liveScale)
            || Math.Abs(liveScale) < MinScale
            || Math.Abs(liveScale - PinnedScale) <= ScaleEpsilon)
        {
            return live;
        }

        var inv = PinnedScale / liveScale;
        // C.origin = p - (r/k)·p, lifted through the live basis (origin' = basis·C.origin + origin).
        var shift = 1.0 - inv;
        var cx = pivotX * shift;
        var cy = pivotY * shift;
        return new RuntimeSceneTransform2DSnapshot(
            new RuntimeSceneVector2Snapshot(live.XAxis.X * inv, live.XAxis.Y * inv),
            new RuntimeSceneVector2Snapshot(live.YAxis.X * inv, live.YAxis.Y * inv),
            new RuntimeSceneVector2Snapshot(
                live.Origin.X + live.XAxis.X * cx + live.YAxis.X * cy,
                live.Origin.Y + live.XAxis.Y * cx + live.YAxis.Y * cy));
    }

    /// <summary>
    /// The modulate value to emit while the loop runs: this tick's LIVE rgb with the ANALYTIC loop alpha, so a real
    /// colour change still ships while the per-frame alpha sweep is invisible to the change test. Returns
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

    /// <summary>
    /// The `Opacity` to emit while the loop runs. The watcher reads a node's opacity as `modulate.A`, so it has to
    /// be pinned in lockstep with <see cref="PinAlpha"/> — otherwise the per-frame alpha sweep would keep the node
    /// changing through the opacity channel alone and the fold would silence nothing.
    /// </summary>
    internal static double PinOpacity(double live) => double.IsFinite(live) ? PinnedAlpha : live;
}
