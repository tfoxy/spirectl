using System;
using Spirectl.Sts2.Core.SceneInspection;

namespace Spirectl.Sts2.Live;

// R11 producer cosmetic-emit cap decision — PURE and Godot-free (like Sts2RegionEmitCap / Sts2TweenEndpointTuples) so
// the pacing is unit-testable without the Godot-coupled Sts2RuntimeSceneWatcher.
//
// The Tezcatara rest-site fire VFX (NRestSiteFireVfx + their Sprite2D/Node2D children) animate their TRANSFORM every
// frame — a continuous flicker. The recorded wire (tezca-defaultbridge.ndjson) shows the ONLY per-tick-changing channel
// is `transform` (modulate/selfModulate/zIndex/localRect/texture never change), at ~97 upserts/drain × ~30 drains/s.
// Because a transform change classifies as a Draw client-side, this both floods the wire AND keeps the wide-screen
// spread walk from bailing → the 9-14fps signature.
//
// NOTE: this is DEFAULT OFF in the watcher (SPIRECTL_COSMETIC_EMIT_CAP_MS). A replay A/B proved the Tezcatara fps is
// bounded by client-side particle/shader re-simulation (~166 continuous nodes), NOT the upsert flood — stripping 98% of
// transform upserts moved fps only 11→12. The cap is a correct BANDWIDTH/GC lever (cuts wire + client GC) with no fps
// win here; enable it where wire/GC pressure matters (phones). The pacing logic below is what runs when enabled.
//
// This paces SUSTAINED per-frame transform churn to at most one emit per window per node, mirroring Sts2RegionEmitCap:
// within the window the previously-emitted transform is substituted before the change test so the swap is invisible to
// the change signature (BuildNodeDelta reads the same substituted transform, so the wire and tracked.LastTransform stay
// consistent — no phantom re-emit). The window then elapses and the newest transform ships (flames keep animating —
// paced to ~10-15Hz, never frozen).
//
// A SUSTAINED-CHURN detector (the streak) is what keeps this cosmetic-only: a node is paced only after its transform has
// moved for >= K consecutive ticks. A one-shot move (a card play snap, a reparent) moves for a tick or two then stops —
// its streak never reaches K, so it streams INSTANTLY (never delayed). The moment churn breaks (a stationary tick) the
// streak resets and the node passes through, so it settles to its EXACT final transform (the stale substituted value is
// never left behind). Churn is detected at FULL precision (a tiny epsilon, finer than the watcher's 2-dp emit
// threshold) precisely so a flame that only crosses the 2-dp threshold intermittently is still recognised as
// continuously churning — otherwise the streak would keep breaking on sub-threshold ticks and the pacing would barely
// fire.
internal static class Sts2CosmeticEmitCap
{
    internal enum Decision
    {
        // Cap off, missing history, OR the transform did not move this tick (churn broke) OR churn is not yet sustained
        // (streak < K). Leave the transform untouched and let the watcher's normal change test decide — this is what
        // makes one-shot moves instant and makes a churn-exit settle to the exact final value. Never touch the window.
        Pass,

        // Sustained churn, still inside the cap window → substitute the previously-emitted transform (withhold this
        // frame's motion until the window elapses).
        Pin,

        // Sustained churn whose window elapsed → let this frame's transform through and restart the window.
        Emit,
    }

    // Full-precision motion test (deliberately FINER than the watcher's 2-dp XformEq): any component differing by more
    // than a hair counts as churn, so a continuously-animating flame is detected as churning every tick even on the
    // ticks where its rounded (emitted) value happens not to cross the 2-dp threshold.
    private const double MoveEpsilon = 1e-4;

    private static bool Moved(RuntimeSceneTransform2DSnapshot a, RuntimeSceneTransform2DSnapshot b)
        => Math.Abs(a.XAxis.X - b.XAxis.X) > MoveEpsilon
           || Math.Abs(a.XAxis.Y - b.XAxis.Y) > MoveEpsilon
           || Math.Abs(a.YAxis.X - b.YAxis.X) > MoveEpsilon
           || Math.Abs(a.YAxis.Y - b.YAxis.Y) > MoveEpsilon
           || Math.Abs(a.Origin.X - b.Origin.X) > MoveEpsilon
           || Math.Abs(a.Origin.Y - b.Origin.Y) > MoveEpsilon;

    // `prevReal`/`curReal` are the node's REAL (unsubstituted) transform last tick and this tick. `streakBefore` is its
    // consecutive-churn count so far; `streakAfter` is the updated count the caller must store back. `k` is the sustained
    // threshold (ticks of continuous churn before pacing engages). Pure so it is unit-testable without the watcher.
    internal static Decision Decide(
        RuntimeSceneTransform2DSnapshot? prevReal,
        RuntimeSceneTransform2DSnapshot? curReal,
        int streakBefore,
        long lastEmitAtMs,
        long now,
        int capMs,
        int k,
        out int streakAfter)
    {
        if (capMs <= 0 || prevReal is null || curReal is null || !Moved(prevReal, curReal))
        {
            // Off, first tick, or churn broke → reset the streak and pass. The reset is what guarantees the settle
            // frame: the next Pass lets the watcher's change test ship the exact current transform.
            streakAfter = 0;
            return Decision.Pass;
        }

        streakAfter = streakBefore + 1;
        if (streakAfter < k)
        {
            // Not yet sustained — a one-shot / short move streams instantly (this is the contract that protects card
            // moves and reparents from ever being delayed).
            return Decision.Pass;
        }

        // Sustained churn: pace to one emit per window. `now - lastEmit == cap` is NOT < cap → Emit, so pacing never
        // stalls at the boundary (matches Sts2RegionEmitCap). lastEmit=0 (never emitted) → the first frame always ships.
        return now - lastEmitAtMs < capMs ? Decision.Pin : Decision.Emit;
    }
}
