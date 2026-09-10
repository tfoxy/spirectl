using System.Collections.Generic;

namespace Spirectl.Sts2.Live;

// Q3 (P2) tween-hint emitted-parent selection order — PURE and Godot-free so the fallback precedence is
// unit-testable without the Godot-coupled watcher (the watcher supplies the live-tree ancestor chain + the
// registry-fallback resolvability; this decides WHICH source localizes the endpoint and WHICH viewport prefix).
//
// Why: for LOCAL-mode tween-endpoint localization the watcher re-bases the endpoint against the node's emitted
// parent global (EmittedParentGlobalTuple). Round-3 read that parent from the tracked node's registry ParentId,
// which is STALE at the deferred tween-finalize time of a reparent+tween same frame (the discard select): a
// freed old parent → the identity fallback ships a GLOBAL tuple as if it were LOCAL (double-apply, lower-right),
// or a still-live old parent → the endpoint is localized against the WRONG parent. Resolving the nearest
// CanvasItem ancestor from the LIVE tree finds the node's true current parent regardless.
//
// Precedence (default ON; SPIRECTL_SCENE_WATCH_TWEEN_PARENT_LIVE gates whether the watcher populates the live
// chain — an empty chain here naturally falls through to the registry fallback, i.e. the round-3 behaviour):
//   1. LiveAncestor    — the nearest live-tree ancestor that is a CanvasItem. Its viewport→screen prefix is that
//                        ancestor's own registry prefix WHEN it is tracked, else the tracked node's own prefix
//                        (same SubViewport). This is the node's true current emitted parent.
//   2. RegistryFallback — the tracked node's registry ParentId resolves to a valid CanvasItem (used when the live
//                        walk found no CanvasItem ancestor, e.g. the live walk is disabled).
//   3. Identity        — no emitted parent (a root, or nothing resolved) → identity tuple, endpoint unchanged.
internal static class Sts2TweenParentResolver
{
    // One live-tree ancestor descriptor (nearest-first): whether it is a CanvasItem (a candidate emitted parent)
    // and whether the watcher's registry currently tracks it (picks its viewport prefix vs the tracked node's).
    internal readonly record struct Candidate(bool IsCanvasItem, bool Registered);

    internal enum Source
    {
        LiveAncestor,
        RegistryFallback,
        Identity,
    }

    // Source = which provider localizes the endpoint. AncestorIndex = the chosen live-ancestor's index (LiveAncestor
    // only; -1 otherwise). UseAncestorPrefix = use the chosen ancestor's own registry viewport prefix (it is tracked)
    // rather than the tracked node's prefix.
    internal readonly record struct Pick(Source Source, int AncestorIndex, bool UseAncestorPrefix);

    internal static Pick Resolve(IReadOnlyList<Candidate> liveAncestors, bool registryFallbackResolves)
    {
        for (var i = 0; i < liveAncestors.Count; i++)
        {
            if (liveAncestors[i].IsCanvasItem)
            {
                return new Pick(Source.LiveAncestor, i, liveAncestors[i].Registered);
            }
        }

        if (registryFallbackResolves)
        {
            return new Pick(Source.RegistryFallback, -1, false);
        }

        return new Pick(Source.Identity, -1, false);
    }
}
