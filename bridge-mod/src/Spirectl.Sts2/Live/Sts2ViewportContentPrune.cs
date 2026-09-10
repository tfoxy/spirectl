namespace Spirectl.Sts2.Live;

// Producer decision: SKIP a SubViewport's whole subtree when the producer has no viewport→screen mapping for it —
// PURE and Godot-free (like Sts2ReparentEmit / Sts2RegionEmitCap / Sts2CosmeticEmitCap) so it is unit-testable
// without the Godot-coupled watcher.
//
// Background (phantom potion at the top-left of the event screen). `NPotion.DoFlash()` DUPLICATES the whole potion
// subtree into a 60x60 `SubViewport` inside `res://scenes/vfx/vfx_potion_flash.tscn`. That viewport is never shown
// by a Control/Sprite2D consumer: its texture is fed to a `CPUParticles2D` ("Flash", one_shot) as the particle
// SPRITE. `Sts2RuntimeSceneWatcher.TryComputeViewportPrefix` only resolves Control/Sprite2D/SubViewportContainer
// consumers, so a PARTICLE-consumer viewport gets NO prefix — and its CanvasItem content (which reports
// viewport-LOCAL transforms) then streamed at viewport-local coordinates, i.e. pinned at ~the design origin. The
// live-confirmed phantom was `.../PotionHolders/PotionHolder/Potion/VfxPotionFlash/Potion/Container/Image` rendered
// at ~(3,3)-(57,57) over the portrait.
//
// It PERSISTS because of the headless permanent particle freeze: the one-shot Flash never advances, so its
// `finished` signal never fires and `FlashAndFree()` never `QueueFree()`s the VFX. That is a GAME-SIDE node leak in
// the couch-coop headless visual suspender — documented here, fixed separately; this prune keeps the leaked
// duplicate off the wire regardless.
//
// Fix: when a SubViewport has no computable prefix, do not descend into it at all. `Reconcile`'s stale sweep then
// emits proper `RemovedIds` for any descendants that were tracked before (they simply stop appearing in `seen`;
// `_registry` is exactly `seen ∪ stale`, so its count test never misses them), and re-admission is symmetric: if a
// prefix later becomes computable the walk descends again, the nodes are absent from `_registry`, so they are
// re-added as `JustAdded` and ship a full static-bearing upsert.
//
// No new staleness class: this decision is re-evaluated exactly as often as the prefix it is derived from, since
// both are computed inside `Reconcile`, which runs only on structure-dirty (or keyframe) ticks — and a
// structure-dirty tick is also what re-emits `OrderedIds`, so the client's draw order never disagrees with the
// removals.
//
// Scope guards:
//   * enabled                       — kill-switch, see Sts2SceneWatchRuntimeSettings.PruneUnmappedViewportContent.
//   * prefixComputable              — a viewport WITH a prefix keeps streaming exactly as today. The prefix path
//                                     (single-player map drawing, multiplayer card intent, monster-death render
//                                     textures) is untouched by construction.
//   * parentIsSubViewportContainer  — a `SubViewportContainer` DISPLAYS its child viewport directly at its own
//                                     on-screen rect (the timeline epoch screens), so its content is genuinely
//                                     visible in-game and must never be pruned. Belt-and-braces: the container
//                                     prefix branch added alongside this normally makes `prefixComputable` true for
//                                     those, but the exclusion keeps today's behaviour even if the prefix math
//                                     conservatively bails for some container configuration.
//
// KNOWN BEHAVIOUR FLIP (accepted, an improvement): the other two prefix-less VFX viewports —
// `res://scenes/vfx/vfx_doom.tscn` (687x687 "Viewport") and `res://scenes/vfx/vfx_card_enchant.tscn` (144x108
// "EnchantmentViewport") — go from MISPLACED-AT-THE-ORIGIN to ABSENT in the mirror. Their content was never
// correctly placed, so absence is strictly better than a phantom stamped over the HUD.
//
// DEFAULT ON; SPIRECTL_SCENE_WATCH_PRUNE_UNMAPPED_VIEWPORTS=0 seeds it OFF (restoring the pre-fix stream
// byte-identically). Read on the game main thread in the capture loop; a one-tick stale read is harmless.
internal static class Sts2ViewportContentPrune
{
    // True when the reconcile walk should SKIP this SubViewport's entire subtree (no tracking, no emission — and
    // therefore a clean stale-sweep removal for anything tracked from an earlier pass). Pure so it is unit-testable
    // without the watcher.
    internal static bool ShouldPrune(bool enabled, bool prefixComputable, bool parentIsSubViewportContainer)
        => enabled && !prefixComputable && !parentIsSubViewportContainer;
}
