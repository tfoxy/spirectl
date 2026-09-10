namespace Spirectl.Sts2.Live;

// Producer reparent-emit transform-omission decision — PURE and Godot-free (like Sts2RegionEmitCap /
// Sts2CosmeticEmitCap / Sts2TweenEndpointTuples) so it is unit-testable without the Godot-coupled watcher.
//
// Background (P2 discard-select flash). The round-3 reparent force-emit (Sts2SceneWatcher: parentChanged →
// wholesale re-attach) ships the node's CURRENT streamed transform. When the reparent coincides with a
// streaming-suppression window (the mirror is REPLAYING a tween on this node or an ancestor — e.g. a hand card
// selected for discard reparents into a fresh NSelectedHandCardHolder under the centre container WHILE that
// container lifts to centre on a real tween), the node's live transform at the emit tick is its TRANSITION-START
// value (the card still sitting at the hand position, re-based under the new centre holder ≈ local (0, +677) =
// global bottom-centre). The client wholesale-adopts that transform and PINS it (the per-frame deltas are
// suppressed for the rest of the tween window), so the card flashes at the transition-start position until the
// window closes and the settle re-emit snaps it to centre — the observed "1-frame lower-right, holds, then snaps
// to centre" defect.
//
// Fix: OMIT the transform payload from the reparent force-emit while a suppression window covers the node. The
// wire/DTO transform is already nullable; the client's MergeNode wholesale-adopts the (Name-bearing) upsert, so
// Transform=null makes the node hold its LAST-KNOWN placed local (the Q6 HasPlacedTransform stamp) — for a hand
// card that is local (0,0), which under the new centre holder renders AT CENTRE immediately. The card rides the
// container's tween to centre and the settle re-emit lands exact. Deselect (no tween ⇒ no window) is unaffected.
//
// Scope guards:
//   * parentChanged  — only the reparent force-emit path leaks a transform under suppression; a normal
//                       changedNow emit outside a window still ships its transform.
//   * suppressTransform — only when a tween window actually covers the node (deselect and any un-tweened reparent
//                       keep shipping their transform, so the client re-parents AND re-places in one upsert).
//   * !justAdded     — a brand-new node (JustAdded) has no last-known transform to hold, so it MUST ship its
//                       initial placement; omitting it would strand the fresh node at the origin.
//
// DEFAULT ON; SPIRECTL_SCENE_WATCH_REPARENT_HOLD_TWEENED=0 seeds it OFF (restoring the round-3 ship-the-transform
// behaviour). Read on the game main thread in the capture loop; a one-tick stale read is harmless.
internal static class Sts2ReparentEmit
{
    // True when the reparent force-emit should ship its upsert WITHOUT a transform payload (client holds its
    // last-known placed local). Pure so it is unit-testable without the watcher.
    internal static bool OmitTransformOnReparent(bool enabled, bool parentChanged, bool suppressTransform, bool justAdded)
        => enabled && parentChanged && suppressTransform && !justAdded;
}
