namespace Spirectl.Sts2.Live;

/// <summary>
/// Runtime-adjustable knobs for the live scene watcher (<see cref="Sts2RuntimeSceneWatcher"/>). The watcher type
/// is internal, but these levers need to be flipped at RUN TIME by an embedder (the CouchCoop browser Settings
/// panel toggles "send tween properties vs node properties every frame"), so they live on this public holder that
/// the watcher consults. Each seeds from the same environment variable the watcher used before (so launch-time
/// behavior is unchanged) and can then be overwritten by an embedder. Static because the watcher reads them on the
/// game main thread while an embedder writes them from a background thread — plain bool reads/writes are atomic and
/// these are advisory kill-switches (a one-tick stale read is harmless), so no locking is needed.
/// </summary>
public static class Sts2SceneWatchRuntimeSettings
{
    /// <summary>
    /// While the mirror REPLAYS a tween on a node, suppress the per-frame transform deltas for that node's subtree
    /// (the client pins them, so streaming them is waste). Default ON; <c>SPIRECTL_SCENE_WATCH_SUPPRESS_TWEENED=0</c>
    /// seeds it OFF. Turning this OFF makes the producer stream node transforms every frame instead.
    /// </summary>
    public static bool SuppressTweenedTransforms { get; set; } =
        ReadBool("SPIRECTL_SCENE_WATCH_SUPPRESS_TWEENED", defaultOn: true);

    /// <summary>
    /// Same as <see cref="SuppressTweenedTransforms"/> for modulate/self_modulate alpha during a replayed fade.
    /// Default ON; <c>SPIRECTL_SCENE_WATCH_SUPPRESS_TWEENED_OPACITY=0</c> seeds it OFF.
    /// </summary>
    public static bool SuppressTweenedOpacity { get; set; } =
        ReadBool("SPIRECTL_SCENE_WATCH_SUPPRESS_TWEENED_OPACITY", defaultOn: true);

    /// <summary>
    /// When a replayed tween is KILLED/STOPPED (interrupted or superseded — e.g. the shared main-menu focus reticle
    /// re-anchored to a new button), drop its half-emitted mirror hint and immediately re-sync the affected nodes'
    /// live values instead of leaving a stale endpoint pinned and the per-frame stream frozen for up to the
    /// suppression cap. Default ON; <c>SPIRECTL_SCENE_WATCH_CANCEL_KILLED_TWEENS=0</c> seeds it OFF.
    /// </summary>
    public static bool CancelKilledTweens { get; set; } =
        ReadBool("SPIRECTL_SCENE_WATCH_CANCEL_KILLED_TWEENS", defaultOn: true);

    /// <summary>
    /// WS-REST belt-and-braces for the rest-site "description stuck invisible" defect. When a fade-IN tween is
    /// KILLED before its deferred Finalize (a re-focused option's description whose unfocus completes first), its
    /// opacity-suppression window was never opened, so <see cref="Sts2RuntimeSceneWatcher.CancelTweenSuppression"/>
    /// has nothing to collapse and no settle re-emit fires — the producer streams the resting alpha as a plain
    /// delta that the client's hide-latch (armed by the earlier fade-OUT) mis-clamps to 0. When ON, a killed tween
    /// carrying a VISIBLE-target opacity step forces ONE settled opacity re-emit of the node's LIVE alpha on the next
    /// tick even with no window open, so the client always gets a fresh, un-suppressed alpha write. The client fix
    /// (held-restore short expiry) is the primary cure; this is defence in depth. Default ON;
    /// <c>SPIRECTL_SCENE_WATCH_CANCELLED_RISE_RESYNC=0</c> seeds it OFF (a killed tween only collapses an
    /// already-open window, as before). Read on the game main thread (Harmony Kill/Stop postfix); a one-tick stale
    /// read is harmless.
    /// </summary>
    public static bool CancelledRiseResync { get; set; } =
        ReadBool("SPIRECTL_SCENE_WATCH_CANCELLED_RISE_RESYNC", defaultOn: true);

    /// <summary>
    /// Populate an IMPLICIT tween START (the target property's value sampled at tween-creation time, when the node
    /// still sits at its pre-tween value) in every emitted animation hint — even when the game did NOT declare an
    /// explicit <c>.From(...)</c>. The client PRIMES the property to this start before replaying, so a slow mirror
    /// client that folds the node-create + settle re-emit + hint into one coalesced message still animates from the
    /// real start instead of collapsing to the near-final streamed value. An explicit <c>.From(...)</c> always wins
    /// over the implicit sample. Default ON; <c>SPIRECTL_TWEEN_IMPLICIT_START=0</c> seeds it OFF (restoring the prior
    /// declared-start-only behavior — no implicit prime). Read on the game main thread (the tween hook) and, when the
    /// resolver runs, on the capture thread; a one-tick stale read is harmless (advisory kill-switch).
    /// </summary>
    public static bool TweenImplicitStart { get; set; } =
        ReadBool("SPIRECTL_TWEEN_IMPLICIT_START", defaultOn: true);

    /// <summary>
    /// Emit each node's <c>Transform</c> PARENT-RELATIVE (re-based against its nearest EMITTED ancestor's streamed
    /// global) instead of the default GLOBAL transform. The client composes locals down the emitted parent chain to
    /// reproduce today's globals exactly, so a container move/scroll becomes ONE node delta instead of re-emitting
    /// every descendant. Default OFF (the watcher streams globals as before); <c>SPIRECTL_SCENE_WATCH_LOCAL_TRANSFORMS=1</c>
    /// seeds it ON. Flipping this at run time forces a full keyframe so clients rebuild in the new space (see the
    /// watcher's mode-flip sentinel), which is why an embedder (the CouchCoop Settings panel) can toggle it live.
    /// </summary>
    public static bool EmitLocalTransforms { get; set; } =
        ReadBool("SPIRECTL_SCENE_WATCH_LOCAL_TRANSFORMS", defaultOn: false);

    /// <summary>
    /// Emit a tracked node's upsert (carrying its new <c>parentId</c>) when it is REPARENTED to a different node —
    /// even if none of its volatile properties (transform/opacity/texture/…) changed. The per-node change detector
    /// (<c>ApplyIfChanged</c>) only compares volatile properties, so a pure reparent whose emitted local transform is
    /// unchanged (e.g. a hand card deselecting: it moves from the <c>SelectedHandCardHolder</c> back to a fresh
    /// <c>NHandCardHolder</c>, sitting at local (0,0) under both) produced NO upsert — the client never learned the
    /// new parent, its stale parent then got removed, and the orphaned card collapsed to the design origin under the
    /// HUD. When ON, a changed parent forces a wholesale (static-bearing) re-attach so the client re-parents the node.
    /// Default ON; <c>SPIRECTL_SCENE_WATCH_EMIT_REPARENT=0</c> seeds it OFF (restoring the prior emit-only-on-volatile-
    /// change behavior). Read on the game main thread in the capture loop; a one-tick stale read is harmless.
    /// </summary>
    public static bool EmitReparents { get; set; } =
        ReadBool("SPIRECTL_SCENE_WATCH_EMIT_REPARENT", defaultOn: true);

    /// <summary>
    /// When a REPARENT force-emit (<see cref="EmitReparents"/>) fires WHILE a streaming-suppression window covers
    /// the node (the mirror is replaying a tween on it or an ancestor), OMIT the transform payload from the
    /// re-attach upsert so the client holds the node's LAST-KNOWN placed transform instead of adopting the
    /// transition-START value the reparent tick happened to sample. Fixes the P2 discard-select flash: a hand card
    /// selected for discard reparents into a fresh <c>NSelectedHandCardHolder</c> under the centre container WHILE
    /// that container lifts to centre on a real tween; round-3 shipped the card's mid-reparent local (≈ global
    /// bottom-centre) which the client then PINNED for the whole window, flashing the card off-centre until the
    /// settle re-emit snapped it back. Ships <c>Transform=null</c> (already nullable); the client's MergeNode
    /// wholesale-adopts the null → the card renders at centre immediately (last-known local (0,0) under the new
    /// holder) and the settle resync is unchanged. Deselect (no tween ⇒ no window) is byte-identical either way.
    /// Default ON; <c>SPIRECTL_SCENE_WATCH_REPARENT_HOLD_TWEENED=0</c> seeds it OFF (restoring the round-3
    /// ship-the-transform behaviour). Read on the game main thread in the capture loop; a one-tick stale read is
    /// harmless.
    /// </summary>
    public static bool ReparentHoldTweened { get; set; } =
        ReadBool("SPIRECTL_SCENE_WATCH_REPARENT_HOLD_TWEENED", defaultOn: true);

    /// <summary>
    /// WS-3 discard→draw shuffle card flight. The game spawns one <c>NCardFlyShuffleVfx</c> per shuffled card and
    /// animates it in a per-rendered-frame async loop (not a Godot <c>Tween</c>, so the tween recorder captures
    /// nothing) with a <c>NCardTrailVfx</c> comet copying its pose every frame. A 34.8s wire recording attributed
    /// 45% of a shuffle's upsert bytes — 1852 of 3469 upserts — to that one subtree. When ON, the producer instead
    /// emits ONE declarative <see cref="Embedding.CardFlightHint"/> per card (the closed-form curve + integrator
    /// parameters) and opens a streaming-suppression window over the flight node's and the trail's subtrees for the
    /// flight's whole analytic lifetime, so the client replays it at its own frame rate with zero further deltas.
    ///
    /// <para>The hint and the window are ONE lever on purpose: with the window open and the hint ignored the
    /// subtree would freeze mid-flight, so a consumer that cannot replay must turn BOTH off — which is exactly what
    /// flipping this does (it gates the emit and the window in the same place). Default ON;
    /// <c>SPIRECTL_SCENE_WATCH_CARD_FLIGHT=0</c> seeds it OFF (byte-identical to the pre-WS-3 producer: every
    /// flight frame streams as a transform delta). Read on the game main thread (the <c>_Ready</c> Harmony
    /// postfix's deferred resolve); a one-tick stale read is harmless.</para>
    /// </summary>
    public static bool CardFlightHints { get; set; } =
        ReadBool("SPIRECTL_SCENE_WATCH_CARD_FLIGHT", defaultOn: true);

    /// <summary>
    /// R13 narrowing of a card flight's trail suppression: also freeze the trail VFX ROOT's OWN transform writes
    /// (and only its own — its <c>Sprites</c> spark branch keeps streaming, so the sparks still follow the card).
    /// The root is a boxless container, so nothing on screen is painted from its transform directly; on the R12
    /// capture its own writes were 1158 of the ~1.9k trail-subtree writes a 30-card shuffle produced, all of them
    /// invisible.
    ///
    /// <para>In LOCAL emit mode this additionally requires <see cref="TrailSelfSuppressLocalMode"/>, because a
    /// consumer there has to place the frozen root itself to keep its descendants placed. Default ON;
    /// <c>SPIRECTL_SCENE_WATCH_TRAIL_SELF_SUPPRESS=0</c> seeds it OFF (the R12 behaviour: the root streams every
    /// frame of every flight). Read on the game main thread (the <c>_Ready</c> hook's deferred resolve); a one-tick
    /// stale read is harmless. See <see cref="Sts2TrailWindowPolicy"/> for the combined rule.</para>
    /// </summary>
    public static bool TrailSelfSuppress { get; set; } =
        ReadBool("SPIRECTL_SCENE_WATCH_TRAIL_SELF_SUPPRESS", defaultOn: true);

    /// <summary>
    /// R14 opt-in extending <see cref="TrailSelfSuppress"/> into LOCAL emit mode (<see cref="EmitLocalTransforms"/>),
    /// which is the mode the CouchCoop mirror actually runs — so without this the trail root's self-window never
    /// engages in production and the root keeps streaming ~30 writes per card per flight.
    ///
    /// <para>Gated separately from <see cref="TrailSelfSuppress"/> because it is a CONSUMER capability, not a
    /// producer preference. In local mode a node's transform is composed down its emitted parent chain, so a client
    /// only stays correct while the root's writes are withheld if it drives that root from the declarative
    /// <see cref="Embedding.CardFlightHint"/> instead. A client that ignores the hint would leave the root wherever
    /// the last streamed write put it. Nothing below the root is affected either way: the capture walk records each
    /// node's REAL global for its children to re-base against whether or not that node emitted, so the trail's inner
    /// containers keep counter-moving on the wire exactly as before and their leaves stay correct.</para>
    ///
    /// <para>Default OFF, and deliberately so — an embedder turns it on per process only once every attached viewer
    /// is known to replay the flight (the CouchCoop server couples it to a unanimous viewer capability vote, and
    /// drops it back off as soon as a viewer that lacks the capability joins).
    /// <c>SPIRECTL_SCENE_WATCH_TRAIL_SELF_SUPPRESS_LOCAL=1</c> seeds it ON for a process. Read on the game main
    /// thread (the <c>_Ready</c> hook's deferred resolve); a one-tick stale read is harmless (the next flight picks
    /// up the new value).</para>
    /// </summary>
    public static bool TrailSelfSuppressLocalMode { get; set; } =
        ReadBool("SPIRECTL_SCENE_WATCH_TRAIL_SELF_SUPPRESS_LOCAL", defaultOn: false);

    /// <summary>
    /// R13 HAND→DISCARD CARD FLY (<c>vfx/vfx_card_fly</c>). The played card leaves the hand, arcs to the discard
    /// pile darkening and shrinking away, and drags a comet behind it — driven, like the shuffle sweep, by a
    /// per-rendered-frame loop rather than a Godot <c>Tween</c>, so the tween recorder captures nothing and every
    /// frame of it streamed as a transform delta on the busiest node in the scene (the card the player is watching).
    /// When ON, the producer instead emits ONE declarative <see cref="Embedding.CardFlightHint"/> with <c>Kind</c>
    /// <c>"discard"</c> per fly and freezes the card's subtree for the fly's analytic lifetime.
    ///
    /// <para>Subordinate to <see cref="CardFlightHints"/>: that lever gates BOTH card flights (a consumer that
    /// cannot replay a declarative flight cannot replay either of them), and this one narrows the switch to the
    /// discard fly alone — so a consumer that replays the shuffle sweep but not this can keep the first. Same
    /// hint-and-window-as-one contract: with the window open and the hint ignored the card would freeze mid-arc,
    /// so both are gated in the same place. Default ON; <c>SPIRECTL_SCENE_WATCH_DISCARD_FLIGHT=0</c> seeds it OFF
    /// (every fly frame streams as a transform delta again). Read on the game main thread (the <c>_Ready</c> hook
    /// and its deferred resolve); a one-tick stale read is harmless.</para>
    /// </summary>
    public static bool DiscardFlightHints { get; set; } =
        ReadBool("SPIRECTL_SCENE_WATCH_DISCARD_FLIGHT", defaultOn: true);

    /// <summary>
    /// WS-4 HAND-LAYOUT LERP. A hand card holder's position/angle/scale are animated by three async per-process-frame
    /// <c>Lerp</c> loops, not by a Godot <c>Tween</c>, so <see cref="Sts2TweenRecorderHooks"/> — a Tween-only hook —
    /// captures nothing from them and every intermediate frame streamed as a
    /// raw transform. A device trace of a start-of-turn draw attributed 369.3 KB / 594 upserts — 21.4% of a 3s draw
    /// window, peaking at 1.03 MB / 2477 upserts in one second — to that one subtree, because every newly drawn card
    /// re-targets every already-settled holder and the whole hand re-animates once per card.
    ///
    /// <para>When ON, a hand FAN-OUT batch (<c>NPlayerHand.RefreshLayout</c> / <c>NHandCardHolder.SetDefaultTargets</c>)
    /// instead publishes ONE ordinary <see cref="Embedding.TweenAnimationHint"/> per holder — the resolved end
    /// transform plus <c>Expo</c>/<c>Out</c> and the analytic settle duration (<see cref="Sts2HandTweenMath"/>) —
    /// and opens the SAME streaming-suppression window a real tween's endpoint opens, over the holder (whose card
    /// subtree follows via the walk's depth sentinel). An exponential approach and a CSS expo-out transition are the
    /// same curve, so no new wire type and no consumer change is needed.</para>
    ///
    /// <para>The hint and the window are ONE lever on purpose, exactly like <see cref="CardFlightHints"/>: with the
    /// window open and the hint ignored, the hand would FREEZE mid-fan for up to the hint's duration, so a consumer
    /// that cannot replay must turn BOTH off — which is what flipping this does (it gates the emit and, because the
    /// window is only ever opened on the emit path, the window too). Default ON;
    /// <c>SPIRECTL_SCENE_WATCH_HAND_TWEEN=0</c> seeds it OFF (byte-identical to the pre-WS-4 producer: every lerp
    /// frame streams as a transform delta). Read on the game main thread (the setters' postfixes and their deferred
    /// idle publish); a one-tick stale read is harmless.</para>
    /// </summary>
    public static bool HandTweenHints { get; set; } =
        ReadBool("SPIRECTL_SCENE_WATCH_HAND_TWEEN", defaultOn: true);

    /// <summary>
    /// WS-REST7 rest-site "option description stuck invisible on refocus" cure (producer-side). A rest-site option's
    /// description (a shared <c>MegaRichTextLabel</c> under <c>ChoicesScreen</c>, driven by <c>modulate.a</c>) is
    /// PRUNED every tick because a <c>CanvasItem.Visible</c> in the ChoicesScreen chain reads FALSE in browser/headless
    /// mode — the documented rest-site overlay quirk — so its focus fade-in is never read/diffed/emitted and both
    /// mirror clients stay stuck at alpha 0. When ON, nodes inside the <c>NRestSiteRoom</c> subtree are (1) EXEMPT from
    /// the Visible=false prune gate so they keep streaming, and (2) have that overlay-quirk false <c>visible</c> flipped
    /// false→true (only inside the ACTIVE rest-site overlay) right after <c>ReadVolatile</c> so diff/emit/LastVisible
    /// stay consistent — the TRUE <c>modulate.a</c> is streamed untouched (an unfocused description still fades
    /// transparent). Scoped strictly to the rest-site subtree; combat/map/shop/event are byte-identical. Default ON;
    /// <c>SPIRECTL_SCENE_WATCH_REST_OVERLAY_STREAM=0</c> seeds it OFF (byte-identical pre-fix). Read on the game main
    /// thread in the capture loop; a one-tick stale read is harmless.
    /// </summary>
    public static bool RestOverlayStream { get; set; } =
        ReadBool("SPIRECTL_SCENE_WATCH_REST_OVERLAY_STREAM", defaultOn: true);

    /// <summary>
    /// R10 narrowing of <see cref="RestOverlayStream"/>'s visible-flip: a gamepad-prompt glyph inside the rest room
    /// (<c>%ControllerIcon</c> and friends — see <c>Sts2RestOverlayStream.IsControllerPromptNode</c>) is NEVER flipped
    /// visible. The game hides those on its own global input state ("is a controller attached"), not through the
    /// rest-overlay compositing quirk, so the flip was streaming a "press Y" prompt onto a mouse/touch mirror while
    /// the game itself showed none. Nothing else in the room changes — the ProceedButton and the ChoicesScreen
    /// description chain the original fix targeted still flip. Default ON;
    /// <c>SPIRECTL_SCENE_WATCH_REST_OVERLAY_PROMPT_DENYLIST=0</c> seeds it OFF (flip everything, pre-R10 behaviour).
    /// </summary>
    public static bool RestOverlayPromptDenylist { get; set; } =
        ReadBool("SPIRECTL_SCENE_WATCH_REST_OVERLAY_PROMPT_DENYLIST", defaultOn: true);

    /// <summary>
    /// Resolve a tween-hint's emitted-parent (for LOCAL-mode endpoint localization,
    /// <c>EmittedParentGlobalTuple</c>) from the LIVE scene tree — the node's nearest CanvasItem ancestor via
    /// <c>GetParent()</c> — instead of trusting the tracked node's registry <c>ParentId</c>. At a reparent+tween
    /// same-frame (the discard select), the registry <c>ParentId</c> can be STALE at the deferred tween-finalize
    /// time: the freed old parent → the identity fallback ships a GLOBAL endpoint tuple as if it were LOCAL
    /// (double-apply, lower-right), or a still-live old parent → the endpoint is localized against the WRONG
    /// parent. Walking the live tree finds the node's true current parent; the registry path stays as the
    /// fallback when the live ancestor isn't (yet) tracked. Default ON;
    /// <c>SPIRECTL_SCENE_WATCH_TWEEN_PARENT_LIVE=0</c> seeds it OFF (restoring the registry-parent-only lookup).
    /// Read on the capture thread from the tween hook's deferred Finalize; a one-tick stale read is harmless.
    /// </summary>
    public static bool TweenParentLive { get; set; } =
        ReadBool("SPIRECTL_SCENE_WATCH_TWEEN_PARENT_LIVE", defaultOn: true);

    /// <summary>
    /// Stream a rich-text label's PER-ROLE theme fonts (<c>bold_font</c> / <c>italics_font</c> /
    /// <c>bold_italics_font</c>) alongside its normal font, each resolved to the underlying <c>.ttf</c>/<c>.otf</c>
    /// binary, plus their per-role theme font SIZES and glyph SPACING. Godot renders a <c>[b]</c> span by swapping
    /// the label to a different font FILE (STS2: <c>res://fonts/kreon_bold.ttf</c>) — it never synthesises bold — so
    /// with one font per node on the wire the mirror's <c>&lt;strong class="godot-rich-bold"&gt;</c> inherited
    /// kreon_regular (a single 400 face) and rendered un-bold. Each field is omitted (null) when the role font is
    /// unresolvable to a font binary or is the SAME file as the node's normal font, so nearly every node's wire is
    /// unchanged. Default ON; <c>SPIRECTL_SCENE_WATCH_RICH_ROLE_FONTS=0</c> seeds it OFF (byte-identical pre-fix
    /// wire). Read on the game main thread from the once-per-node static probe; a one-tick stale read is harmless.
    /// See <see cref="Sts2RichRoleFontEmit"/>.
    /// </summary>
    public static bool StreamRichRoleFonts { get; set; } =
        ReadBool("SPIRECTL_SCENE_WATCH_RICH_ROLE_FONTS", defaultOn: true);

    /// <summary>
    /// SKIP the whole subtree of a <c>SubViewport</c> the producer has no viewport→screen prefix for (and whose
    /// parent is not a <c>SubViewportContainer</c>). Such content reports VIEWPORT-LOCAL transforms, so without a
    /// prefix it streamed pinned at ~the design origin — the live-confirmed phantom potion at the top-left of the
    /// event screen, which <c>NPotion.DoFlash()</c> duplicates into the 60x60 SubViewport of
    /// <c>res://scenes/vfx/vfx_potion_flash.tscn</c> that is only ever sampled as a <c>CPUParticles2D</c> texture.
    /// Viewports WITH a prefix (map drawing, multiplayer card intent, monster-death render textures) and
    /// SubViewportContainer content (the timeline epoch screens) are untouched. Default ON;
    /// <c>SPIRECTL_SCENE_WATCH_PRUNE_UNMAPPED_VIEWPORTS=0</c> seeds it OFF (byte-identical pre-fix wire). Read on
    /// the game main thread in the capture loop; a one-tick stale read is harmless.
    /// See <see cref="Sts2ViewportContentPrune"/>.
    /// </summary>
    public static bool PruneUnmappedViewportContent { get; set; } =
        ReadBool("SPIRECTL_SCENE_WATCH_PRUNE_UNMAPPED_VIEWPORTS", defaultOn: true);

    /// <summary>
    /// Stream a <c>Line2D</c>'s STROKE GEOMETRY — its flattened node-local <c>points</c>, its <c>width</c>, and its
    /// <c>default_color</c> — as <c>linePoints</c> / <c>lineWidth</c> / <c>lineColor</c>. The map quill annotations are
    /// <c>Line2D</c> nodes appended live under <c>…/MapDrawing/DrawViewport</c>; the watcher already streamed them
    /// with correct transforms but with NO geometry, and a <c>Line2D</c> has nothing else to render (no texture rect,
    /// no text), so every client drew a blank node. Emitted as ONE sticky unit on add/keyframe or when the node's
    /// cheap per-tick signature (point count + last point + width + colour) changes; the full points array is
    /// marshalled only at emit time.
    /// <para>
    /// SCOPED to those map strokes (by scene identity, with a <c>DrawViewport</c>-parent fallback) — a
    /// <c>Line2D</c> is a generic primitive and the game also uses it for VFX, most visibly the
    /// <c>card_trail_&lt;character&gt;.tscn</c> strokes behind every flying card. Those get their look from a
    /// <c>width_curve</c>, an alpha <c>gradient</c>, a stretched texture and an additive material, none of which
    /// this unit carries, so streaming their points painted a solid bar instead of a comet — and a
    /// discard→draw reshuffle re-marshalled 2 GROWING arrays per card per tick. They are reconstructed
    /// client-side instead.
    /// </para>
    /// Default <c>map</c>; <c>SPIRECTL_SCENE_WATCH_LINE2D_GEOMETRY=0</c> seeds it OFF, which restores the
    /// pre-feature wire byte-identically (no signature is computed and all three fields stay null ⇒ omitted);
    /// <c>=all</c> restores the unscoped type-only behaviour and exists only as an A/B lever. Read on the game
    /// main thread in the capture loop; a one-tick stale read is harmless.
    /// See <see cref="Sts2Line2DGeometryEmit"/>.
    /// </summary>
    internal static Sts2Line2DGeometryEmit.Scope Line2DGeometryScope { get; set; } =
        Sts2Line2DGeometryEmit.ParseScope(
            System.Environment.GetEnvironmentVariable("SPIRECTL_SCENE_WATCH_LINE2D_GEOMETRY"));

    /// <summary>
    /// Honor the <c>spirectl_stream_skip</c> Godot metadata key: SKIP the whole subtree of any node an EMBEDDER has
    /// stamped with it, so the nodes a downstream mod injects into the live tree (the CouchCoop host-lobby "Show
    /// Couch Co-Op QR Code" button and its dialog, added to <c>NCharacterSelectScreen</c> /
    /// <c>NMultiplayerLoadGameScreen</c>) never reach mirror clients. Not cosmetic: mirror clients drive the host
    /// with REAL injected input, so a phone tapping a mirrored HOST-ONLY button would open that dialog on the host's
    /// TV. Checked as the first statement of the reconcile walk — above the CanvasItem branch, so a plain logic-node
    /// root is skipped too and the subtree is never descended. Deliberately NOT applied to
    /// <c>Sts2RuntimeSceneProvider</c> (the dev <c>scene tree</c>/<c>node</c>/<c>hover</c> surface), so QA probes can
    /// still see and drive the stamped UI. Default ON; <c>SPIRECTL_SCENE_WATCH_HONOR_STREAM_SKIP=0</c> seeds it OFF
    /// (pre-fix stream; a tree nobody stamped is byte-identical either way). Read on the game main thread in the
    /// capture loop; a one-tick stale read is harmless.
    /// See <see cref="Sts2StreamSkipMeta"/>.
    /// </summary>
    public static bool HonorStreamSkipMetadata { get; set; } =
        ReadBool("SPIRECTL_SCENE_WATCH_HONOR_STREAM_SKIP", defaultOn: true);

    /// <summary>
    /// RE-EVALUATE every viewport→screen prefix on EVERY capture pass, instead of only on a structure-dirty one.
    /// A <c>SubViewport</c>'s prefix embeds the LIVE on-screen transform of the node that displays its texture, so
    /// it is a volatile quantity: scrolling the map slides <c>MapDrawing/DrawViewportTextureRect</c>, and the quill
    /// annotations flattened out of <c>DrawViewport</c> must slide with it. Computing the prefix once per reconcile
    /// pinned them to the SCREEN until an unrelated node add (a HoverTip appearing) dirtied the structure and
    /// snapped them back onto the map — the live-reported "drawings only move when something else pops up".
    /// <para>
    /// Cost is O(prefix-producing viewports) per pass — a handful of consumer probes, NOT a tree walk — because the
    /// prefix is held once per viewport and shared by reference with every node inside it.
    /// </para>
    /// Default ON; <c>SPIRECTL_SCENE_WATCH_VIEWPORT_PREFIX_REFRESH=0</c> restores the reconcile-only behaviour.
    /// Read on the game main thread in the capture loop; a one-tick stale read is harmless.
    /// </summary>
    public static bool RefreshViewportPrefixes { get; set; } =
        ReadBool("SPIRECTL_SCENE_WATCH_VIEWPORT_PREFIX_REFRESH", defaultOn: true);

    /// <summary>
    /// Stream <c>Control.clip_contents</c> as the node delta's <c>ClipContents</c> flag. This is a DIFFERENT Godot
    /// property from the already-streamed <c>CanvasItem.clip_children</c>: <c>clip_children</c> stencils
    /// descendants against the node's own DRAWN alpha, while <c>clip_contents</c> clips a Control's children to its
    /// RECTANGLE regardless of whether the Control paints anything. A layout container that paints nothing
    /// therefore reads <c>clip_children == 0</c> and can still be the only thing bounding its children — which is
    /// how a game panel hides its content by parking it outside the panel box rather than by touching
    /// <c>visible</c> / <c>modulate</c>. A consumer without this flag draws that parked content in full.
    /// <para>
    /// Read once per node on the STATIC probe (<c>clip_contents</c> is an authored layout property, not a runtime
    /// one) and emitted only when TRUE, so every node that does not clip keeps a byte-identical wire. Default ON;
    /// <c>SPIRECTL_SCENE_WATCH_CLIP_CONTENTS=0</c> seeds it OFF (pre-feature wire, byte-identical). Read on the
    /// game main thread from the once-per-node static probe; a one-tick stale read is harmless.
    /// </para>
    /// </summary>
    public static bool StreamClipContents { get; set; } =
        ReadBool("SPIRECTL_SCENE_WATCH_CLIP_CONTENTS", defaultOn: true);

    private static bool ReadBool(string name, bool defaultOn)
    {
        var raw = System.Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultOn;
        }

        return raw.Trim().ToLowerInvariant() is not ("0" or "false" or "off" or "no");
    }
}
