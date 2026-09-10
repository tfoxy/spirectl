#if ENABLE_STS2_LIVE_HOST
using Godot;
#endif

using Spirectl.Sts2.Core.SceneInspection;

namespace Spirectl.Sts2.Live;

// Producer decision: SKIP a node's ENTIRE subtree when an EMBEDDER has stamped it with the `spirectl_stream_skip`
// Godot metadata key — the decision itself is PURE and Godot-free (like Sts2ViewportContentPrune / Sts2ReparentEmit)
// so it is unit-testable without the Godot-coupled watcher; only the one-line convenience overload that probes the
// live node needs Godot (hence the live-host guard, as in Sts2LiveIntrospection).
//
// Background. The runtime scene watcher streams EVERY node under the game's root to mirror clients — including nodes
// the game itself never authored. An embedder (the CouchCoop mod) injects its own Godot UI into the live tree by
// `AddChild`ing hand-built `Control` subtrees onto a game screen (the host lobby's "Show Couch Co-Op QR Code" button
// and its QR dialog are added to `NCharacterSelectScreen` / `NMultiplayerLoadGameScreen`). Those nodes are HOST-LOCAL
// chrome: they exist to be clicked on the TV by the person sitting at the machine, and they are meaningless — worse,
// actively harmful — on a phone.
//
// Why this is mandatory rather than cosmetic: mirror clients drive the host with REAL injected input (the action
// handler warps the host mouse and clicks), so a phone tapping the MIRRORED image of a host-only button would open
// that dialog on the host's TV, in the middle of somebody else's lobby. Filtering has to happen at the producer, not
// at each client.
//
// Contract (embedder-facing, published — this comment is its specification):
//   * The embedder calls `node.SetMeta("spirectl_stream_skip", true)` on the SUBTREE ROOT — ideally BEFORE
//     `AddChild`, so the node is already stamped the first time the watcher walks it and it never reaches the wire
//     even for one tick.
//   * PRESENCE of the key is the whole signal; the value is never read (any Variant works, `true` is the convention).
//     There is deliberately no "false means stream me" escape — an embedder that wants the subtree back calls
//     `RemoveMeta`, which is exactly symmetric (see below).
//   * The stamp covers the whole subtree: the walk returns before descending, so descendants need no stamp of their
//     own and can never leak by being reparented deeper under a stamped root.
//
// Placement in the walk: the check is the FIRST statement of `Sts2RuntimeSceneWatcher.ReconcileNode`, ABOVE the
// `node is not CanvasItem` branch. That matters — an injected root is often a plain logic `Node` (or a `CanvasLayer`)
// whose CanvasItem children carry the visuals; testing after the CanvasItem branch would descend into and stream them
// anyway. Above it, a stamped root of ANY node type ends the walk for that branch.
//
// Removal symmetry is free: a skipped node never enters the watcher's `_registry`, so there is nothing to evict.
// Anything that WAS tracked before the stamp appeared simply stops appearing in `seen` and `Reconcile`'s stale sweep
// emits proper `RemovedIds` for it; if the meta is later removed the walk descends again, the nodes are absent from
// `_registry`, so they are re-added as `JustAdded` and ship a full static-bearing upsert.
//
// Cost: one metadata-dictionary probe per node, on `Reconcile` ticks only (structure-dirty or keyframe), not on the
// per-frame volatile read. The `enabled` gate is evaluated FIRST so the kill-switch also removes the probe.
//
// Scope guard — this is applied ONLY to the streaming watcher, deliberately NOT to `Sts2RuntimeSceneProvider` (the
// stateless dev `scene tree` / `scene node` / `scene hover` inspection surface). QA probes must still be able to see
// and drive the stamped UI from the CLI; hiding it there would make the embedder's own injected screens untestable.
//
// DEFAULT ON; SPIRECTL_SCENE_WATCH_HONOR_STREAM_SKIP=0 seeds it OFF (see
// Sts2SceneWatchRuntimeSettings.HonorStreamSkipMetadata), restoring the pre-fix stream. A tree in which nobody
// stamped the key is byte-identical either way. Read on the game main thread in the capture loop; a one-tick stale
// read is harmless.
internal static class Sts2StreamSkipMeta
{
    // The Godot metadata key an embedder stamps on the subtree ROOT it wants kept off the mirror wire. An ALIAS of
    // the published SpirectlSceneStreamMeta.StreamSkipMetaKey, which is what an embedder references; this internal
    // spelling stays only so the watcher's call sites read in the vocabulary of this file. One definition, so a
    // rename cannot desynchronise the producer from the contract it publishes.
    internal const string MetaKey = SpirectlSceneStreamMeta.StreamSkipMetaKey;

#if ENABLE_STS2_LIVE_HOST
    // Cached: `HasMeta(string)` marshals a fresh StringName on every call, and this runs once per node per
    // structure-dirty tick.
    private static readonly StringName MetaKeyName = MetaKey;
#endif

    // True when the reconcile walk should SKIP this node and everything under it (no tracking, no emission — and
    // therefore a clean stale-sweep removal for anything tracked from an earlier pass). Pure so it is unit-testable
    // without the watcher.
    internal static bool ShouldSkip(bool enabled, bool hasMeta)
        => enabled && hasMeta;

#if ENABLE_STS2_LIVE_HOST
    // Convenience overload for the walk: gates on `enabled` FIRST so the kill-switch also skips the HasMeta probe,
    // then funnels through the pure decision above (single source of truth for the rule).
    internal static bool ShouldSkip(bool enabled, Node node)
        => enabled && ShouldSkip(enabled, node.HasMeta(MetaKeyName));
#endif
}
