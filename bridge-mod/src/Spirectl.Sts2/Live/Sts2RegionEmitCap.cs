using System;
using Spirectl.Sts2.Core.SceneInspection;

namespace Spirectl.Sts2.Live;

// R11 producer region-emit cap decision — PURE and Godot-free (like Sts2TweenEndpointTuples) so the pacing is
// unit-testable without the Godot-coupled Sts2RuntimeSceneWatcher. A Sprite2D playing an AtlasTexture flip-book (the
// Tezcatara candle flames) swaps its TextureRegion crop EVERY tick → a per-frame region delta per flame floods
// producer→wire→client. The watcher's capture loop calls Decide before ApplyIfChanged and, on Pin, substitutes the
// previously-emitted region into the volatile read (invisible to the change signature; BuildNodeDelta reads the same
// substituted value so the wire and the tracked last-region stay consistent).
internal static class Sts2RegionEmitCap
{
    internal enum Decision
    {
        // Not a pure same-size frame swap: cap off, a null↔non-null transition, an unchanged region, or a
        // size-changing swap (a real LocalRect Draw). Leave the region untouched; do not touch the emit window.
        Pass,

        // A same-size swap still inside the cap window → substitute the previously-emitted region (withhold this frame
        // until the window elapses).
        Pin,

        // A same-size swap whose window elapsed → let this frame through and restart the window (10Hz default pacing).
        Emit,
    }

    // 2-dp quantized comparisons, matching the watcher's own RectEq quantization (transforms/rects rounded to 2 dp).
    private static bool QEq(double a, double b) => Math.Round(a, 2) == Math.Round(b, 2);

    private static bool SameSize(RuntimeSceneRect2Snapshot a, RuntimeSceneRect2Snapshot b)
        => QEq(a.Size.X, b.Size.X) && QEq(a.Size.Y, b.Size.Y);

    private static bool SamePosition(RuntimeSceneRect2Snapshot a, RuntimeSceneRect2Snapshot b)
        => QEq(a.Position.X, b.Position.X) && QEq(a.Position.Y, b.Position.Y);

    internal static Decision Decide(
        RuntimeSceneRect2Snapshot? last, RuntimeSceneRect2Snapshot? cur, long lastEmitAtMs, long now, int capMs)
    {
        if (capMs <= 0 || last is null || cur is null)
        {
            return Decision.Pass;
        }

        // A pure atlas-FRAME swap = SAME size, DIFFERENT position. An equal region (same size + same position) is no
        // change; a size-changing swap is a real Draw (Sprite2D LocalRect derives from region size) — both Pass.
        if (!SameSize(last, cur) || SamePosition(last, cur))
        {
            return Decision.Pass;
        }

        return now - lastEmitAtMs < capMs ? Decision.Pin : Decision.Emit;
    }
}
