namespace Spirectl.Sts2.Live;

// FIX 4 — rest-site option DESCRIPTION stuck invisible on refocus — PURE and Godot-free (like Sts2ReparentEmit /
// Sts2CancelledTweenResync / Sts2RegionEmitCap) so the routing truth-table is offline-unit-testable without the
// Godot-coupled watcher. The Godot-typed watcher (Sts2RuntimeSceneWatcher) reads nodeType/sceneFilePath/_screenType
// and consumes these three predicates in the capture walk.
//
// Background. A rest-site option's description is a shared MegaRichTextLabel under ChoicesScreen whose reveal is
// driven by modulate.a (0→1 on focus). But in browser/headless mode the game does NOT report the ChoicesScreen
// sub-controls as engine-Visible even when the option is active — the documented rest-site overlay quirk
// (Sts2RestSiteScreenInspector: "Browser/headless mode does not report these room sub-controls as engine-Visible
// even when the option is active … gate on the button TYPE … rather than the Visible flag"). So on every tick after
// the first, a CanvasItem.Visible in the ChoicesScreen chain reads FALSE, and the description is PRUNED at the
// capture gate (`if (!full && !read.Visible) skipDepth = tracked.Depth`). Its focus-driven fade-in is never
// read/diffed/emitted; pruning ships no removal and there are no periodic keyframes, so both mirror clients stay
// stuck at alpha 0.
//
// The fix is TWO COUPLED parts (the second is a critical trap). The wire serializes `visible` ONLY when FALSE
// (absent ⇒ visible) and both clients display:none / hide the subtree of a `visible:false` ancestor, so merely
// UN-pruning would let ChoicesScreen ship its overlay `visible:false` and HIDE THE WHOLE REST-SITE UI — worse than
// the bug. Therefore:
//   1. ExemptFromVisiblePrune — do NOT prune a Visible=false node inside the NRestSiteRoom subtree (so the fade-in
//      is read/diffed/emitted instead of skipped).
//   2. EmitVisible — flip the overlay-quirk false→true for nodes inside the ACTIVE rest-site overlay, right AFTER
//      ReadVolatile and BEFORE ApplyIfChanged (so diff/emit/LastVisible stay consistent). The TRUE modulate.a is
//      streamed UNTOUCHED — an unfocused description still fades transparent; only the structural `visible` flag is
//      flipped, only false→true, only inside the ACTIVE overlay (matches how the game composites the overlay).
//
// Scope. Everything is gated on `insideRestOverlay` (a depth sentinel pinned at the room root) so combat/map/shop/
// event prune+emit are byte-identical (insideRestOverlay=false there). The visible-flip is additionally gated on the
// ACTIVE screen (`restSiteActive`, _screenType == "Rooms.NRestSiteRoom") so a BACKGROUNDED rest room isn't
// resurrected. Root detection reads only cached strings (nodeType / sceneFilePath) — NEVER a raw Visible.
//
// DEFAULT ON; SPIRECTL_SCENE_WATCH_REST_OVERLAY_STREAM=0 seeds it OFF (byte-identical pre-fix: no exemption, no flip).
internal static class Sts2RestOverlayStream
{
    // The managed full type name of the rest-site room root (node.GetType().FullName). Cached — matches
    // Sts2RestSiteScreenInspector.RestSiteRoomType / Sts2SupportedScreenIds, duplicated here so this pure file stays
    // Godot-free and offline-compilable (both live-host and pure-test builds).
    private static readonly string RestSiteRoomManagedType = "MegaCrit.Sts2.Core.Nodes.Rooms.NRestSiteRoom";

    // The scene file path of the instanced rest-site room root scene (Node.SceneFilePath — non-empty only on an
    // instanced-scene root, which the NRestSiteRoom node is). Cached; verified against a live capture.
    private static readonly string RestSiteRoomScenePath = "res://scenes/rooms/rest_site_room.tscn";

    // True when this tracked node is the rest-site room root (the pin point for the overlay depth sentinel). Matches
    // on either the managed type OR the instanced-scene path so it works whether the node exposes its C# type name or
    // only its scene root path. Reads only cached strings — no Godot call, no raw Visible read.
    internal static bool IsRestSiteRoomRoot(string? nodeType, string? sceneFilePath)
        => string.Equals(nodeType, RestSiteRoomManagedType, StringComparison.Ordinal)
           || string.Equals(sceneFilePath, RestSiteRoomScenePath, StringComparison.Ordinal);

    // Part 1: do NOT prune a Visible=false node inside the rest-site overlay subtree (stream it so its focus fade-in
    // is read/diffed/emitted). A VISIBLE node is never "exempted" — the prune gate never touches it anyway.
    internal static bool ExemptFromVisiblePrune(bool enabled, bool insideRestOverlay, bool localVisible)
        => enabled && insideRestOverlay && !localVisible;

    // ── R10: the flip's collateral, and its denylist ───────────────────────────────────────────────────────────
    //
    // EmitVisible flips EVERY hidden node in the room, which is more than the quirk it was written for. The
    // rest site's ProceedButton owns a `%ControllerIcon` TextureRect holding the gamepad face-button glyph, and the
    // game hides it — genuinely, globally, on its own input state — whenever no controller is attached. That hidden
    // flag is NOT the overlay quirk, so flipping it streamed a "press Y" prompt onto a mouse/touch mirror sitting
    // next to a game that shows none. (Evidence: in `.sts2/bench/audit-rest-refocus-postfix.ndjson`, the SIX
    // ControllerIcon nodes elsewhere in the run — map Back, settings BackButton, the three top-bar buttons, the
    // potion shortcut — all stream `visible:false`; only `RestSiteRoom/ProceedButton/ControllerIcon` streams visible,
    // and the only thing different about it is that it sits inside the flip's blast radius.)
    //
    // The distinguishing property is the node NAME: STS2 names every controller-prompt glyph in the game with one of
    // these, and each is hidden by a real game decision (`InputManager` device state), never by the compositing quirk
    // — which is why the identical nodes OUTSIDE the rest room are hidden in the very same capture. So the flip
    // simply refuses to touch them. Everything else in the room is unchanged, including the ProceedButton itself
    // (its own quirk-hidden flag still flips, so the button keeps streaming) and the ChoicesScreen description chain
    // the fix was written for.
    //
    // Kill switch: SPIRECTL_SCENE_WATCH_REST_OVERLAY_PROMPT_DENYLIST=0 seeds it OFF (flip everything, i.e. the
    // pre-R10 behaviour) for an A/B.
    private static readonly string[] ControllerPromptNodeNames =
    [
        "ControllerIcon",
        "ControllerBindingIcon",
        "ControllerHeader",
    ];

    /// <summary>
    /// True when <paramref name="nodeName"/> is a gamepad-prompt glyph the game hides on its own input state, so the
    /// rest-overlay visible-flip must leave it alone. Exact (ordinal) name match — a node merely CONTAINING the word
    /// (a hypothetical "ControllerIconContainer" holding real content) is not a prompt and keeps today's behaviour.
    /// </summary>
    internal static bool IsControllerPromptNode(string? nodeName)
    {
        if (string.IsNullOrEmpty(nodeName))
        {
            return false;
        }

        foreach (var candidate in ControllerPromptNodeNames)
        {
            if (string.Equals(nodeName, candidate, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // Part 2: the emitted `visible` for a node — flips the overlay-quirk false→true ONLY inside the ACTIVE rest-site
    // overlay. A truly-visible node stays visible; outside the active overlay (kill-switch off, outside the room, or
    // a backgrounded room) the live value passes through untouched (so a backgrounded room isn't resurrected). R10:
    // a controller-prompt glyph (see above) is never flipped when `promptDenylist` is on — its hidden flag is the
    // game's own answer to "is a gamepad attached", not the compositing quirk.
    internal static bool EmitVisible(
        bool enabled,
        bool insideRestOverlay,
        bool restSiteActive,
        bool localVisible,
        bool promptDenylist = false,
        string? nodeName = null)
        => localVisible
           || (enabled
               && insideRestOverlay
               && restSiteActive
               && !(promptDenylist && IsControllerPromptNode(nodeName)));
}
