using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using Spirectl.Sts2.Common;
using Spirectl.Sts2.Core.SceneInspection;

namespace Spirectl.Sts2.Live;

// Stateful, incremental scene-tree watcher — the high-frequency transport behind the live "mirror".
//
// Why it exists: the stateless Sts2RuntimeSceneProvider.GetTree rebuilds the WHOLE tree (paths +
// reflection + properties) every call. Running that ~30-60×/s on the game main thread drops the game to
// ~20fps. This watcher instead:
//   - Caches STATIC per-node data (id/name/type/parent), keyed by node instance id, refreshed only when
//     the structure changes (driven by SceneTree node_added/node_removed/tree_changed signals).
//   - Per tick reads ONLY cheap LOCAL volatile fields (global rect, local visible, modulate alpha,
//     z-index, rotation, primary texture, lean text) — no IsVisibleInTree / EffectiveDrawModulate parent
//     walks (the client composes effective visibility/opacity down the parent chain).
//   - Prunes hidden subtrees (a locally-invisible node skips its descendants).
//   - Emits DELTAS (changed nodes only), so the client patches a retained map instead of re-rendering.
//
// Everything Godot-touching runs on the main thread (the tick + signals fire there), so the registry needs
// no locks; only the subscriber list is lock-guarded and delta dispatch is handed to a background task.
internal sealed partial class Sts2RuntimeSceneWatcher : IRuntimeSceneWatcher, IDisposable
{
    private const long MinEmitIntervalMs = 16; // ~60Hz cap. Client flow-control + per-connection coalescing pace
    // a slow client regardless, so a higher emit rate only adds game-process CPU (tree walk + serialize) on the
    // host — costly on a low-end laptop where the game, producer, and browser share the CPU. 60Hz matches refresh.

    // Idle backoff ceiling for the capture walk. When the scene is quiescent the (relatively expensive) tree walk
    // + per-node signature rebuild finds nothing to send, yet without a backoff it re-runs on every ~100Hz
    // dispatcher tick for zero output — the headless instance's idle CPU floor. We instead poll for change at
    // ~8Hz when idle, snapping back to the active cap on any change. Bounded low enough that the worst-case wake
    // latency after true idle (before a structural signal resets the gate) is imperceptible.
    private const long MaxIdleIntervalMs = 128;

    // Per managed-Type cache of how to read a node's primary texture NATIVELY: which native property to Get
    // ("texture"/"texture_normal"), the wire Field name, and whether the node is a NinePatchRect. Resolved ONCE per
    // type via IsClass on the first instance (the native class is stable per script type) — a script-attached native
    // texture node's managed wrapper is its SCRIPT's base class (e.g. an NCommonBanner : Control on a native
    // TextureRect), so managed reflection for a "Texture" property returns null and the baked texture never ships;
    // the native class (IsClass/Get) sees the real engine property for script-attached AND script-less nodes alike.
    private static readonly Dictionary<Type, TextureProbe> TextureProbeByType = new();

    private readonly object _subscriberGate = new();
    private readonly List<SubscriberEntry> _subscribers = [];
    private SubscriberEntry[] _subscriberSnapshot = [];
    private long _subscriberGeneration;
    private long _fullRequestVersion;
    private long _acceptedFullRequestVersion;
    private readonly Dictionary<ulong, Tracked> _registry = [];
    private readonly List<Tracked> _ordered = [];
    // Every prefix-producing SubViewport found by the last Reconcile, in pre-order DFS (outer viewports first).
    // RefreshViewportPrefixes re-evaluates these once per capture pass so a scrolling display node's motion reaches
    // the content flattened out of its viewport on EVERY tick, not only on a structure-dirty one.
    private readonly List<ViewportPrefixChain> _prefixChains = [];

    private readonly SceneAnimationCoordinator _animations;
    private readonly Sts2SceneAnimationBinding _animationBinding;
    private int _disposed;

    public Sts2RuntimeSceneWatcher(ISts2RuntimeInstrumentation? instrumentation = null)
    {
        _instrumentation = instrumentation ?? Sts2RuntimeInstrumentation.None;
        _profile = _instrumentation.CreateProducerWalkAccumulator();
        _animations = new SceneAnimationCoordinator(
            _registry, _profile, () => _instrumentation.ProducerProfilingEnabled, EmittedParentGlobalTuple);
        _animationBinding = new Sts2SceneAnimationBinding(
            _animations.ResolveTweenEndpoint,
            _animations.CancelTweenSuppression,
            request => _animations.ResolveCardFlight(
                request.FlightInstanceId, request.TrailInstanceId, request.TrailStrokeInstanceIds,
                request.StartGlobal, request.EndGlobal, request.ControlGlobal,
                request.Speed0, request.Accel, request.Duration, request.Scale0, request.NodeRotation),
            request => _animations.ResolveDiscardFlight(
                request.CardInstanceId, request.TrailInstanceId, request.TrailStrokeInstanceIds,
                request.StartGlobal, request.EndGlobal, request.ControlGlobal,
                request.Speed0, request.Accel, request.Duration),
            _animations.HasOpenTransformWindow,
            activate: false);
        _animations.ShuffleFlightPublisher = Sts2CardFlightHooks.PublishResolvedHint;
        _animations.DiscardFlightPublisher = Sts2DiscardFlightHooks.PublishResolvedHint;
    }

    private bool _tickHooked;
    private IDisposable? _tickLease;
    private bool _signalsHooked;
    private SceneTree? _tree;
    private ulong _rootId;
    private bool _structureDirty = true;
    private bool _needsFull = true;
    // LOCAL-transform emission mode (Sts2SceneWatchRuntimeSettings.EmitLocalTransforms), LATCHED once per capture so a
    // single capture is internally consistent even if an embedder flips the knob mid-walk. When it differs from the
    // last capture's mode a FULL keyframe is forced (see Capture) so clients rebuild in the new space and every
    // Tracked.LastTransform is overwritten with new-space values.
    private bool _lastCaptureLocalMode;
    // Reusable depth-indexed scratch of each emitted node's streamed GLOBAL transform, for LOCAL-mode re-basing:
    // scratch[d] holds the last emitted depth-d node's global, so a depth-d child re-bases against scratch[d-1] (its
    // nearest emitted ancestor). The pre-order DFS walk fills a slot before any child reads it. Grown, never
    // re-allocated per capture (the walk is allocation-sensitive). Only populated/read in local mode.
    private readonly List<Transform2D> _emittedGlobalByDepth = [];

    // A parent transform with |determinant| at or below this is treated as singular (visually collapsed) when
    // re-basing a child to LOCAL space — no inverse exists, so the child emits identity local (the collapsed parent
    // already zeroes the subtree on screen). Matches Sts2TweenEndpointTuples' singular epsilon.
    private const double SingularParentDeterminantEpsilon = 1e-9;
    // Adaptive capture cadence. _lastCaptureMs advances on every gated attempt (not just on emit), and
    // _captureIntervalMs grows geometrically toward MaxIdleIntervalMs while captures keep finding nothing, so an
    // idle client stops re-walking the tree ~100x/sec. Any change (a delta emitted, or a structural signal)
    // resets it to MinEmitIntervalMs so the next real change captures at full rate.
    private long _lastCaptureMs;
    private long _captureIntervalMs = MinEmitIntervalMs;
    private string _screenType = "unknown";
    private string _screenInstanceId = "screen:unknown:live";

    // Optional bridge-owned producer instrumentation; embedded runtimes keep the no-op default.

    // How much wall-clock one profiler window covers before it is logged, retained and reset.
    private const long ProfileWindowMs = 1000;

    // Frozen spine-skeleton read-elision (default ON; SPIRECTL_SCENE_WATCH_ELIDE_FROZEN_SPINE=0 disables). When a
    // SpineSprite clip root is frozen (ProcessMode.Disabled — the mod permanently freezes all spine skeleton nodes)
    // AND its global transform is unchanged this tick, its skeleton-leaf descendants (bones/slots/meshes) have
    // unchanged globals and would produce no delta, so we skip their ReadVolatile+diff entirely. Kill-switch for A/B.
    private static readonly bool ElideFrozenSpine =
        (System.Environment.GetEnvironmentVariable("SPIRECTL_SCENE_WATCH_ELIDE_FROZEN_SPINE") ?? "1")
            .Trim().ToLowerInvariant() is not ("0" or "false" or "off" or "no");

    // R10 ORDER/UPSERT CONTRACT (default ON; SPIRECTL_SCENE_WATCH_ORDER_EMITTED_ONLY=0 restores the pre-R10 array).
    // ON: OrderedIds carries only nodes that have actually been emitted, and a node's FIRST emit re-ships the order
    // even without a tree-shape change. See the orderedIds build in Capture for why both halves are one feature.
    private static readonly bool OrderEmittedOnly =
        (System.Environment.GetEnvironmentVariable("SPIRECTL_SCENE_WATCH_ORDER_EMITTED_ONLY") ?? "1")
            .Trim().ToLowerInvariant() is not ("0" or "false" or "off" or "no");

    // Defer color #RRGGBBAA formatting to emitted nodes only (default ON; SPIRECTL_SCENE_WATCH_DEFER_COLOR_HTML=0
    // restores eager formatting). The per-tick change test compares NUMERIC channels (ColorEq), so the hex string
    // is dead weight for every unchanged node — build it in BuildNodeDelta (runs only for emitted nodes) instead of
    // in ReadVolatile (runs for every visible node). Kill-switch for A/B + safety.
    private static readonly bool DeferColorHtml =
        (System.Environment.GetEnvironmentVariable("SPIRECTL_SCENE_WATCH_DEFER_COLOR_HTML") ?? "1")
            .Trim().ToLowerInvariant() is not ("0" or "false" or "off" or "no");

    // Resolve PROCEDURAL ramp sampler uniforms (Gradient/Curve) to their authored data on the shader-parameter
    // snapshot (default ON; SPIRECTL_SCENE_WATCH_SHADER_PARAM_GRADIENTS=0 emits the resource PATH only, as before).
    // STS2's VFX particle shaders colour every texel through a `lut` GradientTexture1D
    // (`COLOR = vec4(texture(lut, texture_color.rr).rgb, erosion) * vertex_color`), so a client holding only the
    // sampler path renders the raw red-channel MASK (the energy orb / hit streaks as a red-orange square). The
    // stops are read ONCE, on the static add walk (ToShaderParams), off the per-tick volatile path.
    private static readonly bool EmitShaderParamGradients =
        (System.Environment.GetEnvironmentVariable("SPIRECTL_SCENE_WATCH_SHADER_PARAM_GRADIENTS") ?? "1")
            .Trim().ToLowerInvariant() is not ("0" or "false" or "off" or "no");

    // #7 (creature death shrink): honor a SubViewport's `size_2d_override` when flattening its content (default
    // ON; SPIRECTL_SCENE_WATCH_VIEWPORT_SIZE_OVERRIDE=0 restores the pre-fix behavior). A SubViewport that renders
    // at a higher pixel `Size` than its 2D coordinate space (`size_2d_override`) supersamples: its child transforms
    // live in `size_2d_override` space, but the ViewportTexture the consumer draws is `Size` pixels. The
    // MonsterDeathVfx uses exactly this (`Size = 2·num`, `size_2d_override = num`) so its "Visual" Sprite2D can
    // display a crisp, dissolving corpse at the creature's normal on-screen size. TryComputeViewportPrefix measured
    // the fit against `Size` and dropped the `Size / size_2d_override` factor, so the flattened creature was emitted
    // at half scale — the reported "creature gets small before disappearing". Composing the supersample factor back
    // restores its true on-screen size. Only SubViewports that actually set size_2d_override are affected (the
    // card-intent viewport does not), so this is a no-op for every existing case.
    private static readonly bool HonorViewportSizeOverride =
        (System.Environment.GetEnvironmentVariable("SPIRECTL_SCENE_WATCH_VIEWPORT_SIZE_OVERRIDE") ?? "1")
            .Trim().ToLowerInvariant() is not ("0" or "false" or "off" or "no");

    // R11 producer region-emit cap (default 100ms = 10Hz; SPIRECTL_REGION_EMIT_CAP_MS=0 disables). A Sprite2D playing
    // an AtlasTexture flip-book (the Tezcatara candle flames) swaps its TextureRegion crop EVERY tick → a per-frame
    // TextureRegion delta per flame floods producer→wire→client (the 9-14fps signature). This paces a PURE same-size
    // frame swap (a real Draw — a size-changing swap — is NEVER capped) to at most one emit per window per node: within
    // the window the previously-emitted region is substituted before the change test so the swap is invisible to the
    // signature; the window then elapses and the newest crop ships (icons/flames keep animating — paced, never frozen).
    // See the region-cap block in the capture loop + Tracked.LastRegionEmitAtMs.
    private static readonly int RegionEmitCapMs = ParseRegionEmitCapMs();

    private static int ParseRegionEmitCapMs()
    {
        var raw = System.Environment.GetEnvironmentVariable("SPIRECTL_REGION_EMIT_CAP_MS");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return 100;
        }

        return int.TryParse(raw.Trim(), out var v) && v >= 0 ? v : 100;
    }

    // R11 producer cosmetic-emit cap (DEFAULT OFF; set SPIRECTL_COSMETIC_EMIT_CAP_MS=100 for 10Hz pacing, 0 disables).
    // The Tezcatara rest-site fire VFX animate their TRANSFORM every frame (a continuous flicker) — the recorded wire
    // shows transform is the ONLY per-tick-changing channel, ~97 upserts/drain × ~30 drains/s (~6 MB/s). This paces
    // SUSTAINED per-frame transform churn (a node whose transform has moved for >= CosmeticChurnTicks consecutive ticks)
    // to at most one emit per window per node: within the window the previously-emitted transform is substituted before
    // the change test (invisible to the signature; children re-base against the pinned global so an emitting child stays
    // placed), then the window elapses and the newest transform ships. One-shot moves (card plays, reparents) never
    // reach the streak so they stream instantly; a node leaving churn settles to its exact final transform.
    //
    // DEFAULT OFF because a replay A/B (tezca-defaultbridge.ndjson through the real wide-screen client) showed the
    // Tezcatara 9-14fps is NOT bounded by the upsert flood: stripping even 98% of the transform upserts moved fps only
    // 11→12 (frameMs 89.6→81), while the frame is dominated by ~166 client-side particle/shader nodes the browser
    // re-simulates every frame (render[continuous=166]) — which the producer cap cannot touch. The cap remains a ready,
    // correct BANDWIDTH/GC lever (43% churn reduction cut client GC allocMb 404→292 and gcPause 156→104ms — useful for
    // phone/low-end clients) with no fps regression; enable it where wire/GC pressure matters. See the cosmetic-cap
    // block in the capture loop + Tracked.PrevRealTransform/CosmeticChurnStreak/LastCosmeticEmitAtMs and
    // Sts2CosmeticEmitCap.
    private static readonly int CosmeticEmitCapMs = ParseCosmeticEmitCapMs();

    // Consecutive ticks of continuous (full-precision) transform churn before pacing engages. Chosen from the recording:
    // one-shot moves last 1-2 ticks; the fire VFX churn for the whole event — K=10 (~0.33s at 30Hz) cleanly protects the
    // former while paying the warmup only once per continuous flame. SPIRECTL_COSMETIC_CHURN_TICKS overrides.
    private static readonly int CosmeticChurnTicks = ParseCosmeticChurnTicks();

    private static int ParseCosmeticEmitCapMs()
    {
        var raw = System.Environment.GetEnvironmentVariable("SPIRECTL_COSMETIC_EMIT_CAP_MS");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return 0; // default OFF — see the note above; enable with =100 (10Hz) where bandwidth/GC matters
        }

        return int.TryParse(raw.Trim(), out var v) && v >= 0 ? v : 0;
    }

    private static int ParseCosmeticChurnTicks()
    {
        var raw = System.Environment.GetEnvironmentVariable("SPIRECTL_COSMETIC_CHURN_TICKS");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return 10;
        }

        return int.TryParse(raw.Trim(), out var v) && v >= 1 ? v : 10;
    }

    // R12 decorative-animator folds MASTER switch (default ON; SPIRECTL_DECOR_EMIT_SUPPRESS=0 restores the pre-fix
    // streaming for all of them). A handful of always-mounted UI nodes run an INFINITE purely-decorative per-frame
    // animator (the top-bar deck/map/settings icon rock-or-spin, the Proceed button's pulsing glow alpha) whose churn
    // alone keeps a VISUALLY IDLE screen emitting 25-60 deltas/s — on the map and the treasure/proceed screens it is
    // the SOLE churner, i.e. 100% of the wire. For a node matched by scene identity the producer divides the ONE
    // churning channel analytically back out of the stream and names the loop on the wire
    // (`RuntimeSceneNodeDelta.PinnedLoopAnim`) so the client replays it on its own clock. See Sts2DecorEmitSuppress
    // for the rule table and Sts2TopBarFold / Sts2ProceedGlow / Sts2MapPointPulse for each animation's own notes.
    private static readonly bool DecorEmitSuppress =
        (System.Environment.GetEnvironmentVariable("SPIRECTL_DECOR_EMIT_SUPPRESS") ?? "1")
            .Trim().ToLowerInvariant() is not ("0" or "false" or "off" or "no");

    // R13 TOP-BAR ICON FOLD (default ON; SPIRECTL_TOPBAR_FOLD=0 restores plain streaming for the three icons). Also
    // gated by the master switch above. See Sts2TopBarFold: while a button's screen is open the icon's ROTATION is
    // divided back out of the streamed transform (position + scale keep streaming — which is what fixes the WS-D
    // mispositioning bug) and the per-button loop token is shipped for the client to replay.
    private static readonly bool TopBarFold =
        (System.Environment.GetEnvironmentVariable("SPIRECTL_TOPBAR_FOLD") ?? "1")
            .Trim().ToLowerInvariant() is not ("0" or "false" or "off" or "no");

    // R13 PROCEED-GLOW FOLD (default ON; SPIRECTL_PROCEED_FOLD=0 restores plain streaming for the glow). Also gated
    // by the master switch. See Sts2ProceedGlow: while the infinite glow tween runs, self_modulate's ALPHA is
    // substituted with the loop's analytic 0.75 (RGB keeps streaming) and "proceedGlow" is named on the wire.
    private static readonly bool ProceedFold =
        (System.Environment.GetEnvironmentVariable("SPIRECTL_PROCEED_FOLD") ?? "1")
            .Trim().ToLowerInvariant() is not ("0" or "false" or "off" or "no");

    // R13 CONTENT KEY (default ON; SPIRECTL_CONTENT_KEY=0 stops emitting the field). Independent of the folds above
    // (it costs nothing per tick — it rides the STATIC block only) but kept switchable like every other wire
    // addition. See Sts2ContentKey: a stable `nc:{entry}#{serial}` identity for the POOLED card visuals.
    private static readonly bool ContentKeys =
        (System.Environment.GetEnvironmentVariable("SPIRECTL_CONTENT_KEY") ?? "1")
            .Trim().ToLowerInvariant() is not ("0" or "false" or "off" or "no");

    // R12b MAP-POINT PULSE FOLD (default ON; SPIRECTL_MAPPOINT_FOLD=0 restores the pre-fix streaming). The one
    // per-frame animator WS-D deliberately left running — the travelable map node's icon-scale sweep — is the top
    // residual churner: a map screen still emits ~15-24 deltas/s and every one of them is a map point. It cannot be
    // pinned-and-forgotten like the four garnish animators (WHICH nodes pulse is how the game shows you where you
    // may travel), so this is a REAL fold: divide the pulse's uniform scale back out of the streamed transform AND
    // ship a declarative `PinnedLoopAnim` flag that changes only on pulse MEMBERSHIP changes, which the client
    // replays on its own clock. A separate switch from DecorEmitSuppress because the client replay is separate too.
    // See Sts2MapPointPulse for the pulse's shape, the gates, and the analytic rest value.
    private static readonly bool MapPointFold =
        (System.Environment.GetEnvironmentVariable("SPIRECTL_MAPPOINT_FOLD") ?? "1")
            .Trim().ToLowerInvariant() is not ("0" or "false" or "off" or "no");

    // R14 IDLE-WIRE COMBAT FOLDS. Four more always-mounted decorative animators, all in COMBAT, which is why an
    // idle combat kept streaming ~30 msgs/s (measured: 898 messages / 30s, longest gap 91ms) after R12/R13 had
    // silenced the map and the dialog screens. Each has its own switch; all four are ALSO gated by the master
    // SPIRECTL_DECOR_EMIT_SUPPRESS above. See Sts2IntentBobFold / Sts2IntentGlyphFold / Sts2OrbSpinFold /
    // Sts2EndTurnGlowFold for each animation's shape, the analytic rest values and the accepted costs.

    // The enemy-intent holder's per-frame sine POSITION, subtracted back out so the client's path-keyed `bob`
    // replay composes to the exact on-screen motion. SPIRECTL_INTENT_BOB_FOLD=0 restores streaming.
    private static readonly bool IntentBobFold =
        (System.Environment.GetEnvironmentVariable("SPIRECTL_INTENT_BOB_FOLD") ?? "1")
            .Trim().ToLowerInvariant() is not ("0" or "false" or "off" or "no");

    // The enemy-intent glyph's 15fps flip-book crop, replaced by FRAME 0 of the frame set this node already streams
    // (`IntentFrames`) — which the mirror client uses instead of the streamed crop regardless.
    // SPIRECTL_INTENT_GLYPH_FOLD=0 restores the per-frame crop.
    private static readonly bool IntentGlyphFold =
        (System.Environment.GetEnvironmentVariable("SPIRECTL_INTENT_GLYPH_FOLD") ?? "1")
            .Trim().ToLowerInvariant() is not ("0" or "false" or "off" or "no");

    // The energy/star counter orb layers' accumulated ROTATION, divided back out to the authored 0.
    // SPIRECTL_ORB_SPIN_FOLD=0 restores streaming.
    private static readonly bool OrbSpinFold =
        (System.Environment.GetEnvironmentVariable("SPIRECTL_ORB_SPIN_FOLD") ?? "1")
            .Trim().ToLowerInvariant() is not ("0" or "false" or "off" or "no");

    // The end-turn button glow's infinite scale+alpha pulse, pinned to the loop's own start values and named on the
    // wire for the client to replay. SPIRECTL_ENDTURN_GLOW_FOLD=0 restores streaming.
    private static readonly bool EndTurnGlowFold =
        (System.Environment.GetEnvironmentVariable("SPIRECTL_ENDTURN_GLOW_FOLD") ?? "1")
            .Trim().ToLowerInvariant() is not ("0" or "false" or "off" or "no");

    // R15 CREATURE SPINE-ANCHOR FOLD (default ON; SPIRECTL_SPINE_ANCHOR_FOLD=0 restores plain streaming for the
    // anchors). Also gated by the master SPIRECTL_DECOR_EMIT_SUPPRESS above. After R14 landed, a live 6s capture of
    // an IDLE combat measured 52.8 msg/s across EIGHT node ids: four `SpineSlotNode` creature anchors plus their
    // emitter children — 100% of the wire. Unlike every fold above this one substitutes NOTHING and names no loop:
    // an anchor paints no pixel of its own, so while it provably cannot move anything on screen (no descendants at
    // all, or only idle particle emitters) the watcher simply WITHHOLDS its transform through the same
    // SuppressTransformUntil depth sentinel that streaming tween-suppression uses — which silences the emitter
    // children too. See Sts2SpineAnchorFold for the two arms, the scene table and the burst-tail gate.
    private static readonly bool SpineAnchorFold =
        (System.Environment.GetEnvironmentVariable("SPIRECTL_SPINE_ANCHOR_FOLD") ?? "1")
            .Trim().ToLowerInvariant() is not ("0" or "false" or "off" or "no");

    // Cached Godot member names for the fold gates. Every one is a REGISTERED script member
    // (`PropertyUsageFlags.ScriptVariable`, or a MethodInfo for the one Call), so they are readable through Godot's
    // own Get/Call — no System.Reflection on the capture path.
    //
    // `_isEnabled` + `IsFocused` are declared on the shared `NClickableControl` base, so the SAME two names serve
    // both the map point (NNormalMapPoint) and the Proceed button (NProceedButton : NButton : NClickableControl).
    private static readonly StringName ClickableEnabledProp = "_isEnabled";
    private static readonly StringName ClickableFocusedProp = Sts2ClickableFocus.PropertyName;
    private static readonly StringName MapPointInputAllowedMethod = "IsInputAllowed";

    // `NTopBarButton.IsScreenOpen` — the gate of the deck rock + settings spin (see Sts2TopBarFold). The MAP
    // button's flag is dead (never updated); its gate is the registered `IsOpen()` method below — see
    // ReadTopBarScreenOpen.
    private static readonly StringName TopBarScreenOpenProp = "IsScreenOpen";
    private static readonly StringName TopBarIsOpenMethod = "IsOpen";

    // `NProceedButton._shouldPulse` — the third gate of the glow loop (see Sts2ProceedGlow).
    private static readonly StringName ProceedShouldPulseProp = "_shouldPulse";

    // `NEndTurnButton._isShiny` — the single, exact gate of the end-turn glow's infinite pulse loop: it is set true
    // immediately before `GlowPulse()` (its only caller) and false immediately before the kill. See
    // Sts2EndTurnGlowFold.
    private static readonly StringName EndTurnShinyProp = "_isShiny";

    // Streaming suppression (Part C, default ON): while the mirror REPLAYS a tween on a node, the client pins that
    // subtree's transform and DISCARDS every streamed transform for it — so suppressing those transform-only deltas
    // producer-side is pure bandwidth/CPU savings with no on-screen change (see ResolveTweenEndpoint + the capture
    // loop). Kept as a kill-switch for A/B + safety. Now a RUNTIME-adjustable knob on Sts2SceneWatchRuntimeSettings
    // (seeded from SPIRECTL_SCENE_WATCH_SUPPRESS_TWEENED) so an embedder (the CouchCoop Settings panel) can flip
    // "send tween properties vs per-frame node properties" live.
    private static bool SuppressTweenedTransforms => Sts2SceneWatchRuntimeSettings.SuppressTweenedTransforms;

    // Opacity streaming suppression (Part C Stage 4, default ON). While the mirror REPLAYS a modulate/self_modulate
    // fade the client PINS the target's opacity, so every per-frame streamed opacity delta for it is discarded —
    // dropping them producer-side is pure savings. Unlike transform suppression, opacity is emitted PER-NODE-LOCAL
    // (ReadVolatile reads each node's OWN modulate/self_modulate), so a parent fade changes only the parent's local
    // alpha → suppression is TARGET-ONLY (no subtree/depth sentinel). Runtime-adjustable (seeded from
    // SPIRECTL_SCENE_WATCH_SUPPRESS_TWEENED_OPACITY) via Sts2SceneWatchRuntimeSettings.
    private static bool SuppressTweenedOpacity => Sts2SceneWatchRuntimeSettings.SuppressTweenedOpacity;

    // A tween-suppression window can never exceed this — a stuck-window backstop if a captured duration is bogus.
    private const long SuppressCapMs = 2000;

    // WS-3: how many not-yet-tracked card-flight windows may be parked at once. A shuffle spawns one flight per
    // shuffled card and each parks at most 4 ids (flight node + trail root + its two NCardTrail strokes), so this
    // covers a ~30-card reshuffle with headroom; past it the oldest-deadline entries are dropped and those nodes
    // simply keep streaming (fail-open, exactly like every other producer suppression).
    private const int CardFlightPendingCap = 256;

    // Bounded #8 late-static re-probe attempts per spine root whose animation list came up empty on add (a
    // dynamically-spawned chest whose skeleton is assigned a frame or more later). Covers a few seconds of ticks;
    // after this the node keeps its empty snapshot (a spine that genuinely exposes no animations never renders a
    // clip anyway) so the re-probe can never spin forever.
    private const int SpineReprobeMaxAttempts = 30;
    private readonly System.Diagnostics.Stopwatch _captureStopwatch = new();
    // The optional producer-walk instrumentation is disabled for embedded runtimes. The watcher only feeds its
    // narrow counter seam; bridge-owned diagnostics own aggregation, logging, and report serialization:
    //   * RecordCapture       — per-capture wall time + whether it emitted (producer share of one core).
    //   * RecordNode          — per-category read+diff cost, so a walk-CPU claim is attributable to a category
    //                           (frozen spine skeletons vs text vs sprites vs controls) rather than asserted.
    //   * RecordSkelElided    — skeleton-leaf reads skipped by ElideFrozenSpine.
    //   * RecordPrefixRefresh — per-pass viewport-prefix refresh cost; must stay a rounding error next to the walk.
    //   * RecordSuppress*     — tween/opacity suppression windows opened and per-capture changes actually withheld
    //                           because a node was mid-replay/mid-fade (drops>0 proves suppression is firing).
    private readonly ISts2RuntimeInstrumentation _instrumentation;
    private readonly ISts2ProducerWalkAccumulator _profile;

    private enum NodeCategory
    {
        SpineSkeleton = 0,
        SpineRoot = 1,
        Text = 2,
        Particle = 3,
        Sprite = 4,
        Control = 5,
        Other = 6,
    }

    public IDisposable Subscribe(Action<RuntimeSceneDelta> onDelta)
    {
        ArgumentNullException.ThrowIfNull(onDelta);
        // Tell the bridge-owned instrumentation whether windows are coming at all.
        _instrumentation.MarkProducerProfilingEnabled(_instrumentation.ProducerProfilingEnabled);
        var subscriber = new SubscriberEntry(onDelta);
        lock (_subscriberGate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_subscribers.Count == 0) _subscriberGeneration++;
            _subscribers.Add(subscriber);
            _subscriberSnapshot = [.. _subscribers];
            // An in-flight capture cannot acknowledge a later subscriber's keyframe request.
            _fullRequestVersion++;
            if (!_tickHooked)
            {
                _captureIntervalMs = MinEmitIntervalMs;
                _lastCaptureMs = 0;
                _animationBinding.Activate();
                Sts2MainThreadDispatcher.MainThreadTick += OnTick;
                _tickLease = Sts2MainThreadDispatcher.AcquireMainThreadTickLease();
                _tickHooked = true;
            }
        }

        return new Subscription(this, subscriber);
    }

    public void Dispose()
    {
        IDisposable? tickLease;
        lock (_subscriberGate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            foreach (var subscriber in _subscribers) subscriber.Deactivate();
            _subscribers.Clear();
            _subscriberSnapshot = [];
            _subscriberGeneration++;
            if (_tickHooked)
            {
                Sts2MainThreadDispatcher.MainThreadTick -= OnTick;
                _tickHooked = false;
            }
            tickLease = _tickLease;
            _tickLease = null;
        }

        // Disable callbacks immediately; release Godot subscriptions on their owning thread.
        _animationBinding.Dispose();
        tickLease?.Dispose();
        Sts2MainThreadDispatcher.Invoke(() =>
        {
            SuspendOnMainThread();
            _animations.ShuffleFlightPublisher = null;
            _animations.DiscardFlightPublisher = null;
            return true;
        });
    }

    private void Unsubscribe(SubscriberEntry subscriber)
    {
        IDisposable? tickLease = null;
        long dormantGeneration = 0;
        lock (_subscriberGate)
        {
            if (_subscribers.Remove(subscriber))
            {
                subscriber.Deactivate();
                _subscriberSnapshot = [.. _subscribers];
            }
            if (_subscribers.Count == 0 && _tickHooked)
            {
                _subscriberGeneration++;
                dormantGeneration = _subscriberGeneration;
                _animationBinding.Deactivate();
                Sts2MainThreadDispatcher.MainThreadTick -= OnTick;
                _tickHooked = false;
                tickLease = _tickLease;
                _tickLease = null;
            }
        }

        tickLease?.Dispose();
        if (dormantGeneration != 0)
        {
            Sts2MainThreadDispatcher.Invoke(() =>
            {
                lock (_subscriberGate)
                {
                    // A queued teardown must not dismantle a newly resubscribed watcher.
                    if (_subscribers.Count == 0 && _subscriberGeneration == dormantGeneration)
                        SuspendOnMainThread();
                }
                return true;
            });
        }
    }

    internal readonly record struct CaptureAdmission(
        long Generation,
        long FullRequestVersion,
        bool NeedsFull,
        SubscriberEntry[] Subscribers);

    internal CaptureAdmission? AdmitCapture()
    {
        lock (_subscriberGate)
        {
            if (_disposed != 0 || _subscriberSnapshot.Length == 0) return null;
            var version = _fullRequestVersion;
            return new CaptureAdmission(
                _subscriberGeneration, version,
                version != _acceptedFullRequestVersion,
                _subscriberSnapshot);
        }
    }

    internal bool TryAcceptCapture(CaptureAdmission admission, RuntimeSceneDelta? delta)
    {
        lock (_subscriberGate)
        {
            if (_disposed != 0 || _subscriberGeneration != admission.Generation || _subscriberSnapshot.Length == 0)
                return false;
            // Only an accepted full capture acknowledges requests present at its admission.
            if (delta is { Full: true })
                _acceptedFullRequestVersion = Math.Max(_acceptedFullRequestVersion, admission.FullRequestVersion);
            return true;
        }
    }

    // Runs on the GAME MAIN THREAD (NotifyMainThreadTick fires from the dispatcher pump). Keep it cheap.
    private void OnTick()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try
        {
            var now = System.Environment.TickCount64;
            if (now - _lastCaptureMs < _captureIntervalMs)
            {
                return;
            }
            var admission = AdmitCapture();
            if (admission is null) return;
            _lastCaptureMs = now;

            EnsureSignalsHooked();

            RuntimeSceneDelta? delta;
            if (_instrumentation.ProducerProfilingEnabled)
            {
                _captureStopwatch.Restart();
                delta = Capture(admission.Value.NeedsFull);
                _captureStopwatch.Stop();
                RecordCaptureProfile(now, _captureStopwatch.Elapsed.TotalMilliseconds, delta is not null);
            }
            else
            {
                delta = Capture(admission.Value.NeedsFull);
            }

            if (!TryAcceptCapture(admission.Value, delta)) return;

            if (delta is null)
            {
                // Nothing to send — back the walk off geometrically (bounded) so an idle client stops burning CPU.
                _captureIntervalMs = Math.Min(_captureIntervalMs * 2, MaxIdleIntervalMs);
                return;
            }

            _captureIntervalMs = MinEmitIntervalMs; // activity → capture at the full active rate
            _ = Dispatch(delta, admission.Value);
        }
        catch
        {
            // A capture failure must never take down the game tick; the next tick retries.
            _structureDirty = true;
        }
    }

    private void SuspendOnMainThread()
    {
        if (_signalsHooked && _tree is not null && GodotObject.IsInstanceValid(_tree))
        {
            _tree.NodeAdded -= OnNodeStructureChanged;
            _tree.NodeRemoved -= OnNodeStructureChanged;
            _tree.TreeChanged -= OnTreeChanged;
        }

        _signalsHooked = false;
        _tree = null;
        _rootId = 0;
        _structureDirty = true;
        _needsFull = true;
        _lastCaptureMs = 0;
        _captureIntervalMs = MinEmitIntervalMs;
        _animations.Reset();
        _registry.Clear();
        _ordered.Clear();
        _prefixChains.Clear();
        _emittedGlobalByDepth.Clear();
    }

    // Accumulate per-capture timing and log one summary per ~second. producer_busy% is the fraction of
    // wall-clock the main thread spent inside Capture() — i.e. the producer's share of one core, which lets us
    // split a headless client's total cpu% into producer vs base-game.
    //
    // The two HUMAN lines are unchanged (same counters, same text). The third line is the same window rendered as
    // machine-readable JSON, when bridge instrumentation is installed.
    private void RecordCaptureProfile(long nowMs, double captureMs, bool emitted)
    {
        _profile.RecordCapture(nowMs, captureMs, emitted);
        _profile.CompleteWindow(nowMs, ProfileWindowMs, _ordered.Count, _prefixChains.Count);
    }

    // Attribute this node's read+diff time (profiler only) to a coarse category, so the walk-cost split is visible.
    private void RecordNodeProfile(Tracked tracked, VolatileRead read, Node node, long ticks)
        => _profile.RecordNode((int)Classify(tracked, read, node), ticks);

    private static NodeCategory Classify(Tracked tracked, VolatileRead read, Node node)
    {
        if (tracked.IsSpineSkeletonLeafType)
        {
            return NodeCategory.SpineSkeleton;
        }
        if (tracked.Style.Spine is not null)
        {
            return NodeCategory.SpineRoot;
        }
        if (read.Text is not null)
        {
            return NodeCategory.Text;
        }
        if (tracked.Style.ParticleSpec is not null)
        {
            return NodeCategory.Particle;
        }
        if (read.Texture is not null)
        {
            return NodeCategory.Sprite;
        }
        return node is Control ? NodeCategory.Control : NodeCategory.Other;
    }

    private void EnsureSignalsHooked()
    {
        if (_signalsHooked)
        {
            return;
        }

        _tree = Engine.GetMainLoop() as SceneTree;
        if (_tree is null)
        {
            return;
        }

        _tree.NodeAdded += OnNodeStructureChanged;
        _tree.NodeRemoved += OnNodeStructureChanged;
        _tree.TreeChanged += OnTreeChanged;
        _signalsHooked = true;
        _structureDirty = true;
    }

    // A structural change wakes the adaptive gate immediately (reset to the active cap) so the first frame after
    // an idle period captures at full rate rather than waiting out a backed-off interval.
    private void OnNodeStructureChanged(Node _)
    {
        _structureDirty = true;
        _captureIntervalMs = MinEmitIntervalMs;
    }

    private void OnTreeChanged()
    {
        _structureDirty = true;
        _captureIntervalMs = MinEmitIntervalMs;
    }

    private RuntimeSceneDelta? Capture(bool subscriberFullRequested)
    {
        var root = ResolveRoot();
        if (root is null || !GodotObject.IsInstanceValid(root))
        {
            return null;
        }

        // Latch the transform-space mode ONCE for this whole capture so the per-node walk, the delta stamp, and any
        // tween endpoint resolved on this thread stay internally consistent even if an embedder flips the knob.
        var localMode = Sts2SceneWatchRuntimeSettings.EmitLocalTransforms;
        var rootId = root.GetInstanceId();
        var full = _needsFull || subscriberFullRequested;
        if (rootId != _rootId)
        {
            // The watched scene root changed (run start/end, screen swap): rebuild from scratch + keyframe.
            _rootId = rootId;
            _registry.Clear();
            _ordered.Clear();
            // WS-3: a whole-scene rebuild also drops any parked card-flight window — the nodes those deadlines named
            // belong to the scene that just went away, and holding them would freeze whatever reuses their ids.
            _animations.Reset();
            // R14: and any resolve still waiting for its mover to be tracked. That mover belonged to the scene that
            // just went away, so no future reconcile can satisfy it — and a node reusing its instance id must not
            // inherit someone else's flight.

            _structureDirty = true;
            full = true;
        }

        // A transform-space flip (global↔local) must re-key: the client rebuilds its retained map in the new space,
        // and every Tracked.LastTransform is rewritten with new-space values on this forced-full pass (a full keyframe
        // disables pruning/elision, so every node is read + emitted).
        if (localMode != _lastCaptureLocalMode)
        {
            full = true;
        }
        _lastCaptureLocalMode = localMode;

        _needsFull = false;

        var removedIds = new List<string>();
        var orderChanged = _structureDirty || full;
        if (_structureDirty || full)
        {
            Reconcile(root, removedIds);
            // R14: the registry has just been refilled — this is the ONE instant in the tick where a card-flight
            // resolve that missed the registry can newly succeed, so retry the parked ones here, before the emission
            // walk below reads the suppression windows a successful retry opens. Deliberately NOT inside
            // ReconcileNode: a node's Depth/ParentId/PrefixChain are still unassigned while it is being reconciled,
            // and the resolve needs its viewport prefix.
            _animations.DrainParkedFlightResolves();
            RefreshScreenMetadata();
            _structureDirty = false;
        }
        else if (Sts2SceneWatchRuntimeSettings.RefreshViewportPrefixes)
        {
            // A viewport→screen prefix embeds the LIVE transform of the node that displays the viewport texture, so
            // it is volatile — it must be re-read on the same cadence as every other transform. Reconcile already
            // recomputed them a few lines up, so only the non-reconcile path needs this.
            RefreshViewportPrefixes();
        }

        var upserts = new List<RuntimeSceneNodeDelta>();
        // R10 ORDER/UPSERT CONTRACT: set when any node emits for the FIRST time this capture (see Tracked.EverEmitted
        // and the orderedIds build below). Cheap — one bool, written at most once per node per session.
        var firstEmitThisPass = false;
        var skipDepth = int.MaxValue;
        // Streaming-suppression sentinel: while inside a tween target's subtree (a node with an active
        // SuppressTransformUntil, or anything deeper than it in this pre-order walk), the node's TRANSFORM delta is
        // suppressed — the client pins-and-discards it anyway. `suppressDepth` = the depth of the current suppression
        // root; a node deeper than it is inside its subtree.
        var now = System.Environment.TickCount64;
        var suppressDepth = int.MaxValue;
        // Frozen spine-skeleton elision context (Stage B): while inside a frozen + stationary SpineSprite root's
        // subtree, skeleton-leaf descendants are read-elided. frozenRootDepth = the active root's depth (-1 = none).
        var frozenRootDepth = -1;
        var frozenRootStationary = false;
        // FIX 4 rest-site overlay stream. A depth sentinel pinned at the NRestSiteRoom root: while inside its subtree
        // (restOverlayDepth >= 0), a Visible=false node is EXEMPT from the prune gate and — only while the rest room
        // is the ACTIVE screen — has the overlay-quirk `visible` flipped false→true (see Sts2RestOverlayStream).
        // Both flags are read once here (main thread; a one-tick stale read is harmless). restSiteActive gates the
        // visible-flip so a BACKGROUNDED rest room isn't resurrected; the exemption alone rides insideRestOverlay.
        var restOverlayEnabled = Sts2SceneWatchRuntimeSettings.RestOverlayStream;
        // R10: read alongside restOverlayEnabled (same once-per-pass discipline) — narrows the visible-flip so a
        // gamepad-prompt glyph the game hides on its own input state is never resurrected onto a touch mirror.
        var restOverlayPromptDenylist = Sts2SceneWatchRuntimeSettings.RestOverlayPromptDenylist;
        var restSiteActive = string.Equals(_screenType, Sts2SupportedScreenIds.RestSiteRoomScreenId, StringComparison.Ordinal);
        var restOverlayDepth = -1;
        // WS-2 Line2D stroke geometry (map quill annotations). Read ONCE per pass like the rest-overlay flag above
        // (main thread; a one-tick stale read is harmless). Off ⇒ no signature is computed and no geometry is ever
        // attached, so the wire is byte-identical to the pre-feature producer. WS4 scoped the DEFAULT to the map
        // strokes; `all` is the A/B lever that restores the type-only behaviour.
        var lineGeometryScope = Sts2SceneWatchRuntimeSettings.Line2DGeometryScope;
        // Enemy-intent glyph frame sets discovered THIS pass, keyed by the glyph Sprite2D's instance id. Populated
        // when an NIntent is visited (pre-order → the parent is reached before its `%Intent` child), consumed when
        // that child glyph node is emitted a few iterations later. Lazily allocated (no combat = no dictionary).
        Dictionary<ulong, RuntimeSceneIntentFramesSnapshot>? intentFramesByGlyph = null;
        // INDEXED (not foreach) so the R15 spine-anchor fold can scan a candidate anchor's SUBTREE — the entries
        // that follow it in this pre-order list while their Depth exceeds its own — without a per-node child list.
        for (var orderIndex = 0; orderIndex < _ordered.Count; orderIndex++)
        {
            var tracked = _ordered[orderIndex];
            // Left the frozen spine root's subtree (depth returned to/above it) → clear the elision context.
            if (frozenRootDepth >= 0 && tracked.Depth <= frozenRootDepth)
            {
                frozenRootDepth = -1;
                frozenRootStationary = false;
            }

            // FIX 4: maintain the rest-site overlay depth sentinel. Reset on subtree exit (depth back to/above the
            // pinned root), then (re-)pin on the NRestSiteRoom root. Root detection reads only cached strings
            // (nodeType / sceneFilePath) — no Godot call, no Visible read — so it is safe before the skip/prune gates.
            // Pinning at the room root makes the WHOLE ChoicesScreen subtree (+ ProceedButton + future panels) stream.
            if (restOverlayDepth >= 0 && tracked.Depth <= restOverlayDepth)
            {
                restOverlayDepth = -1;
            }
            if (restOverlayEnabled && restOverlayDepth < 0
                && Sts2RestOverlayStream.IsRestSiteRoomRoot(tracked.NodeType, tracked.Style.SceneFilePath))
            {
                restOverlayDepth = tracked.Depth;
            }
            var insideRestOverlay = restOverlayDepth >= 0;

            if (tracked.Depth > skipDepth)
            {
                continue; // inside a hidden subtree — skip reading entirely
            }

            skipDepth = int.MaxValue;

            // Frozen spine-skeleton read-elision (incremental ticks only): a frozen, stationary SpineSprite root's
            // skeleton leaves have unchanged globals (frozen locals × unchanged root global) → ApplyIfChanged would
            // return false → no upsert. Skipping the read+diff produces the identical (empty) result. Scoped to
            // skeleton-leaf CLASSES (bones/slots/meshes) — the SpineSprite root and bone-attached non-spine VFX
            // children are separate entries and stay fully read, so nothing hook-driven or rendered is dropped.
            if (ElideFrozenSpine && !full && frozenRootDepth >= 0 && frozenRootStationary && tracked.IsSpineSkeletonLeafType)
            {
                if (_instrumentation.ProducerProfilingEnabled)
                {
                    _profile.RecordSkelElided();
                }
                // Local mode still needs this frozen leaf's GLOBAL in the depth scratch so any emitted (non-skeleton,
                // e.g. bone-attached VFX) descendant re-bases against the right parent. The leaf is stationary, so
                // this is just the cheap transform read — the expensive ReadVolatile (texture/text/diff) stays elided.
                if (localMode && GodotObject.IsInstanceValid(tracked.Node) && tracked.Node is CanvasItem leafCanvas)
                {
                    StoreEmittedGlobal(tracked.Depth, tracked.ViewportPrefix * leafCanvas.GetGlobalTransformWithCanvas());
                }
                continue;
            }

            var node = tracked.Node;
            if (!GodotObject.IsInstanceValid(node))
            {
                _structureDirty = true; // a freed node slipped through — reconcile next tick
                continue;
            }

            // #8 late-static re-probe: a spine root whose skeleton was assigned after add reports 0 animations
            // forever, so its clip never fetches (the treasure chest). Re-run InspectStatic for a bounded number
            // of ticks; the first re-probe that finds animations swaps in the richer snapshot BEFORE ReadVolatile
            // below reads it (so the new anim streams THIS tick) and forces a keyframe-style upsert so the client
            // learns the (now non-empty) Spine snapshot. Cheap: only touched for a spine root that came up empty.
            var spineReprobeUpgraded = MaybeReprobeSpineAnimations(tracked, node);

            // Enemy intent (frozen on a headless host, so the per-frame glyph texture swap never advances): resolve
            // its glyph Sprite2D's current animation frame set and stash it by the glyph's id, to be emitted on the
            // glyph node when that child is visited below. Cheap dictionary Get on _animationName + the already-loaded
            // frame list; only touched for NIntent nodes (one shallow reflection read per enemy).
            if (Sts2IntentFramesInspector.IsIntentNode(node)
                && Sts2IntentFramesInspector.TryRead(node, out var glyphId, out var glyphFrames)
                && glyphFrames is not null)
            {
                (intentFramesByGlyph ??= new Dictionary<ulong, RuntimeSceneIntentFramesSnapshot>())[glyphId] = glyphFrames;
            }

            // A suppression window that just elapsed forces a ONE-SHOT settle re-emit even if the tween's endpoint
            // equals the stale retained Last* (e.g. a reveal fade whose resting alpha is 1 == its endpoint 1): the
            // per-frame deltas were withheld while pinned, so without this the client keeps whatever transient value it
            // last received (the visibility-flip emit ships the mid-tween alpha ≈0) and the node stays wrongly faded.
            bool forceTransformResync;
            bool forceOpacityResync;

            bool suppressTransform;
            if (tracked.Depth > suppressDepth)
            {
                suppressTransform = true; // inside a suppressed subtree
                forceTransformResync = false; // only the suppression root (below) owns the window/settle re-emit
            }
            else
            {
                suppressDepth = int.MaxValue;
                suppressTransform = tracked.SuppressTransformUntil > now;
                if (suppressTransform)
                {
                    suppressDepth = tracked.Depth; // this node is a suppression root → its subtree follows
                }
                // Window open→closed on THIS node this tick: force one settled emit, then clear the deadline.
                forceTransformResync = WindowClosed(tracked.SuppressTransformUntil, now);
                if (forceTransformResync)
                {
                    tracked.SuppressTransformUntil = 0;
                }
            }

            // R13 SELF-ONLY transform suppression, checked for EVERY node (a self window is armed independently of
            // the subtree one above, and unlike it never opens the depth sentinel — descendants keep streaming).
            // Its settle re-emit is ORed in with the subtree window's for the same reason that one exists: the
            // per-frame deltas were withheld, so the one tick after the window closes must ship the live transform
            // even when it happens to equal the stale retained value.
            if (tracked.SuppressTransformSelfUntil > now)
            {
                suppressTransform = true;
            }
            else if (WindowClosed(tracked.SuppressTransformSelfUntil, now))
            {
                tracked.SuppressTransformSelfUntil = 0;
                forceTransformResync = true;
            }

            // Opacity suppression is TARGET-ONLY (no depth propagation): opacity is emitted per-node-local, so a
            // fade changes only the target's own modulate/self_modulate → only the target ever needs suppressing.
            var suppressOpacity = tracked.SuppressOpacityUntil > now;
            forceOpacityResync = WindowClosed(tracked.SuppressOpacityUntil, now);
            if (forceOpacityResync)
            {
                tracked.SuppressOpacityUntil = 0; // window just elapsed → settle re-emit this tick, then off
            }

            // Capture the prior transform BEFORE ApplyIfChanged overwrites LastTransform — a SpineSprite root uses it
            // to decide whether it is stationary this tick (gates skeleton elision for its subtree, below).
            var priorTransform = tracked.LastTransform;
            // Likewise capture the prior local rect: a node whose renderable box first APPEARS (a Sprite2D whose
            // texture just finished loading lazily — ReadLocalRect returns null until then) must re-ship a WHOLESALE
            // keyframe-style upsert, not a volatile merge. The client's element-creation gate needs a box; a boxless
            // node was cached element-less, and a Name=null volatile merge never re-creates it (see includeStatic).
            var priorLocalRect = tracked.LastLocalRect;
            // SPIRECTL_SPINE_DEBUG only: capture a spine root's prior visibility/opacity so a flip (the signal a
            // client needs to hide an exploded/dead creature) is attributable to the producer — ApplyIfChanged
            // overwrites LastVisible/LastOpacity below.
            var priorSpineVisible = tracked.LastVisible;
            var priorSpineOpacity = tracked.LastOpacity;

            // LOCAL mode: this node's Transform is emitted parent-relative, re-based against its nearest EMITTED
            // ancestor's streamed global (the depth scratch; identity for a root). Pre-order DFS guarantees the
            // parent's slot was filled before this child reads it. Global mode passes identity + localMode=false, so
            // ReadVolatile emits today's global byte-for-byte.
            var emittedParentGlobal = localMode && tracked.Depth > 0 && tracked.Depth - 1 < _emittedGlobalByDepth.Count
                ? _emittedGlobalByDepth[tracked.Depth - 1]
                : Transform2D.Identity;

            var profStart = _instrumentation.ProducerProfilingEnabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
            var read = ReadVolatile(node, tracked.Style.ShaderParameters, tracked.Style.Spine, tracked.FocusCapable, tracked.ViewportPrefix, localMode, emittedParentGlobal, out var streamedGlobal);
            // Record this node's streamed GLOBAL (pre-rebase) so its children re-base against it. Every read node
            // fills its slot regardless of whether it emits a delta — a child needs the parent global either way.
            if (localMode)
            {
                StoreEmittedGlobal(tracked.Depth, streamedGlobal);
            }
            // FIX 4: neutralize the rest-site overlay quirk's false `visible` (flip false→true) for a node inside the
            // ACTIVE rest-site overlay — right after ReadVolatile and BEFORE ApplyIfChanged so diff/emit/LastVisible
            // stay consistent and the prune gate below (which reads read.Visible) sees the same value. The TRUE
            // modulate.a is streamed untouched (an unfocused description still fades transparent); only the structural
            // `visible` flag is flipped, only false→true, only inside the active overlay. No-op (byte-identical)
            // outside the rest overlay or with the kill-switch off. Guarded so combat/map/shop/event never call in.
            if (insideRestOverlay)
            {
                // R10: the flip skips gamepad-prompt glyphs (%ControllerIcon …) — the game hides those on its own
                // input state, so flipping them showed a "press Y" prompt on a mouse/touch mirror. `tracked.Name` is
                // the cached static name (no Godot call). Kill switch REST_OVERLAY_PROMPT_DENYLIST.
                var overlayVisible = Sts2RestOverlayStream.EmitVisible(
                    restOverlayEnabled,
                    insideRestOverlay,
                    restSiteActive,
                    read.Visible,
                    restOverlayPromptDenylist,
                    tracked.Name);
                if (overlayVisible != read.Visible)
                {
                    read = read with { Visible = overlayVisible };
                }
            }
            // R15: remember when this emitter was last actually emitting, so a spine anchor above it can tell a
            // burst that ENDED THIS INSTANT (whose particles are still on screen and still following the emitter)
            // from one that finished long ago. One assignment for particle nodes, nothing for anything else.
            // Pre-order puts an anchor BEFORE its emitters, so an anchor reads a timestamp one capture old — which
            // only ever makes the burst tail one tick longer, and never delays the START edge (that gate reads
            // `Emitting` live off the node).
            if (read.ParticleEmitting)
            {
                tracked.LastEmittingAtMs = now;
            }

            // ---- R15 CREATURE SPINE-ANCHOR FOLD ---------------------------------------------------------------
            // Withhold the transform of a spine skeleton anchor that provably cannot move a pixel right now: it
            // paints nothing itself (the client's `placementBox` returns null for it) and it either has no
            // descendants at all (arm A — structural, corpus-wide) or its whole subtree is idle particle emitters
            // named by Sts2SpineAnchorFold's scene table (arm B). Suppression — not a pin — so the SUBTREE follows
            // through the depth sentinel above, which is what also stops the emitter children re-sending the local
            // that wobbles in its 4th decimal as the anchor moves.
            //
            // Placed AFTER ReadVolatile because the "carries no browser-visible field of its own" half of the rule
            // is a fact about THIS tick's read, and BEFORE the caps/folds below so a folded anchor is never also
            // paced. Setting `suppressDepth` here still covers the subtree: the sentinel is only consulted on the
            // NEXT iteration, which is this node's first child.
            //
            // Not gated on localMode, unlike the R12-R14 transform folds: those substitute a rest pose the client
            // must compose a replay against (only meaningful for parent-relative locals), whereas this one just
            // stops sending — in global mode the subtree simply keeps its last-known globals, which is equally
            // correct. Never applied on a node's first appearance (JustAdded), whose upsert carries the static
            // block the client builds its element from.
            //
            // The facts are gathered cheapest-first and SHORT-CIRCUITED: the (bounded) subtree scan runs only for a
            // paintless skeleton leaf that both has descendants and is named by the table, i.e. a handful of nodes
            // per creature — never for the hundreds of skeleton meshes a SpineSprite root owns.
            if (DecorEmitSuppress && SpineAnchorFold && !suppressTransform && !tracked.JustAdded
                && tracked.IsSpineSkeletonLeafType && !CarriesOwnPaint(read))
            {
                var hasDescendants = HasTrackedDescendants(orderIndex, tracked);
                var tabledAnchor = (tracked.DecorChannels & Sts2DecorEmitSuppress.Channels.SpineAnchorTransform) != 0;
                var subtreeQuiet = hasDescendants && tabledAnchor && SpineAnchorSubtreeQuiet(orderIndex, tracked, now);
                if (Sts2SpineAnchorFold.ShouldSuppressTransform(
                        skeletonLeaf: true,
                        carriesOwnPaint: false,
                        hasDescendants: hasDescendants,
                        tabledEmitterAnchor: tabledAnchor,
                        subtreeQuiet: subtreeQuiet))
                {
                    suppressTransform = true;
                    suppressDepth = tracked.Depth; // this anchor is a suppression root → its subtree follows
                }
            }

            if (_instrumentation.ProducerProfilingEnabled && suppressTransform && !XformEq(tracked.LastTransform, read.Transform))
            {
                _profile.RecordSuppressDrop(); // a transform change withheld — mid-replay, or a folded spine anchor (profiler only)
            }
            if (_instrumentation.ProducerProfilingEnabled && suppressOpacity && OpacityChannelChanged(tracked, read, tracked.SuppressOpacityIsSelf))
            {
                _profile.RecordSuppressOpacityDrop(); // an opacity change withheld because this node is mid-fade (profiler only)
            }
            // R11 region-emit cap: a pure same-size atlas-frame swap (a Sprite2D flip-book flame) is paced to one emit
            // per RegionEmitCapMs per node. PIN → substitute the previously-emitted region so it's invisible to the
            // change signature (BuildNodeDelta reads the same substituted `read`, so the wire and tracked.LastTexture-
            // Region stay consistent — no phantom re-emit). EMIT → the window elapsed, let this frame through and
            // restart the window. PASS → not a pure frame swap (size change / null transition / cap off) → untouched.
            switch (Sts2RegionEmitCap.Decide(tracked.LastTextureRegion, read.TextureRegion, tracked.LastRegionEmitAtMs, now, RegionEmitCapMs))
            {
                case Sts2RegionEmitCap.Decision.Pin:
                    read = read with { TextureRegion = tracked.LastTextureRegion };
                    break;
                case Sts2RegionEmitCap.Decision.Emit:
                    tracked.LastRegionEmitAtMs = now;
                    break;
            }

            // R11 cosmetic-emit cap: pace SUSTAINED per-frame transform churn (the fire-VFX flicker) to one emit per
            // CosmeticEmitCapMs per node. Runs on the REAL transform (not the tween-suppressed path — a client-pinned
            // tween already owns its subtree). PIN → substitute the last-emitted transform so the swap is invisible to
            // the change signature AND re-base children against the pinned (client-shown) global so an emitting child
            // stays placed. EMIT → the window elapsed, let the frame through and restart the window. PASS → churn broke
            // / not yet sustained / cap off → untouched (a one-shot move streams instantly; a churn-exit settles exact).
            // PrevRealTransform is ALWAYS advanced to the real read so next tick's churn test is honest even while pinned.
            if (!suppressTransform && read.Transform is not null)
            {
                var cosmetic = Sts2CosmeticEmitCap.Decide(
                    tracked.PrevRealTransform, read.Transform, tracked.CosmeticChurnStreak,
                    tracked.LastCosmeticEmitAtMs, now, CosmeticEmitCapMs, CosmeticChurnTicks, out var churnStreak);
                tracked.CosmeticChurnStreak = churnStreak;
                tracked.PrevRealTransform = read.Transform;
                switch (cosmetic)
                {
                    case Sts2CosmeticEmitCap.Decision.Pin:
                        read = read with { Transform = tracked.LastTransform };
                        if (localMode && tracked.LastTransform is not null)
                        {
                            // Store the global the CLIENT currently shows for this node (its emitted parent global, itself
                            // possibly pinned, composed with its last-emitted local) so descendants re-base against what's
                            // on screen, not the withheld real motion.
                            StoreEmittedGlobal(tracked.Depth, emittedParentGlobal * ToTransform2D(tracked.LastTransform));
                        }
                        break;
                    case Sts2CosmeticEmitCap.Decision.Emit:
                        tracked.LastCosmeticEmitAtMs = now;
                        break;
                }
            }
            else
            {
                tracked.CosmeticChurnStreak = 0;
                tracked.PrevRealTransform = read.Transform;
            }

            // R13 top-bar icon fold: while a top-bar button's screen is OPEN, divide its icon's ROTATION back out of
            // the streamed transform (position + scale keep streaming) and name the loop on the wire so the client
            // replays it. See Sts2TopBarFold. Runs AFTER the caps above so the folded node's transform is the pinned
            // rest pose, not a paced one.
            //
            // Note what is NOT here any more: WS-D pinned the icon's WHOLE first-emitted transform, which froze the
            // deck + settings icons at a PRE-LAYOUT pose (deck is center-anchored, settings is GROW_BOTH at its
            // min-size clamp) and rendered them misplaced for the rest of the run. Rotation is the only channel the
            // animators write, so folding only rotation is both sufficient and structurally incapable of that bug.
            //
            // SCREEN CLOSED → no pin, no token: the hover / press-down / unhover one-shots are then the only rotation
            // writers and they stream live, so the icons still tilt under the pointer for free.
            //
            // LOCAL mode only, same reason as the map-point fold below: the client nests this node's children INSIDE
            // its element, so the CSS rotation it re-applies cascades to them — which reproduces the game exactly
            // ONLY when they stream as parent-relative locals. In global mode the fold is a no-op (byte-identical to
            // today, i.e. the live rotation simply streams).
            if (DecorEmitSuppress && TopBarFold && localMode
                && (tracked.DecorChannels & Sts2DecorEmitSuppress.Channels.TopBarRotation) != 0
                && ReadTopBarScreenOpen(ResolveDecorScopeRoot(tracked), tracked.DecorSceneFile))
            {
                // NOTE (deliberate, load-bearing — the same reasoning as the map-point fold below): this pin does NOT
                // re-base descendants against the pinned value. The icon's children have CONSTANT locals (the loop is
                // the ICON's own rotation), so leaving the REAL streamed global in the depth scratch keeps them
                // constant → they never emit either. The client then composes pinned-icon × constant-child = the
                // group at REST, and its CSS loop rotates the icon about the SAME pivot the game does, so the
                // composed result is the game's transform exactly. Re-basing against the pinned value would instead
                // make every child's local churn with the rotation — the opposite of the fold's purpose.
                if (node is Control topBarIcon)
                {
                    var pivot = topBarIcon.PivotOffset;
                    read = read with
                    {
                        Transform = Sts2TopBarFold.PinRestRotation(read.Transform, topBarIcon.Rotation, pivot.X, pivot.Y),
                        PinnedLoopAnim = Sts2TopBarFold.LoopAnimFor(tracked.DecorSceneFile),
                    };
                }
            }

            // R13 proceed-glow fold: while the infinite glow tween runs, substitute the loop's ANALYTIC alpha into
            // self_modulate (RGB keeps streaming, so a real colour/state change still ships) and name the loop. Every
            // non-loop state — the focus flash to alpha 1, the fade-to-0 stop, the disabled hold — fails the gate and
            // streams live at its true value, which matters because alpha 0 is MEANINGFUL here. See Sts2ProceedGlow.
            // No local/global gate: alpha is a per-node-local channel, so nothing composes down the parent chain.
            if (DecorEmitSuppress && ProceedFold
                && (tracked.DecorChannels & Sts2DecorEmitSuppress.Channels.ProceedGlowAlpha) != 0
                && ReadProceedGlowLooping(ResolveDecorScopeRoot(tracked)))
            {
                read = read with
                {
                    SelfModulate = Sts2ProceedGlow.PinAlpha(read.SelfModulate),
                    PinnedLoopAnim = Sts2ProceedGlow.LoopAnimName,
                };
            }

            // R12b map-point pulse fold: divide the travelable-node icon pulse's UNIFORM scale back out of this
            // node's transform (rotation/placement keep streaming — the per-point tilt is a one-off random value)
            // and name the loop on the wire so the client replays it. See Sts2MapPointPulse.
            //
            // LOCAL mode only: the client nests this node's icons INSIDE its element, so the CSS pulse it re-applies
            // to the container cascades to them — which reproduces the game exactly ONLY when they stream as
            // parent-relative locals. In global mode each icon streams its own (still pulsing) absolute transform and
            // the client would double-apply, so the fold is a no-op there (byte-identical to today).
            if (MapPointFold && localMode
                && (tracked.DecorChannels & Sts2DecorEmitSuppress.Channels.MapPointPulseScale) != 0)
            {
                if (node is Control pulseControl)
                {
                    // NOTE (deliberate, load-bearing): unlike the emit CAPS above — but exactly like the two R13 folds
                    // — this pin does NOT re-base descendants against the pinned value (a cap's pinned value is what
                    // the client SHOWS; a fold's is what the client's replay multiplies back up). The icons' own
                    // locals are constant (the pulse is the
                    // CONTAINER's scale), so leaving the REAL streamed global in the depth scratch keeps them
                    // constant → they never emit either. The client then composes pinned-container × constant-icon =
                    // the whole group at REST, and its CSS pulse multiplies the container by the SAME
                    // pivot-anchored scale the game applies, so the composed result is the game's transform exactly.
                    var scale = pulseControl.Scale;
                    var pivot = pulseControl.PivotOffset;
                    read = read with
                    {
                        Transform = Sts2MapPointPulse.PinRestScale(read.Transform, scale.X, pivot.X, pivot.Y),
                    };
                }

                read = read with
                {
                    PinnedLoopAnim = ReadMapPointPulsing(ResolveDecorScopeRoot(tracked)) ? Sts2MapPointPulse.LoopAnimName : null,
                };
            }

            // The enemy-intent glyph frame set stashed when this glyph's parent NIntent was visited earlier this
            // pass (pre-order DFS guarantees the parent came first). Hoisted ABOVE the R14 folds because the
            // flip-book fold substitutes FRAME 0 of this very set; the emit block below reuses the same lookup.
            RuntimeSceneIntentFramesSnapshot? stashedIntentFrames = null;
            intentFramesByGlyph?.TryGetValue(tracked.Id, out stashedIntentFrames);

            // ---- R14 idle-wire combat folds -------------------------------------------------------------------
            // Four always-mounted combat animators whose per-frame churn alone keeps a VISUALLY IDLE combat wire
            // awake. Each divides/substitutes ONE analytic channel; everything else about these nodes streams
            // untouched. All four run AFTER the emit caps above, so a folded node's transform is the pinned rest
            // pose rather than a paced one, and all three transform folds are LOCAL-mode only (the house rule —
            // the client's replays compose against parent-relative locals; in global mode they are byte-identical
            // no-ops). See Sts2IntentBobFold / Sts2IntentGlyphFold / Sts2OrbSpinFold / Sts2EndTurnGlowFold.

            // Intent BOB: subtract the bob's per-frame sine position out of the holder's local transform. No gate
            // (the bob has no meaningful off state) and no descendant re-base — the holder's children have
            // CONSTANT locals, so leaving the REAL streamed global in the depth scratch keeps them constant and
            // they never emit either; the client composes pinned-holder × constant-child under its CSS translate,
            // which is the game's transform exactly. Same reasoning as the top-bar and map-point folds.
            if (DecorEmitSuppress && IntentBobFold && localMode
                && (tracked.DecorChannels & Sts2DecorEmitSuppress.Channels.IntentBobPosition) != 0
                && node is Control intentHolder)
            {
                var holderPos = intentHolder.Position;
                read = read with
                {
                    Transform = Sts2IntentBobFold.PinRestPosition(read.Transform, holderPos.X, holderPos.Y),
                };
            }

            // Intent GLYPH flip-book: substitute frame 0 of the frame set already being streamed on this node. A
            // glyph with no resolved set streams its live texture untouched.
            if (DecorEmitSuppress && IntentGlyphFold && stashedIntentFrames is not null
                && (tracked.DecorChannels & Sts2DecorEmitSuppress.Channels.IntentGlyphTexture) != 0)
            {
                var glyphTexture = read.Texture;
                var glyphRegion = read.TextureRegion;
                var glyphMargin = read.TextureMargin;
                Sts2IntentGlyphFold.PinFrameZero(stashedIntentFrames, ref glyphTexture, ref glyphRegion, ref glyphMargin);
                if (!ReferenceEquals(glyphTexture, read.Texture)
                    || !ReferenceEquals(glyphRegion, read.TextureRegion)
                    || !ReferenceEquals(glyphMargin, read.TextureMargin))
                {
                    read = read with { Texture = glyphTexture, TextureRegion = glyphRegion, TextureMargin = glyphMargin };
                }
            }

            // Energy/star ORB SPIN: divide the layer's accumulated rotation back out to the authored 0. Leaves are
            // childless, so nothing composes down; position + scale keep streaming.
            if (DecorEmitSuppress && OrbSpinFold && localMode
                && (tracked.DecorChannels & Sts2DecorEmitSuppress.Channels.OrbSpinRotation) != 0
                && node is Control orbLayer)
            {
                var orbPivot = orbLayer.PivotOffset;
                read = read with
                {
                    Transform = Sts2OrbSpinFold.PinRestRotation(read.Transform, orbLayer.Rotation, orbPivot.X, orbPivot.Y),
                };
            }

            // END-TURN GLOW: while the infinite pulse loop runs (gate = `NEndTurnButton._isShiny`, exact — see the
            // fold), pin BOTH channels the loop writes to the values every cycle starts and restarts at, and name
            // the loop. The alpha pin has to cover `Opacity` too: the watcher reads a node's opacity as `modulate.A`,
            // so pinning modulate alone would leave the sweep churning through the opacity channel. Everything that
            // is NOT the loop — the 0.5s Expo/Out fade-to-0 that ends a pulse, the authored resting alpha 0 —
            // fails the gate and streams live, which matters because alpha 0 is how the glow goes away.
            if (DecorEmitSuppress && EndTurnGlowFold
                && (tracked.DecorChannels & Sts2DecorEmitSuppress.Channels.EndTurnGlowPulse) != 0
                && ReadEndTurnGlowLooping(ResolveDecorScopeRoot(tracked)))
            {
                if (localMode && node is Control glowVfx)
                {
                    var glowScale = glowVfx.Scale;
                    var glowPivot = glowVfx.PivotOffset;
                    read = read with
                    {
                        Transform = Sts2EndTurnGlowFold.PinRestScale(read.Transform, glowScale.X, glowPivot.X, glowPivot.Y),
                    };
                }

                read = read with
                {
                    Modulate = Sts2EndTurnGlowFold.PinAlpha(read.Modulate),
                    Opacity = Sts2EndTurnGlowFold.PinOpacity(read.Opacity),
                    PinnedLoopAnim = Sts2EndTurnGlowFold.LoopAnimName,
                };
            }

            // Always update last-volatile (so a full keyframe doesn't mark every node changed next tick);
            // emit on a keyframe OR when this node actually changed.
            var changedNow = ApplyIfChanged(tracked, read, suppressTransform, suppressOpacity, tracked.SuppressOpacityIsSelf, forceTransformResync, forceOpacityResync);
            if (Sts2SpineDiagnostics.Current.Enabled && tracked.Style.Spine is not null
                && (priorSpineVisible != read.Visible || !NearlyEqual(priorSpineOpacity, read.Opacity)))
            {
                Sts2SpineDiagnostics.Current.Log(
                    $"spine flip node={tracked.Id} name={tracked.Name} "
                    + $"visible {priorSpineVisible}→{read.Visible} opacity {priorSpineOpacity:0.###}→{read.Opacity:0.###} "
                    + $"emit={(full || changedNow)}");
            }
            if (_instrumentation.ProducerProfilingEnabled)
            {
                RecordNodeProfile(tracked, read, node, System.Diagnostics.Stopwatch.GetTimestamp() - profStart);
            }
            // The stashed frame set (resolved above, before the R14 folds) drives its own emit trigger: the
            // animation changes on the CombatStateChanged signal even while the node is frozen (so the frozen
            // glyph's own volatile read may not change), and the frame set re-ships ONLY on keyframe/add or that
            // change (carried forward by MergeVolatile otherwise).
            var intentAnimChanged = stashedIntentFrames is not null
                && !string.Equals(tracked.LastIntentAnim, stashedIntentFrames.AnimationName, StringComparison.Ordinal);

            // WS-2 Line2D stroke geometry (the map quill annotations), on exactly the intentFrames side-channel
            // pattern above. A stroke grows by `AddPoint` under `…/MapDrawing/DrawViewport`; NOTHING the volatile
            // diff reads changes when it does (a Line2D has no texture, no text, and its transform is fixed), so
            // like an intent-animation change this needs its own emit trigger.
            //
            // Per tick we compute ONLY the cheap signature — point COUNT + last point + width + colour — never the
            // point array itself (see Sts2Line2DGeometryEmit for why that distinction is the whole design). Gated on
            // the two once-computed structural bools (native Line2D + map-stroke identity), so every other node —
            // including a card trail's two Line2Ds, which must NOT stream geometry — pays a single bool test.
            var lineSig = Sts2Line2DGeometryEmit.ShouldStream(lineGeometryScope, tracked.IsLine2DType, tracked.IsMapStrokeLine2D)
                ? ReadLineSignature(node)
                : null;
            var lineSigChanged = lineSig is not null
                && !string.Equals(tracked.LastLineSig, lineSig, StringComparison.Ordinal);

            // REPARENT emit: ApplyIfChanged only compares volatile PROPERTIES, so a node moved to a new parent whose
            // emitted local transform is unchanged (a deselecting hand card: SelectedHandCardHolder → fresh
            // NHandCardHolder, local (0,0) under both) produced no upsert. The client kept the stale parent, which then
            // got removed, orphaning the card to the design origin. Force an emit when ParentIdStr (updated by
            // Reconcile this pass) differs from what the client last heard, so it re-parents. Kill-switchable.
            var parentChanged = Sts2SceneWatchRuntimeSettings.EmitReparents
                && !string.Equals(tracked.ParentIdStr, tracked.LastEmittedParentId, StringComparison.Ordinal);

            if (full || changedNow || intentAnimChanged || lineSigChanged || spineReprobeUpgraded || parentChanged)
            {
                // Force a wholesale keyframe-style upsert when the node's renderable box first appears (localRect
                // null→present): the client cached it element-less, and only a Name-bearing wholesale upsert
                // re-creates the element (a volatile merge keeps it boxless forever). Matches what a browser reload's
                // full keyframe does, scoped to the one node — the fix for the first-select-invisible targeting arrow.
                // A #8 spine re-probe upgrade likewise needs the static block so the client gets the now-populated
                // Spine snapshot (scene/node/skel + animation list) it previously cached empty. A REPARENT likewise
                // ships the static block so the client fully re-attaches the node under its new parent.
                var includeStatic = full || tracked.JustAdded || spineReprobeUpgraded || parentChanged || (priorLocalRect is null && read.LocalRect is not null);
                RuntimeSceneIntentFramesSnapshot? intentFrames = null;
                if (stashedIntentFrames is not null && (includeStatic || intentAnimChanged))
                {
                    intentFrames = stashedIntentFrames;
                    tracked.LastIntentAnim = stashedIntentFrames.AnimationName;
                }
                // WS-2: the ONLY place a stroke's full point array is ever marshalled — on a keyframe/add, or on the
                // tick its signature actually changed. Deferred-work philosophy, same as intentFrames above: the
                // per-tick path stays a count+tip probe, and a resting map (dozens of finished strokes) pays nothing.
                LineGeometry? lineGeometry = null;
                if (lineSig is not null && (includeStatic || lineSigChanged) && TryReadLineGeometry(node, out var strokeGeometry))
                {
                    lineGeometry = strokeGeometry;
                    tracked.LastLineSig = lineSig;
                }
                // P2 discard-select flash: when this emit is a REPARENT force-emit AND the node is inside an open
                // streaming-suppression window (the mirror is replaying a tween on it or an ancestor), OMIT the
                // transform payload. Otherwise the round-3 reparent emit ships the node's transition-START local
                // (the card mid-reparent ≈ global bottom-centre) which the client PINS for the rest of the window,
                // flashing it off-centre until the settle resync. With Transform=null the client holds the node's
                // last-known placed local (Q6 HasPlacedTransform) — (0,0) under the new centre holder ⇒ rendered at
                // centre immediately, riding the container's tween, with the settle re-emit unchanged. The re-attach
                // still carries parentId + static so the client re-parents. Un-suppressed reparents (deselect) keep
                // shipping their transform. Kill-switch REPARENT_HOLD_TWEENED (default ON).
                var emitRead = read;
                if (Sts2ReparentEmit.OmitTransformOnReparent(
                        Sts2SceneWatchRuntimeSettings.ReparentHoldTweened, parentChanged, suppressTransform, tracked.JustAdded))
                {
                    emitRead = read with { Transform = null };
                }
                upserts.Add(BuildNodeDelta(tracked, emitRead, includeStatic, intentFrames, lineGeometry));
                // R10: this node's FIRST appearance on the wire. Its id was withheld from OrderedIds until now
                // (see Tracked.EverEmitted), so the order must be re-shipped this capture or the client would
                // merge the node with no structure trigger and never build an element for it.
                if (!tracked.EverEmitted)
                {
                    tracked.EverEmitted = true;
                    firstEmitThisPass = true;
                }
                tracked.JustAdded = false;
                // Record the parentId the client now knows, so a later reparent (not a first emit) is detected.
                tracked.LastEmittedParentId = tracked.ParentIdStr;
            }

            // A frozen (ProcessMode.Disabled) SpineSprite root with an unchanged global transform gates read-elision
            // of its skeleton subtree (visited next in this pre-order walk). One cheap ProcessMode read per creature.
            if (tracked.Style.Spine is not null)
            {
                frozenRootDepth = tracked.Depth;
                frozenRootStationary = node.ProcessMode == Node.ProcessModeEnum.Disabled
                    && XformEq(priorTransform, read.Transform);
            }

            // Prune hidden subtrees on incremental ticks only — a keyframe must carry every node so the
            // client's retained map matches OrderedIds. The client hides descendants via the parent chain.
            // FIX 4: EXEMPT a Visible=false node inside the NRestSiteRoom subtree from the prune so its focus fade-in
            // is read/diffed/emitted instead of skipped (the description-stuck-invisible cure). Merely un-pruning
            // would let ChoicesScreen ship its overlay `visible:false` and hide the whole rest-site UI — but the
            // neutralization above already flipped an ACTIVE overlay's false→true, so an exempted node is only
            // shipped visible:false for a BACKGROUNDED room (the client hides it via the parent chain, correctly).
            // Byte-identical outside the rest overlay / with the kill-switch off (ExemptFromVisiblePrune → false).
            if (!full && !read.Visible
                && !Sts2RestOverlayStream.ExemptFromVisiblePrune(restOverlayEnabled, insideRestOverlay, read.Visible))
            {
                skipDepth = tracked.Depth;
            }
        }

        if (!full && upserts.Count == 0 && removedIds.Count == 0)
        {
            return null; // nothing changed → emit nothing (natural dedup)
        }

        // R10 ORDER/UPSERT CONTRACT (kill switch SPIRECTL_SCENE_WATCH_ORDER_EMITTED_ONLY=0 restores the pre-R10
        // array verbatim). TWO coupled halves, both required:
        //   1. The array carries only nodes the client has actually RECEIVED. A pruned hidden subtree's ids used to
        //      ride the order while their upserts never did, so the client held order entries for nodes it had no
        //      state for — which BOTH broke the compact order patch (SceneOrderDiff's self-verification can't
        //      reconstruct an order containing ids the structure index skips, so every structural send fell back to
        //      the full ~52KB array) AND, worse, meant the id was ALREADY in the order when the node finally
        //      emitted.
        //   2. Because of that, a first emit must re-ship the order even when the tree shape did not change:
        //      `state.orderedIds !== lastOrderedIds` is the client's only structural-walk trigger, so a node that
        //      arrives with an unchanged order is merged into the map and then never placed in the tree.
        // Together they restore the invariant `orderedIds ⊆ nodes the client holds`, which is exactly what both
        // clients' structure builders (and the couch host's SceneStructureIndex) already assume.
        IReadOnlyList<string>? orderedIds = orderChanged || (OrderEmittedOnly && firstEmitThisPass)
            ? BuildOrderedIds()
            : null;

        return new RuntimeSceneDelta(
            Full: full,
            ScreenType: _screenType,
            ScreenInstanceId: _screenInstanceId,
            Upserts: upserts,
            RemovedIds: removedIds,
            OrderedIds: orderedIds,
            // Tell the client which space every Transform in this delta is in, so it composes locals down the
            // emitted parent chain (local) or positions absolutely (global).
            TransformSpace: localMode ? "local" : "global");
    }

    // The emitted draw order. R10: only nodes the client actually holds (Tracked.EverEmitted); with the kill switch
    // off, the pre-R10 array (every registered node, including pruned hidden subtrees the client has no state for).
    // Allocation is unchanged in kind — the pre-R10 build already materialised a fresh array per structural send.
    private string[] BuildOrderedIds()
        => OrderEmittedOnly
            ? _ordered.Where(t => t.EverEmitted).Select(t => t.IdStr).ToArray()
            : _ordered.Select(t => t.IdStr).ToArray();

    // Pre-order DFS that rebuilds the ordered list and reconciles the registry against the live tree:
    // new nodes get their (expensive) static snapshot derived ONCE; vanished nodes are evicted + reported.
    private void Reconcile(Node root, List<string> removedIds)
    {
        var seen = new HashSet<ulong>();
        _ordered.Clear();
        // Rebuilt from scratch every reconcile: a chain node is only reachable through the Tracked entries this walk
        // hands it to, so a viewport that left the tree simply isn't re-recorded (no stale-sweep of its own needed).
        // Discovery order is pre-order DFS ⇒ an outer viewport is always recorded BEFORE any viewport nested inside
        // it, which is what lets RefreshViewportPrefixes recompute the list in one forward pass.
        _prefixChains.Clear();
        ReconcileNode(root, null, 0, seen, null);

        if (_registry.Count != seen.Count)
        {
            var stale = _registry.Keys.Where(id => !seen.Contains(id)).ToArray();
            foreach (var id in stale)
            {
                var staleTracked = _registry[id];
                removedIds.Add(staleTracked.IdStr);
                if (Sts2SpineDiagnostics.Current.Enabled && staleTracked.Style.Spine is { } evictedSpine)
                {
                    Sts2SpineDiagnostics.Current.Log(
                        $"spine evict node={id} name={staleTracked.Name} scene={evictedSpine.SceneResPath} "
                        + "→ added to RemovedIds (client should release the subtree)");
                }
                Sts2SpineInspector.Forget(id, staleTracked.Node); // drop live anim tracking + disconnect signal
                Sts2ParticleRestartHooks.Forget(id); // drop the node's hook-driven restart counter (keep Counts bounded)
                _registry.Remove(id);
            }
        }
    }

    // Re-evaluate every prefix-producing SubViewport recorded by the last Reconcile. THE MAP-DRAWING FIX: a stroke's
    // streamed global is `prefix · strokeGlobalWithCanvas`, and only the PREFIX moves when the map scrolls (the
    // stroke's own transform is viewport-local and stands still). Computing the prefix once per Reconcile therefore
    // pinned every quill annotation to the SCREEN until an unrelated node add — a HoverTip popping up — dirtied the
    // structure and snapped them back onto the map.
    //
    // Cheap by construction: one pass over a list that holds a handful of entries (the map's DrawViewport, the
    // multiplayer card-intent preview, a monster-death render texture), NOT a tree walk. The list is in pre-order
    // DFS, so a nested viewport's parent is already refreshed when it is reached.
    private void RefreshViewportPrefixes()
    {
        if (_prefixChains.Count == 0)
        {
            return;
        }

        var start = _instrumentation.ProducerProfilingEnabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
        foreach (var chain in _prefixChains)
        {
            // A viewport freed since the last Reconcile: leave the last good prefix in place. The freed node is
            // reported through the stale sweep on the next structure-dirty tick, which also rebuilds the list.
            if (!GodotObject.IsInstanceValid(chain.Viewport))
            {
                _structureDirty = true;
                continue;
            }

            if (TryComputeViewportPrefix(chain.Viewport, out var fit))
            {
                chain.Prefix = (chain.Parent?.Prefix ?? Transform2D.Identity) * fit;
            }
        }

        if (_instrumentation.ProducerProfilingEnabled)
        {
            _profile.RecordPrefixRefresh(System.Diagnostics.Stopwatch.GetTimestamp() - start);
        }
    }

    private void ReconcileNode(Node node, ulong? parentId, int depth, HashSet<ulong> seen, ViewportPrefixChain? prefixChain)
    {
        // EMBEDDER OPT-OUT, checked before anything else (and deliberately ABOVE the CanvasItem branch, since an
        // injected root is often a plain logic Node whose children carry the visuals): a subtree stamped with the
        // `spirectl_stream_skip` metadata key is neither tracked nor descended, so a downstream mod's own host-local
        // UI (the CouchCoop lobby QR button/dialog) never reaches mirror clients — which matters because clients
        // drive the host with real injected input. Stale sweep handles removal; re-admission is symmetric. The dev
        // scene-inspection surface (Sts2RuntimeSceneProvider) intentionally still sees these nodes.
        // See Sts2StreamSkipMeta.
        if (Sts2StreamSkipMeta.ShouldSkip(Sts2SceneWatchRuntimeSettings.HonorStreamSkipMetadata, node))
        {
            return;
        }

        if (node is not CanvasItem)
        {
            // Non-drawable nodes (pure logic/Node) don't affect the rendered mirror; their CanvasItem
            // descendants are still reached below, parented to the nearest tracked CanvasItem ancestor.
            // A SubViewport is one such node — but its CanvasItem content reports VIEWPORT-LOCAL transforms,
            // so compose a viewport→screen prefix (its consumer's on-screen frame) and thread it down so the
            // flattened content lands at the right place instead of at (0,0). Non-viewport logic nodes just
            // pass the inherited prefix through.
            var childPrefix = prefixChain;
            if (node is SubViewport subViewport)
            {
                var prefixComputable = TryComputeViewportPrefix(subViewport, out var fit);

                // No prefix ⇒ this viewport's content has no place on screen: it would stream at VIEWPORT-LOCAL
                // coordinates, i.e. pinned at ~the design origin. That is the phantom potion — NPotion.DoFlash()
                // duplicates the potion subtree into the 60x60 SubViewport of vfx_potion_flash.tscn, which is only
                // ever sampled as a CPUParticles2D texture (no Control/Sprite2D consumer to resolve), and under the
                // headless permanent particle freeze the one-shot never finishes so the VFX never QueueFrees. Skip
                // the subtree entirely; Reconcile's stale sweep emits the removals, and re-admission is symmetric
                // (absent from _registry ⇒ re-added JustAdded ⇒ full static-bearing upsert). SubViewportContainer
                // content is NEVER pruned — it is genuinely displayed in-game. See Sts2ViewportContentPrune.
                if (Sts2ViewportContentPrune.ShouldPrune(
                        Sts2SceneWatchRuntimeSettings.PruneUnmappedViewportContent,
                        prefixComputable,
                        ParentIsSubViewportContainer(subViewport)))
                {
                    return;
                }

                if (prefixComputable)
                {
                    // parent * child: the prefix maps viewport-local → screen. Recorded as a chain node so the
                    // per-capture refresh can recompute it (the display node moves when the map scrolls) without
                    // touching a single Tracked entry.
                    var composed = (prefixChain?.Prefix ?? Transform2D.Identity) * fit;
                    childPrefix = new ViewportPrefixChain(subViewport, prefixChain, composed);
                    _prefixChains.Add(childPrefix);
                }
            }

            foreach (var child in node.GetChildren())
            {
                ReconcileNode(child, parentId, depth, seen, childPrefix);
            }

            return;
        }

        var id = node.GetInstanceId();
        seen.Add(id);
        if (!_registry.TryGetValue(id, out var tracked))
        {
            tracked = new Tracked(
                node,
                id,
                DescribeStaticName(node),
                node.GetType().FullName ?? node.GetType().Name,
                DescribeStaticStyle(node))
            {
                JustAdded = true,
            };
            _registry[id] = tracked;

            // WS-3: adopt a card-flight suppression window parked before this node was ever tracked (the flight VFX
            // and its trail are hooked in their own `_Ready`, i.e. one reconcile before they land here). Entries are
            // one-shot — removed on adoption — so a later node that happens to reuse the instance id can't inherit
            // a stale freeze, and an already-expired deadline is simply dropped.
            if (_animations.TryTakePendingWindow(id, out var parkedFlight)
                && parkedFlight.Deadline > System.Environment.TickCount64)
            {
                if (parkedFlight.SelfOnly)
                {
                    tracked.SuppressTransformSelfUntil = parkedFlight.Deadline;
                }
                else
                {
                    tracked.SuppressTransformUntil = parkedFlight.Deadline;
                }
            }

            if (Sts2SpineDiagnostics.Current.Enabled && tracked.Style.Spine is { } addedSpine)
            {
                var animCount = addedSpine.Animations.Count;
                Sts2SpineDiagnostics.Current.Log(
                    $"spine add node={id} name={tracked.Name} scene={addedSpine.SceneResPath} "
                    + $"path={addedSpine.NodePath ?? "<root>"} skel={addedSpine.SkelResPath ?? "<unknown>"} "
                    + $"animCount={animCount}{(animCount == 0 ? " [0-ANIMATIONS-AT-ADD]" : string.Empty)}");
            }
        }

        // R12 decorative-animator suppression: resolve this node's SCENE IDENTITY (nearest instanced-scene root +
        // the path below it) from its parent's, which pre-order DFS guarantees is already resolved. Re-resolved on
        // add AND on a reparent (the only two ways a node's scene-relative path can change), so a cached scope can
        // never go stale. The IsWatchedScene probe at each scene root is what keeps this near-free: outside the few
        // watched scenes DecorSceneFile stays null and no path string is ever built.
        var parentTracked = parentId is { } pid && _registry.TryGetValue(pid, out var pt) ? pt : null;
        if (tracked.JustAdded || tracked.ParentId != parentId)
        {
            ResolveDecorScope(tracked, parentTracked);
        }

        tracked.Node = node;
        tracked.ParentId = parentId;
        tracked.ParentIdStr = parentId is { } p ? p.ToString() : null;
        tracked.Depth = depth;
        tracked.PrefixChain = prefixChain;
        _ordered.Add(tracked);

        foreach (var child in node.GetChildren())
        {
            ReconcileNode(child, id, depth + 1, seen, prefixChain);
        }
    }

    // R12: resolve one node's decorative-suppression scope from its parent's. A node carrying a SceneFilePath is an
    // instanced-scene ROOT — it starts a fresh scope (rel path ".") only when the table has rules for that scene;
    // otherwise it CLEARS the scope, so a nested unwatched scene inside a watched one can't inherit the wrong
    // identity. A node without a SceneFilePath extends its parent's rel path (null past MaxRelDepth). Resetting the
    // rest samples alongside is what makes a reparented node re-capture its rest pose in its new scope.
    private static void ResolveDecorScope(Tracked tracked, Tracked? parent)
    {
        if (tracked.Style.SceneFilePath is { } sceneFile)
        {
            tracked.DecorSceneFile = Sts2DecorEmitSuppress.IsWatchedScene(sceneFile) ? sceneFile : null;
            tracked.DecorRelPath = tracked.DecorSceneFile is null ? null : Sts2DecorEmitSuppress.RootRelPath;
            // R12b: the scene-instance ROOT is where a scoped node's game-side STATE lives (the map point's
            // travelable/focused/input gates hang off NNormalMapPoint, not off its icon container). Remember it as an
            // ID, not a Node reference, so a freed root can never be dereferenced — the capture path resolves it
            // through the registry, which pre-order DFS guarantees is populated before any descendant is read.
            tracked.DecorScopeRootId = tracked.DecorSceneFile is null ? null : tracked.Id;
        }
        else if (parent?.DecorSceneFile is { } inherited)
        {
            tracked.DecorRelPath = Sts2DecorEmitSuppress.ChildRelPath(parent.DecorRelPath, tracked.Name);
            tracked.DecorSceneFile = tracked.DecorRelPath is null ? null : inherited;
            tracked.DecorScopeRootId = tracked.DecorSceneFile is null ? null : parent.DecorScopeRootId;
        }
        else
        {
            tracked.DecorSceneFile = null;
            tracked.DecorRelPath = null;
            tracked.DecorScopeRootId = null;
        }

        tracked.DecorChannels = Sts2DecorEmitSuppress.Lookup(tracked.DecorSceneFile, tracked.DecorRelPath);
    }

    // ---- R15 creature spine-anchor fold: the three facts the walk feeds Sts2SpineAnchorFold ------------------

    /// <summary>
    /// Does this node contribute a browser-visible field OF ITS OWN? A spine anchor contributes none — it has no
    /// rect (it is a Node2D, not a Control), no texture, no text, no fill, no range, no shader uniforms and no
    /// spine clip of its own — which is exactly why the client's `placementBox` (`nodeStyles.ts:256`) returns null
    /// for it and its element is a bare grouping div. Anything that DOES report one is a node the client places, so
    /// withholding its transform would strand it on screen: the fold refuses.
    /// </summary>
    private static bool CarriesOwnPaint(VolatileRead read)
        => read.LocalRect is not null
           || read.Texture is not null
           || read.Text is not null
           || read.FillColor is not null
           || read.RangeValue is not null
           || read.ShaderParameters is not null
           || read.SpineCurrentAnim is not null;

    /// <summary>
    /// Does the node at <paramref name="orderIndex"/> have any tracked descendant? `_ordered` is pre-order DFS, so
    /// the next entry is this node's first child exactly when its Depth is greater. Re-evaluated EVERY tick, which
    /// is what makes arm A safe against a VFX attached to a bone at runtime: the child is in `_ordered` before the
    /// anchor is read on that same capture, so the anchor stops qualifying on the very tick the child appears.
    /// </summary>
    private bool HasTrackedDescendants(int orderIndex, Tracked tracked)
        => orderIndex + 1 < _ordered.Count && _ordered[orderIndex + 1].Depth > tracked.Depth;

    /// <summary>
    /// Arm B's live gate: is every descendant of this anchor an INERT grouping node or a QUIET particle emitter?
    /// Quiet means not emitting AND past its own burst tail (see Sts2SpineAnchorFold.BurstTailMs) so a burst that
    /// just ended isn't left hanging at a stale mouth. Fails OPEN (streams) on anything unexpected: an unknown
    /// native class, a subtree deeper/wider than the caps, an invalid node.
    ///
    /// <para>Costs, per matched anchor per tick: one <c>Depth</c> compare per descendant, one cached class probe
    /// the first time each descendant is ever scanned, and one <c>Emitting</c> property read per emitter. The
    /// authored subtrees are 1-3 nodes.</para>
    /// </summary>
    private bool SpineAnchorSubtreeQuiet(int orderIndex, Tracked anchor, long now)
    {
        var scanned = 0;
        for (var i = orderIndex + 1; i < _ordered.Count; i++)
        {
            var descendant = _ordered[i];
            if (descendant.Depth <= anchor.Depth)
            {
                break; // left the subtree
            }

            if (++scanned > Sts2SpineAnchorFold.MaxSubtreeNodes
                || descendant.Depth - anchor.Depth > Sts2SpineAnchorFold.MaxSubtreeDepth
                || !GodotObject.IsInstanceValid(descendant.Node))
            {
                return false;
            }

            if (descendant.Style.ParticleSpec is { } spec)
            {
                var emitting = descendant.Node switch
                {
                    GpuParticles2D gpu => gpu.Emitting,
                    CpuParticles2D cpu => cpu.Emitting,
                    _ => true, // a spec on something that is neither: unexpected → fail open
                };
                if (emitting || now - descendant.LastEmittingAtMs < Sts2SpineAnchorFold.BurstTailMs(spec.Lifetime, spec.Explosiveness))
                {
                    return false;
                }

                continue;
            }

            if (!DescendantIsInert(descendant))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Is this descendant a node that paints nothing itself (a grouping Node2D, a Marker2D, a nested spine
    /// attachment)? Resolved from the NATIVE class ONCE per node and cached on the entry — class strings are
    /// invariant, so this is a single probe per node for its whole lifetime, paid only by nodes that actually sit
    /// under a candidate anchor. A node carrying its own spine clip or a Line2D is never inert whatever its class
    /// says.
    /// </summary>
    private static bool DescendantIsInert(Tracked descendant)
    {
        if (descendant.AnchorInertKind == Tracked.InertUnknown)
        {
            var inert = descendant.Style.Spine is null && !descendant.IsLine2DType;
            if (inert)
            {
                try
                {
                    inert = Sts2SpineAnchorFold.IsInertDescendantClass(descendant.Node.GetClass());
                }
                catch
                {
                    inert = false;
                }
            }

            descendant.AnchorInertKind = inert ? Tracked.InertYes : Tracked.InertNo;
        }

        return descendant.AnchorInertKind == Tracked.InertYes;
    }

    // The live node of a folded node's SCENE-INSTANCE ROOT — where the game-side state a fold gates on lives (the
    // map point's travelable flags hang off NNormalMapPoint, the icon loops off NTopBarButton, the glow off
    // NProceedButton; never off the animated child itself). Resolved through the registry from a remembered ID, so a
    // freed root can never be dereferenced; pre-order DFS guarantees the root was tracked before any descendant.
    private Node? ResolveDecorScopeRoot(Tracked tracked)
        => tracked.DecorScopeRootId is { } rootId && _registry.TryGetValue(rootId, out var root) ? root.Node : null;

    // R13: is this top-bar button's icon loop RUNNING right now? Read off the SCENE ROOT, no System.Reflection.
    //
    // NOT one uniform read (live-validated 2026-08-05): on the deck and settings buttons the registered script
    // variable `IsScreenOpen` tracks the screen exactly, so `Get(IsScreenOpen)` is the gate. On the MAP button that
    // variable is never updated and stays false forever even while the map is open and the icon is rocking — the
    // rock is driven from the map screen instead — so the truthful live gate there is the button's registered
    // `IsOpen()` method, invoked via Godot `Call`, the same pattern as the map point's `IsInputAllowed`.
    // See Sts2TopBarFold.
    private static bool ReadTopBarScreenOpen(Node? sceneRoot, string? sceneFile)
    {
        if (sceneRoot is null || !GodotObject.IsInstanceValid(sceneRoot))
        {
            return false;
        }

        if (Sts2TopBarFold.GateIsOpenMethod(sceneFile))
        {
            return sceneRoot.HasMethod(TopBarIsOpenMethod)
                && Sts2TopBarFold.IsAnimating(sceneRoot.Call(TopBarIsOpenMethod).AsBool());
        }

        return Sts2TopBarFold.IsAnimating(sceneRoot.Get(TopBarScreenOpenProp).AsBool());
    }

    // R13: is the Proceed button's infinite glow tween RUNNING right now? Reads the three gates off the SCENE ROOT
    // (an NProceedButton); all three are registered Godot script members, so this is Get — no System.Reflection.
    // Ordered cheapest-and-most-selective first: a Proceed button is DISABLED on most screens/ticks, so the other
    // two reads only run for a button that is actually showing. See Sts2ProceedGlow.
    private static bool ReadProceedGlowLooping(Node? sceneRoot)
    {
        if (sceneRoot is null || !GodotObject.IsInstanceValid(sceneRoot))
        {
            return false;
        }

        var isEnabled = sceneRoot.Get(ClickableEnabledProp).AsBool();
        if (!isEnabled)
        {
            return false;
        }

        var shouldPulse = sceneRoot.Get(ProceedShouldPulseProp).AsBool();
        if (!shouldPulse)
        {
            return false;
        }

        return Sts2ProceedGlow.IsLooping(isEnabled, shouldPulse, sceneRoot.Get(ClickableFocusedProp).AsBool());
    }

    // R14: is the end-turn button's infinite glow pulse RUNNING right now? ONE registered script member off the
    // SCENE ROOT (an NEndTurnButton), so this is Get — no System.Reflection, no allocation. `_isShiny` is set
    // immediately before `GlowPulse()` (its only caller) and cleared immediately before the kill, so the flag and
    // the loop are the same fact. See Sts2EndTurnGlowFold.
    private static bool ReadEndTurnGlowLooping(Node? sceneRoot)
        => sceneRoot is not null
           && GodotObject.IsInstanceValid(sceneRoot)
           && Sts2EndTurnGlowFold.IsLooping(sceneRoot.Get(EndTurnShinyProp).AsBool());

    // R12b: is this map point's icon pulse RUNNING right now? Reads the pulse's three gates off the map point's
    // SCENE ROOT. All three are registered Godot script members, so this is Get/Call — no System.Reflection, no
    // per-tick allocation. Ordered cheapest-and-most-selective first: the enabled flag is false for the ~57 of 61
    // map points that are not travelable, so the other two reads (and the HasMethod probe) only run for the handful
    // that could actually be pulsing.
    private static bool ReadMapPointPulsing(Node? sceneRoot)
    {
        if (sceneRoot is null || !GodotObject.IsInstanceValid(sceneRoot))
        {
            return false;
        }

        var isEnabled = sceneRoot.Get(ClickableEnabledProp).AsBool();
        if (!isEnabled)
        {
            return false;
        }

        var isFocused = sceneRoot.Get(ClickableFocusedProp).AsBool();
        if (isFocused)
        {
            return false;
        }

        // HasMethod guards the (rare) Call against a scene whose root script ever stops exposing it — a missing
        // method would otherwise push a Godot error every tick rather than degrade quietly to "not pulsing".
        var inputAllowed = sceneRoot.HasMethod(MapPointInputAllowedMethod)
            && sceneRoot.Call(MapPointInputAllowedMethod).AsBool();
        return Sts2MapPointPulse.IsPulsing(isEnabled, isFocused, inputAllowed);
    }

    // Compute the viewport→screen prefix for a SubViewport: the on-screen transform of the node that DISPLAYS it,
    // composed with the fit-scale that maps the viewport's pixel space into that node's display rect. Two display
    // paths: a ViewportTexture CONSUMER (Control/Sprite2D), and the SubViewport's own parent when that parent is a
    // SubViewportContainer (which draws its child viewport itself). Returns false (→ no prefix, current behavior)
    // when neither resolves or sizes are degenerate — so 3D/particle VFX viewports and the self-managed map drawing
    // are left untouched (and, with the prune on, the prefix-less ones stop streaming altogether).
    private static bool TryComputeViewportPrefix(SubViewport subViewport, out Transform2D prefix)
    {
        prefix = Transform2D.Identity;
        try
        {
            var size = subViewport.Size;
            if (size.X <= 0 || size.Y <= 0)
            {
                return false;
            }

            // The ViewportTexture consumer is probed FIRST so every scene that resolves one today keeps its exact
            // prefix (map drawing, multiplayer card intent, monster-death render textures are byte-identical). The
            // container branch is a strict ADDITION for viewports that resolved nothing before.
            //
            // A resolved consumer's display rect + fit mode are read NATIVELY (IsClass/Get). A script-attached
            // consumer's managed C# type can diverge from its native class — the card-intent node is a native
            // TextureRect but runs an `NMultiplayerCardIntent : Control` script, so `consumer is TextureRect` /
            // `.StretchMode` are unreliable. IsClass/Get see the real native object.
            var consumer = FindViewportConsumer(subViewport);
            CanvasItem display;
            Transform2D fitTransform;
            if (consumer is null)
            {
                if (!TryComputeContainerFit(subViewport, size, out var container, out fitTransform))
                {
                    return false;
                }

                display = container;
            }
            else if (consumer.IsClass("Control"))
            {
                display = consumer;
                var rect = consumer.Get("size").AsVector2();
                // stretch_mode is a TextureRect property; a plain Control lacks it → fill (anisotropic).
                var keepAspect = TryGetNativeInt(consumer, "stretch_mode", out var stretchMode)
                    && Sts2ViewportPrefixTransform.StretchKeepsAspect(stretchMode);
                if (Sts2ViewportPrefixTransform.Compute(rect.X, rect.Y, size.X, size.Y, keepAspect) is not { } fit)
                {
                    return false;
                }

                fitTransform = MakeFit(fit.ScaleX, fit.ScaleY, fit.OffsetX, fit.OffsetY);
            }
            else if (consumer.IsClass("Sprite2D"))
            {
                display = consumer;
                // A Sprite2D draws the viewport texture at native pixel size (1:1; its own scale rides the consumer
                // transform). Default `centered` puts the texture's (0,0) at sprite-local (-w/2,-h/2).
                var texSize = consumer.Get("region_enabled").AsBool()
                    ? consumer.Get("region_rect").AsRect2().Size
                    : (consumer.Get("texture").AsGodotObject() as Texture2D)?.GetSize() ?? Vector2.Zero;
                if (texSize.X <= 0 || texSize.Y <= 0)
                {
                    return false;
                }

                var centered = consumer.Get("centered").AsBool();
                fitTransform = MakeFit(
                    texSize.X / size.X,
                    texSize.Y / size.Y,
                    centered ? -texSize.X / 2.0 : 0.0,
                    centered ? -texSize.Y / 2.0 : 0.0);
            }
            else
            {
                return false;
            }

            // #7: the fit above maps the viewport's RENDER-pixel space (`size`) into the display node. But the child
            // transforms flattened under this viewport live in its 2D coordinate space, which is `size_2d_override`
            // when set (the viewport renders that logical space at the higher `size` resolution — supersampling).
            // Compose the child→render-pixel scale (`size / size_2d_override`) on the right so a supersampled
            // viewport's content lands at its true on-screen size instead of `size_2d_override/size` of it. This is
            // also what makes the SubViewportContainer branch above correct under EITHER Godot convention for what a
            // `stretch`ing container overrides (container size, or container size / shrink): the net content→screen
            // scale is `containerSize / size_2d_override` either way.
            var superSample = Transform2D.Identity;
            if (HonorViewportSizeOverride && TryGetViewportSize2DOverride(subViewport, out var overrideSize)
                && overrideSize.X > 0 && overrideSize.Y > 0
                && (overrideSize.X != size.X || overrideSize.Y != size.Y))
            {
                superSample = MakeFit((double)size.X / overrideSize.X, (double)size.Y / overrideSize.Y, 0.0, 0.0);
            }

            prefix = display.GetGlobalTransformWithCanvas() * fitTransform * superSample;
            return true;
        }
        catch
        {
            return false; // any probe failure → no prefix, never worse than today
        }
    }

    // The SubViewport→screen fit when the viewport's PARENT is a `SubViewportContainer` — the container has no
    // ViewportTexture of its own to find, it DRAWS its child viewport's texture itself, at its own control-local
    // origin. Used by the timeline epoch screens (`scenes/timeline_screen/epoch.tscn`, `epoch_slot.tscn`: a
    // 324x200 / 162x100 container over an identically-sized, non-stretching SubViewport), whose content previously
    // resolved NO consumer and therefore streamed at ~the design origin.
    //
    // `stretch`/`stretch_shrink` are read NATIVELY (IsClass/Get), like every other consumer probe in this file, so a
    // script-attached container still resolves. Both live under the pure `Sts2ViewportPrefixTransform` math, which
    // conservatively returns no fit for a degenerate size or an invalid shrink — the caller then reports no prefix,
    // which is exactly today's behaviour, and Sts2ViewportContentPrune's SubViewportContainer exclusion keeps such
    // content streaming rather than pruning it.
    private static bool TryComputeContainerFit(
        SubViewport subViewport,
        Vector2I size,
        out CanvasItem container,
        out Transform2D fitTransform)
    {
        container = null!;
        fitTransform = Transform2D.Identity;

        if (subViewport.GetParent() is not CanvasItem parent || !parent.IsClass("SubViewportContainer"))
        {
            return false;
        }

        var containerSize = parent.Get("size").AsVector2();
        var stretch = parent.Get("stretch").AsBool();
        var stretchShrink = TryGetNativeInt(parent, "stretch_shrink", out var shrink) ? shrink : 1;
        if (Sts2ViewportPrefixTransform.ComputeContainerFit(
                containerSize.X, containerSize.Y, size.X, size.Y, stretch, stretchShrink) is not { } fit)
        {
            return false;
        }

        container = parent;
        fitTransform = MakeFit(fit.ScaleX, fit.ScaleY, fit.OffsetX, fit.OffsetY);
        return true;
    }

    // True when this SubViewport's parent is a `SubViewportContainer` — read NATIVELY (IsClass) so a script-attached
    // container whose managed wrapper is its script's base class still resolves. Content under such a viewport is
    // genuinely displayed in-game and must never be pruned (see Sts2ViewportContentPrune).
    private static bool ParentIsSubViewportContainer(SubViewport subViewport)
    {
        try
        {
            return subViewport.GetParent() is { } parent && parent.IsClass("SubViewportContainer");
        }
        catch
        {
            return false; // unreadable parent → treat as "not a container"; the prefix probe already failed safe
        }
    }

    // Read a SubViewport's `size_2d_override` (Vector2i) natively. Returns false when absent/zero so the caller
    // treats the viewport's render `Size` as its coordinate space (today's behavior). The override is only active
    // as a distinct 2D space when its components are positive; the native property is present on every Viewport.
    private static bool TryGetViewportSize2DOverride(SubViewport subViewport, out Vector2I overrideSize)
    {
        overrideSize = Vector2I.Zero;
        try
        {
            var variant = subViewport.Get("size_2d_override");
            if (variant.VariantType is Variant.Type.Vector2I)
            {
                overrideSize = variant.AsVector2I();
                return true;
            }

            if (variant.VariantType is Variant.Type.Vector2)
            {
                var v = variant.AsVector2();
                overrideSize = new Vector2I((int)v.X, (int)v.Y);
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    // Find the CanvasItem that displays `subViewport` via a ViewportTexture. The consumer is the SubViewport's
    // PARENT (multiplayer CardIntent TextureRect) or a SIBLING/descendant Sprite2D (single-player RenderTexture),
    // so probe the parent and a shallow slice of its subtree.
    private static CanvasItem? FindViewportConsumer(SubViewport subViewport)
    {
        var parent = subViewport.GetParent();
        if (parent is null)
        {
            return null;
        }

        if (parent is CanvasItem parentCanvas && DisplaysViewport(parent, subViewport))
        {
            return parentCanvas;
        }

        foreach (var child in parent.GetChildren())
        {
            if (child is CanvasItem childCanvas && DisplaysViewport(child, subViewport))
            {
                return childCanvas;
            }
        }

        return null;
    }

    // True when `candidate`'s displayed texture/texture_normal is a ViewportTexture whose viewport path resolves
    // to `subViewport`. Read the texture NATIVELY (Get) not via managed reflection: a script-attached consumer's
    // C# type can diverge from its native class (the card-intent TextureRect runs a `: Control` script, which has
    // no managed `Texture` property), so reflection would miss the real native texture. The viewport path is
    // relative to the texture's local scene, so try several bases.
    private static bool DisplaysViewport(Node candidate, SubViewport subViewport)
    {
        try
        {
            if (!TryGetNativeViewportTexture(candidate, out var viewportTexture))
            {
                return false;
            }

            var path = viewportTexture.ViewportPath;
            if (path is null || path.IsEmpty)
            {
                return false;
            }

            var targetId = subViewport.GetInstanceId();
            foreach (var basis in new[] { candidate, candidate.Owner, subViewport.GetParent(), candidate.GetParent() })
            {
                if (basis is not null
                    && basis.GetNodeOrNull(path) is { } resolved
                    && resolved.GetInstanceId() == targetId)
                {
                    return true;
                }
            }
        }
        catch
        {
            // Inaccessible texture property — not a resolvable viewport consumer.
        }

        return false;
    }

    // Read a node's displayed texture (texture / texture_normal) via the NATIVE property accessor and return it
    // when it is a ViewportTexture. Native Get sees the real engine property even when the managed wrapper type
    // (a script's base class) does not expose it.
    private static bool TryGetNativeViewportTexture(GodotObject node, out ViewportTexture viewportTexture)
    {
        foreach (var property in new[] { "texture", "texture_normal" })
        {
            var value = node.Get(property);
            if (value.VariantType == Variant.Type.Object && value.AsGodotObject() is ViewportTexture vt)
            {
                viewportTexture = vt;
                return true;
            }
        }

        viewportTexture = null!;
        return false;
    }

    private static Transform2D MakeFit(double scaleX, double scaleY, double offsetX, double offsetY) => new(
        new Vector2((float)scaleX, 0f),
        new Vector2(0f, (float)scaleY),
        new Vector2((float)offsetX, (float)offsetY));

    private static bool TryGetNativeInt(GodotObject node, string property, out int value)
    {
        var variant = node.Get(property);
        if (variant.VariantType == Variant.Type.Int)
        {
            value = (int)variant.AsInt64();
            return true;
        }

        value = 0;
        return false;
    }

    private static string DescribeStaticName(Node node)
    {
        try
        {
            return node.Name.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    // #8/#13 late-static re-probe. Returns true (once) when a spine root whose cached snapshot had an EMPTY
    // animation list is re-inspected and now exposes animations — the caller then swaps in the richer snapshot and
    // forces a keyframe-style upsert. A no-op for every node that isn't a spine root with a still-empty list, so
    // it stays off the hot path for normal creatures.
    //
    // ROUND-8 (#13, "the treasure chest isn't visible until it opens"): the attempt budget used to be spent one
    // per TICK while the list was empty, so a node whose skeleton is injected more than 30 ticks after the watcher
    // first saw it (the chest / the boss map point — both get their skeleton resource assigned only once the node
    // enters the tree, and the room screen can be tracked well before that) burned the whole budget and then
    // reported 0 animations + no `skelResPath` FOREVER. Both clients bail without an anim, and the skeleton path
    // the `&skel=` bake fallback needs never arrives.
    //
    // The budget is now spent per SKELETON, not per tick: a cheap one-native-Get gate (ReadSkeletonDataInstanceId)
    // skips — free, and WITHOUT consuming an attempt — as long as the skeleton-data resource is unchanged and
    // still absent, and RE-ARMS the full budget on every skeleton_data_res TRANSITION. So the wait for a runtime
    // injection is unbounded in time (and ~free), while the retries against a freshly-assigned-but-not-yet-loaded
    // resource stay bounded exactly as before.
    private static bool MaybeReprobeSpineAnimations(Tracked tracked, Node node)
    {
        if (tracked.Style.Spine is not { } spine || spine.Animations.Count > 0)
        {
            return false;
        }

        var decision = Sts2SpineReprobeGate.Decide(
            Sts2SpineInspector.ReadSkeletonDataInstanceId(node),
            tracked.SpineSkeletonProbeId,
            tracked.SpineReprobesLeft,
            SpineReprobeMaxAttempts);
        tracked.SpineSkeletonProbeId = decision.SkeletonProbeId;
        tracked.SpineReprobesLeft = decision.AttemptsLeft;
        if (decision.ReArmed && Sts2SpineDiagnostics.Current.Enabled)
        {
            Sts2SpineDiagnostics.Current.Log(
                $"spine re-probe RE-ARMED node={tracked.Id} name={tracked.Name} "
                + $"skeletonDataId={decision.SkeletonProbeId} (skeleton_data_res transition; budget={SpineReprobeMaxAttempts})");
        }

        if (!decision.ShouldInspect)
        {
            return false;
        }

        RuntimeSceneSpineSnapshot? reprobed;
        try
        {
            reprobed = Sts2SpineInspector.InspectStatic(node);
        }
        catch
        {
            return false; // inspection failed this tick — try again next tick until the budget runs out
        }

        if (reprobed is null || reprobed.Animations.Count == 0)
        {
            return false;
        }

        tracked.Style = tracked.Style with { Spine = reprobed };
        if (Sts2SpineDiagnostics.Current.Enabled)
        {
            Sts2SpineDiagnostics.Current.Log(
                $"spine re-probe SUCCEEDED node={tracked.Id} name={tracked.Name} "
                + $"animCount={reprobed.Animations.Count} skel={reprobed.SkelResPath ?? "<unknown>"} "
                + $"(was 0-at-add; forcing keyframe upsert)");
        }
        return true;
    }

    // Probed ONCE per node (on add). Runtime-invariant styling: nine-patch margins, text font/outline/shadow
    // (the expensive text reflection the per-tick path skips), bbcode flag, material/shader refs, and the
    // draw-order flag. Each probe is independently guarded so one failure never blanks the rest.
    private static StaticStyle DescribeStaticStyle(Node node)
    {
        var canvasItem = node as CanvasItem;
        var showBehind = canvasItem?.ShowBehindParent ?? false;
        // CanvasItem.ClipChildren (0 Disabled / 1 Only / 2 AndDraw): a node clips its descendants to its own
        // texture's alpha (the combat health-bar `Mask` rounds the fills to the capsule). Static — it never
        // changes at runtime. The client uses it to clip; Only also means the node itself paints nothing.
        var clipChildren = (int)(canvasItem?.ClipChildren ?? CanvasItem.ClipChildrenMode.Disabled);

        // Control.clip_contents — a DIFFERENT property from clip_children above, and the one that actually bounds a
        // layout container: clip_children stencils descendants against this node's own DRAWN alpha (so a container
        // that paints nothing clips nothing), while clip_contents clips a Control's children to its RECTANGLE
        // regardless. Static like clip_children (an authored layout property), read through the provider's single
        // ReadControlClipContents so the dev scene surface and the wire cannot disagree. Null (non-Control) reads as
        // false. Probed unconditionally; the kill-switch is applied at EMIT time (BuildNodeDelta), so flipping it on
        // a running process takes effect without re-probing every already-registered node.
        var clipContents = Sts2ControlClipContents.Read(node) == true;

        // NinePatchRect 9-slice margins read NATIVELY (IsClass + Get patch_margin_*), not a `node is NinePatchRect`
        // typed cast: a script-attached NinePatchRect's managed wrapper is its script's base class (same divergence
        // as the reward banner's TextureRect), so the typed cast misses it and the client loses its 9-slice. IsClass/
        // Get resolve the real native node for both script-attached and script-less cases.
        RuntimeScenePatchMarginsSnapshot? margins = null;
        if (node.IsClass("NinePatchRect"))
        {
            try
            {
                margins = new RuntimeScenePatchMarginsSnapshot(
                    node.Get("patch_margin_left").AsInt32(),
                    node.Get("patch_margin_top").AsInt32(),
                    node.Get("patch_margin_right").AsInt32(),
                    node.Get("patch_margin_bottom").AsInt32());
            }
            catch
            {
                // Margins unavailable — client falls back to a stretched texture.
            }
        }

        RuntimeSceneResourceRefSnapshot? font = null;
        string? fontWeight = null;
        string? fontStyle = null;
        RuntimeSceneColorSnapshot? outlineColor = null;
        double? outlineSize = null;
        RuntimeSceneTextShadowSnapshot? shadow = null;
        var richText = node is RichTextLabel { BbcodeEnabled: true };
        IReadOnlyList<int>? textLineRanges = null;
        string? textLineBasis = null;
        string? textParsedText = null;
        int? textLineSourceLength = null;
        int? textLineSourceHash = null;
        try
        {
            // Full (non-lean) describe runs the expensive font/shadow/outline reflection ONCE here; non-text
            // nodes return null cheaply. The per-tick path stays lean (string + color + alignment only).
            var fullText = Sts2RuntimeSceneTextDiagnostics.Describe(node, lean: false);
            if (fullText is not null)
            {
                // Resolve the font to its underlying .ttf/.otf so the client can actually @font-face it —
                // the raw FontVariation/.tres only serves as JSON over /res/.
                font = ResolveFontBinary(fullText.Font);
                fontWeight = fullText.FontWeight;
                fontStyle = fullText.FontStyle;
                outlineColor = fullText.OutlineColor;
                outlineSize = fullText.OutlineSize;
                shadow = fullText.Shadow;

                // GODOT'S OWN LINE BREAKING, flattened for the wire. Free of any new probing cost: the non-lean
                // describe above already builds the TextParagraph (or reads the node's line metrics) that these
                // come from, so this is the walk keeping an answer it was already paying for and throwing away.
                //
                // Emitted ALL-OR-NOTHING. A basis names the string the offsets address and the hash is what lets
                // a consumer notice they have gone stale; ranges without either are ranges nobody may safely use,
                // so the whole block is dropped rather than partially sent.
                var metrics = fullText.RenderedMetrics;
                if (metrics is { RangeBasis: not null, RangeSourceHash: not null })
                {
                    var flat = new List<int>(metrics.Lines.Count * 2);
                    foreach (var line in metrics.Lines)
                    {
                        if (line.RangeStart is { } start && line.RangeEnd is { } end)
                        {
                            flat.Add(start);
                            flat.Add(end);
                        }
                    }

                    if (flat.Count > 0)
                    {
                        textLineRanges = flat;
                        textLineBasis = metrics.RangeBasis;
                        textParsedText = metrics.ParsedText;
                        textLineSourceLength = metrics.RangeSourceLength;
                        textLineSourceHash = metrics.RangeSourceHash;
                    }
                }
            }
        }
        catch
        {
            // Text styling unavailable — client renders unstyled text.
        }

        // PER-ROLE rich-text fonts. Godot renders `[b]`/`[i]`/`[b][i]` spans by swapping the label to its
        // `bold_font`/`italics_font`/`bold_italics_font` THEME ITEM — a different font FILE (STS2: kreon_bold.ttf) —
        // it never synthesises bold, and neither does the mirror (`font-synthesis: none`). Streaming one font per
        // node therefore rendered every `[b]` span un-bold. Probe each role with the EFFECTIVE theme lookup
        // (`GetThemeFont`: per-node override → theme chain → default theme), resolve it to a font BINARY exactly
        // like the normal font, and suppress it when it is unresolvable or is the SAME file the node already uses.
        // Alongside each: the role's theme font SIZE (omitted when it equals the node's normal size) and the role
        // font's `spacing_glyph` (omitted when zero). `mono_font` is skipped by design — gsw has no CSS variable
        // for it. Kill-switch SPIRECTL_SCENE_WATCH_RICH_ROLE_FONTS (default ON) makes this block a no-op.
        RuntimeSceneResourceRefSnapshot? richBoldFont = null, richItalicFont = null, richBoldItalicFont = null;
        double? richBoldFontSizePx = null, richItalicFontSizePx = null, richBoldItalicFontSizePx = null;
        double? richBoldFontSpacingPx = null, richItalicFontSpacingPx = null, richBoldItalicFontSpacingPx = null;
        if (richText && Sts2SceneWatchRuntimeSettings.StreamRichRoleFonts && node is Control richControl)
        {
            // `RichText` is only ever true for a `RichTextLabel { BbcodeEnabled: true }`, so the Control cast holds;
            // the normal font's resolved path is what each role is de-duplicated against.
            var normalFontPath = font?.ResourcePath;
            var normalFontSizePx = ProbeThemeFontSizePx(richControl, RichNormalFontSizeThemeItem);

            (richBoldFont, richBoldFontSizePx, richBoldFontSpacingPx) =
                ProbeRichRoleFont(richControl, RichBoldFontThemeItem, RichBoldFontSizeThemeItem, normalFontPath, normalFontSizePx);
            (richItalicFont, richItalicFontSizePx, richItalicFontSpacingPx) =
                ProbeRichRoleFont(richControl, RichItalicFontThemeItem, RichItalicFontSizeThemeItem, normalFontPath, normalFontSizePx);
            (richBoldItalicFont, richBoldItalicFontSizePx, richBoldItalicFontSpacingPx) =
                ProbeRichRoleFont(richControl, RichBoldItalicFontThemeItem, RichBoldItalicFontSizeThemeItem, normalFontPath, normalFontSizePx);
        }

        RuntimeSceneResourceRefSnapshot? material = null;
        RuntimeSceneResourceRefSnapshot? shader = null;
        IReadOnlyList<RuntimeSceneShaderParamSnapshot>? shaderParameters = null;
        try
        {
            if (canvasItem?.Material is Material mat)
            {
                material = ToResourceRef("Material", mat);
                if (mat is ShaderMaterial { Shader: { } sh })
                {
                    shader = ToResourceRef("Shader", sh);
                    // Capture the shader's uniform values so the client can run the real shader (not just
                    // its CSS/SVG approximation). Reuses the generic STS2 shader-parameter inspector.
                    shaderParameters = ToShaderParams(Sts2ShaderMaterialInspector.Inspect(mat, sh, includeExtendedKinds: true));
                }
            }
        }
        catch
        {
            // Material inaccessible — client renders without a shader fallback.
        }

        // TextureRect.StretchMode (Godot enum 0..6), FlipH, FlipV — all static. StretchMode controls how the
        // texture fits the node rect; FlipH/FlipV let the same atlas tile be used for mirror caps (e.g.
        // NScrollbar TrackTop/TrackBot share one atlas region, TrackBot has FlipV=true to flip the arrow).
        int? textureStretchMode = null;
        bool textureFlipH = false, textureFlipV = false;
        // Read stretch/flip NATIVELY (IsClass + Get), not a `node is TextureRect` cast — the reward-dialog parchment
        // banner is a native TextureRect running an NCommonBanner : Control script, so the typed cast misses it and
        // the client would lose its fit mode. stretch_mode is Godot's StretchModeEnum (0..6).
        if (node.IsClass("TextureRect"))
        {
            try
            {
                textureStretchMode = (int)node.Get("stretch_mode").AsInt64();
                textureFlipH = node.Get("flip_h").AsBool();
                textureFlipV = node.Get("flip_v").AsBool();
            }
            catch
            {
                // Stretch/flip unavailable — client falls back to the default fit.
            }
        }

        // Blend mode via the node's CanvasItemMaterial — STS2's Godot binding puts BlendMode on the material,
        // not on CanvasItem directly. Null when Mix (0, default) to avoid JSON noise.
        int? canvasBlendMode = null;
        if (canvasItem?.Material is CanvasItemMaterial cim && cim.BlendMode != CanvasItemMaterial.BlendModeEnum.Mix)
        {
            canvasBlendMode = (int)cim.BlendMode;
        }

        // GpuParticles2D/CpuParticles2D → the full ParticleSpec (shape/velocity/forces/texture/curves). Probed
        // once on add: STS2 particles never re-author these at runtime; only `emitting` flips (read per tick).
        RuntimeSceneParticleSpecSnapshot? particleSpec = null;
        try
        {
            if (node is GpuParticles2D or CpuParticles2D)
            {
                particleSpec = Sts2ParticleInspector.Inspect(node);
            }
        }
        catch
        {
            // Particle inspection failed — the node still renders (just without the VFX simulation).
        }

        // SpineSprite → its canonical clip address (scene + scene-relative node path) + animation list.
        // Probed once on add (the skeleton data + scene boundary are runtime-invariant); the live current
        // animation + track time are read per tick. Null for non-Spine nodes.
        RuntimeSceneSpineSnapshot? spine = null;
        try
        {
            spine = Sts2SpineInspector.InspectStatic(node);
        }
        catch
        {
            // Spine inspection failed — the node still renders its flat texture (just without a clip).
        }

        // Node.SceneFilePath is non-empty ONLY on an instanced-scene root; null it out otherwise so the client's
        // parent-chain walk to the nearest non-empty path resolves the owning scene + its root id.
        string? sceneFilePath = null;
        try
        {
            sceneFilePath = string.IsNullOrWhiteSpace(node.SceneFilePath) ? null : node.SceneFilePath;
        }
        catch
        {
            // Scene path unavailable — the node still renders; it just can't be scene-identified.
        }

        var containerLayout = DescribeContainerLayout(node);

        return new StaticStyle(showBehind, clipChildren, clipContents, margins, font, fontWeight, fontStyle, outlineColor, outlineSize, shadow, richText, material, shader, shaderParameters, textureStretchMode, textureFlipH, textureFlipV, canvasBlendMode, particleSpec, spine, sceneFilePath, containerLayout, richBoldFont, richItalicFont, richBoldItalicFont, richBoldFontSizePx, richItalicFontSizePx, richBoldItalicFontSizePx, richBoldFontSpacingPx, richItalicFontSpacingPx, richBoldItalicFontSpacingPx, textLineRanges, textLineBasis, textParsedText, textLineSourceLength, textLineSourceHash);
    }

    // Godot-4 RichTextLabel theme item names for the per-role fonts + sizes the mirror needs. NOTE the Godot-4
    // spelling: `italics_font` / `bold_italics_font` (NOT `italic_font` / `bold_italic_font` — those name no theme
    // item and silently resolve to the default-theme font). `mono_font`/`mono_font_size` are skipped by design:
    // godot-scene-web exposes no CSS variable to consume them.
    private const string RichNormalFontSizeThemeItem = "normal_font_size";
    private const string RichBoldFontThemeItem = "bold_font";
    private const string RichBoldFontSizeThemeItem = "bold_font_size";
    private const string RichItalicFontThemeItem = "italics_font";
    private const string RichItalicFontSizeThemeItem = "italics_font_size";
    private const string RichBoldItalicFontThemeItem = "bold_italics_font";
    private const string RichBoldItalicFontSizeThemeItem = "bold_italics_font_size";
    private const string FontVariationSpacingGlyphProp = "spacing_glyph";

    // Probe ONE rich-text role (bold / italics / bold-italics) off a RichTextLabel: its effective theme font
    // resolved to a font BINARY, that role's theme font size, and the role font's glyph spacing. Returns nulls for
    // anything the client can't use or that carries no information (see Sts2RichRoleFontEmit for the two emit
    // gates). Wrapped in try/catch per this file's house pattern so one unavailable theme item never blanks the
    // rest of the node's static style.
    private static (RuntimeSceneResourceRefSnapshot? Font, double? SizePx, double? SpacingPx) ProbeRichRoleFont(
        Control control,
        string fontThemeItem,
        string fontSizeThemeItem,
        string? normalFontPath,
        int normalFontSizePx)
    {
        try
        {
            // GetThemeFont is the EFFECTIVE lookup (override → theme chain → default theme), so it never fails —
            // a label with no such item still gets Godot's built-in default font back. The binary-suffix gate in
            // ShouldEmitRoleFont is what turns that fallback into "nothing streamed".
            if (control.GetThemeFont(fontThemeItem) is not { } roleThemeFont)
            {
                return (null, null, null);
            }

            var resolved = ResolveFontBinary(ToResourceRef("Font", roleThemeFont));
            if (!Sts2RichRoleFontEmit.ShouldEmitRoleFont(resolved?.ResourcePath, normalFontPath))
            {
                return (null, null, null);
            }

            // The glyph spacing lives on the FontVariation the THEME names (kreon_bold_glyph_space_one.tres), not on
            // the .ttf the resolution above walks down to — so read it off the theme font, before resolution.
            var spacingPx = Sts2RichRoleFontEmit.RoleFontSpacingPxOrNull(ProbeGlyphSpacingPx(roleThemeFont));
            var sizePx = Sts2RichRoleFontEmit.RoleFontSizePxOrNull(
                ProbeThemeFontSizePx(control, fontSizeThemeItem), normalFontSizePx);
            return (resolved, sizePx, spacingPx);
        }
        catch
        {
            // Theme lookup unavailable — the client renders the span in the node's own font (pre-fix behaviour).
            return (null, null, null);
        }
    }

    // A Control's EFFECTIVE theme font size for one item, or 0 when unavailable (which both callers treat as
    // "unset" → no size streamed).
    private static int ProbeThemeFontSizePx(Control control, string themeItem)
    {
        try
        {
            return control.GetThemeFontSize(themeItem);
        }
        catch
        {
            return 0;
        }
    }

    // A theme font's `spacing_glyph` (extra px after every glyph), or 0 when the font is not a FontVariation / the
    // property is unavailable. Read NATIVELY (Get) rather than via a typed FontVariation cast for the same reason
    // the rest of this file does: a resource's managed wrapper can diverge, and a nil Variant degrades to 0.
    private static int ProbeGlyphSpacingPx(Font themeFont)
    {
        try
        {
            var value = themeFont.Get(FontVariationSpacingGlyphProp);
            return value.VariantType is Variant.Type.Int or Variant.Type.Float ? (int)value.AsInt64() : 0;
        }
        catch
        {
            return 0;
        }
    }

    // BoxContainer layout hint ("hbox-begin"/"hbox-center"/"hbox-end"/"vbox-…") for a Godot BoxContainer-derived
    // node, or null for every other node. Read via the DYNAMIC Godot API (IsClass + Get) rather than a typed
    // `node is BoxContainer` cast: STS2 wraps many scene nodes in a C# script class that does NOT inherit the
    // Godot node's type (the same wrapped-Control gotcha MouseFilter/AnchorLeft handle in ReadVolatile), so a typed
    // cast would miss a wrapped container — but the UNDERLYING native node is still a BoxContainer, so IsClass +
    // Get("vertical")/Get("alignment") resolve it uniformly. `alignment` is Godot's BoxContainer.AlignmentMode
    // (0 Begin / 1 Center / 2 End); `vertical` picks H vs V orientation (HBoxContainer=false, VBoxContainer=true).
    private static string? DescribeContainerLayout(Node node)
    {
        try
        {
            if (!node.IsClass("BoxContainer"))
            {
                return null;
            }

            var vertical = node.Get("vertical").AsBool();
            var alignment = (int)node.Get("alignment").AsInt64();
            var orientation = vertical ? "vbox" : "hbox";
            var packing = alignment switch
            {
                1 => "center",
                2 => "end",
                _ => "begin",
            };
            return $"{orientation}-{packing}";
        }
        catch
        {
            // Container introspection unavailable — the client falls back to the anchor algebra (pre-hint behavior).
            return null;
        }
    }

    // The producer reports fonts as FontVariation/.tres resources; the /res/ route serves those as JSON, so
    // the client can't @font-face them. Walk the FontVariation.BaseFont chain down to the concrete FontFile
    // and report ITS path (the imported .ttf/.otf), which /res/ serves as a real font binary.
    private static RuntimeSceneResourceRefSnapshot? ResolveFontBinary(RuntimeSceneResourceRefSnapshot? font)
    {
        var path = font?.ResourcePath;
        if (font is null || string.IsNullOrWhiteSpace(path))
        {
            return font;
        }

        // Shared with the role-font probes, where the same suffix test is the EMIT GATE rather than a
        // stop-walking short-circuit (see Sts2RichRoleFontEmit.IsFontBinaryPath).
        if (Sts2RichRoleFontEmit.IsFontBinaryPath(path))
        {
            return font; // already a binary
        }

        try
        {
            Resource? current = ResourceLoader.Load(path);
            for (var i = 0; current is FontVariation variation && i < 8; i++)
            {
                current = variation.BaseFont;
            }

            if (current is FontFile { ResourcePath: { Length: > 0 } ttfPath } fontFile)
            {
                return new RuntimeSceneResourceRefSnapshot(
                    Field: "Font",
                    ResourcePath: ttfPath,
                    ResourceType: fontFile.GetType().FullName ?? "Godot.FontFile",
                    ResourceName: fontFile.ResourceName.ToString());
            }
        }
        catch
        {
            // Resolution failed — fall back to the original ref (client may still derive a family name).
        }

        return font;
    }

    private static RuntimeSceneResourceRefSnapshot ToResourceRef(string field, Resource resource)
        => new(
            Field: field,
            ResourcePath: string.IsNullOrWhiteSpace(resource.ResourcePath) ? string.Empty : resource.ResourcePath,
            ResourceType: resource.GetType().FullName ?? resource.GetType().Name,
            ResourceName: resource.ResourceName.ToString());

    // Map the shader-parameter inspection result into serializable snapshots (one per uniform). Null when the
    // shader has no inspectable uniforms, so the client falls back to default uniform values.
    private static IReadOnlyList<RuntimeSceneShaderParamSnapshot>? ToShaderParams(Sts2ShaderParameterInspectionResult result)
    {
        if (result.Parameters.Count == 0)
        {
            return null;
        }

        var snapshots = new List<RuntimeSceneShaderParamSnapshot>(result.Parameters.Count);
        foreach (var p in result.Parameters)
        {
            // A `resource` sampler that is a procedural ramp carries its AUTHORED data alongside the path, so the
            // client can run the real shader (the VFX `lut` colour lookup) instead of guessing from the mask.
            // The live GetShaderParameter already handed us the LOADED object, so there is no .tscn/.tres parse here.
            var gradientStops = EmitShaderParamGradients ? Sts2RampExtractor.ExtractGradientStops(p.ResourceValue) : null;
            snapshots.Add(new RuntimeSceneShaderParamSnapshot(
                Name: p.Name,
                Kind: p.ValueKind,
                Number: p.NumberValue,
                Bool: p.BoolValue,
                String: p.StringValue,
                Color: p.ColorValue is { } c ? ToColor(c) : null,
                Vector2: p.Vector2Value is { } v ? new RuntimeSceneVector2Snapshot(v.X, v.Y) : null,
                Resource: p.ResourceValue is { } r ? ToResourceRef("ShaderParameter", r) : null,
                Vector3: p.Vector3Value is { } v3 ? new RuntimeSceneVector3Snapshot(v3.X, v3.Y, v3.Z) : null,
                Vector4: p.Vector4Value is { } v4 ? new RuntimeSceneVector4Snapshot(v4.X, v4.Y, v4.Z, v4.W) : null,
                Rect2: p.Rect2Value is { } rc ? new RuntimeSceneShaderRectSnapshot(rc.Position.X, rc.Position.Y, rc.Size.X, rc.Size.Y) : null,
                Transform2D: p.Transform2DValue is { } tx
                    ? new double[] { tx.X.X, tx.X.Y, tx.Y.X, tx.Y.Y, tx.Origin.X, tx.Origin.Y }
                    : null,
                NumberArray: p.NumberArrayValue,
                GradientStops: gradientStops,
                GradientInterpolation: gradientStops is null ? null : Sts2RampExtractor.ExtractGradientInterpolation(p.ResourceValue),
                CurvePoints: EmitShaderParamGradients ? Sts2RampExtractor.ExtractCurvePoints(p.ResourceValue) : null));
        }

        return snapshots;
    }

    private Node? ResolveRoot()
    {
        // Scope to NGame (the game-UI root Control). Its subtree holds the active run/char-select
        // (RootSceneContainer.CurrentScene), the hover-tip container, and the relic/card detail dialogs
        // (InspectionContainer) — overlay nodes mounted OUTSIDE NRun that the mirror must show. NGame is
        // visible-node-count ≈ the run (idle overlay containers are empty/hidden → pruned), so cost is
        // unchanged versus scoping to NRun, and NGame.Instance is stable for the whole session (no
        // per-run keyframe churn; run swaps arrive via the structure signals).
        try
        {
            if (NGame.Instance is Node game && GodotObject.IsInstanceValid(game) && game.IsInsideTree())
            {
                return game;
            }
        }
        catch
        {
            // NGame not available — fall through.
        }

        try
        {
            if (NRun.Instance is Node run && GodotObject.IsInstanceValid(run) && run.IsInsideTree())
            {
                return run;
            }
        }
        catch
        {
            // NRun not available (e.g. menus / char-select) — fall through to the scene root.
        }

        return (Engine.GetMainLoop() as SceneTree)?.Root;
    }

    private void RefreshScreenMetadata()
    {
        try
        {
            var screen = new Sts2ScreenLocator().Locate();
            _screenType = screen.ScreenType;
            _screenInstanceId = screen.ScreenInstanceId;
        }
        catch
        {
            // Best-effort metadata; the node deltas are what matter.
        }
    }

    // Re-base a child's streamed GLOBAL against its emitted parent's streamed global: L = parentGlobal⁻¹ · childGlobal
    // (the Transform2D form of Sts2TweenEndpointTuples.RebaseLocalTuple). A singular (collapsed) parent has no inverse
    // → identity local (the parent already zeroes the subtree on screen). Identity parent ⇒ L == childGlobal (roots).
    private static Transform2D RebaseGlobalToLocal(Transform2D parentGlobal, Transform2D childGlobal)
    {
        if (Math.Abs(parentGlobal.Determinant()) <= SingularParentDeterminantEpsilon)
        {
            return Transform2D.Identity;
        }

        return parentGlobal.AffineInverse() * childGlobal;
    }

    // Record an emitted node's streamed GLOBAL in the depth scratch (LOCAL mode). Grows the backing list only when a
    // deeper node than ever before is seen; steady-state captures re-use it without allocating.
    private void StoreEmittedGlobal(int depth, Transform2D global)
    {
        while (_emittedGlobalByDepth.Count <= depth)
        {
            _emittedGlobalByDepth.Add(Transform2D.Identity);
        }

        _emittedGlobalByDepth[depth] = global;
    }

    // The emitted parent's streamed GLOBAL as a 6-tuple, for LOCAL-mode tween-endpoint localization. The tracked
    // node's emitted parent is registry[ParentId] (ReconcileNode reparents through skipped non-CanvasItem nodes, so
    // this is the nearest emitted ancestor); its global is parentPrefix · parent.GetGlobalTransformWithCanvas() — the
    // SAME value the capture walk stores in its depth scratch, so a localized endpoint lines up with the node's
    // localized Transform even across a viewport-prefix boundary. Identity when the node has no emitted parent (root).
    private double[] EmittedParentGlobalTuple(Tracked tracked)
    {
        // Q3 (P2): resolve the emitted parent from the LIVE tree (nearest CanvasItem ancestor) so a reparent+tween
        // same frame can't localize the endpoint against a STALE registry ParentId (freed old parent → identity
        // fallback shipping a global as local = the lower-right double-apply; live old parent → wrong re-base). The
        // pure Sts2TweenParentResolver owns the precedence; the registry ParentId is the fallback. Kill-switch
        // TWEEN_PARENT_LIVE (default ON) — OFF leaves the live chain empty so the registry fallback (round-3) runs.
        var liveNodes = new List<Node>();
        var candidates = new List<Sts2TweenParentResolver.Candidate>();
        if (Sts2SceneWatchRuntimeSettings.TweenParentLive && GodotObject.IsInstanceValid(tracked.Node))
        {
            for (var ancestor = tracked.Node.GetParentOrNull<Node>(); ancestor is not null; ancestor = ancestor.GetParentOrNull<Node>())
            {
                liveNodes.Add(ancestor);
                candidates.Add(new Sts2TweenParentResolver.Candidate(
                    ancestor is CanvasItem, _registry.ContainsKey(ancestor.GetInstanceId())));
            }
        }

        var registryFallbackResolves = tracked.ParentId is { } parentId
            && _registry.TryGetValue(parentId, out var parent)
            && GodotObject.IsInstanceValid(parent.Node)
            && parent.Node is CanvasItem;

        var pick = Sts2TweenParentResolver.Resolve(candidates, registryFallbackResolves);
        switch (pick.Source)
        {
            case Sts2TweenParentResolver.Source.LiveAncestor:
                var ancestorCanvas = (CanvasItem)liveNodes[pick.AncestorIndex];
                var prefix = tracked.ViewportPrefix;
                if (pick.UseAncestorPrefix
                    && _registry.TryGetValue(((Node)ancestorCanvas).GetInstanceId(), out var trackedAncestor))
                {
                    prefix = trackedAncestor.ViewportPrefix;
                }

                return Sts2TweenEndpointMath.ToTuple(prefix * ancestorCanvas.GetGlobalTransformWithCanvas());

            case Sts2TweenParentResolver.Source.RegistryFallback:
                var registryParent = _registry[tracked.ParentId!.Value];
                return Sts2TweenEndpointMath.ToTuple(
                    registryParent.ViewportPrefix * ((CanvasItem)registryParent.Node).GetGlobalTransformWithCanvas());
        }

        return new double[] { 1, 0, 0, 1, 0, 0 };
    }

    private static VolatileRead ReadVolatile(Node node, IReadOnlyList<RuntimeSceneShaderParamSnapshot>? staticShaderParams, RuntimeSceneSpineSnapshot? spine, bool focusCapable, Transform2D viewportPrefix, bool localMode, Transform2D emittedParentGlobal, out Transform2D streamedGlobal)
    {
        streamedGlobal = Transform2D.Identity;
        var canvasItem = node as CanvasItem;
        var visible = canvasItem?.Visible ?? true;
        var modulate = canvasItem?.Modulate ?? new Color(1, 1, 1, 1);
        var selfModulate = canvasItem?.SelfModulate ?? new Color(1, 1, 1, 1);

        // SCREEN transform + node-LOCAL box. GetGlobalTransformWithCanvas() (NOT GetGlobalTransform()) folds in
        // the node's CANVAS transform — i.e. the active Camera2D's pan/zoom for world nodes AND a CanvasLayer's
        // own transform — so the snapshot is the node's actual on-screen placement, mapped to the viewport space
        // the mirror's fixed 1920x1080 design mirrors. Without it, combat world spines (under a zoomed battle
        // camera) render at world scale (~half size) while CanvasLayer UI looks fine; it equals GetGlobalTransform
        // wherever the canvas transform is identity (no camera), so non-camera screens are unchanged. The client
        // renders the box via CSS matrix(), so rotation / scale / pivot AND every ancestor transform bake in.
        RuntimeSceneTransform2DSnapshot? transform = null;
        RuntimeSceneRect2Snapshot? localRect = null;
        if (canvasItem is not null)
        {
            // Identity prefix (the common case) is a no-op, so non-viewport nodes are byte-for-byte unchanged.
            var gt = viewportPrefix * canvasItem.GetGlobalTransformWithCanvas();
            streamedGlobal = gt; // raw GLOBAL, handed back so the caller's depth scratch feeds children's re-basing
            // Emit the GLOBAL (default) or, in LOCAL mode, re-base against the emitted parent's global so the value is
            // PARENT-RELATIVE (a container scroll then costs one node delta, not the whole subtree). The client
            // composes locals down the emitted parent chain to reproduce this exact global.
            var emitted = localMode ? RebaseGlobalToLocal(emittedParentGlobal, gt) : gt;
            transform = new RuntimeSceneTransform2DSnapshot(
                new RuntimeSceneVector2Snapshot(emitted.X.X, emitted.X.Y),
                new RuntimeSceneVector2Snapshot(emitted.Y.X, emitted.Y.Y),
                new RuntimeSceneVector2Snapshot(emitted.Origin.X, emitted.Origin.Y));
            localRect = ReadLocalRect(node);
        }

        var texture = ReadPrimaryTexture(node, out var ninePatch, out var textureRegion, out var textureMargin);
        var text = Sts2RuntimeSceneTextDiagnostics.Describe(node, lean: true);

        RuntimeSceneColorSnapshot? fill = node switch
        {
            ColorRect cr => VolatileColor(cr.Color),
            _ => null
        };
        // For nodes whose C# script class does NOT inherit Godot.ColorRect but whose native Godot class IS
        // ColorRect (e.g. NButton, which extends Godot.Control but lives on a ColorRect scene node): the
        // typed `is ColorRect` branch above misses them. Fall back to a dynamic property read so the mirror
        // sees the fill color (modulate then tints it to produce the backdrop effect).
        if (fill is null && node.IsClass("ColorRect"))
        {
            try
            {
                var v = node.Get("color");
                if (v.VariantType == Variant.Type.Color)
                {
                    fill = VolatileColor(v.AsColor());
                }
            }
            catch { }
        }
        double? rangeValue = null, rangeMin = null, rangeMax = null;
        if (node is Godot.Range range)
        {
            rangeValue = range.Value;
            rangeMin = range.MinValue;
            rangeMax = range.MaxValue;
        }

        // Clickable-control focus is exposed through Godot's property bridge. Capability was discovered once when
        // the node was tracked, so arbitrary scene nodes never pay a speculative Get (or produce Godot
        // "property not found" diagnostics) per tick.
        bool? focused = null;
        if (focusCapable)
        {
            try
            {
                var value = node.Get(ClickableFocusedProp);
                if (value.VariantType == Variant.Type.Bool)
                {
                    focused = value.AsBool();
                }
            }
            catch
            {
                // A script can disappear during teardown between the capability probe and this read. Null is the
                // safe compatibility value: consumers keep their legacy focus-on-first-tap behavior.
            }
        }

        // Control.MouseFilter (0 Stop / 1 Pass / 2 Ignore) — read here (per tick, settled) rather than in the
        // ONCE-on-add static probe: STS2 wraps many scene nodes in a C# script class that does NOT inherit the
        // node's Godot type (the same mismatch the ColorRect fill read above handles — e.g. an event option's
        // NinePatchRect flashes), so `node is Control` is false at track time and the static read came back null.
        // Typed read for real Godot.Controls; dynamic `mouse_filter` fallback for the wrapped ones. Null = not a
        // Control. The client renders Pass/Ignore as pointer-events:none so its touch hit-test matches the game.
        int? mouseFilter = node is Control mfControl ? (int)mfControl.MouseFilter : null;
        if (mouseFilter is null && node.IsClass("Control"))
        {
            try
            {
                var mf = node.Get("mouse_filter");
                if (mf.VariantType == Variant.Type.Int)
                {
                    mouseFilter = (int)mf.AsInt64();
                }
            }
            catch { }
        }

        // Control.AnchorLeft / AnchorRight (0..1 of the parent's width) — same wrapped-Control gotcha as
        // MouseFilter above: many STS2 scene nodes wear a C# script class that doesn't inherit Godot.Control,
        // so the typed read misses them and we fall back to a dynamic `anchor_left`/`anchor_right` get. The
        // mirror client reproduces Godot's own resize from these when it widens the stage past 1920 (a node
        // shifts by anchorLeft·ΔparentWidth, widens by (anchorRight−anchorLeft)·ΔparentWidth). Null = not a
        // Control. Anchors are static; they ride add/keyframe only (BuildNodeDelta gates on includeStatic).
        double? anchorLeft = node is Control acControl ? acControl.AnchorLeft : null;
        double? anchorRight = node is Control acRightControl ? acRightControl.AnchorRight : null;
        if (anchorLeft is null && node.IsClass("Control"))
        {
            try
            {
                var al = node.Get("anchor_left");
                if (al.VariantType is Variant.Type.Float or Variant.Type.Int)
                {
                    anchorLeft = al.AsSingle();
                }
                var ar = node.Get("anchor_right");
                if (ar.VariantType is Variant.Type.Float or Variant.Type.Int)
                {
                    anchorRight = ar.AsSingle();
                }
            }
            catch { }
        }

        // Spine playback state — EVENT-DRIVEN, never polled. The producer reacts to each SpineSprite's
        // `animation_started` signal (Sts2SpineInspector) for the current track-0 animation; per tick we just
        // read that cached value + a derived track time. We must NOT poll MegaAnimationState.get_current(0):
        // on a SpineSprite whose native runtime is uninitialised (just added) or torn down (scene transition)
        // it is an UNCATCHABLE SIGSEGV — it killed the headless at the Neow → map "Continue" (see
        // Sts2SpineInspector.PickDefaultAnimation for the full diagnosis). Until a node fires its first signal
        // we fall back to a stable default animation; track time advances on a real-time clock (resetting at
        // each anim change), and the frontend loops the clip on its own duration.
        string? spineCurrentAnim = null;
        double spineTrackTime = 0;
        var spineLooping = true;
        string? spineSkin = null;
        string? spineMat = null;
        var spinePaused = false;
        if (spine is not null)
        {
            (spineCurrentAnim, spineTrackTime, spineLooping, spineSkin, spineMat, spinePaused) = Sts2SpineInspector.ReadLive(node, spine.Animations);
        }

        return new VolatileRead(
            Transform: transform,
            LocalRect: localRect,
            Visible: visible,
            Opacity: modulate.A,
            ZIndex: canvasItem?.ZIndex,
            Texture: texture,
            NinePatch: ninePatch,
            Text: text,
            Modulate: VolatileColor(modulate),
            SelfModulate: VolatileColor(selfModulate),
            FillColor: fill,
            RangeValue: rangeValue,
            RangeMin: rangeMin,
            RangeMax: rangeMax,
            TextureRegion: textureRegion,
            TextureMargin: textureMargin,
            // Re-read NUMERIC (and, R9, COLOR) shader uniforms each tick (cheap GetShaderParameter lookups; the
            // uniform LIST was discovered once on add) so re-parameterised shaders — e.g. the screen-transition
            // dissolve's threshold/alpha, or a map point's per-act/travel-state tint — update instead of freezing at
            // their add-time values. Sampler/vector uniforms are still reused from the static snapshot.
            ShaderParameters: RefreshShaderParams(canvasItem, staticShaderParams),
            // VOLATILE: a particle node's emitting flag flips at runtime (one-shot ends → false; an energy gain
            // calls Restart() → true). Cheap bool read. The client re-triggers a one-shot burst on the false→true
            // edge (tracked via ParticleRestartEpoch in ApplyIfChanged).
            ParticleEmitting: node switch
            {
                GpuParticles2D gpu => gpu.Emitting,
                CpuParticles2D cpu => cpu.Emitting,
                _ => false,
            },
            // Hook-driven restart count: bumped by Sts2ParticleRestartHooks on the ACTUAL Restart()/Emitting=true
            // call, so a re-trigger drives the burst epoch even when the node is frozen (ProcessMode.Disabled) and
            // its Emitting latches true, producing no false→true edge. Cheap dictionary lookup; 0 for non-particles.
            ParticleRestartCount: Sts2ParticleRestartHooks.GetRestartCount(node.GetInstanceId()),
            SpineCurrentAnim: spineCurrentAnim,
            SpineTrackTime: spineTrackTime,
            SpineLooping: spineLooping,
            SpineSkin: spineSkin,
            SpineMat: spineMat,
            SpinePaused: spinePaused,
            MouseFilter: mouseFilter,
            AnchorLeft: anchorLeft,
            AnchorRight: anchorRight,
            Focused: focused);
    }

    private static IReadOnlyList<RuntimeSceneShaderParamSnapshot>? RefreshShaderParams(
        CanvasItem? canvasItem,
        IReadOnlyList<RuntimeSceneShaderParamSnapshot>? staticParams)
    {
        if (staticParams is null || staticParams.Count == 0 || canvasItem?.Material is not ShaderMaterial material)
        {
            return staticParams;
        }

        List<RuntimeSceneShaderParamSnapshot>? refreshed = null;
        for (var i = 0; i < staticParams.Count; i++)
        {
            var p = staticParams[i];
            if (p.Kind is not ("number" or "color"))
            {
                continue;
            }
            try
            {
                if (p.Kind == "number")
                {
                    var value = material.GetShaderParameter(p.Name).AsDouble();
                    if (value != p.Number)
                    {
                        refreshed ??= [.. staticParams];
                        refreshed[i] = p with { Number = value };
                    }

                    continue;
                }

                // R9: COLOR uniforms used to freeze at their add-time values, so a shader the game re-tints on a
                // state change (the map points' act/travel colors — the same class of live re-parameterisation the
                // spine `&mat=` signature exists for) kept streaming its first-seen tint forever. Same shape as the
                // numeric branch: one cheap GetShaderParameter + an equality check, and the snapshot list is only
                // cloned when something actually moved. Kill switch: SPIRECTL_SHADER_COLOR_REFRESH=0.
                if (!ShaderColorRefreshEnabled)
                {
                    continue;
                }

                var color = material.GetShaderParameter(p.Name).AsColor();
                if (p.Color is not { } previous
                    || previous.R != color.R || previous.G != color.G || previous.B != color.B || previous.A != color.A)
                {
                    refreshed ??= [.. staticParams];
                    refreshed[i] = p with { Color = ToColor(color) };
                }
            }
            catch
            {
                // Uniform not readable this tick — keep the last value.
            }
        }

        return refreshed ?? staticParams;
    }

    // SPIRECTL_SHADER_COLOR_REFRESH: escape hatch for the R9 per-tick COLOR uniform refresh above. Default ON;
    // `0`/`false`/`off`/`no` restores the numbers-only refresh (colors frozen at add) for an A/B, in case a shipped
    // shader turns out to animate a color per frame and churns the wire. Read once — env vars are process-stable.
    private static readonly bool ShaderColorRefreshEnabled =
        (System.Environment.GetEnvironmentVariable("SPIRECTL_SHADER_COLOR_REFRESH") ?? string.Empty)
            .Trim().ToLowerInvariant() is not ("0" or "false" or "off" or "no");

    // Node-LOCAL box (unscaled, in the node's own coordinates — the global transform carries scale/rotation).
    // Control → (0,0,Size). Sprite2D → its draw rect (centered/offset by texture size). Other Node2D
    // subclasses (Line2D, custom _draw, SpineSprite) have no generic box and stay rect-less.
    private static RuntimeSceneRect2Snapshot? ReadLocalRect(Node node)
    {
        if (node is Control control)
        {
            var size = control.Size;
            return new RuntimeSceneRect2Snapshot(
                new RuntimeSceneVector2Snapshot(0, 0),
                new RuntimeSceneVector2Snapshot(size.X, size.Y));
        }

        if (node is Sprite2D sprite && sprite.Texture is Texture2D tex)
        {
            try
            {
                var size = sprite.RegionEnabled ? sprite.RegionRect.Size : tex.GetSize();
                var pos = sprite.Offset - (sprite.Centered ? size / 2f : Vector2.Zero);
                return new RuntimeSceneRect2Snapshot(
                    new RuntimeSceneVector2Snapshot(pos.X, pos.Y),
                    new RuntimeSceneVector2Snapshot(size.X, size.Y));
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static RuntimeSceneColorSnapshot ToColor(Color c)
        => new(c.R, c.G, c.B, c.A, $"#{c.ToHtml(includeAlpha: true)}");

    // Per-tick volatile color. With DeferColorHtml (default) the #RRGGBBAA string is left null and built later in
    // BuildNodeDelta only for EMITTED nodes — the change test (ColorEq) uses numeric channels, so the hex is dead
    // weight for every unchanged node. Kill-switch restores eager formatting (identical output either way).
    private static RuntimeSceneColorSnapshot VolatileColor(Color c)
        => DeferColorHtml ? new RuntimeSceneColorSnapshot(c.R, c.G, c.B, c.A, null) : ToColor(c);

    // Populate a deferred color's #RRGGBBAA string at emit time. No-op when already set (eager mode) or null. The
    // reconstructed hex is byte-identical to ToColor's (channels round-trip float→double→float exactly).
    private static RuntimeSceneColorSnapshot? WithHtml(RuntimeSceneColorSnapshot? c)
    {
        if (c is null || c.Html is not null)
        {
            return c;
        }

        var color = new Color((float)c.R, (float)c.G, (float)c.B, (float)c.A);
        return c with { Html = $"#{color.ToHtml(includeAlpha: true)}" };
    }

    // How to read a node's primary texture natively (cached by managed Type in TextureProbeByType). Property/Field
    // null ⇒ the node class exposes no primary texture. NinePatch mirrors the old `node is NinePatchRect` flag, but
    // resolved from the NATIVE class so a script-attached NinePatchRect (managed wrapper diverges) is still tagged.
    private readonly record struct TextureProbe(string? Property, string? Field, bool NinePatch);

    // Resolve which native property carries a node's primary texture, via the NATIVE class (IsClass) rather than
    // managed reflection. The set mirrors the classes the old GetProperty("Texture")/("TextureNormal") reflection
    // matched: `texture` on the texture/sprite/particle classes, `texture_normal` on the button classes. Runs ONCE
    // per managed Type (result cached), so this stays off the per-tick capture path. The Field string preserves the
    // old managed-property casing ("Texture"/"TextureNormal") so the wire payload is byte-identical for script-less
    // nodes; a script-attached node (which used to ship NO texture) now resolves the same native property.
    private static TextureProbe ResolveTextureProbe(Node node)
    {
        var ninePatch = node.IsClass("NinePatchRect");
        if (ninePatch || node.IsClass("TextureRect") || node.IsClass("Sprite2D") || node.IsClass("Sprite3D")
            || node.IsClass("GpuParticles2D") || node.IsClass("CpuParticles2D")
            || node.IsClass("Line2D") || node.IsClass("Polygon2D") || node.IsClass("MeshInstance2D")
            || node.IsClass("PointLight2D"))
        {
            return new TextureProbe("texture", "Texture", ninePatch);
        }

        if (node.IsClass("TextureButton") || node.IsClass("TouchScreenButton"))
        {
            return new TextureProbe("texture_normal", "TextureNormal", ninePatch);
        }

        return new TextureProbe(null, null, ninePatch);
    }

    private static RuntimeSceneResourceRefSnapshot? ReadPrimaryTexture(
        Node node,
        out bool ninePatch,
        out RuntimeSceneRect2Snapshot? region,
        out RuntimeSceneRect2Snapshot? margin)
    {
        region = null;
        margin = null;
        var type = node.GetType();
        if (!TextureProbeByType.TryGetValue(type, out var probe))
        {
            probe = ResolveTextureProbe(node);
            TextureProbeByType[type] = probe;
        }

        ninePatch = probe.NinePatch;
        if (probe.Property is null)
        {
            return null;
        }

        try
        {
            // NATIVE property read (Get), NOT managed reflection: a script-attached native node's managed wrapper is
            // its script's base class, so GetType().GetProperty("Texture") returns null and the baked texture never
            // ships (the reward-dialog parchment banner is a native TextureRect running an NCommonBanner : Control
            // script). Get sees the real engine property for script-attached AND script-less nodes. Same Variant→
            // Resource conversion as TryGetNativeViewportTexture; the atlas/path resolution below is unchanged.
            var value = node.Get(probe.Property);
            if (value.VariantType == Variant.Type.Object && value.AsGodotObject() is Resource resource)
            {
                // AtlasTexture: emit the UNDERLYING atlas image (stable across animation frames) + the crop
                // rect/margin, so the client can re-point a CSS viewport without swapping the image URL (the
                // source of the intent-icon flicker). Plain textures pass through unchanged.
                if (resource is AtlasTexture { Atlas: { } atlasImage } atlasTexture
                    && !string.IsNullOrWhiteSpace(atlasImage.ResourcePath))
                {
                    region = ToRect(atlasTexture.Region);
                    margin = ToRect(atlasTexture.Margin);
                    return new RuntimeSceneResourceRefSnapshot(
                        Field: probe.Field!,
                        ResourcePath: atlasImage.ResourcePath,
                        ResourceType: atlasImage.GetType().FullName ?? atlasImage.GetType().Name,
                        ResourceName: atlasImage.ResourceName.ToString());
                }

                return new RuntimeSceneResourceRefSnapshot(
                    Field: probe.Field!,
                    ResourcePath: string.IsNullOrWhiteSpace(resource.ResourcePath) ? string.Empty : resource.ResourcePath,
                    ResourceType: resource.GetType().FullName ?? resource.GetType().Name,
                    ResourceName: resource.ResourceName.ToString());
            }
        }
        catch
        {
            // Inaccessible texture property — render without a texture.
        }

        return null;
    }

    private static RuntimeSceneRect2Snapshot ToRect(Rect2 r)
        => new(
            new RuntimeSceneVector2Snapshot(r.Position.X, r.Position.Y),
            new RuntimeSceneVector2Snapshot(r.Size.X, r.Size.Y));

    // Rebuild a Godot Transform2D from an emitted LOCAL snapshot — used by the cosmetic-emit cap to reconstruct the
    // client-shown global (emittedParentGlobal · lastEmittedLocal) so descendants re-base against a pinned parent.
    private static Transform2D ToTransform2D(RuntimeSceneTransform2DSnapshot t)
        => new(
            new Vector2((float)t.XAxis.X, (float)t.XAxis.Y),
            new Vector2((float)t.YAxis.X, (float)t.YAxis.Y),
            new Vector2((float)t.Origin.X, (float)t.Origin.Y));

    // Compare the fresh read against the node's last-emitted volatile state; update + return true on change.
    // `suppressTransform` (streaming suppression): when true, the node's TRANSFORM is excluded from the change test
    // AND its LastTransform is NOT updated — so a transform-only mover produces no delta (the client is pinning it),
    // while any other change still emits, and the stale LastTransform guarantees the post-window settle frame emits.
    // `suppressOpacity`/`suppressOpacityIsSelf` do the SAME for the alpha channel while the client pins a fade: when
    // set, the tweened channel's change tests are dropped and its Last* is NOT updated (settle frame re-emits after
    // the window). isSelf=false suppresses the MODULATE channel (both the LastOpacity and LastModulate triggers, which
    // are two triggers for the same modulate value); isSelf=true suppresses the SELF_MODULATE channel. The other
    // (untweened) channel keeps emitting normally.
    // A suppression window (Environment.TickCount64 deadline) is "just closed" when it was set (!= 0) and has now
    // elapsed (<= now). Pure so the settle-re-emit decision is unit-testable without the Godot-coupled watcher.
    internal static bool WindowClosed(long until, long now) => until != 0 && until <= now;

    private static bool ApplyIfChanged(Tracked tracked, VolatileRead read, bool suppressTransform, bool suppressOpacity, bool suppressOpacityIsSelf, bool forceTransformResync = false, bool forceOpacityResync = false)
    {
        // Split the single opacity-suppression flag into its two channels; at most one is ever active.
        var suppressModulate = suppressOpacity && !suppressOpacityIsSelf;
        var suppressSelfModulate = suppressOpacity && suppressOpacityIsSelf;
        var texPath = read.Texture?.ResourcePath ?? string.Empty;
        var textSig = TextSignature(read.Text);
        var shaderParamSig = ShaderParamSignature(read.ShaderParameters);
        var rangeValue = read.RangeValue ?? double.NaN;
        var changed = tracked.JustAdded
            // A just-closed suppression window forces one settled emit (the endpoint may equal the stale Last*).
            || forceTransformResync
            || forceOpacityResync
            || tracked.LastVisible != read.Visible
            // While a modulate fade is replayed client-side we suppress only the ALPHA streaming (the client pins the
            // opacity, so streaming it is waste) — but NOT the RGB tint: LastOpacity is the alpha trigger (dropped
            // when suppressed), while the LastModulate trigger below stays live on RGB so a white→gold tint change
            // still ships during the fade (else the node paints its untinted base until the settle re-emit).
            || (!suppressModulate && !NearlyEqual(tracked.LastOpacity, read.Opacity))
            || tracked.LastZ != (read.ZIndex ?? int.MinValue)
            || tracked.LastNinePatch != read.NinePatch
            || !string.Equals(tracked.LastTexture, texPath, StringComparison.Ordinal)
            // The atlas path is stable across animation frames, but the crop region changes per frame — compare it
            // so the new region is emitted (otherwise the icon would freeze on frame 1).
            || !RectEq(tracked.LastTextureRegion, read.TextureRegion)
            || !string.Equals(tracked.LastTextSig, textSig, StringComparison.Ordinal)
            // When suppressed, compare RGB only so the tint (white→gold) still emits while the alpha stays pinned.
            || (suppressModulate ? !ColorRgbEq(tracked.LastModulate, read.Modulate) : !ColorEq(tracked.LastModulate, read.Modulate))
            || (suppressSelfModulate ? !ColorRgbEq(tracked.LastSelfModulate, read.SelfModulate) : !ColorEq(tracked.LastSelfModulate, read.SelfModulate))
            || !ColorEq(tracked.LastFillColor, read.FillColor)
            || (!suppressTransform && !XformEq(tracked.LastTransform, read.Transform))
            || !RectEq(tracked.LastLocalRect, read.LocalRect)
            || !string.Equals(tracked.LastShaderParamSig, shaderParamSig, StringComparison.Ordinal)
            || tracked.LastParticleEmitting != read.ParticleEmitting
            // A hook-driven Restart() with Emitting already latched true (frozen node) has no emitting edge, so the
            // count change is its own trigger — otherwise the re-triggered burst never re-ships.
            || tracked.LastParticleRestartCount != read.ParticleRestartCount
            // Spine: emit on animation NAME change (idle→attack). Track time advances every tick but is
            // intentionally NOT in the change signature — it piggybacks on other emissions for this node
            // (transform/modulate churn in combat); the client free-runs the clip between updates.
            || !string.Equals(tracked.LastSpineAnim, read.SpineCurrentAnim, StringComparison.Ordinal)
            // Loop flag can flip with the name unchanged (looping fallback → real one-shot flag), so it's its
            // own trigger — without this the client keeps looping a finished one-shot (Regent weapon flicker).
            || tracked.LastSpineLooping != read.SpineLooping
            // Runtime skin change (or first known/unknown flip) → re-fetch the clip under the new `&skin=`.
            || !string.Equals(tracked.LastSpineSkin, read.SpineSkin, StringComparison.Ordinal)
            // Shader-material change (#8: the boss map point re-tints its normal_material as the act/travel state
            // changes) → re-fetch the clip under the new `&mat=`, else the first-baked tint is cached forever.
            || !string.Equals(tracked.LastSpineMat, read.SpineMat, StringComparison.Ordinal)
            // Paused flips when the game freezes/resumes a track (#13: the chest sits at frame 0 of "animation"
            // until it is opened) → the client must stop/start free-running the clip.
            || tracked.LastSpinePaused != read.SpinePaused
            // R12b: a pinned loop starting or stopping (the map point became travelable / was travelled to) is its
            // OWN trigger — the fold's whole premise is that nothing else about the node changes while it pulses,
            // so without this the client would never learn about a membership change.
            || !string.Equals(tracked.LastPinnedLoopAnim, read.PinnedLoopAnim, StringComparison.Ordinal)
            || Sts2ClickableFocus.Changed(tracked.LastFocused, read.Focused)
            || !NearlyEqualNaN(tracked.LastRangeValue, rangeValue);

        if (!changed)
        {
            return false;
        }

        // Bump the burst epoch on a real re-trigger BEFORE building the delta so the new epoch ships in this same
        // upsert. Two triggers: the hook-driven restart count (the authoritative path — survives a frozen node
        // whose latched Emitting produces no edge, and catches a Restart() that leaves Emitting unchanged), and the
        // emitting false→true edge as a fallback for any restart the hooks didn't observe.
        var emittingEdge = !tracked.LastParticleEmitting && read.ParticleEmitting;
        var restartCalled = read.ParticleRestartCount != tracked.LastParticleRestartCount;
        if (emittingEdge || restartCalled)
        {
            tracked.ParticleRestartEpoch++;
        }
        tracked.LastParticleEmitting = read.ParticleEmitting;
        tracked.LastParticleRestartCount = read.ParticleRestartCount;

        tracked.LastVisible = read.Visible;
        // Skip the tweened channel's Last* while it's suppressed (mirrors the transform gate) so the stale value
        // forces a re-emit on the settle frame right after the window closes.
        if (!suppressModulate)
        {
            tracked.LastOpacity = read.Opacity;
        }
        tracked.LastZ = read.ZIndex ?? int.MinValue;
        tracked.LastNinePatch = read.NinePatch;
        tracked.LastTexture = texPath;
        tracked.LastTextureRegion = read.TextureRegion;
        tracked.LastTextSig = textSig;
        // Always advance the full-RGBA snapshot (even while suppressed) so per-frame RGB change-detection converges
        // and a constant tint doesn't re-emit every frame. Only the ALPHA is suppressed (LastOpacity, above); the
        // settle alpha is re-shipped by the independent forceOpacityResync path when the window closes.
        tracked.LastModulate = read.Modulate;
        tracked.LastSelfModulate = read.SelfModulate;
        tracked.LastFillColor = read.FillColor;
        if (!suppressTransform)
        {
            tracked.LastTransform = read.Transform;
        }
        tracked.LastLocalRect = read.LocalRect;
        tracked.LastShaderParamSig = shaderParamSig;
        tracked.LastRangeValue = rangeValue;
        tracked.LastSpineAnim = read.SpineCurrentAnim;
        tracked.LastSpineLooping = read.SpineLooping;
        tracked.LastSpineSkin = read.SpineSkin;
        tracked.LastSpineMat = read.SpineMat;
        tracked.LastSpinePaused = read.SpinePaused;
        tracked.LastPinnedLoopAnim = read.PinnedLoopAnim;
        tracked.LastFocused = read.Focused;
        return true;
    }

    // Allocation-free change comparators (replace the old per-node interpolated-string signatures — the
    // per-frame GC/CPU hot spot when walking ~3300 nodes at fps). Same quantization the strings used: transforms
    // and rects rounded to 2 decimals, colors to 8-bit (matching the old Html-hex signature). null-vs-null is
    // equal; null-vs-value differs.
    private static bool QEq(double a, double b) => Math.Round(a, 2) == Math.Round(b, 2);
    private static bool ByteEq(double a, double b) => (int)Math.Round(a * 255.0) == (int)Math.Round(b * 255.0);

    private static bool XformEq(RuntimeSceneTransform2DSnapshot? a, RuntimeSceneTransform2DSnapshot? b)
    {
        if (a is null || b is null)
        {
            return ReferenceEquals(a, b);
        }

        return QEq(a.XAxis.X, b.XAxis.X) && QEq(a.XAxis.Y, b.XAxis.Y)
            && QEq(a.YAxis.X, b.YAxis.X) && QEq(a.YAxis.Y, b.YAxis.Y)
            && QEq(a.Origin.X, b.Origin.X) && QEq(a.Origin.Y, b.Origin.Y);
    }

    private static bool RectEq(RuntimeSceneRect2Snapshot? a, RuntimeSceneRect2Snapshot? b)
    {
        if (a is null || b is null)
        {
            return ReferenceEquals(a, b);
        }

        return QEq(a.Position.X, b.Position.X) && QEq(a.Position.Y, b.Position.Y)
            && QEq(a.Size.X, b.Size.X) && QEq(a.Size.Y, b.Size.Y);
    }


    private static bool ColorEq(RuntimeSceneColorSnapshot? a, RuntimeSceneColorSnapshot? b)
    {
        if (a is null || b is null)
        {
            return ReferenceEquals(a, b);
        }

        return ByteEq(a.R, b.R) && ByteEq(a.G, b.G) && ByteEq(a.B, b.B) && ByteEq(a.A, b.A);
    }

    // RGB-only color equality (alpha excluded). Used while an opacity fade is suppressed: the alpha is pinned
    // client-side, but the tint (R/G/B) must still stream so a white→gold modulate change isn't withheld until settle.
    private static bool ColorRgbEq(RuntimeSceneColorSnapshot? a, RuntimeSceneColorSnapshot? b)
    {
        if (a is null || b is null)
        {
            return ReferenceEquals(a, b);
        }

        return ByteEq(a.R, b.R) && ByteEq(a.G, b.G) && ByteEq(a.B, b.B);
    }

    // True when the SUPPRESSED opacity channel actually changed this capture (profiler-only, proves a real
    // opacity delta was withheld). Only the ALPHA is suppressed now (RGB tint keeps streaming), so this compares
    // alpha alone — modulate via LastOpacity, self via the SelfModulate alpha channel.
    private static bool OpacityChannelChanged(Tracked tracked, VolatileRead read, bool isSelf)
        => isSelf
            ? !NearlyEqual(tracked.LastSelfModulate?.A ?? read.SelfModulate?.A ?? 1.0, read.SelfModulate?.A ?? 1.0)
            : !NearlyEqual(tracked.LastOpacity, read.Opacity);

    // Signature of the NUMERIC shader uniforms only (the volatile ones) so an animating uniform re-emits the
    // node; sampler/color uniforms are static and excluded.
    private static string ShaderParamSignature(IReadOnlyList<RuntimeSceneShaderParamSnapshot>? parameters)
    {
        if (parameters is null)
        {
            return string.Empty;
        }
        var sig = string.Empty;
        foreach (var p in parameters)
        {
            if (p.Kind == "number")
            {
                sig += $"{p.Name}:{Math.Round(p.Number ?? 0, 4)}|";
            }
        }
        return sig;
    }

    private static bool NearlyEqualNaN(double a, double b)
        => (double.IsNaN(a) && double.IsNaN(b)) || NearlyEqual(a, b);

    private static string TextSignature(RuntimeSceneTextPropertiesSnapshot? text)
        => text is null
            ? string.Empty
            : $"{text.Text}{text.TextColor?.Html}{text.OutlineColor?.Html}{text.AppliedFontSize ?? text.FontSize}{text.Layout?.HorizontalAlignment}{text.Layout?.VerticalAlignment}";

    private static bool NearlyEqual(double a, double b) => Math.Abs(a - b) < 0.01;

    private static RuntimeSceneNodeDelta BuildNodeDelta(
        Tracked tracked,
        VolatileRead read,
        bool includeStatic,
        RuntimeSceneIntentFramesSnapshot? intentFrames = null,
        LineGeometry? lineGeometry = null)
    {
        var style = tracked.Style;
        return new RuntimeSceneNodeDelta(
            Id: tracked.IdStr,
            ParentId: tracked.ParentIdStr,
            Name: includeStatic ? tracked.Name : null,
            NodeType: includeStatic ? tracked.NodeType : null,
            Rect: null,
            Visible: read.Visible,
            Opacity: read.Opacity,
            ZIndex: read.ZIndex,
            Rotation: 0,
            Texture: read.Texture,
            NinePatch: read.NinePatch,
            Text: read.Text,
            // Volatile styling — every emission. WithHtml builds the deferred #RRGGBBAA string here (emitted nodes
            // only); no-op when it was formatted eagerly (kill-switch) or the color is null.
            Modulate: WithHtml(read.Modulate),
            SelfModulate: WithHtml(read.SelfModulate),
            FillColor: WithHtml(read.FillColor),
            RangeValue: read.RangeValue,
            RangeMin: read.RangeMin,
            RangeMax: read.RangeMax,
            // Static styling — add/keyframe only (default otherwise).
            ShowBehindParent: includeStatic && style.ShowBehindParent,
            ClipChildren: includeStatic ? style.ClipChildren : 0,
            // Static, and emitted ONLY when true — a false is the consumer default, so every non-clipping node's
            // wire is byte-identical. The kill-switch is applied here (not at probe time) so an embedder can flip
            // it on a running process; OFF restores the pre-feature wire exactly.
            ClipContents: includeStatic && style.ClipContents && Sts2SceneWatchRuntimeSettings.StreamClipContents,
            NinePatchMargins: includeStatic ? style.NinePatchMargins : null,
            Font: includeStatic ? style.Font : null,
            // Outline is VOLATILE (every emission): the game recolors a label's outline at runtime — e.g. the
            // combat HP label's outline turns blue while blocking — so read the live value from the per-tick
            // text snapshot rather than the once-on-add static probe (which would freeze it red).
            OutlineColor: read.Text?.OutlineColor,
            OutlineSize: read.Text?.OutlineSize,
            Shadow: includeStatic ? style.Shadow : null,
            RichText: includeStatic && style.RichText,
            Material: includeStatic ? style.Material : null,
            Shader: includeStatic ? style.Shader : null,
            // VOLATILE: the per-tick read refreshes numeric uniforms (animating transitions) over the on-add
            // list; emit every time so the client tracks the animation. Null for non-shader nodes.
            ShaderParameters: read.ShaderParameters,
            // Placement — every emission.
            Transform: read.Transform,
            LocalRect: read.LocalRect,
            FontWeight: includeStatic ? style.FontWeight : null,
            FontStyle: includeStatic ? style.FontStyle : null,
            // Atlas crop — volatile, every emission (region animates per frame for AnimatedSprite-style atlases).
            TextureRegion: read.TextureRegion,
            TextureMargin: read.TextureMargin,
            // Static (add/keyframe only): texture fit mode + horizontal/vertical flip for TextureRect.
            TextureStretchMode: includeStatic ? style.TextureStretchMode : null,
            TextureFlipH: includeStatic && style.TextureFlipH,
            TextureFlipV: includeStatic && style.TextureFlipV,
            // Static (add/keyframe only): non-Mix blend mode (Add/Sub/Mul). Null = Mix (default).
            CanvasBlendMode: includeStatic ? style.CanvasBlendMode : null,
            // Particle system: spec is STATIC (add/keyframe); emitting + restart epoch are VOLATILE (every tick)
            // so the client starts/re-triggers a one-shot burst without re-initialising the simulation.
            ParticleSpec: includeStatic ? style.ParticleSpec : null,
            ParticleEmitting: read.ParticleEmitting,
            ParticleRestartEpoch: tracked.ParticleRestartEpoch,
            // Spine: the canonical address + animation list are STATIC (add/keyframe only); the current
            // animation + track time are VOLATILE (every emission) so the client syncs clip playback.
            Spine: includeStatic ? style.Spine : null,
            SpineCurrentAnim: read.SpineCurrentAnim,
            SpineTrackTime: read.SpineTrackTime,
            SpineLooping: read.SpineLooping,
            // VOLATILE (#3): the runtime skin, so the client requests the correctly-skinned clip. Null = unknown
            // → the client omits `&skin=` and its URL stays byte-identical to today.
            SpineSkin: read.SpineSkin,
            // VOLATILE (#8): the shader-material signature, so the client requests the clip under the tint the
            // game currently has applied. Null = no shader material → the client omits `&mat=` (byte-identical URL).
            SpineMat: read.SpineMat,
            // VOLATILE (#13): the game froze this track (MegaAnimationState.SetTimeScale(0) — the closed chest),
            // so the client must HOLD the reported track time instead of free-running the clip off wall clock.
            SpinePaused: read.SpinePaused,
            // Static (add/keyframe only): Node.SceneFilePath — non-null only on an instanced-scene root, so the
            // client can identify which scene a node belongs to (and that scene's root id).
            SceneFilePath: includeStatic ? style.SceneFilePath : null,
            // Emitted with the static block (add/keyframe), but VALUE comes from the per-tick volatile read, not
            // the once-on-add static probe — many STS2 Controls fail the `node is Control` check at track time
            // (see ReadVolatile). Lets the client render Pass/Ignore nodes pointer-events:none. Null = non-Control.
            MouseFilter: includeStatic ? read.MouseFilter : null,
            // Enemy-intent glyph frame set: non-null ONLY on the glyph delta the capture loop chose to (re)ship it on
            // (keyframe/add or an intent-animation change). Null otherwise → MergeVolatile carries the retained set.
            IntentFrames: intentFrames,
            // Static (add/keyframe only): horizontal anchor fractions. Value comes from the per-tick volatile read
            // (same wrapped-Control gotcha as MouseFilter), gated to add/keyframe since anchors don't change at
            // runtime; MergeVolatile carries them forward between keyframes. Null = non-Control.
            AnchorLeft: includeStatic ? read.AnchorLeft : null,
            AnchorRight: includeStatic ? read.AnchorRight : null,
            // Static (add/keyframe only): the id of the control this floater is positioned from (NHoverTipSet's
            // owner). Resolved via reflection ONLY on the static block so the per-tick walk pays nothing; the
            // owner is set at instantiation and never changes, and MergeVolatile carries it forward. Null for
            // every node that isn't an owner-anchored floater.
            AnchorOwnerId: includeStatic ? ResolveAnchorOwnerId(tracked.Node) : null,
            // Static (add/keyframe only): BoxContainer orientation + packing alignment. Probed once on add in
            // DescribeStaticStyle; MergeVolatile carries it forward. Null for non-BoxContainer nodes.
            ContainerLayout: includeStatic ? style.ContainerLayout : null,
            // VOLATILE (R12b): the declarative infinite animation the producer pinned on this node, for the client
            // to replay. It only ever changes when the animation starts/stops, so shipping it on every emission is
            // free — and it MUST ride the volatile block so a stop (null) reaches a client that already heard the
            // start. Null for every node without a pinned loop.
            PinnedLoopAnim: read.PinnedLoopAnim,
            // Static (add/keyframe only, R13): a stable identity for the CONTENT this node shows (today: which card).
            // Resolved on every static emission rather than cached on the tracked node, because NCard visuals are
            // POOLED — the same instance id is a Strike now and a Bash after the next recycle, so an id-keyed cache
            // would go stale silently. MergeVolatile carries it forward between static emissions like every other
            // static field. Null for every non-card node (one cached-string compare on the static path).
            ContentKey: includeStatic ? ResolveContentKey(tracked) : null,
            // Static (add/keyframe only), mirroring `Font` exactly: the per-role rich-text theme fonts + their
            // sizes/glyph spacing, probed once on add in DescribeStaticStyle. Non-null only on a bbcode
            // RichTextLabel whose theme names a DIFFERENT font file / size / non-zero spacing for that role, so
            // this is null for every other node and MergeVolatile carries it forward between static emissions.
            RichBoldFont: includeStatic ? style.RichBoldFont : null,
            RichItalicFont: includeStatic ? style.RichItalicFont : null,
            RichBoldItalicFont: includeStatic ? style.RichBoldItalicFont : null,
            RichBoldFontSizePx: includeStatic ? style.RichBoldFontSizePx : null,
            RichItalicFontSizePx: includeStatic ? style.RichItalicFontSizePx : null,
            RichBoldItalicFontSizePx: includeStatic ? style.RichBoldItalicFontSizePx : null,
            RichBoldFontSpacingPx: includeStatic ? style.RichBoldFontSpacingPx : null,
            RichItalicFontSpacingPx: includeStatic ? style.RichItalicFontSpacingPx : null,
            RichBoldItalicFontSpacingPx: includeStatic ? style.RichBoldItalicFontSpacingPx : null,
            // Godot's own line breaking, on the STATIC path like the role fonts above — which is exactly why each
            // block carries its source length and hash: unlike a theme font, a wrap goes stale when the label's
            // words change on the per-tick path, and the consumer is required to detect that rather than trust it.
            TextLineRanges: includeStatic ? style.TextLineRanges : null,
            TextLineBasis: includeStatic ? style.TextLineBasis : null,
            TextParsedText: includeStatic ? style.TextParsedText : null,
            TextLineSourceLength: includeStatic ? style.TextLineSourceLength : null,
            TextLineSourceHash: includeStatic ? style.TextLineSourceHash : null,
            // WS-2 Line2D stroke geometry — non-null ONLY on the stroke deltas the capture loop chose to (re)ship it
            // on (keyframe/add or a changed stroke signature). All three ride together or not at all: a half-shipped
            // stroke (new points at the old width) would render wrong, and the client's MergeVolatile carries the
            // retained unit forward when they are null. An EMPTY LinePoints list is meaningful (the stroke was
            // cleared); null is "unchanged". See Sts2Line2DGeometryEmit.
            LinePoints: lineGeometry?.Points,
            LineWidth: lineGeometry?.Width,
            LineColor: WithHtml(lineGeometry?.Color),
            // VOLATILE: null for non-clickable nodes, otherwise the authoritative focus bit.
            Focused: read.Focused);
    }

    // One emitted Line2D stroke's geometry, resolved at EMIT time only. `Points` is already flattened + 2-dp rounded
    // (Sts2Line2DGeometryEmit.FlattenRounded); `Color` uses the deferred-html shape every other colour field does, so
    // BuildNodeDelta's WithHtml builds its `#RRGGBBAA` string on the one path that actually ships it.
    private sealed record LineGeometry(IReadOnlyList<double> Points, double Width, RuntimeSceneColorSnapshot? Color);

    // The CHEAP per-tick stroke signature, or null when this node cannot be read as a Line2D after all. Reads point
    // COUNT + the LAST point + width + default_color — never the point array, which is the entire point (an active
    // stroke reaches ~300 points and marshalling that every tick for every stroke is the cost this design avoids).
    //
    // Typed fast path for a plain `Line2D`; a `Get`/`Call` fallback for a SCRIPT-WRAPPED stroke, whose managed type is
    // the script's own class even though its native class is Line2D (the same wrapper hazard ResolveTextureProbe
    // exists for). Any failure returns null, which simply means "no geometry streamed for this node".
    private static string? ReadLineSignature(Node node)
    {
        try
        {
            int pointCount;
            double width;
            Color color;
            double lastX = 0;
            double lastY = 0;

            if (node is Line2D line)
            {
                pointCount = line.GetPointCount();
                width = line.Width;
                color = line.DefaultColor;
                if (pointCount > 0)
                {
                    var last = line.GetPointPosition(pointCount - 1);
                    lastX = last.X;
                    lastY = last.Y;
                }
            }
            else
            {
                pointCount = node.Call("get_point_count").AsInt32();
                width = node.Get("width").AsSingle();
                color = node.Get("default_color").AsColor();
                if (pointCount > 0)
                {
                    var last = node.Call("get_point_position", pointCount - 1).AsVector2();
                    lastX = last.X;
                    lastY = last.Y;
                }
            }

            return Sts2Line2DGeometryEmit.Signature(
                pointCount, width, $"#{color.ToHtml(includeAlpha: true)}", lastX, lastY);
        }
        catch
        {
            return null;
        }
    }

    // The FULL stroke geometry, marshalled ONCE per emitted delta (never per tick). Points come back in NODE-LOCAL
    // coordinates — the space `LocalRect` is in — and are flattened + rounded to 2 dp for the wire. Same typed
    // fast path / script-wrapper fallback split as ReadLineSignature.
    private static bool TryReadLineGeometry(Node node, out LineGeometry geometry)
    {
        geometry = null!;
        try
        {
            Vector2[] points;
            double width;
            Color color;

            if (node is Line2D line)
            {
                points = line.Points;
                width = line.Width;
                color = line.DefaultColor;
            }
            else
            {
                points = node.Get("points").AsVector2Array();
                width = node.Get("width").AsSingle();
                color = node.Get("default_color").AsColor();
            }

            var interleaved = new double[points.Length * 2];
            for (var i = 0; i < points.Length; i++)
            {
                interleaved[i * 2] = points[i].X;
                interleaved[(i * 2) + 1] = points[i].Y;
            }

            geometry = new LineGeometry(Sts2Line2DGeometryEmit.FlattenRounded(interleaved), width, VolatileColor(color));
            return true;
        }
        catch
        {
            return false;
        }
    }

    // A stable `nc:{entry}#{serial}` identity for the card this pooled NCard is currently showing, or null. Called on
    // the STATIC path only (add/keyframe/re-attach), so the typed read stays off the hot per-tick walk. See
    // Sts2ContentKey for why the serial is keyed on the MODEL rather than the node.
    private static string? ResolveContentKey(Tracked tracked)
    {
        if (!ContentKeys || !Sts2ContentKey.IsCardScene(tracked.Style.SceneFilePath))
        {
            return null;
        }

        // A TYPED read: `NCard.Model` is a plain C# property (a CardModel), NOT a Godot-registered script variable,
        // so `Node.Get("Model")` cannot see it — but bridge-mod links sts2.dll directly, so this is just a cast.
        return tracked.Node is NCard card && card.Model is { } model
            ? Sts2ContentKey.ForModel(model, model.Id?.Entry)
            : null;
    }

    // The instance id (as a decimal string, same id space as every streamed node) of an owner-anchored floater's
    // OWNER, or null. Today the one case is STS2's on-hover tooltip `NHoverTipSet`, positioned each frame from its
    // `_owner` control's GLOBAL rect while living on a persistent, un-anchored container — so the mirror's
    // wide-screen re-layout (which shifts only down the parent chain) would strand it at the owner's un-shifted
    // native x. Streaming the owner id lets the client apply the owner's horizontal shift to the whole tooltip
    // subtree. Called on the STATIC path only (add/keyframe), so the type-probe reflection stays off the hot walk.
    private static string? ResolveAnchorOwnerId(Node node)
    {
        if (!Sts2LiveIntrospection.IsType(node, "MegaCrit.Sts2.Core.Nodes.HoverTips.NHoverTipSet"))
        {
            return null;
        }
        return Sts2LiveIntrospection.GetMemberValue(node, "_owner") is GodotObject owner
            && GodotObject.IsInstanceValid(owner)
            ? owner.GetInstanceId().ToString()
            : null;
    }

    internal Task Dispatch(RuntimeSceneDelta delta, CaptureAdmission admission)
        => Task.Run(() =>
        {
            foreach (var subscriber in admission.Subscribers)
            {
                try
                {
                    if (Volatile.Read(ref _disposed) == 0 && subscriber.IsActive)
                        subscriber.Callback(delta);
                }
                catch
                {
                    // Subscriber failures are isolated from the watcher and the game thread.
                }
            }
        });

    internal sealed class SubscriberEntry(Action<RuntimeSceneDelta> callback)
    {
        private int _active = 1;
        internal Action<RuntimeSceneDelta> Callback { get; } = callback;
        internal bool IsActive => Volatile.Read(ref _active) != 0;
        internal void Deactivate() => Volatile.Write(ref _active, 0);
    }

    private sealed class Subscription(Sts2RuntimeSceneWatcher watcher, SubscriberEntry subscriber) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                watcher.Unsubscribe(subscriber);
            }
        }
    }

    // One prefix-producing SubViewport, discovered by Reconcile and re-evaluated once per capture pass.
    //
    // The prefix a SubViewport contributes is `display.GetGlobalTransformWithCanvas() * fit * superSample` (see
    // TryComputeViewportPrefix) — it embeds the LIVE on-screen transform of the node that displays the viewport
    // texture, so it changes whenever that node moves: scrolling the map slides the map's DrawViewportTextureRect,
    // and every quill stroke inside DrawViewport must slide with it. `Parent` is the enclosing viewport's chain node
    // (viewports can nest), composed on the LEFT exactly as ReconcileNode composed it.
    //
    // Deliberately holds the SubViewport (not a Transform2D copy per tracked node): a refresh writes ONE field here
    // and every node inside the viewport reads the new value through its `PrefixChain` reference, so the per-pass
    // cost is O(prefix-producing viewports) — a handful — not O(tracked nodes).
    private sealed class ViewportPrefixChain(SubViewport viewport, ViewportPrefixChain? parent, Transform2D prefix)
    {
        public readonly SubViewport Viewport = viewport;
        public readonly ViewportPrefixChain? Parent = parent;

        // Last successfully computed viewport→screen prefix. RETAINED when a refresh cannot resolve one (a consumer
        // mid-teardown): holding the last good value keeps the content where it was for a tick, whereas resetting to
        // identity would teleport a whole subtree to the design origin.
        public Transform2D Prefix = prefix;
    }

    private sealed class Tracked(Node node, ulong id, string name, string nodeType, StaticStyle style)
    {
        public Node Node = node;
        public ulong Id = id;
        public string IdStr = id.ToString();
        public ulong? ParentId;
        public string? ParentIdStr;
        // The parentId as of this node's LAST EMITTED upsert (Reconcile overwrites ParentIdStr every walk, so it can't
        // double as the "what the client last heard" baseline). A mismatch vs ParentIdStr means the node was
        // REPARENTED since the client last learned its parent — the capture loop then force-emits so the client
        // re-attaches it (see Sts2SceneWatchRuntimeSettings.EmitReparents). null until the first emit.
        public string? LastEmittedParentId;
        public string Name = name;
        public string NodeType = nodeType;
        public StaticStyle Style = style;
        public int Depth;
        public bool JustAdded;

        // Whether this node exposes NClickableControl's authoritative IsFocused property through Godot's dynamic
        // property bridge. Probed once on add; the per-tick path performs a Get only when this is true.
        public readonly bool FocusCapable = HasBoolProperty(node, ClickableFocusedProp);

        // R10 ORDER/UPSERT CONTRACT: has this node EVER been shipped to clients? A node born inside a hidden
        // subtree is registered (so it sits in `_ordered`) but PRUNED from every incremental capture, so it has no
        // upsert on the wire — while its id still rode the OrderedIds array. That array is the client's ONLY
        // structure trigger (`state.orderedIds !== lastOrderedIds` selects a structural walk), so the id was already
        // in the order when the node finally emitted on its visible-flip: the client merged the node into its map
        // with NO order change, took the volatile-only "update" walk, and never built an element for it. That is the
        // missing targeting arrow after a game restart and the missing treasure relics until a browser reload.
        // Distinct from `JustAdded` (which the emit block also clears) so the meaning stays readable: JustAdded is
        // "needs the static block", EverEmitted is "the client has heard of this node at all".
        public bool EverEmitted;

        // Late-static re-probe budget (#8). A dynamically-spawned SpineSprite (the treasure chest) has its
        // skeleton assigned a frame or more AFTER it enters the tree, so InspectStatic on add reads an EMPTY
        // animation list → ReadLive returns no anim → the client never fetches a clip → the chest never renders.
        // While Style.Spine has 0 animations we re-run InspectStatic for a bounded number of ticks; the first
        // re-probe that finds animations swaps in the richer snapshot and forces a keyframe-style upsert. Only a
        // spine root with an empty list ever consumes budget (rare), so this is near-free for normal creatures.
        public int SpineReprobesLeft = SpineReprobeMaxAttempts;

        // The skeleton-data resource instance id last seen by the re-probe gate (#13). 0 = "no skeleton assigned",
        // which is exactly the state a runtime-injected spine (chest / boss map point) is in at add. A CHANGE
        // re-arms SpineReprobesLeft; an unchanged 0 costs one native property Get and no budget, so waiting for a
        // late `MegaSprite.SetSkeletonDataRes` is unbounded in time instead of expiring after 30 ticks.
        public ulong SpineSkeletonProbeId;

        // A Spine SKELETON leaf (bone/slot/mesh) — native class contains "Spine" but it is NOT the SpineSprite clip
        // root (InspectStatic sets Style.Spine only on the root). Computed ONCE on add (class strings are invariant,
        // so this is freeze-timing-independent). Skeleton leaves are browser-inert (no texture/localRect/spine) and
        // carry no hook-driven state, so they are the safe scope for read-elision under a frozen, stationary root.
        public readonly bool IsSpineSkeletonLeafType = IsSpineSkeletonLeaf(node, style);

        // WS-2/WS4: the two structural facts the stroke-geometry stream is gated on, packed into ONE field so the
        // native probes run exactly once per add. Computed via the NATIVE class — like IsSpineSkeletonLeafType, and
        // for the same two reasons: class strings are invariant (so it is freeze-timing-independent) and IsClass sees
        // through a script-attached wrapper whose managed type is the SCRIPT's base (a card trail's Line2D IS
        // script-wrapped: `NCardTrail`).
        //   bit 0 — natively a `Line2D`. Every other node skips the per-tick stroke signature on this bool alone, so
        //           the walk's cost outside a Line2D is unchanged.
        //   bit 1 — and it is a MAP QUILL stroke, the only scope whose geometry may stream by default. A card trail's
        //           two Line2Ds are the same native class but get their look from a width curve / gradient /
        //           stretched texture / additive material that this unit deliberately drops, so shipping their points
        //           painted a solid bar behind every flying card (and re-marshalled 2 growing arrays per reshuffled
        //           card per tick). See Sts2Line2DGeometryEmit's SCOPE note.
        private readonly byte _lineKind = ClassifyLine(node, style);

        public bool IsLine2DType => (_lineKind & LineKindLine2D) != 0;

        public bool IsMapStrokeLine2D => (_lineKind & LineKindMapStroke) != 0;

        private const byte LineKindLine2D = 1;
        private const byte LineKindMapStroke = 2;

        // The stroke SIGNATURE last EMITTED for this node (Sts2Line2DGeometryEmit.Signature: point count + last point
        // + width + colour). A change is its own emit trigger — appending to a stroke changes nothing else about the
        // node that the volatile diff can see — and it is what makes the geometry STICKY: the full point array ships
        // only when this differs (or on a keyframe), and the client's MergeVolatile carries it forward in between.
        // Empty until the first emit, so a stroke's first delta always carries its geometry.
        public string LastLineSig = string.Empty;

        // ONE native class probe per node on add; the parent-name probe (the fallback for a stroke built with
        // `new Line2D()` rather than instanced from the authored scene, which therefore carries no SceneFilePath)
        // only runs for an actual Line2D whose scene identity did not already answer the question.
        private static byte ClassifyLine(Node node, StaticStyle style)
        {
            try
            {
                if (!node.IsClass("Line2D"))
                {
                    return 0;
                }
            }
            catch
            {
                return 0;
            }

            string? parentName = null;
            if (!Sts2Line2DGeometryEmit.IsMapStroke(style.SceneFilePath, parentName: null))
            {
                try
                {
                    parentName = node.GetParent()?.Name.ToString();
                }
                catch
                {
                    parentName = null;
                }
            }

            return Sts2Line2DGeometryEmit.IsMapStroke(style.SceneFilePath, parentName)
                ? (byte)(LineKindLine2D | LineKindMapStroke)
                : LineKindLine2D;
        }

        private static bool IsSpineSkeletonLeaf(Node node, StaticStyle style)
        {
            if (style.Spine is not null)
            {
                return false; // the SpineSprite clip root itself, not a skeleton leaf
            }

            try
            {
                return node.GetClass().Contains("Spine", StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        // Streaming suppression (Part C): while this deadline (Environment.TickCount64) is in the future, the node's
        // TRANSFORM deltas are suppressed because the mirror is REPLAYING a tween on it (or an ancestor) and pins the
        // transform client-side — so streaming it is pure waste. Set by ResolveTweenEndpoint for the tween's duration;
        // propagated to the subtree by depth in the capture loop. Never suppresses non-transform changes. 0 = off.
        public long SuppressTransformUntil;

        // R13 SELF-ONLY transform suppression. Same deadline semantics as SuppressTransformUntil, but TARGET-ONLY:
        // the node's OWN transform writes are withheld and its DESCENDANTS are untouched — no depth sentinel, so
        // everything below it keeps streaming exactly as before. The precedent is SuppressOpacityUntil (also
        // target-only, for the same reason: what is suppressed is written per-node). Set for a node whose own
        // transform reaches the screen through nothing — a boxless container the consumer cannot paint — while its
        // children still do (the card-flight trail root). Independent of SuppressTransformUntil: a node may hold
        // both, and each window arms its own settle re-emit. 0 = off.
        public long SuppressTransformSelfUntil;

        // Opacity streaming suppression (Part C Stage 4): while this deadline is in the future, the node's OWN alpha
        // deltas are suppressed because the mirror is REPLAYING a fade on it and pins the opacity client-side. Set by
        // ResolveTweenEndpoint for the fade's duration. TARGET-ONLY (opacity is per-node-local — no subtree
        // propagation, unlike SuppressTransformUntil). SuppressOpacityIsSelf picks the channel: false = modulate
        // (LastOpacity + LastModulate), true = self_modulate (LastSelfModulate); the untweened channel keeps
        // streaming. 0 = off.
        public long SuppressOpacityUntil;
        public bool SuppressOpacityIsSelf;

        // Viewport→screen prefix baked into this node's streamed global transform when it (or an ancestor) lives
        // inside a SubViewport displayed via a ViewportTexture (e.g. the multiplayer card-intent preview, the map
        // quill's DrawViewport). The node's own GetGlobalTransformWithCanvas() is viewport-LOCAL there; the prefix
        // maps it to the consumer's on-screen frame so the mirror doesn't paint it at (0,0).
        //
        // Held INDIRECTLY, as a reference to the shared chain node the enclosing SubViewport owns (null = no
        // enclosing prefix-producing viewport ⇒ identity). Reconcile rebuilds the chain objects; the per-capture
        // refresh then recomputes each chain ONCE and every node inside that viewport sees the new value for free.
        // That indirection is the whole fix for "map drawings don't follow the map": the prefix embeds the DISPLAY
        // node's live on-screen transform, which moves whenever the map scrolls, while Reconcile only runs on a
        // STRUCTURE-dirty tick — so a per-node copy went stale between structure changes and the strokes stayed
        // pinned to the screen until some unrelated node add (a HoverTip) forced a reconcile.
        public ViewportPrefixChain? PrefixChain;

        public Transform2D ViewportPrefix => PrefixChain?.Prefix ?? Transform2D.Identity;

        public bool LastVisible;
        public double LastOpacity;
        public int LastZ = int.MinValue;
        public string LastTexture = string.Empty;
        public bool LastNinePatch;
        public string LastTextSig = string.Empty;
        // Numeric change-tracking (allocation-free): hold the last-emitted snapshot records (already allocated by
        // ReadVolatile — no extra cost) and compare rounded components next frame, instead of building an
        // interpolated-string signature per node per capture (the old per-frame GC/CPU hot spot). Records are
        // immutable so retaining a reference is safe.
        public RuntimeSceneTransform2DSnapshot? LastTransform;
        public RuntimeSceneRect2Snapshot? LastLocalRect;
        public RuntimeSceneRect2Snapshot? LastTextureRegion;
        // R11 region-emit cap: the TickCount64 at which this node last let a same-size TextureRegion swap (a flame
        // flip-book frame) THROUGH. The cap pins same-size swaps in between, so the atlas frame ships at most once per
        // RegionEmitCapMs (10Hz default) instead of every tick. 0 = never emitted a region yet (first swap ships).
        public long LastRegionEmitAtMs;
        // R11 cosmetic-emit cap state. PrevRealTransform is the REAL (unsubstituted) transform read last tick — the
        // churn detector compares it to this tick's real read at full precision (finer than the 2-dp emit test) so a
        // continuously-flickering flame is seen as churning every tick even between 2-dp crossings. CosmeticChurnStreak
        // counts consecutive churn ticks (pacing engages at CosmeticChurnTicks; a stationary tick resets it, which is
        // what forces a settle emit). LastCosmeticEmitAtMs is the TickCount64 of the last paced transform emit; the cap
        // pins motion in between so the transform ships at most once per CosmeticEmitCapMs. 0 = never paced yet.
        public RuntimeSceneTransform2DSnapshot? PrevRealTransform;
        public int CosmeticChurnStreak;
        public long LastCosmeticEmitAtMs;
        // R12 decorative-animator folds (Sts2DecorEmitSuppress). DecorSceneFile/DecorRelPath are this node's SCENE
        // IDENTITY (nearest instanced-scene root + path below it), resolved in ReconcileNode ONLY while inside one of
        // the few watched scenes (null everywhere else, so the whole feature costs one dictionary probe per scene
        // root for the rest of the tree). DecorChannels is the resulting rule — which fold owns this node. No REST
        // SAMPLE is kept: every fold pins ANALYTICALLY (see the type comments), which is what makes them immune to
        // the pre-layout first-emitted pose that the original transform pin froze.
        public string? DecorSceneFile;
        public string? DecorRelPath;
        public Sts2DecorEmitSuppress.Channels DecorChannels;
        // R12b: instance id of the node's scene-instance ROOT while it is inside a watched scene (null otherwise).
        // Every fold's gates live on that root (NNormalMapPoint / NTopBarButton / NProceedButton), not on the
        // animated child itself.
        public ulong? DecorScopeRootId;
        // R12b: the loop name last EMITTED for this node (`RuntimeSceneNodeDelta.PinnedLoopAnim`), so a loop
        // starting/stopping is its own emit trigger — the whole point of a fold is that nothing else about the
        // node changes while it runs.
        public string? LastPinnedLoopAnim;
        public RuntimeSceneColorSnapshot? LastModulate;
        public RuntimeSceneColorSnapshot? LastSelfModulate;
        public RuntimeSceneColorSnapshot? LastFillColor;
        public double LastRangeValue = double.NaN;
        public string LastShaderParamSig = string.Empty;
        public bool LastParticleEmitting;
        // R15 spine-anchor fold: the TickCount64 of the last capture at which this node read `Emitting == true`.
        // A spine anchor above it refuses to fold until every emitter below it has been quiet for its own burst
        // tail, so a burst that ended THIS INSTANT — whose particles are still alive and still follow their
        // emitter's element — never has its mouth frozen out from under it. 0 = never seen emitting (the common
        // case: the tail check then passes immediately, since TickCount64 is uptime).
        public long LastEmittingAtMs;
        // R15 spine-anchor fold: does this node paint nothing of its own (so an ancestor anchor may freeze)? A
        // NATIVE class probe, resolved lazily the first time this node is scanned under a candidate anchor and
        // cached forever after (class strings are invariant). Unknown until then, so nodes outside every anchor
        // subtree — i.e. almost all of them — never pay the probe at all.
        public byte AnchorInertKind = InertUnknown;

        public const byte InertUnknown = 0;
        public const byte InertYes = 1;
        public const byte InertNo = 2;

        // Last-seen hook-driven restart count (Sts2ParticleRestartHooks); a change is a real Restart()/Emitting=true
        // call and bumps ParticleRestartEpoch even when a frozen node's Emitting produces no false→true edge.
        public long LastParticleRestartCount;
        // Monotonic burst counter, bumped on each real Restart() (hook-driven count change) or, as a fallback, on the
        // emitting false→true edge. Shipped as a volatile delta field so the client re-inits a one-shot burst
        // without re-attaching the whole sim.
        public long ParticleRestartEpoch;
        // Last streamed Spine animation name (track 0); a change is the emit trigger for clip switches.
        public string? LastSpineAnim;
        // Last streamed Spine loop flag. The flag can flip while the NAME stays the same — the fallback
        // presents a node's default as looping until its first animation_started arrives with the real flag
        // (e.g. a weapon's one-shot "attack") — so it's its own emit trigger, else the client never learns to
        // stop looping. Init true to match the looping fallback (so the first real loop=true causes no churn).
        public bool LastSpineLooping = true;
        // Last streamed runtime spine skin (#3). A change (the game swapped the applied skin, or it became
        // known/unknown) re-emits so the client re-fetches the clip under the new `&skin=`. Null until first set
        // (matching the "unknown skin" wire default), so a spine that never reports a skin never churns.
        public string? LastSpineSkin;
        public string? LastSpineMat;
        public bool LastSpinePaused;
        public bool? LastFocused;
        // Last streamed enemy-intent animation name (glyph nodes only). An intent change updates NIntent._animationName
        // on the CombatStateChanged signal even while the node is frozen, so the glyph's own volatile read may not
        // change — a change here is what forces the glyph delta (re-shipping its frame set). Null until first set.
        public string? LastIntentAnim;

        private static bool HasBoolProperty(Node node, StringName property)
        {
            try
            {
                var wanted = property.ToString();
                foreach (var info in node.GetPropertyList())
                {
                    if (!info.TryGetValue("name", out var nameValue)
                        || !info.TryGetValue("type", out var typeValue)
                        || !Sts2ClickableFocus.IsCapability(
                            nameValue.AsString(),
                            (Variant.Type)typeValue.AsInt64() == Variant.Type.Bool))
                    {
                        continue;
                    }

                    return string.Equals(nameValue.AsString(), wanted, StringComparison.Ordinal);
                }
            }
            catch
            {
                // A node being freed while the structure is reconciled is not focus-capable for this tenure.
            }

            return false;
        }
    }

    private sealed record VolatileRead(
        RuntimeSceneTransform2DSnapshot? Transform,
        RuntimeSceneRect2Snapshot? LocalRect,
        bool Visible,
        double Opacity,
        int? ZIndex,
        RuntimeSceneResourceRefSnapshot? Texture,
        bool NinePatch,
        RuntimeSceneTextPropertiesSnapshot? Text,
        RuntimeSceneColorSnapshot? Modulate,
        RuntimeSceneColorSnapshot? SelfModulate,
        RuntimeSceneColorSnapshot? FillColor,
        double? RangeValue,
        double? RangeMin,
        double? RangeMax,
        RuntimeSceneRect2Snapshot? TextureRegion,
        RuntimeSceneRect2Snapshot? TextureMargin,
        IReadOnlyList<RuntimeSceneShaderParamSnapshot>? ShaderParameters,
        bool ParticleEmitting,
        long ParticleRestartCount,
        string? SpineCurrentAnim,
        double SpineTrackTime,
        bool SpineLooping,
        string? SpineSkin,
        string? SpineMat,
        bool SpinePaused,
        int? MouseFilter,
        double? AnchorLeft,
        double? AnchorRight,
        bool? Focused,
        // R12b: the declarative infinite animation the producer pinned on this node (null for all but a pulsing
        // map point). Appended with a default so ReadVolatile's construction site is unchanged — only the
        // map-point fold's `with` expression ever sets it.
        string? PinnedLoopAnim = null);

    // Per-node STATIC styling — probed ONCE on add (the expensive text/material reflection the watcher
    // deliberately keeps off the per-tick path), reused on every keyframe/add. Text styling (font, shadow,
    // outline) does not change at runtime, so it never needs polling.
    private sealed record StaticStyle(
        bool ShowBehindParent,
        int ClipChildren,
        // Control.clip_contents (see DescribeStaticStyle). Sits beside ClipChildren because the two are read at the
        // same place and consumed together, but they are DIFFERENT Godot properties — see RuntimeSceneNodeDelta.
        bool ClipContents,
        RuntimeScenePatchMarginsSnapshot? NinePatchMargins,
        RuntimeSceneResourceRefSnapshot? Font,
        string? FontWeight,
        string? FontStyle,
        RuntimeSceneColorSnapshot? OutlineColor,
        double? OutlineSize,
        RuntimeSceneTextShadowSnapshot? Shadow,
        bool RichText,
        RuntimeSceneResourceRefSnapshot? Material,
        RuntimeSceneResourceRefSnapshot? Shader,
        IReadOnlyList<RuntimeSceneShaderParamSnapshot>? ShaderParameters,
        int? TextureStretchMode,
        bool TextureFlipH,
        bool TextureFlipV,
        int? CanvasBlendMode,
        RuntimeSceneParticleSpecSnapshot? ParticleSpec,
        RuntimeSceneSpineSnapshot? Spine,
        string? SceneFilePath,
        string? ContainerLayout,
        // Per-role rich-text theme fonts + their sizes/glyph spacing (bold / italics / bold-italics). Non-null only
        // on a bbcode RichTextLabel whose theme names a different font file (resp. a different size / non-zero
        // spacing) for that role — see Sts2RichRoleFontEmit and the wire doc on RuntimeSceneNodeDelta. Appended with
        // defaults so the (single) construction site stays readable.
        RuntimeSceneResourceRefSnapshot? RichBoldFont = null,
        RuntimeSceneResourceRefSnapshot? RichItalicFont = null,
        RuntimeSceneResourceRefSnapshot? RichBoldItalicFont = null,
        double? RichBoldFontSizePx = null,
        double? RichItalicFontSizePx = null,
        double? RichBoldItalicFontSizePx = null,
        double? RichBoldFontSpacingPx = null,
        double? RichItalicFontSpacingPx = null,
        double? RichBoldItalicFontSpacingPx = null,
        // Godot's own line breaking for this label — see RuntimeSceneNodeDelta for the wire contract and for why
        // the source length/hash are not optional.
        IReadOnlyList<int>? TextLineRanges = null,
        string? TextLineBasis = null,
        string? TextParsedText = null,
        int? TextLineSourceLength = null,
        int? TextLineSourceHash = null);
}
