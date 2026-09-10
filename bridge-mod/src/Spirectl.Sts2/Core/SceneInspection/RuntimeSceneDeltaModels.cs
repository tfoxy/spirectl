namespace Spirectl.Sts2.Core.SceneInspection;

// Incremental scene-tree update for the live "mirror". A delta carries ONLY what changed since the last
// emission (plus structure when it changed), so a thin client patches a retained node map in place rather
// than re-parsing/re-rendering the whole tree every frame — the key to a 60fps client.
//
// `Full` = a keyframe: `Upserts` contains every tracked node and `OrderedIds` is the complete draw order
// (sent on first subscribe, on a new client connect, and when the watched root changes). Incremental
// deltas carry only changed nodes in `Upserts`, dropped node ids in `RemovedIds`, and `OrderedIds` ONLY
// when the structure changed this tick (otherwise null → order unchanged).
public sealed record RuntimeSceneDelta(
    bool Full,
    string ScreenType,
    string ScreenInstanceId,
    IReadOnlyList<RuntimeSceneNodeDelta> Upserts,
    IReadOnlyList<string> RemovedIds,
    IReadOnlyList<string>? OrderedIds,
    // Fire-and-forget decorative tween hints emitted this tick (appended with a default so existing construction
    // sites stay compiling; `WhenWritingNull` omits it when empty). The client replays these declaratively on the
    // matching node so an active tween animates on the browser's own clock instead of via streamed per-frame
    // transforms. `TargetId` is the tween target's instance id — the SAME id space as `RuntimeSceneNodeDelta.Id`.
    IReadOnlyList<TweenHintDelta>? Hints = null,
    // The coordinate space of every node's `Transform` (and every tween hint's End/Start transform) in this delta.
    // `"global"` (default) = each Transform is the node's absolute streamed global. `"local"` = each Transform is
    // PARENT-RELATIVE (re-based against the nearest EMITTED ancestor's global; identity for roots), so the client
    // composes locals down the emitted parent chain to reproduce the globals — a container move/scroll then costs
    // ONE node delta instead of re-emitting the whole subtree. A space flip forces a `Full` keyframe so the client
    // rebuilds in the new space (see Sts2SceneWatchRuntimeSettings.EmitLocalTransforms). Appended with a default so
    // existing construction sites stay compiling.
    string TransformSpace = "global",
    // WS-3: declarative discard→draw shuffle CARD FLIGHTS started this tick (appended with a default so existing
    // construction sites stay compiling; omitted when empty). Distinct from `Hints` because these are NOT tweens —
    // they are a closed-form animation the client INTEGRATES per frame — and because the producer has stopped
    // streaming the named nodes' transforms for the flight's duration, which `Hints` never implies.
    IReadOnlyList<CardFlightHintDelta>? CardFlights = null);

// One declarative card flight on the mirror wire — a whole multi-second card animation as ten numbers (`Kind` says
// which of the two flights it is; the shuffle sweep unless stated otherwise), replacing ~60 transform deltas/s for
// the flight node's subtree and the trail's two stroke Line2Ds. `TargetId`/`TrailId` join to
// `RuntimeSceneNodeDelta.Id` (`TrailId` addresses the comet's ROOT, which keeps streaming — the client only
// needs it to find the strokes whose synthesized ribbon it feeds). Every point/basis is in the watcher's STREAMED space and always GLOBAL (never
// re-based to a parent, even in local-transform mode). See Spirectl.Sts2.Embedding.CardFlightHint for the full
// replay contract and the game-side derivation; this is its wire twin with the instance ids stringified to match
// the node-id space.
public sealed record CardFlightHintDelta(
    string TargetId,
    string? TrailId,
    IReadOnlyList<double> Start,
    IReadOnlyList<double> End,
    IReadOnlyList<double> Control,
    IReadOnlyList<double> Basis,
    double Speed0,
    double Accel,
    // The game's `_duration`, in its own pseudo-time unit (`time` accumulates `speed*dt`), NOT seconds.
    double Duration,
    // The flight node's own uniform scale at spawn; phase 2's absolute scale assignment is divided by it.
    double Scale0,
    // How long (wall-clock ms) the producer has stopped streaming these nodes' transforms — i.e. how long the
    // client owns them. The client releases its pin on this deadline, one frame after the producer's settle
    // re-emit, so the two can never leave a gap where a frozen transform paints.
    double WindowMs,
    // WHICH flight this entry describes, so one array carries both kinds: null (the default) = the shuffle sweep
    // every field above was written for; "discard" = the hand→discard fly, whose mover is the REAL card the player
    // just played. NULLABLE on purpose — with the wire's omit-when-null policy a shuffle entry serializes to exactly
    // the bytes it did before this field existed, so an already-deployed client is untouched. A client that does not
    // recognise a kind replays it as the shuffle flight.
    string? Kind = null,
    // The moving node's on-screen rotation (radians, streamed space) when the flight was captured — the rotation
    // seed the "discard" replay eases out of, so the played card turns smoothly into the curve's tangent instead of
    // flicking to it on its first frame. Null (omitted) for the shuffle flight, whose flier has no prior pose.
    double? Rot0 = null);

// One declarative tween hint on the mirror wire. `TargetId` joins to a `RuntimeSceneNodeDelta.Id`. `Property` is
// the raw Godot property (`"position"`/`"scale"`/`"modulate:a"`/`"rotation"`); `To` is the target value as compact
// JSON (or null); `Trans`/`Ease` are raw Godot enum names the client maps to a CSS timing function.
//
// The trailing fields (appended with defaults so existing construction sites stay compiling) make a hint REPLAYABLE
// (Part C): `EndTransform` is the tween TARGET node's END GLOBAL Transform2D as a 6-tuple `[a,b,c,d,tx,ty]` in the
// watcher's streamed space (the client rigidly propagates it across the target's subtree, so the whole tweened node
// moves in lockstep); `EndOpacity` is the target's END `modulate.a`; `Group` ties one Godot tween's hints together.
// Null when the producer resolved no endpoint (looping / unsupported tween) — the client then ignores the hint.
public sealed record TweenHintDelta(
    string TargetId,
    string Property,
    string? To,
    double DurationMs,
    string? Trans,
    string? Ease,
    IReadOnlyList<double>? EndTransform = null,
    double? EndOpacity = null,
    string? Group = null,
    // Fix 2: the tween's DECLARED start (`.From(...)`), same 6-tuple / alpha shape as End*. The client PRIMES the
    // node to this (transition-less) before transitioning to the endpoint, so a re-anchored/primed node replays from
    // its true start instead of showing a 1-frame transient. Null unless the tween declared a start.
    IReadOnlyList<double>? StartTransform = null,
    double? StartOpacity = null);

// One node's state in a delta. `Id`/`ParentId` are node instance ids (stable for the node's lifetime).
// Static fields (Name/NodeType + the styling block below) ride only on add/keyframe (null/default on a
// volatile-only upsert). Volatile fields are the LOCAL values the client composes down the parent chain
// (modulate → tint/opacity, visible → effective visibility) and the absolute `Rect` it positions by. Nodes
// without a `Rect` still appear so the client has the full parent chain for composition.
//
// The fields after `Text` are APPENDED with defaults so existing construction sites stay compiling; they
// carry the generic styling the CSS mirror needs for visual parity:
//   - Volatile (cheap, every tick): Modulate/SelfModulate full RGBA (only alpha was sent before),
//     Scale/Pivot (card fan + focus animations rotate about the pivot), FillColor (ColorRect backdrops),
//     Range value/min/max (health/progress bars).
//   - Static (probed ONCE on add, emitted on add/keyframe): ShowBehindParent (outline z-order),
//     NinePatchMargins (stretched scrollbars/frames), Font/OutlineColor/OutlineSize/Shadow (text styling
//     never changes at runtime), RichText (bbcode-enabled flag), Material/Shader (client shader fallback).
public sealed record RuntimeSceneNodeDelta(
    string Id,
    string? ParentId,
    string? Name,
    string? NodeType,
    RuntimeSceneRect2Snapshot? Rect,
    bool Visible,
    double Opacity,
    int? ZIndex,
    double Rotation,
    RuntimeSceneResourceRefSnapshot? Texture,
    bool NinePatch,
    RuntimeSceneTextPropertiesSnapshot? Text,
    // Volatile styling (every tick) — appended with defaults.
    RuntimeSceneColorSnapshot? Modulate = null,
    RuntimeSceneColorSnapshot? SelfModulate = null,
    double ScaleX = 1,
    double ScaleY = 1,
    double PivotX = 0,
    double PivotY = 0,
    RuntimeSceneColorSnapshot? FillColor = null,
    double? RangeValue = null,
    double? RangeMin = null,
    double? RangeMax = null,
    // Static styling (add/keyframe only) — appended with defaults.
    bool ShowBehindParent = false,
    // CanvasItem.ClipChildren (0 Disabled / 1 Only / 2 AndDraw): clip descendants to this node's texture alpha.
    int ClipChildren = 0,
    // Control.MouseFilter (0 Stop / 1 Pass / 2 Ignore) — STATIC (add/keyframe only). Null for non-Control nodes.
    // Ignore means the node is invisible to the mouse in-game; the client renders such nodes `pointer-events:none`
    // so its identity hit-test (which widget a tap targets) matches the game's, instead of letting a decorative
    // overlay (a card's glow, mouse_filter=Ignore) capture taps meant for the widget beneath it.
    int? MouseFilter = null,
    RuntimeScenePatchMarginsSnapshot? NinePatchMargins = null,
    RuntimeSceneResourceRefSnapshot? Font = null,
    RuntimeSceneColorSnapshot? OutlineColor = null,
    double? OutlineSize = null,
    RuntimeSceneTextShadowSnapshot? Shadow = null,
    bool RichText = false,
    RuntimeSceneResourceRefSnapshot? Material = null,
    RuntimeSceneResourceRefSnapshot? Shader = null,
    // ShaderMaterial uniform values (add/keyframe only) — lets the client run the real shader. Reuses the
    // existing Sts2ShaderMaterialInspector. Static: probed once on add (a uniform changed at runtime goes stale).
    IReadOnlyList<RuntimeSceneShaderParamSnapshot>? ShaderParameters = null,
    // Placement (volatile). `Transform` is the node's GLOBAL Transform2D and `LocalRect` its node-local box;
    // the client renders the box with `matrix(...)` so rotation/scale/pivot AND ancestor transforms are
    // baked in consistently (no double transform / orbiting). Supersedes the legacy global `Rect`/`Rotation`/
    // `Scale`/`Pivot` fields above, which the watcher no longer populates.
    RuntimeSceneTransform2DSnapshot? Transform = null,
    RuntimeSceneRect2Snapshot? LocalRect = null,
    // Static text styling (add/keyframe). `Font` is resolved to the underlying .ttf/.otf binary (not the
    // FontVariation/.tres, which the /res/ route returns as JSON and can't @font-face). Weight/Style let the
    // client pick the right face (and avoid faux-bold/italic).
    string? FontWeight = null,
    string? FontStyle = null,
    // Atlas crop (volatile). When `Texture` is an AtlasTexture, `Texture` carries the UNDERLYING atlas image
    // path (stable across animation frames) and these carry the sub-rect to show: `TextureRegion` is the
    // source rect inside the atlas, `TextureMargin` the transparent frame (Godot draws the region at
    // `Margin.Position` within a box of `Region.Size + Margin.Size`). The client crops via CSS
    // background-position so the image URL never changes between frames (no flicker). Null for plain textures.
    RuntimeSceneRect2Snapshot? TextureRegion = null,
    RuntimeSceneRect2Snapshot? TextureMargin = null,
    // TextureRect.StretchMode (add/keyframe only; Godot enum 0..6). How the texture fits the node rect; the
    // client maps it to a fit (KeepAspectCentered → contain) so a non-stretched texture (e.g. the card_ripple
    // SDF in its oversized Highlight box) isn't fill-stretched. Null for non-TextureRect nodes. Static.
    int? TextureStretchMode = null,
    // TextureRect.FlipH / FlipV (add/keyframe only). When true the texture is mirrored horizontally or
    // vertically. The client applies a CSS scale(-1) on the affected axis and adjusts the transform origin
    // so the region stays inside the node's localRect. Used by NScrollbar caps (TrackTop/TrackBot share one
    // atlas region; TrackBot has FlipV=true so the arrow points down). False = no flip (default). Static.
    bool TextureFlipH = false,
    bool TextureFlipV = false,
    // CanvasItem.BlendMode (add/keyframe only). Null when Mix (0, the default); non-null when the node uses
    // additive (1), subtractive (2), or multiplicative (3) blending. The client maps it to CSS mix-blend-mode
    // so e.g. additive card-highlight glows render as plus-lighter instead of opaque overlays. Static.
    int? CanvasBlendMode = null,
    // Particle system (GpuParticles2D/CpuParticles2D), flattened into gsw's ParticleSpecConfig shape so the
    // client runs the real CPU simulation. `ParticleSpec` is STATIC (add/keyframe only). `ParticleEmitting` and
    // `ParticleRestartEpoch` are VOLATILE (every tick): the epoch bumps on each emitting false→true edge (a
    // proxy for Restart()), letting the client re-trigger a one-shot burst without re-initialising the sim.
    // Null/default for non-particle nodes.
    RuntimeSceneParticleSpecSnapshot? ParticleSpec = null,
    bool ParticleEmitting = false,
    long ParticleRestartEpoch = 0,
    // SpineSprite (skeletal animation). `Spine` is STATIC (add/keyframe only): the canonical clip
    // address (scene + scene-relative node path) + available animation names — the client fetches a
    // rendered clip via spine://. `SpineCurrentAnim` + `SpineTrackTime` are VOLATILE (every emission):
    // the animation the game is currently playing and how far into it (seconds), so the client picks the
    // right clip and seeks to the matching frame. Null/default for non-Spine nodes.
    RuntimeSceneSpineSnapshot? Spine = null,
    string? SpineCurrentAnim = null,
    double SpineTrackTime = 0,
    // VOLATILE: whether the current animation LOOPS (the game's track-entry loop flag). The client loops the
    // clip when true and FREEZES on its last frame when false, so a one-shot (attack/cast/hurt/die) plays once
    // and holds instead of replaying forever (the flicker). Defaults true (the looping fallback presentation).
    bool SpineLooping = true,
    // VOLATILE: the runtime skin the game currently has applied to this SpineSprite (#3), captured off the anim
    // hooks (SetNextState postfix + animation_started) while the native skeleton is alive. Null when unknown —
    // the client then appends NO `&skin=` param, so its clip URL is byte-identical to today (zero cache
    // invalidation). Non-null lets the client request `spine://…&skin=<name>` and the bake apply the same skin
    // in isolation, so creatures whose skin is set at runtime (Fossil Stalker / Skulking Colony — no authored
    // skin in the .tscn) render their full body instead of missing pieces. Defaults null (unknown).
    string? SpineSkin = null,
    // VOLATILE: a short signature of the SpineSprite's `normal_material` ShaderMaterial (#8) — the shader's
    // res:// path plus every uniform value, hashed by Sts2SpineMaterialKey. Null when the node has no shader
    // material (nearly every spine node), so its clip URL stays byte-identical to today. Non-null makes the
    // clip cache key depend on the RUNTIME-parameterised material the bake now applies: the boss map point
    // renders through `boss_map_point.gdshader`, an R/G/B channel-remap mask whose colors NBossMapPoint pushes
    // in per act + travel state, so without this discriminator the first-baked tint would be cached forever
    // under the (scene, node, anim, skin, skel) address — including a pre-fix UNSHADED blob.
    string? SpineMat = null,
    // VOLATILE: the game has PAUSED this spine track (MegaAnimationState.SetTimeScale(0)). The treasure chest is
    // set up as SetAnimation("animation") + AddAnimation("shine_fade") + SetTimeScale(0): it sits frozen on the
    // closed-chest first frame until the player opens it. Both clients free-run a clip off the wall clock between
    // animation changes, so without this they walked the chest open (and then into its queued "shine_fade" glow)
    // while the real room shows it shut. False for every running node — the overwhelming majority.
    bool SpinePaused = false,
    // Godot Node.SceneFilePath — STATIC (add/keyframe only). Non-empty ONLY on the root node of an instanced
    // .tscn scene (children of the instance carry empty paths). The client walks the parent chain to the
    // nearest node with a non-empty path to learn which scene a node belongs to (and that node's id is the
    // scene-instance root), used to identify interactive scenes for touch hover/click and to scope per-element
    // text scaling. Null when the node is not a scene-instance root.
    string? SceneFilePath = null,
    // Enemy-intent glyph frame set (Sts2IntentFramesInspector). Attached to the NIntent glyph Sprite2D (`%Intent`,
    // = NIntent._intentSprite) so the browser reproduces the 15fps icon animation client-side off a wall-clock —
    // the headless client FREEZES each NIntent (ProcessMode.Disabled) for CPU, which stops the per-frame texture
    // swap, so a streamed single texture would be stuck on one frame. Each frame is a resolved
    // AtlasTexture (atlas PAGE path + region/margin), so the client crops from the whole page like every other
    // atlas sprite (no cropped-image bytes on the wire). "STICKY, re-sent on change": emitted ONLY on keyframe/add
    // or when the intent animation changes (change-detected on NIntent._animationName, which updates on the
    // CombatStateChanged signal even while the node is frozen), and carried forward by MergeVolatile between
    // changes so the producer stays cheap while the intent is stable. Null for every non-intent-glyph node.
    RuntimeSceneIntentFramesSnapshot? IntentFrames = null,
    // Control.AnchorLeft / Control.AnchorRight — STATIC (add/keyframe only). Null for non-Control nodes. These are
    // the horizontal anchor fractions (0..1 of the PARENT's width) Godot uses to reposition/resize a Control when
    // its parent resizes. The mirror client reproduces Godot's own resize when it widens `.mirror-stage` beyond
    // 1920 on ultra-wide screens: a node shifts by `anchorLeft·Δ(parentWidth)` and widens by
    // `(anchorRight−anchorLeft)·Δ(parentWidth)` (the offsets cancel in the delta, so only anchors are streamed).
    // A right-anchored HUD (1/1) hugs the right edge, a center-anchored hand (0.5/0.5) re-centers (its cards ride
    // one shift → no tearing), a full-anchored backdrop (0/1) stretches to fill. Anchors don't change at runtime,
    // so like MouseFilter they ride add/keyframe only and MergeVolatile carries them forward.
    double? AnchorLeft = null,
    double? AnchorRight = null,
    // The instance id of the node this one is POSITIONALLY ANCHORED TO — STATIC (add/keyframe only). Some STS2
    // floaters (the on-hover HoverTip, `NHoverTipSet`) live on a persistent, un-anchored container but are
    // positioned each frame from ANOTHER control's GLOBAL rect (`NHoverTipSet._owner`). The client's wide-screen
    // re-layout shifts nodes only down the parent chain, so such a floater — parented off the shifted subtree —
    // renders at its owner's UN-shifted native x while the owner moves. Streaming the owner's id lets the client
    // apply the OWNER's horizontal shift (`spreadDx`) to the floater subtree, so the tooltip rides its button.
    // Null for every node that isn't owner-anchored. Like the anchors, MergeVolatile carries it forward.
    string? AnchorOwnerId = null,
    // BoxContainer layout hint — STATIC (add/keyframe only). Non-null ONLY on a Godot BoxContainer-derived node
    // (HBoxContainer/VBoxContainer/plain BoxContainer), encoding its orientation + packing alignment as
    // "hbox-begin" / "hbox-center" / "hbox-end" / "vbox-begin" / "vbox-center" / "vbox-end". A real Godot
    // BoxContainer IGNORES its children's anchors and RE-LAYS-OUT its packed row/column when its own box resizes
    // (a center-aligned HBox re-centers the row on a widened frame). The mirror clients reproduce Godot's own
    // resize on an ultra-wide stage from a node's anchors, but a container's children carry their OWN (ignored)
    // anchors — so without this hint a boxed 0/0 child (e.g. the card-reward "Skip" button in a full-width
    // center-aligned HBox) is stranded LEFT by the anchor algebra instead of riding the container's re-centering.
    // Per Godot-native-first, emitted for EVERY BoxContainer (consumers decide what to use); omit-when-null keeps
    // the wire cheap. Static: a container's orientation/alignment don't change at runtime, so it rides add/keyframe
    // only and MergeVolatile carries it forward. Null for every non-BoxContainer node.
    string? ContainerLayout = null,
    // A DECLARATIVE INFINITE ANIMATION the producer PINNED to its rest value on this node, named so a client can
    // replay it on its own clock — VOLATILE, but it changes only when the animation starts/stops, so it costs one
    // upsert per transition and NOTHING while it runs.
    //
    // Motivation: some STS2 per-frame animators are not garnish (which the producer may simply freeze — see
    // Sts2DecorEmitSuppress) but carry MEANING, so their churn can neither be streamed (a still screen would keep
    // paying a full delta pipeline 15-60x/s for one sine) nor silently dropped (the affordance would vanish). Naming
    // the loop turns per-frame streaming into a membership edge: the client knows WHICH nodes are animating and
    // reproduces the motion locally.
    //
    // The value is a short token from a vocabulary the CLIENTS own (period/amplitude/easing live with the replay,
    // exactly like the path-keyed decorative-animation bindings this extends). The tokens today:
    //   "mapPointPulse"  — Sts2MapPointPulse: the travelable map node's 0.95..1.45 icon scale sweep, period 1570.8ms
    //   "topBarDeckRock" — Sts2TopBarFold: the deck icon's ±0.12 rad sine rock, period 1570.8ms
    //   "topBarMapRock"  — Sts2TopBarFold: the map icon's ∓0.12 rad Sine/InOut tween, period 1600ms
    //   "topBarSpin"     — Sts2TopBarFold: the settings icon's constant 1 rad/s spin, period 6283.2ms
    //   "proceedGlow"    — Sts2ProceedGlow: the Proceed outline's 0.75↔0.25 self_modulate:a sweep, period 1000ms
    // Null for every node that has no pinned loop, i.e. almost all of them, so the wire is unchanged in practice
    // (`WhenWritingNull` omits it). A client that does not understand a token ignores it and renders the node at
    // rest — the pinned value is always a real resting pose, never a stranded mid-animation frame.
    string? PinnedLoopAnim = null,
    // A stable identity for the CONTENT a node is currently showing — STATIC (add/keyframe only), null for every
    // node that has none. Today the one case is a card: STS2 POOLS `NCard` visuals (~30 instances re-assigned as
    // cards move between hand/draw/discard/reward/shop), so a node's instance id says nothing about WHICH card is on
    // screen — the same id is a Strike this tick and a Bash the next. The key is `nc:{entry}#{serial}`, where
    // `{entry}` is the card DEFINITION id (shared by duplicate Strikes, so it is content-addressable) and
    // `{serial}` distinguishes the instances that share a definition, allocated per card MODEL so it survives the
    // card being recycled through different pooled nodes. See Sts2ContentKey.
    string? ContentKey = null,
    // PER-ROLE rich-text fonts — STATIC (add/keyframe only), non-null ONLY on a bbcode-enabled RichTextLabel
    // (`RichText = true`) whose theme actually names a DIFFERENT font file for that role.
    //
    // Godot does not synthesise bold/italic inside a RichTextLabel: it renders a `[b]` / `[i]` / `[b][i]` span by
    // swapping the label's font to its `bold_font` / `italics_font` / `bold_italics_font` THEME ITEM, which in STS2
    // is a different font FILE (`res://fonts/kreon_bold.ttf`, reached through the
    // `kreon_bold_glyph_space_one.tres` → `kreon_bold_shared.tres` FontVariation chain). With only `Font` on the
    // wire the mirror's `<strong>` inherited the label's single-face normal font and rendered un-bold (faux bold is
    // deliberately blocked by `font-synthesis: none`).
    //
    // Same value shape as `Font`: probed with `Control.GetThemeFont(name)` (the EFFECTIVE font — per-node override,
    // then the theme chain, then the default theme) and resolved through the same walk down the FontVariation chain
    // to the underlying `.ttf`/`.otf` binary, which the /res/ route serves as a real `@font-face` source. Omitted
    // (null) when the role font is unresolvable to a font binary — including Godot's built-in default-theme font,
    // which `GetThemeFont` always returns as a last resort — or when it resolves to the SAME file as `Font`, in
    // which case the span already renders in the right face and the field would carry no information. That makes
    // this free for the overwhelming majority of nodes. `mono_font` is SKIPPED BY DESIGN: godot-scene-web exposes no
    // `--godot-rich-mono-font-family` variable to consume it.
    //
    // Consumed as gsw's documented `--godot-rich-bold-font-family` / `--godot-rich-italic-font-family` /
    // `--godot-rich-bold-italic-font-family`. Static: theme fonts don't change at runtime, so these ride add/keyframe
    // only and MergeVolatile carries them forward. Kill-switch:
    // Sts2SceneWatchRuntimeSettings.StreamRichRoleFonts (`SPIRECTL_SCENE_WATCH_RICH_ROLE_FONTS=0`).
    RuntimeSceneResourceRefSnapshot? RichBoldFont = null,
    RuntimeSceneResourceRefSnapshot? RichItalicFont = null,
    RuntimeSceneResourceRefSnapshot? RichBoldItalicFont = null,
    // PER-ROLE rich-text font SIZES in px — STATIC, same gating as the role fonts above. Godot resolves each span
    // at its own `bold_font_size` / `italics_font_size` / `bold_italics_font_size` theme item, which STS2 authors
    // independently of `normal_font_size` on several scenes (card.tscn: 21, timeline_screen.tscn: 24,
    // unlock_relics_screen.tscn: 28). Omitted (null) when the role size EQUALS the node's normal font size (nothing
    // to say) or when either is unset, so a client that sees null renders the span at the node's own size.
    // Consumed as gsw's `--godot-rich-bold-font-size` and friends; prefer a RATIO against the node's normal size so
    // a mirror's own text-scale pipeline is not bypassed by an absolute px.
    double? RichBoldFontSizePx = null,
    double? RichItalicFontSizePx = null,
    double? RichBoldItalicFontSizePx = null,
    // PER-ROLE rich-text GLYPH SPACING in px — STATIC, same gating as the role fonts above. STS2 reaches its role
    // fonts through `FontVariation` resources carrying `spacing_glyph` (extra px inserted after every glyph):
    // `kreon_bold_glyph_space_one.tres` sets 1, `..._two` 2, `..._three` 3 — so a `[b]` span is tracked WIDER
    // in-game than the same text in the label's normal font. Read off the ROLE THEME FONT (before the resolution to
    // a binary, which walks past the variation that carries it). Omitted (null) when zero — the default, hence for
    // every node whose role font is a plain FontFile or an unspaced variation. Consumed as gsw's
    // `--godot-rich-bold-letter-spacing` and friends.
    double? RichBoldFontSpacingPx = null,
    double? RichItalicFontSpacingPx = null,
    double? RichBoldItalicFontSpacingPx = null,
    // THE ENGINE'S OWN LINE BREAKING — where Godot wrapped this label, so a client does not have to guess.
    //
    // `TextLineRanges` is FLATTENED `[start0,end0,start1,end1,…]` (the `LinePoints` precedent), half-open
    // character offsets from `TextParagraph.get_line_range` / `RichTextLabel.get_line_range`. Those come out of
    // `TextServer.shaped_text_get_line_breaks` run with the node's real width, autowrap flags, justification
    // flags and overrun behaviour, so they describe the wrap the game ACTUALLY drew rather than a reproduction
    // of it. A consumer that slices at these offsets gets the engine's breaks for free, including the ones a
    // space-breaking heuristic cannot reach at all (unbroken scripts, tab stops, trimmed overruns).
    //
    // `TextLineBasis` names WHICH STRING they index — `"text"` (the node's own) or `"parsed"`
    // (`TextParsedText`, the markup-stripped content of a bbcode RichTextLabel, whose `Text` is the markup).
    // Guessing it is a wrong-words bug, so it is carried rather than inferred, and the ranges are omitted
    // entirely when it cannot be established.
    //
    // STATIC-PATH, PER-TICK TEXT — hence `TextLineSourceLength`/`TextLineSourceHash`, which are NOT optional
    // hygiene. `Text` is streamed on the lean per-tick path and these ride the static one, so a label whose
    // words change without a static re-describe carries ranges for the string it used to hold. A consumer MUST
    // re-compute the hash (FNV-1a 32-bit over UTF-16 code units, see Sts2RuntimeSceneTextDiagnostics.Fnv1a32)
    // over the string it is about to draw and fall back to its own line breaking on any mismatch.
    IReadOnlyList<int>? TextLineRanges = null,
    string? TextLineBasis = null,
    string? TextParsedText = null,
    int? TextLineSourceLength = null,
    int? TextLineSourceHash = null,
    // LINE2D STROKE GEOMETRY (the map quill annotations) — the three fields ship as ONE unit and are STICKY, i.e.
    // emitted on add/keyframe or when the stroke's cheap per-tick signature changed, and carried forward by
    // MergeVolatile in between (exactly the `IntentFrames` policy, NOT the per-tick `Text` policy). Null on every
    // node that is not a `Line2D` — i.e. all but the handful of strokes under `…/MapDrawing/DrawViewport`.
    //
    // A map annotation is a `Line2D` instanced from `res://scenes/screens/map/map_line_draw.tscn` (pen) or
    // `..._erase.tscn` (eraser) and appended live under a 960x1620 SubViewport; its points are pushed with
    // `AddPoint(pos * 0.5)` as the finger drags. Such a node has no texture rect and no text — its ENTIRE appearance
    // is points + width + colour — so before this it streamed (with a correct transform) as a completely blank node.
    //
    // `LinePoints` is FLATTENED — `[x0,y0,x1,y1,…]` — in NODE-LOCAL coordinates, the same space as `LocalRect`, so a
    // client draws the polyline in the node's own box and lets the streamed `Transform` place it like any other node.
    // Every value is rounded to 2 dp (the watcher's house quantization for positional data). An EMPTY list is
    // meaningful and distinct from null: it means the stroke was CLEARED (undo / clear-all) and the client should
    // erase what it drew, whereas null means "unchanged — keep the retained geometry".
    //
    // `LineWidth` is the Godot `width` in node-local units (STS2: 4 for the pen, 12 for the eraser). `LineColor` is
    // `default_color` (the `Line2D` tint the stroke is drawn in), in the same shape as every other colour field.
    //
    // NOT streamed, by design: joint/cap modes (constant `round` on both authored stroke scenes — a client hard-codes
    // round joins and caps), texture/texture_mode (constant `tile`; the texture itself already rides the generic
    // `Texture` probe), and any eraser FLAG — an eraser is exactly the stroke whose `Shader` is
    // `res://shaders/map_drawing/line_erase.gdshader` (blend_sub), and that path already streams.
    //
    // SCOPE: MAP QUILL STROKES ONLY. A `Line2D` is a generic primitive the game also uses for VFX (most visibly the
    // `card_trail_<character>.tscn` strokes behind every flying card, and hyperbeam / bolas / creature-visual lines),
    // and none of those are describable by this unit — their look is a `width_curve` taper, an alpha `gradient`, a
    // stretched texture and an ADDITIVE material, all dropped here — so a consumer given their points painted a solid
    // bar. These three fields therefore appear ONLY on a node instanced from `map_line_draw.tscn` /
    // `map_line_erase.tscn` (or a code-built stroke parented to `DrawViewport`).
    //
    // Cost: an ACTIVELY-DRAWN stroke re-ships its whole array on each changed tick (~4-6 KB at 300 points, for the
    // one stroke under the finger); at rest it is exactly zero. Kill-switch:
    // Sts2SceneWatchRuntimeSettings.Line2DGeometryScope (`SPIRECTL_SCENE_WATCH_LINE2D_GEOMETRY=0` restores the
    // pre-feature wire byte-identically; `=all` un-scopes it back to every Line2D, an A/B lever only).
    // See Sts2Line2DGeometryEmit.
    IReadOnlyList<double>? LinePoints = null,
    double? LineWidth = null,
    RuntimeSceneColorSnapshot? LineColor = null,
    // Control.clip_contents — STATIC (add/keyframe only), and a DIFFERENT Godot property from `ClipChildren`
    // above. `ClipChildren` is CanvasItem.clip_children: it stencils descendants against this node's own DRAWN
    // alpha. `ClipContents` is Control.clip_contents: it clips this Control's children to its RECTANGLE, whether
    // or not the Control paints anything at all. A layout container that paints nothing therefore has
    // clipChildren == 0 and can still be the only thing bounding its children — which is how the game hides a
    // panel's content by parking it outside the panel box instead of touching `visible`/`modulate`.
    //
    // Emitted only when TRUE, so the wire for every other node is byte-identical (the consumer's default is
    // false). Kill-switch Sts2SceneWatchRuntimeSettings.StreamClipContents
    // (`SPIRECTL_SCENE_WATCH_CLIP_CONTENTS=0` restores the pre-feature wire byte-identically).
    bool ClipContents = false,
    // Whether this node is currently focused by STS2's clickable-control input state. Null means the node does
    // not expose a readable focus property; false/true are authoritative and VOLATILE. Consumers use this to
    // distinguish a first touch (focus only) from a touch on a control that is already ready to activate.
    bool? Focused = null);

// The ordered frame set for one enemy-intent animation, streamed once per intent change (see
// RuntimeSceneNodeDelta.IntentFrames). `Fps` is NIntent._animationFps (15). `Frames` are the ordered
// AtlasTexture frames; the client cycles them at `Fps` off a shared wall-clock. Single-frame sets
// (attack/death_blow/hidden) render statically (the client runs no timer).
public sealed record RuntimeSceneIntentFramesSnapshot(
    string AnimationName,
    int Fps,
    IReadOnlyList<RuntimeSceneIntentFrameSnapshot> Frames);

// One intent animation frame, resolved to a whole-atlas crop exactly like RuntimeSceneNodeDelta.Texture +
// TextureRegion/TextureMargin: `AtlasPath` is the underlying atlas PAGE (`res://…`), `Region` the source rect
// inside it, `Margin` the transparent frame Godot draws the region within. The client fetches the page once and
// crops each frame client-side (CSS/canvas), so no per-frame image is sent. `Region`/`Margin` are null only for
// the (unexpected) non-atlas texture fallback, where `AtlasPath` is the whole image.
public sealed record RuntimeSceneIntentFrameSnapshot(
    string AtlasPath,
    RuntimeSceneRect2Snapshot? Region,
    RuntimeSceneRect2Snapshot? Margin);
