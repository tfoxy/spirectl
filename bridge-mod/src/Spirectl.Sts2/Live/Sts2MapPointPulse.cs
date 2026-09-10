using System;
using Spirectl.Sts2.Core.SceneInspection;

namespace Spirectl.Sts2.Live;

// R12b producer MAP-POINT PULSE FOLD — PURE and Godot-free (like Sts2DecorEmitSuppress / Sts2RegionEmitCap /
// Sts2CosmeticEmitCap) so the math + policy are unit-testable without the Godot-coupled Sts2RuntimeSceneWatcher.
//
// WHY. After WS-D pinned the four purely-decorative animators, ONE per-frame churner was deliberately left running
// and became the top residual: `ui/normal_map_point ▸ IconContainer`. Measured off the recorded mirror wire, a map
// screen with the WS-D fix ON still streams ~15-24 deltas/s, and every one of them is a map-point icon container.
//
// ON SCREEN. A travelable map point breathes: its icon container sweeps a UNIFORM scale over 0.95..1.45 on a
// sine, period 1570.8ms (4 rad/s), each point starting at its own random phase so the map does not pulse in
// lockstep. It breathes only while the point is enabled, unfocused and input is allowed — hovering it, travelling,
// or opening the map's drawing mode stops it, and the scale settles back to 1.
//
// So — unlike WS-D's four garnish animators — WHICH nodes pulse is GAMEPLAY INFORMATION (it is how the game shows
// you where you may travel next). Pinning it to rest and stopping there would delete a real affordance. This is a
// REAL FOLD instead: the producer pins the churning channel AND ships a declarative per-node flag
// (`RuntimeSceneNodeDelta.PinnedLoopAnim = "mapPointPulse"`) that changes only when pulse MEMBERSHIP changes, so
// the client replays the pulse on its own clock. Steady state on a map screen is therefore ZERO deltas from the
// map points; a travelable-set change (a click-through) costs ONE upsert per node that entered/left the set.
//
// WHAT WE PIN, AND TO WHAT. Only the node's UNIFORM SCALE, divided back out of the streamed transform (see
// PinRestScale). Not the whole transform: each point also carries a small one-off random TILT, which is not
// recoverable analytically and must keep streaming.
//
// The rest scale is ANALYTIC — exactly 1 — not a first-seen sample. The pulse is the only channel that ever takes
// the scale off 1, and every way of leaving the pulse (settling, deselecting, travelling) ends back at 1, so the
// pin can never strand a node mid-breath. A first-seen sample COULD have: the phase is randomised per point, so
// the first frame observed is an ARBITRARY point of the sine — a node first seen while already travelable would
// have been frozen at up to 1.45x forever.
//
// LOCAL-TRANSFORM MODE ONLY (see the watcher's guard). The client renders this node's DOM children NESTED inside
// its element, so the CSS pulse it re-applies to the container cascades to the icons — which is only equivalent to
// the game when the children are streamed as PARENT-RELATIVE locals. In global mode each child streams its own
// absolute (still-pulsing) global and the client would double-apply; the fold is therefore a no-op there
// (byte-identical to today), exactly like the mirror's other local-space-only machinery.
//
// KNOWN COST, accepted (kill-switch SPIRECTL_MAPPOINT_FOLD=0):
//   * The 0.3s `OnSelected` shrink-to-rest and the `Lerp(One, 0.5f)` settle are no longer streamed — a node
//     leaving the travelable set snaps from its client-side pulse straight to rest instead of easing there.
//   * A client that does NOT consume `PinnedLoopAnim` (today: the native Godot client) renders map points at rest.
//     Its hook points are `MirrorNode.PinnedLoopAnim` + `CosmeticAnimator.BindingFor`; until then the switch above
//     restores the streamed pulse for that client.
internal static class Sts2MapPointPulse
{
    /// <summary>The instanced scene of one travelable map point.</summary>
    internal const string SceneFile = "res://scenes/ui/normal_map_point.tscn";

    /// <summary>Scene-relative path of the container that carries the pulse.</summary>
    internal const string IconContainerRelPath = "IconContainer";

    /// <summary>
    /// The `PinnedLoopAnim` token shipped while a point is pulsing. The client owns the vocabulary (period,
    /// amplitude, easing) exactly like the path-keyed decorative-animation fold it extends — the producer only
    /// says WHICH loop it pinned, so the wire stays one short interned string.
    /// </summary>
    internal const string LoopAnimName = "mapPointPulse";

    /// <summary>The analytic resting scale every non-pulsing map point converges to.</summary>
    internal const double RestScale = 1.0;

    // The pulse's own constants, single-sourced here so a client-side replay can be checked against ONE place:
    // scale(t) = Sin(t) * ScaleAmount + ScaleBase, with t advancing at RateRadPerSec.
    internal const double ScaleAmount = 0.25;
    internal const double ScaleBase = 1.2;
    internal const double RateRadPerSec = 4.0;

    /// <summary>Below this the live scale is treated as degenerate and the transform is left untouched.</summary>
    private const double MinScale = 1e-4;

    /// <summary>Scales within this of <see cref="RestScale"/> need no pin (the common not-pulsing case).</summary>
    private const double ScaleEpsilon = 1e-4;

    /// <summary>True when this scene identity is the pulsing map-point icon container.</summary>
    internal static bool IsIconContainer(string? sceneFilePath, string? relPath)
        => string.Equals(sceneFilePath, SceneFile, StringComparison.Ordinal)
           && string.Equals(relPath, IconContainerRelPath, StringComparison.Ordinal);

    /// <summary>
    /// True when this map point is pulsing RIGHT NOW: enabled, not focused, and accepting input. The watcher reads
    /// the three values off the scene root (they are all registered Godot script members, so no System.Reflection);
    /// keeping the boolean algebra here keeps the gate testable and single-sourced.
    /// </summary>
    internal static bool IsPulsing(bool isEnabled, bool isFocused, bool isInputAllowed)
        => isEnabled && !isFocused && isInputAllowed;

    /// <summary>
    /// Divide the pulse's UNIFORM scale back out of <paramref name="live"/>, leaving the node's rotation, skew and
    /// placement exactly as streamed. Returns <paramref name="live"/> itself (no allocation) when there is nothing
    /// to pin — the not-pulsing steady state, which is most map points on most ticks.
    /// </summary>
    /// <param name="live">The transform as read this tick (the node's LOCAL transform, see the mode guard above).</param>
    /// <param name="liveScale">The pulse's current uniform factor `k` (the Control's `Scale.X`).</param>
    /// <param name="pivotX">`Control.PivotOffset.X` — Godot bakes the pivot into a Control's transform.</param>
    /// <param name="pivotY">`Control.PivotOffset.Y`.</param>
    /// <remarks>
    /// A Godot Control's transform is `M(k) = T(pos + p) · R · S(k) · T(-p)` (p = PivotOffset), so the pulse moves
    /// BOTH the basis (x k) and the origin (`p - k·R·p`) — dividing the basis alone would slide the icon off its
    /// pivot. The exact undo is a RIGHT-multiplication by the pivot-anchored inverse scale:
    ///     C = M(k)^-1 · M(1) = T(p) · S(1/k) · T(-p)
    /// which is independent of R and of `pos`, so `live · C` restores the resting transform whatever the node's
    /// tilt/position — and, because it is a right-multiplication, it works on a GLOBAL transform too
    /// (`(A·M(k))·C = A·M(1)`), which is why the formula needs no separate global-mode branch.
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
            || Math.Abs(liveScale - RestScale) <= ScaleEpsilon)
        {
            return live;
        }

        var inv = RestScale / liveScale;
        // C.origin = p - p/k, lifted through the live basis (Transform2D composition: origin' = basis·C.origin + origin).
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
}
