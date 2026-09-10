using Godot;
using Spirectl.Sts2.Core.SceneInspection;

namespace Spirectl.Sts2.Live;

internal sealed partial class Sts2RuntimeSceneWatcher
{
    // Shares retained nodes with capture; owns all endpoint resolution and pending animation windows.
    private sealed class SceneAnimationCoordinator(
        Dictionary<ulong, Tracked> registry,
        ISts2ProducerWalkAccumulator profile,
        Func<bool> producerProfilingEnabled,
        Func<Tracked, double[]> emittedParentGlobalTuple)
    {
        private readonly Dictionary<ulong, Tracked> _registry = registry;
        private readonly ISts2ProducerWalkAccumulator _profile = profile;
        private readonly Func<bool> _producerProfilingEnabled = producerProfilingEnabled;
        private readonly Func<Tracked, double[]> _emittedParentGlobalTuple = emittedParentGlobalTuple;

        public void Reset()
        {
            _pendingFlightWindows.Clear();
            _flightResolvePark.Clear();
        }

        public bool TryTakePendingWindow(ulong id, out Sts2PendingFlightWindow window)
            => _pendingFlightWindows.Remove(id, out window);

        // WS-3 card-flight suppression windows for nodes that were NOT YET in the registry when the flight was resolved.
        // The `NCardFlyShuffleVfx._Ready` hook fires while the node is being ADDED to the tree, one reconcile before the
        // watcher has ever seen it (and its trail VFX is attached in the same `_Ready`), so the ordinary
        // "write tracked.SuppressTransformUntil" path would silently miss the very frames the window exists to cover.
        // Deadlines parked here are adopted by ReconcileNode the first time each node is tracked. Keyed by instance id;
        // bounded (see CardFlightPendingCap) and swept of expired entries on every insert, so a flight whose node never
        // appears (a shuffle cancelled the same frame) can never leak. The value carries the window's REACH as well as
        // its deadline (see Sts2PendingFlightWindow) — a trail root is parked SELF-ONLY, everything else subtree-wide.
        private readonly Dictionary<ulong, Sts2PendingFlightWindow> _pendingFlightWindows = [];

        // R14 card-flight RESOLVE park — the same "the hook fired before the walk had seen the node" race one level up.
        // The parked windows above cover a flight whose HINT resolved but whose nodes were not tracked yet; this covers
        // the flight whose hint could not be built AT ALL, because the mover itself was untracked when the deferred
        // `_Ready` publish ran. Instead of losing that hint for good, the request waits here and is retried by
        // DrainParkedFlightResolves immediately after the next Reconcile — the only moment the registry can have changed.
        // Bounded + TTL'd by the store (see Sts2CardFlightResolvePark); holds ids and numbers only, never node references.
        private readonly Sts2CardFlightResolveParkStore _flightResolvePark = new();

        // Hand a retried hint back to its producer's hub-publish tail. Wired by Sts2RuntimeFactory alongside the
        // resolvers; null (no hook installed / tests) ⇒ a successful retry is dropped and counted, never published
        // halfway. The watcher does not own a hub reference of its own precisely so there is ONE publish site per family.
        internal Action<Embedding.CardFlightHint, ulong>? ShuffleFlightPublisher;
        internal Action<Embedding.CardFlightHint, ulong>? DiscardFlightPublisher;

        // Part C declarative tween replay: resolve a captured tween's END state into the watcher's STREAMED transform
        // space, reading the live node on the game main thread. Called from the tween hook's DEFERRED (idle-frame)
        // Finalize — the same thread as the capture walk, so the Godot reads are legal. Returns null when the target
        // isn't a tracked CanvasItem or the change resolves to nothing. Only READS the node (never mutates), so it
        // cannot affect gameplay. Handles combined position + scale + rotation (Stage 2); modulate:a fades are Stage 4.
        // `durationMs` is the tween's duration (the client's CSS-transition length); when a transform endpoint resolves we
        // open a suppression window of that length so the redundant per-frame transform deltas the client would pin-and-
        // discard aren't streamed (see SuppressTweenedTransforms + the capture loop). Window starts here (≈ tween_start +
        // 1 frame) and thus ends BEFORE the client's later-armed pin releases → no end-of-tween snap-back.
        public TweenEndpoint? ResolveTweenEndpoint(ulong instanceId, TweenTargetChange change, double durationMs)
        {
            if (!change.HasAny || !_registry.TryGetValue(instanceId, out var tracked))
            {
                return null;
            }

            if (tracked.Node is not CanvasItem canvasItem)
            {
                return null;
            }

            // Only resolve a TRANSLATION endpoint. A scale-only / rotation-only tween (e.g. the targeting arrow head's
            // elastic scale pop) does not move the node, but TransformEndGlobal bakes the node's CURRENT origin into the
            // pinned 6-tuple; pinning it would freeze a translation the game may be driving per-frame independently
            // (arrow head tracks the cursor) until the window expires. Gating on HasPosition (not HasTransform) lets such
            // tweens keep streaming their live transform while their scale still animates via the streamed global.
            double[]? transform = null;
            double[]? startTransform = null;
            if (change.HasGlobalPosition)
            {
                // GLOBAL-space position tween (the shared main-menu reticle slides via `global_position:x`). The tween's
                // value is already in Godot global space, so instead of reconstructing a LOCAL transform we map global
                // points straight into the streamed space with M = gNow · GGT⁻¹ (= viewportPrefix · canvasTransform),
                // keeping the current basis (a global-position tween only translates). This is a fresh path — it does not
                // touch the local position/scale/rotation math below.
                var vp = tracked.ViewportPrefix;
                var gNow = vp * canvasItem.GetGlobalTransformWithCanvas();
                var ggt = canvasItem.GetGlobalTransform();
                if (ggt.Determinant() != 0f)
                {
                    var m = gNow * ggt.AffineInverse();
                    var curGlobal = ggt.Origin; // current global_position; the per-axis fallback for an :x-only / :y-only tween.
                    var endGlobal = new Vector2(
                        change.GlobalPosition?.X ?? change.GlobalPositionX ?? curGlobal.X,
                        change.GlobalPosition?.Y ?? change.GlobalPositionY ?? curGlobal.Y);
                    transform = GlobalOriginTuple(gNow, m, endGlobal);

                    if (change.StartGlobalPosition is not null
                        || change.StartGlobalPositionX is not null
                        || change.StartGlobalPositionY is not null)
                    {
                        var startGlobal = new Vector2(
                            change.StartGlobalPosition?.X ?? change.StartGlobalPositionX ?? curGlobal.X,
                            change.StartGlobalPosition?.Y ?? change.StartGlobalPositionY ?? curGlobal.Y);
                        startTransform = GlobalOriginTuple(gNow, m, startGlobal);
                    }
                }
            }
            else if (change.HasPosition)
            {
                var vp = tracked.ViewportPrefix;
                var gNow = vp * canvasItem.GetGlobalTransformWithCanvas();
                var parentCanvas = tracked.Node.GetParentOrNull<CanvasItem>();
                var pp = parentCanvas is not null ? vp * parentCanvas.GetGlobalTransformWithCanvas() : vp;

                // Read the node's CURRENT transform components; TransformEndGlobal overrides the tween-driven ones and
                // reconstructs the end LOCAL transform (Stage 2: combined position + scale + rotation). Pivot only
                // applies to Controls; a Node2D pivots at its origin.
                var pos0 = tracked.Node.Get("position").AsVector2();
                var rot0 = tracked.Node.Get("rotation").AsSingle();
                var scale0 = tracked.Node.Get("scale").AsVector2();
                var pivot = canvasItem is Control control ? control.PivotOffset : Vector2.Zero;

                transform = Sts2TweenEndpointMath.TransformEndGlobal(gNow, pp, pos0, rot0, scale0, pivot, change);

                // Fix 2: if the tween DECLARED a start (`.From(...)`), compute the START global the same way (live base for
                // untweened axes, the declared From for tweened ones) and ship it so the client primes the element there
                // before transitioning — a re-anchored/primed node's live sample is a stale transient, not its real start.
                // Additive: the END tuple above is unchanged; only a new start tuple is produced when a start exists.
                if (change.StartPosition is not null || change.StartPositionX is not null || change.StartPositionY is not null
                    || change.StartScale is not null || change.StartScaleX is not null || change.StartScaleY is not null
                    || change.StartRotation is not null)
                {
                    var startChange = new TweenTargetChange
                    {
                        Position = change.StartPosition,
                        PositionX = change.StartPositionX,
                        PositionY = change.StartPositionY,
                        Scale = change.StartScale,
                        ScaleX = change.StartScaleX,
                        ScaleY = change.StartScaleY,
                        Rotation = change.StartRotation,
                    };
                    startTransform = Sts2TweenEndpointMath.TransformEndGlobal(gNow, pp, pos0, rot0, scale0, pivot, startChange);
                }
            }

            // Only replay a fade that actually CHANGES the node's alpha. A whole-`modulate`/`self_modulate` TINT/flash
            // tween keeps alpha ≈ current (only rgb moves); arming a no-op opacity transition would needlessly pin the
            // node's opacity for the tween's duration. Exactly one channel is set per resolve (the anchor's channel):
            // `modulate` guards against the LIVE modulate.a, `self_modulate` against the LIVE self_modulate.a (both ≈1/255
            // tolerance). The end alpha rides the same TweenEndpoint.Opacity either way; the client tells them apart from
            // the hint's own `property` string.
            // Fix 2: guard the fade against the tween's DECLARED start (`.From(...)`) when it has one, else the LIVE alpha.
            // A re-anchored/shared node (the reticle) is stale at the PREVIOUS option's alpha (≈1), so a real fade-in
            // `From(0)→1` would read |1−1|<tol against the live value and be wrongly rejected; comparing against the
            // declared start (0) resolves it. When no start was declared, this is the original live-sample behavior.
            double? opacity = null;
            double? startOpacity = null;
            if (change.ModulateA is { } endAlpha
                && Math.Abs(endAlpha - (change.StartModulateA ?? canvasItem.Modulate.A)) > 0.004f)
            {
                opacity = endAlpha;
                if (change.StartModulateA is { } startAlpha)
                {
                    startOpacity = startAlpha;
                }
            }
            else if (change.SelfModulateA is { } endSelfAlpha
                && Math.Abs(endSelfAlpha - (change.StartSelfModulateA ?? canvasItem.SelfModulate.A)) > 0.004f)
            {
                opacity = endSelfAlpha;
                if (change.StartSelfModulateA is { } startSelfAlpha)
                {
                    startOpacity = startSelfAlpha;
                }
            }

            if (transform is null && opacity is null)
            {
                return null;
            }

            // Open the transform-suppression window only for a resolved TRANSFORM endpoint (the client pins transform, not
            // opacity). Best-effort: on any doubt (no transform, disabled, bad duration) we simply keep streaming.
            if (transform is not null && SuppressTweenedTransforms && durationMs > 0)
            {
                tracked.SuppressTransformUntil = System.Environment.TickCount64 + Math.Min((long)durationMs, SuppressCapMs);
                if (_producerProfilingEnabled())
                {
                    _profile.RecordSuppressWindow();
                }
            }

            // Independently, open the opacity-suppression window for a resolved OPACITY endpoint (the client pins the
            // target's opacity for the fade's duration, so its per-frame opacity deltas are pure waste). Target-only —
            // no subtree propagation. SuppressOpacityIsSelf records WHICH channel to drop so a modulate fade doesn't
            // suppress the untweened self_modulate (or vice-versa; exactly one channel resolves per call). A move+fade
            // tween resolves BOTH a transform and an opacity endpoint, opening the transform AND the opacity window.
            if (opacity is not null && SuppressTweenedOpacity && durationMs > 0)
            {
                tracked.SuppressOpacityUntil = System.Environment.TickCount64 + Math.Min((long)durationMs, SuppressCapMs);
                tracked.SuppressOpacityIsSelf = change.SelfModulateA is not null;
                if (_producerProfilingEnabled())
                {
                    _profile.RecordSuppressOpacityWindow();
                }
            }

            // LOCAL mode: the node's streamed Transform is emitted parent-relative, so a pinned endpoint must be too. Keep
            // the GLOBAL end/start tuples computed above (all the position/scale/rotation + global-position math is
            // unchanged), then re-base each against the emitted parent's global — the SAME parent + prefix the capture
            // walk uses (NOT the internal Godot parent-item local, which diverges across chain-breaks). Opacity is
            // space-agnostic. Identity parent (root) leaves the tuple unchanged. Read the knob directly (this runs on the
            // capture thread from the tween hook's deferred Finalize, outside Capture's per-tick latch).
            if (Sts2SceneWatchRuntimeSettings.EmitLocalTransforms)
            {
                var parentGlobal = _emittedParentGlobalTuple(tracked);
                if (transform is not null)
                {
                    transform = Sts2TweenEndpointTuples.LocalizeEndTuple(transform, parentGlobal);
                }

                if (startTransform is not null)
                {
                    startTransform = Sts2TweenEndpointTuples.LocalizeEndTuple(startTransform, parentGlobal);
                }
            }

            return new TweenEndpoint
            {
                Transform = transform,
                Opacity = opacity,
                // Only ship a start for the channel that actually resolved an endpoint (a start without an endpoint would
                // prime the element then never transition). Null start = client transitions from its committed value.
                StartTransform = transform is not null ? startTransform : null,
                StartOpacity = opacity is not null ? startOpacity : null,
            };
        }

        // Map a point in a node's GLOBAL space to a streamed-space 6-tuple [a,b,c,d,tx,ty], keeping gNow's basis (a
        // global-position tween only translates). `m` maps global points into streamed space (gNow · GGT⁻¹); Godot's
        // Transform2D * Vector2 is the affine point transform (basis·v + origin).
        private static double[] GlobalOriginTuple(Transform2D gNow, Transform2D m, Vector2 globalPoint)
        {
            var origin = m * globalPoint;
            return new double[] { gNow.X.X, gNow.X.Y, gNow.Y.X, gNow.Y.Y, origin.X, origin.Y };
        }

        // WS-3 DISCARD→DRAW SHUFFLE CARD FLIGHT. Resolve one `NCardFlyShuffleVfx`'s closed-form animation into the
        // watcher's STREAMED space and open the streaming-suppression windows that make the hint actually pay: the hint
        // alone changes nothing on the wire — it is this window that stops ~60 transform deltas/s per shuffled card.
        //
        // Called from the `_Ready` hook's DEFERRED (idle-frame) callback — the same thread as the capture walk, so the
        // Godot reads below are legal — and only READS the scene, so it cannot affect gameplay.
        //
        // SPACE. The flight is authored in Godot GLOBAL space (its two anchors are pile positions under different
        // parents, and its `endY < 540` arc branch is a design-space constant), so the three curve points are mapped
        // with the same M = gNow · GGT⁻¹ the global-position tween branch of ResolveTweenEndpoint uses. The result is
        // always a GLOBAL streamed value — deliberately NOT localized like a tween endpoint (see the LOCAL-mode block
        // in ResolveTweenEndpoint): the flight node crosses parents' spaces and the consumer composes one global pose
        // per frame, so re-basing it against a parent that is itself frozen by this very window would be circular.
        //
        // BASIS. The consumer needs `Basis · R(rotation) · scale`, so we hand it the node's streamed basis with the
        // node's OWN rotation divided out (its resting scale stays folded in — Scale0 is the divisor for the landing
        // phase's absolute scale assignment). A scene that authored a rotation would otherwise double-apply it — and the angle
        // MUST be read at the same instant as the transform it is divided out of (see ResolveTrackedCardFlight).
        //
        // Returns null (⇒ no hint, no window, today's streamed behaviour) when the kill-switch is off, the flight node
        // is not a tracked CanvasItem, its global transform is singular, or the drawn scalars are not integrable. R14:
        // the "not tracked yet" case is no longer terminal — it PARKS for a retry after the next reconcile (see
        // DrainParkedFlightResolves); this call still returns null, so the caller's behaviour is unchanged.
        public Embedding.CardFlightHint? ResolveCardFlight(
            ulong flightInstanceId,
            ulong trailInstanceId,
            IReadOnlyList<ulong> trailStrokeInstanceIds,
            Vector2 startGlobal,
            Vector2 endGlobal,
            Vector2 controlGlobal,
            double speed0,
            double accel,
            double duration,
            double scale0,
            // R14: intentionally NOT consumed — the basis is computed from the node's LIVE angle in
            // ResolveTrackedCardFlight. Kept in the signature (and in the request record) as the spawn-time record.
            double nodeRotation)
        {
            _ = nodeRotation;
            if (!Sts2SceneWatchRuntimeSettings.CardFlightHints)
            {
                return null;
            }

            if (!Sts2CardFlightMath.IsReplayable(speed0, accel, duration, scale0))
            {
                Sts2AnimationHintInstrumentation.Current.CardFlight.RecordResolveNotReplayable();
                return null;
            }

            if (!_registry.TryGetValue(flightInstanceId, out var flight))
            {
                // THE RACE (R14): the deferred `_Ready` publish beat the capture walk to this node. Park the request and
                // retry it after the next reconcile instead of losing the flight. Nothing is published on this pass, so
                // the hook records its resolve-miss exactly as before — the park buckets say what became of it.
                ParkFlightResolve(new Sts2ParkedFlightResolve(
                    Family: Sts2FlightFamily.Shuffle,
                    MoverInstanceId: flightInstanceId,
                    TrailInstanceId: trailInstanceId,
                    TrailStrokeInstanceIds: trailStrokeInstanceIds,
                    StartX: startGlobal.X,
                    StartY: startGlobal.Y,
                    EndX: endGlobal.X,
                    EndY: endGlobal.Y,
                    ControlX: controlGlobal.X,
                    ControlY: controlGlobal.Y,
                    Speed0: speed0,
                    Accel: accel,
                    Duration: duration,
                    Scale0: scale0,
                    ParkedAtMs: System.Environment.TickCount64,
                    DeadlineMs: Sts2CardFlightResolveParkStore.DeadlineFrom(System.Environment.TickCount64)));
                return null;
            }

            return ResolveTrackedCardFlight(
                flight,
                flightInstanceId,
                trailInstanceId,
                trailStrokeInstanceIds,
                startGlobal,
                endGlobal,
                controlGlobal,
                speed0,
                accel,
                duration,
                scale0);
        }

        // The tracked half of a shuffle resolve: everything that reads the LIVE node. Both entry points funnel through
        // here — the immediate resolve above and the parked retry — so the basis is computed in exactly one place.
        //
        // WHY THAT MATTERS (R14). The streamed basis and the angle divided out of it must be sampled at the SAME instant.
        // A retry runs one or more frames after the flight was created, by which time the animation may have written a
        // fresh angle into the node; dividing out the value captured back at `_Ready` would leave the difference folded
        // into the basis and the replayed card would fly tilted. So the angle is re-read here, next to the transform,
        // and never carried in from the request.
        private Embedding.CardFlightHint? ResolveTrackedCardFlight(
            Tracked flight,
            ulong flightInstanceId,
            ulong trailInstanceId,
            IReadOnlyList<ulong> trailStrokeInstanceIds,
            Vector2 startGlobal,
            Vector2 endGlobal,
            Vector2 controlGlobal,
            double speed0,
            double accel,
            double duration,
            double scale0)
        {
            if (flight.Node is not CanvasItem flightItem)
            {
                // Tracked but not drawable: no retry can change that (a node's class is fixed), so this stays terminal.
                return null;
            }

            var ggt = flightItem.GetGlobalTransform();
            if (ggt.Determinant() == 0f)
            {
                Sts2AnimationHintInstrumentation.Current.CardFlight.RecordResolveSingular();
                return null;
            }

            var gNow = flight.ViewportPrefix * flightItem.GetGlobalTransformWithCanvas();
            var m = gNow * ggt.AffineInverse();

            // Divide the node's own rotation out of the streamed basis: basis(θ) = K · R(θ) ⇒ K = basis(θ) · R(-θ), with
            // BOTH terms read from the live node right now.
            var nodeRotation = flightItem.Get(Control.PropertyName.Rotation).AsSingle();
            var unrotate = new Transform2D(-nodeRotation, Vector2.Zero);
            var flightBasis = new Transform2D(gNow.X, gNow.Y, Vector2.Zero) * unrotate;

            var windowMs = Sts2CardFlightMath.SuppressWindowMs(duration);
            if (windowMs > 0 && SuppressTweenedTransforms)
            {
                OpenCardFlightWindows(
                    moverInstanceId: flightInstanceId,
                    trailInstanceId: trailInstanceId,
                    trailStrokeInstanceIds: trailStrokeInstanceIds,
                    deadline: System.Environment.TickCount64 + windowMs);
            }

            var start = m * startGlobal;
            var end = m * endGlobal;
            var control = m * controlGlobal;
            return new Embedding.CardFlightHint(
                TrailInstanceId: trailInstanceId,
                Start: [start.X, start.Y],
                End: [end.X, end.Y],
                Control: [control.X, control.Y],
                Basis: [flightBasis.X.X, flightBasis.X.Y, flightBasis.Y.X, flightBasis.Y.Y],
                Speed0: speed0,
                Accel: accel,
                Duration: duration,
                Scale0: scale0);
        }

        // R13 HAND→DISCARD CARD FLY. Resolve one `NCardFlyVfx`'s closed-form animation into the watcher's STREAMED
        // space and open the streaming-suppression windows, exactly as ResolveCardFlight does for the shuffle sweep —
        // same curve, same integrator, same space map, same window policy. Called from the `_Ready` hook's DEFERRED
        // (idle-frame) callback, on the same thread as the capture walk, and only READS the scene.
        //
        // WHAT THE MOVER BEING THE REAL CARD CHANGES. The shuffle resolves against the VFX node, which the game created
        // a frame ago; this resolves against the CARD, which the player has been looking at all along. Two consequences:
        //
        //   * the registry lookup is a REAL gate. A card the watcher has never tracked is a card the consumer is not
        //     painting, so there is nothing to replay and nothing to freeze — fail open, keep streaming. (R14: "never
        //     tracked" is only decided after the parked retry has had its chances — see DrainParkedFlightResolves.)
        //
        //   * the basis is stripped of its FULL streamed rotation, not just the node's own. The replayed rotation
        //     channel is an on-screen (global) angle, so any rotation left folded into the basis — the fanned hand's
        //     tilt, say — would be applied twice and the card would fly sideways.
        //
        // Rot0 is that stripped angle handed back: the pose the card is resting in right now, which the replay turns
        // out of over the first part of the arc instead of snapping onto the curve's tangent.
        //
        // Returns null (⇒ no hint, no window, today's streamed behaviour) when either kill-switch is off, the card is
        // not a tracked CanvasItem, its global transform is singular, or the drawn scalars are not integrable.
        public Embedding.CardFlightHint? ResolveDiscardFlight(
            ulong cardInstanceId,
            ulong trailInstanceId,
            IReadOnlyList<ulong> trailStrokeInstanceIds,
            Vector2 startGlobal,
            Vector2 endGlobal,
            Vector2 controlGlobal,
            double speed0,
            double accel,
            double duration)
        {
            if (!Sts2SceneWatchRuntimeSettings.CardFlightHints
                || !Sts2SceneWatchRuntimeSettings.DiscardFlightHints)
            {
                return null;
            }

            if (!_registry.TryGetValue(cardInstanceId, out var card))
            {
                // THE RACE (R14), same as the shuffle's: park and retry after the next reconcile rather than lose the
                // fly. Rarer here — the card has usually been tracked for many ticks before it is played — but a card
                // that flies on the same frame it is created (a reward flying into the deck) hits exactly this.
                ParkFlightResolve(new Sts2ParkedFlightResolve(
                    Family: Sts2FlightFamily.Discard,
                    MoverInstanceId: cardInstanceId,
                    TrailInstanceId: trailInstanceId,
                    TrailStrokeInstanceIds: trailStrokeInstanceIds,
                    StartX: startGlobal.X,
                    StartY: startGlobal.Y,
                    EndX: endGlobal.X,
                    EndY: endGlobal.Y,
                    ControlX: controlGlobal.X,
                    ControlY: controlGlobal.Y,
                    Speed0: speed0,
                    Accel: accel,
                    Duration: duration,
                    // Unused for this family: the card's scale is part of its LIVE pose, so the tracked core reads it at
                    // resolve time rather than carrying a spawn-time copy.
                    Scale0: 0.0,
                    ParkedAtMs: System.Environment.TickCount64,
                    DeadlineMs: Sts2CardFlightResolveParkStore.DeadlineFrom(System.Environment.TickCount64)));
                return null;
            }

            return ResolveTrackedDiscardFlight(
                card,
                cardInstanceId,
                trailInstanceId,
                trailStrokeInstanceIds,
                startGlobal,
                endGlobal,
                controlGlobal,
                speed0,
                accel,
                duration);
        }

        // The tracked half of a discard resolve, the twin of ResolveTrackedCardFlight: everything that reads the LIVE
        // card. Both entry points funnel through here — the immediate resolve above and the parked retry — so the seed
        // angle and the scale are sampled from the card's pose AT RESOLVE TIME on both paths. That is the correctness
        // rule, not an optimisation: a retry publishes a replay that eases out of where the card is when the hint is
        // built, and a stale seed would show as a first-frame flick on a card the player is watching.
        private Embedding.CardFlightHint? ResolveTrackedDiscardFlight(
            Tracked card,
            ulong cardInstanceId,
            ulong trailInstanceId,
            IReadOnlyList<ulong> trailStrokeInstanceIds,
            Vector2 startGlobal,
            Vector2 endGlobal,
            Vector2 controlGlobal,
            double speed0,
            double accel,
            double duration)
        {
            if (card.Node is not CanvasItem cardItem)
            {
                // Tracked but not drawable: no retry can change that (a node's class is fixed), so this stays terminal.
                return null;
            }

            var ggt = cardItem.GetGlobalTransform();
            if (ggt.Determinant() == 0f)
            {
                Sts2AnimationHintInstrumentation.Current.CardDiscard.RecordResolveSingular();
                return null;
            }

            var gNow = card.ViewportPrefix * cardItem.GetGlobalTransformWithCanvas();

            // The card's on-screen angle right now: the X column's direction, i.e. the whole chain's rotation, not the
            // node's own `Rotation` property (which is only the last term of it).
            var rot0 = Math.Atan2(gNow.X.Y, gNow.X.X);

            // The card's own uniform scale. The consumer does NOT divide by it here (the discard's animated scale
            // channel is an absolute value written on a CHILD of the card, not a multiplier on the card itself), but a
            // parser is entitled to reject a zero, so ship the real one and let the replayability gate below refuse a
            // degenerate node.
            var scale0 = cardItem.Get(Control.PropertyName.Scale).AsVector2().X;
            if (!Sts2CardFlightMath.IsDiscardReplayable(speed0, accel, duration, scale0, rot0))
            {
                Sts2AnimationHintInstrumentation.Current.CardDiscard.RecordResolveNotReplayable();
                return null;
            }

            var m = gNow * ggt.AffineInverse();
            var unrotate = new Transform2D((float)-rot0, Vector2.Zero);
            var cardBasis = new Transform2D(gNow.X, gNow.Y, Vector2.Zero) * unrotate;

            var windowMs = Sts2CardFlightMath.SuppressWindowMs(duration);
            if (windowMs > 0 && SuppressTweenedTransforms)
            {
                OpenCardFlightWindows(
                    moverInstanceId: cardInstanceId,
                    trailInstanceId: trailInstanceId,
                    trailStrokeInstanceIds: trailStrokeInstanceIds,
                    deadline: System.Environment.TickCount64 + windowMs);
            }

            var start = m * startGlobal;
            var end = m * endGlobal;
            var control = m * controlGlobal;
            return new Embedding.CardFlightHint(
                TrailInstanceId: trailInstanceId,
                Start: [start.X, start.Y],
                End: [end.X, end.Y],
                Control: [control.X, control.Y],
                Basis: [cardBasis.X.X, cardBasis.X.Y, cardBasis.Y.X, cardBasis.Y.Y],
                Speed0: speed0,
                Accel: accel,
                Duration: duration,
                Scale0: scale0,
                Kind: "discard",
                Rot0: rot0);
        }

        // R14 — half one of the parked-resolve retry: hold a resolve whose mover the walk has not tracked yet.
        //
        // The only outcomes are "parked" and "refused at the cap", and both are counted, because this is the point where
        // a lost hint used to become invisible: the producer would return null, the hook would record one anonymous
        // resolve-miss, and nothing would say whether the node was missing, the draw was degenerate, or the wiring was
        // wrong. A refusal means the park is full of live entries, i.e. the drain is not keeping up — that flight keeps
        // streaming, which is the pre-R14 behaviour.
        private void ParkFlightResolve(Sts2ParkedFlightResolve entry)
        {
            var counters = FlightCountersFor(entry.Family);
            if (_flightResolvePark.TryPark(entry))
            {
                counters.RecordResolveParked();
                return;
            }

            counters.RecordResolveRetryDropped();
        }

        // R14 — half two: retry every parked resolve, immediately after the reconcile that may have made it resolvable.
        //
        // ONE SHOT PER TRACKING. An entry whose mover is still untracked stays parked (that is the whole point) and is
        // tried again next reconcile until its TTL runs out. An entry whose mover IS tracked leaves the park either way:
        // if the tracked resolve refuses, it refused on live state and would refuse identically next tick, and its own
        // bucket has already recorded why. That is what keeps the park from turning into a retry storm.
        //
        // NEVER RE-PARKS. The retry looks the mover up here rather than calling back through the public resolve entry
        // points, which would park the request again with a fresh deadline and make the TTL unreachable.
        public void DrainParkedFlightResolves()
        {
            if (_flightResolvePark.Count == 0)
            {
                return;
            }

            var now = System.Environment.TickCount64;

            // Expire first, so a stale entry is never resolved into a hint for an animation that has already finished.
            var expired = new List<Sts2ParkedFlightResolve>();
            if (_flightResolvePark.Sweep(now, expired) > 0)
            {
                foreach (var entry in expired)
                {
                    FlightCountersFor(entry.Family).RecordResolveRetryDropped();
                }
            }

            var live = _flightResolvePark.SnapshotLive(now);
            for (var i = 0; i < live.Count; i++)
            {
                var entry = live[i];
                var counters = FlightCountersFor(entry.Family);
                if (!FlightHintsEnabledFor(entry.Family))
                {
                    // The family's kill-switch went off while this request was parked. A lever has to be ABSOLUTE for an
                    // A/B to mean anything, so drop the entry rather than let a tail of hints cross the flip.
                    _flightResolvePark.Remove(entry.MoverInstanceId);
                    counters.RecordResolveRetryDropped();
                    continue;
                }

                if (!_registry.TryGetValue(entry.MoverInstanceId, out var mover))
                {
                    continue; // still untracked — leave it parked for the next reconcile
                }

                _flightResolvePark.Remove(entry.MoverInstanceId);
                try
                {
                    var start = new Vector2((float)entry.StartX, (float)entry.StartY);
                    var end = new Vector2((float)entry.EndX, (float)entry.EndY);
                    var control = new Vector2((float)entry.ControlX, (float)entry.ControlY);
                    var hint = entry.Family == Sts2FlightFamily.Discard
                        ? ResolveTrackedDiscardFlight(
                            mover,
                            entry.MoverInstanceId,
                            entry.TrailInstanceId,
                            entry.TrailStrokeInstanceIds,
                            start,
                            end,
                            control,
                            entry.Speed0,
                            entry.Accel,
                            entry.Duration)
                        : ResolveTrackedCardFlight(
                            mover,
                            entry.MoverInstanceId,
                            entry.TrailInstanceId,
                            entry.TrailStrokeInstanceIds,
                            start,
                            end,
                            control,
                            entry.Speed0,
                            entry.Accel,
                            entry.Duration,
                            entry.Scale0);
                    if (hint is null)
                    {
                        continue; // the tracked core refused on live state and counted its own reason
                    }

                    var publisher = entry.Family == Sts2FlightFamily.Discard
                        ? DiscardFlightPublisher
                        : ShuffleFlightPublisher;
                    if (publisher is null)
                    {
                        // A hint with nowhere to go. The suppression windows the resolve just opened are the reason this
                        // is counted rather than shrugged off: the nodes are frozen for the flight's lifetime and the
                        // consumer will never be told why. Only reachable with no hook installed (tests / recording).
                        counters.RecordResolveRetryDropped();
                        continue;
                    }

                    publisher(hint, entry.MoverInstanceId);
                    counters.RecordResolveRetryHit();
                }
                catch
                {
                    // This runs INSIDE the capture tick, so a throw here would take the whole producer down with it —
                    // the same discipline (and the same counter) as the hooks' own publish path.
                    counters.RecordPublishFailed();
                }
            }
        }

        private static ISts2AnimationHintCounter FlightCountersFor(Sts2FlightFamily family)
            => family == Sts2FlightFamily.Discard
                ? Sts2AnimationHintInstrumentation.Current.CardDiscard
                : Sts2AnimationHintInstrumentation.Current.CardFlight;

        // The same gate each family's resolve entry point applies, re-read at drain time (the discard rides BOTH levers).
        private static bool FlightHintsEnabledFor(Sts2FlightFamily family)
            => Sts2SceneWatchRuntimeSettings.CardFlightHints
                && (family != Sts2FlightFamily.Discard || Sts2SceneWatchRuntimeSettings.DiscardFlightHints);

        // The streaming-suppression policy both card flights share: WHICH nodes are frozen for the animation, and how
        // far each freeze REACHES.
        //
        //   * the MOVER, SUBTREE-wide — its descendants follow via the capture walk's depth sentinel, so this one
        //     window covers the whole card face (body, art, labels) riding on it. For the shuffle that mover is the
        //     flight VFX carrying a card silhouette; for the discard it is the played card itself. Either way it is a
        //     Control, so the mirror gives it a placed element the client can pin the whole pose onto.
        //
        //   * the two NCardTrail STROKES, each opened EXPLICITLY rather than inherited: they are the one part of this
        //     VFX that rewrites its own transform every frame INDEPENDENTLY of its parent (`NCardTrail` pins itself to
        //     the world origin, so in local-emit mode its local counter-moves the parent every single frame — the
        //     hardest-churning nodes of the whole effect). Freezing them is provably invisible: their global is the
        //     identity by construction and the mirror synthesizes the ribbon from the flight's motion, not from their
        //     transform.
        //
        //   * the trail VFX ROOT, SELF-ONLY (R13) — its own transform writes and nothing below it. The root is a
        //     boxless container, and the mirror only places nodes that have a box (Control rect / sprite / particle /
        //     line / spine — see the client's `placementBox`), so no pixel is ever painted from the root's own
        //     transform: it reached the screen only by being re-sent ~60 times a second. Measured on the R12 capture
        //     that was 1158 of the ~1.9k trail-subtree writes a 30-card shuffle produced — pure waste, and an order of
        //     magnitude more than the "one upsert per card per frame" this was previously estimated to cost.
        //
        //   * NOT the root's `Sprites` branch, deliberately. The sparks trailing the card are placed from that branch's
        //     OWN stream, so it has to keep flowing or the comet strands at the pile while the card flies on. That is
        //     exactly why the root's window is self-only and does not open the depth sentinel. A client that ignores
        //     the hint therefore still sees the sparks move; only the card freezes.
        private void OpenCardFlightWindows(
            ulong moverInstanceId,
            ulong trailInstanceId,
            IReadOnlyList<ulong> trailStrokeInstanceIds,
            long deadline)
        {
            OpenCardFlightWindow(moverInstanceId, deadline);
            for (var i = 0; i < trailStrokeInstanceIds.Count; i++)
            {
                OpenCardFlightWindow(trailStrokeInstanceIds[i], deadline);
            }

            // GLOBAL-emit mode opens this whenever the feature is on. LOCAL mode opens it only when the process-wide
            // TrailSelfSuppressLocalMode lever is also on, because there the consumer composes each node's transform
            // down its emitted parent chain and so must place the frozen root itself, from the declarative flight hint.
            // That lever is default-OFF and an embedder raises it only for a process whose viewers can all do that (the
            // CouchCoop server couples it to a unanimous viewer capability vote); otherwise this fails open — no window,
            // R12 behaviour, the root keeps streaming. Note the freeze stays confined to the root's OWN writes in either
            // mode: the capture walk stores every read node's REAL global for its children to re-base against, whether
            // or not that node emitted, so the sparks below are unaffected by the root going quiet.
            if (Sts2TrailWindowPolicy.ShouldOpenTrailSelfWindow(
                    Sts2SceneWatchRuntimeSettings.TrailSelfSuppress,
                    Sts2SceneWatchRuntimeSettings.EmitLocalTransforms,
                    Sts2SceneWatchRuntimeSettings.TrailSelfSuppressLocalMode))
            {
                OpenCardFlightWindow(trailInstanceId, deadline, selfOnly: true);
            }
        }

        // Arm one node's transform-suppression window for a card flight. A node already in the registry takes the
        // deadline directly; one the watcher has not seen yet (the flight node and its trail are created and hooked in
        // the SAME `_Ready`, before any reconcile) parks it in _pendingFlightWindows for ReconcileNode to adopt. Never
        // SHORTENS an existing window — a card whose flight overlaps another suppression keeps the later deadline.
        //
        // `selfOnly` picks the REACH: false (the default) freezes the node AND its subtree via the capture walk's depth
        // sentinel; true freezes the node's OWN transform writes and leaves every descendant streaming. The two live in
        // separate fields, so arming one never disturbs the other.
        private void OpenCardFlightWindow(ulong instanceId, long deadline, bool selfOnly = false)
        {
            if (instanceId == 0)
            {
                return;
            }

            if (_registry.TryGetValue(instanceId, out var tracked))
            {
                if (selfOnly)
                {
                    if (tracked.SuppressTransformSelfUntil < deadline)
                    {
                        tracked.SuppressTransformSelfUntil = deadline;
                    }
                }
                else if (tracked.SuppressTransformUntil < deadline)
                {
                    tracked.SuppressTransformUntil = deadline;
                }

                if (_producerProfilingEnabled())
                {
                    _profile.RecordSuppressWindow();
                }

                return;
            }

            if (_pendingFlightWindows.Count >= CardFlightPendingCap)
            {
                SweepPendingFlightWindows();
            }

            if (_pendingFlightWindows.Count >= CardFlightPendingCap)
            {
                return; // still full of live entries — fail open (this node just keeps streaming)
            }

            var incoming = new Sts2PendingFlightWindow(deadline, selfOnly);
            _pendingFlightWindows[instanceId] = _pendingFlightWindows.TryGetValue(instanceId, out var existing)
                ? Sts2PendingFlightWindow.Merge(existing, incoming)
                : incoming;
        }

        // Drop every parked window whose deadline has already passed (a flight whose node was freed before the watcher
        // ever tracked it). Only runs when the park is at its cap, so the steady-state cost is zero.
        private void SweepPendingFlightWindows()
        {
            var now = System.Environment.TickCount64;
            List<ulong>? expired = null;
            foreach (var (id, parked) in _pendingFlightWindows)
            {
                if (parked.Deadline <= now)
                {
                    (expired ??= []).Add(id);
                }
            }

            if (expired is null)
            {
                return;
            }

            foreach (var id in expired)
            {
                _pendingFlightWindows.Remove(id);
            }
        }

        // Fix 1: a recorded tween was KILLED/STOPPED (interrupted or superseded). For each of its target nodes still in
        // the registry, collapse any OPEN streaming-suppression window to the "just elapsed" sentinel (1): the next
        // capture tick treats it as window-closed (WindowClosed(1, now) is true for any real TickCount64), which un-pins
        // the node and forces exactly one settle re-emit shipping its LIVE transform/opacity. Without this, a tween
        // killed mid-flight would keep its node's per-frame stream frozen for the rest of the (up to SuppressCapMs)
        // window, stranding it at the stale pre-tween value. Runs on the game main thread (Harmony postfix), same thread
        // as the capture loop, so the field writes race nothing. Idempotent; only touches nodes with a live window.
        public void CancelTweenSuppression(IReadOnlyCollection<ulong> instanceIds, bool forceOpacityResync = false)
        {
            foreach (var id in instanceIds)
            {
                if (!_registry.TryGetValue(id, out var tracked))
                {
                    continue;
                }

                if (tracked.SuppressTransformUntil > 1)
                {
                    tracked.SuppressTransformUntil = 1;
                }

                // WS-REST: a killed fade-IN never opened an opacity window, so the collapse-open-window branch below is a
                // no-op for it. `forceOpacityResync` sets the just-elapsed sentinel (1) UNCONDITIONALLY so the next tick's
                // WindowClosed(1, now) forces exactly one settled opacity re-emit of the node's live alpha — un-sticking a
                // client whose hide-latch would otherwise clamp the plain resting-alpha re-show to 0.
                if (forceOpacityResync)
                {
                    if (tracked.SuppressOpacityUntil != 1)
                    {
                        tracked.SuppressOpacityUntil = 1;
                    }
                }
                else if (tracked.SuppressOpacityUntil > 1)
                {
                    tracked.SuppressOpacityUntil = 1;
                }
            }
        }

        // Is this node's TRANSFORM streaming currently suppressed by a live window — i.e. does a consumer still hold a
        // pin from a hint we published? The hand-tween hook asks before it decides that a sub-floor batch is safe to drop
        // (see Sts2HandTweenMath.CorrectiveDurationMs): dropping one is only harmless for a holder nobody is pinned to.
        // The "just elapsed" sentinel (1) deliberately reads as CLOSED, matching WindowClosed's own reading of it — that
        // node's settle re-emit is already queued for the next tick, so it needs no corrective hint.
        public bool HasOpenTransformWindow(ulong instanceId)
            => _registry.TryGetValue(instanceId, out var tracked)
                && tracked.SuppressTransformUntil > 1
                && tracked.SuppressTransformUntil > System.Environment.TickCount64;

    }
}
