using System.Reflection;
using Godot;
using HarmonyLib;
using Spirectl.Sts2.Core.Logging;

namespace Spirectl.Sts2.Live;

/// <summary>
/// WS-3: the DISCARD→DRAW SHUFFLE CARD FLIGHT producer. A Harmony postfix on
/// <c>MegaCrit.Sts2.Core.Nodes.Vfx.NCardFlyShuffleVfx._Ready</c> reads the scalars the flight has just drawn,
/// hands them to the scene watcher to be mapped into streamed space (and to open the streaming-suppression
/// windows), and publishes ONE declarative <see cref="Embedding.CardFlightHint"/> on the SAME
/// <see cref="Embedding.EmbeddableAnimationHintHub"/> the tween recorder uses.
///
/// <para>WHY THIS EXISTS AS A SEPARATE HOOK. <see cref="Sts2TweenRecorderHooks"/> patches Godot <c>Tween</c>
/// construction, and this animation is not a tween: it advances once per RENDERED frame, and only its trailing
/// 0.8s alpha fade is a real tween. So there is literally nothing
/// for the recorder to capture, and the mirror was left streaming every frame of every flight — a 34.8s wire
/// recording measured 598 KB (45%) of a shuffle's 1340 KB of upserts, and 1852 of its 3469 upserts, in this one
/// subtree, the only part of a shuffle that scales with the number of cards shuffled.</para>
///
/// <para>Discipline, matching every other telemetry hook here: installed unconditionally but INERT unless a hint
/// subscriber is attached (one volatile-bool read on the disabled path), never blocking, and never allowed to throw
/// into the game. The capture is DEFERRED to the next idle frame exactly like the tween recorder's finalize, both
/// because the watcher's node registry has not seen the freshly-added nodes yet at <c>_Ready</c> time and because
/// the trail VFX is attached during the same <c>_Ready</c>.</para>
/// </summary>
internal static class Sts2CardFlightHooks
{
    private const string LogTarget = "bridge.card-flight";
    private const string FlightTypeName = "MegaCrit.Sts2.Core.Nodes.Vfx.NCardFlyShuffleVfx";

    // The scalar handles and the trail-stroke scan are shared with the discard producer — see
    // Sts2CardFlightVfxProbe for why the reads live there rather than being cloned per hook.

    private static readonly object Sync = new();
    private static bool _installed;
    private static ILogStream? _log;

    // Every decline below is silent by design (this runs on the game main thread, once per flight spawn), so the
    // counters are the only way to answer "which gate ate the hint?" from outside. One interlocked add per spawn.
    private static ISts2AnimationHintCounter Counters => Sts2AnimationHintInstrumentation.Current.CardFlight;

    // Registered by Sts2RuntimeFactory, backed by the live scene watcher: maps the flight into the watcher's
    // streamed space AND opens the suppression windows. Null when no watcher is wired (tests / recording-only) —
    // then no hint is published at all, because a hint without its window is pure wire cost for a client that would
    // still be receiving every streamed frame.
    internal static Func<Sts2CardFlightResolveRequest, Embedding.CardFlightHint?>? FlightResolver { get => Sts2SceneAnimationCallbacks.ShuffleResolver; set => Sts2SceneAnimationCallbacks.ShuffleResolver = value; }

    public static long HintEmitCount => Sts2AnimationHintInstrumentation.Current.CardFlight.Emitted;

    public static void Install(ILogStream logStream)
    {
        lock (Sync)
        {
            if (_installed)
            {
                return;
            }

            _log = logStream;
            Sts2MonoModNativeDependencies.EnsureLoaded(logStream);

            var flightType = AccessTools.TypeByName(FlightTypeName);
            var target = flightType is null ? null : AccessTools.Method(flightType, "_Ready");
            var postfix = typeof(Sts2CardFlightHooks).GetMethod(
                nameof(ReadyPostfix),
                BindingFlags.NonPublic | BindingFlags.Static);
            if (target is null || postfix is null)
            {
                logStream.Write(
                    BridgeLogLevel.Warn,
                    LogTarget,
                    $"Skipping card-flight hook; {FlightTypeName}._Ready was not found.");
                return;
            }

            try
            {
                new Harmony("spirectl.card-flight").Patch(target, postfix: new HarmonyMethod(postfix));
            }
            catch (Exception ex)
            {
                logStream.Write(
                    BridgeLogLevel.Warn,
                    LogTarget,
                    $"Skipping card-flight hook because Harmony patching failed: {ex.GetType().Name}: {ex.Message}");
                return;
            }

            _installed = true;
            logStream.Write(
                BridgeLogLevel.Info,
                LogTarget,
                "Installed card-flight hook; discard→draw shuffle flights now ship as declarative hints "
                + "(SPIRECTL_SCENE_WATCH_CARD_FLIGHT=0 restores per-frame streaming).");
        }
    }

    // Harmony binds by name to the instance method, so `__instance` is the freshly-readied NCardFlyShuffleVfx.
    // Everything the flight needs has been drawn and its trail attached by the time this runs, and nothing has
    // moved yet — the animation's first frame is one rendered frame away.
    private static void ReadyPostfix(Node __instance)
    {
        // Inert unless someone is listening AND the feature is on. All plain volatile/bool reads. Checked one at a
        // time, in this order, purely so each decline lands in its OWN counter: "no hints arrived" used to be
        // indistinguishable from "no subscriber", "lever off" and "no watcher wired".
        if (!Embedding.EmbeddableAnimationHintHub.Shared.HasSubscribers)
        {
            Counters.RecordEarlyOutNoSubscribers();
            return;
        }

        if (!Sts2SceneWatchRuntimeSettings.CardFlightHints)
        {
            Counters.RecordEarlyOutLeverOff();
            return;
        }

        if (FlightResolver is null)
        {
            Counters.RecordEarlyOutNoResolver();
            return;
        }

        try
        {
            var flight = __instance;
            var startPos = flight.Get(Sts2CardFlightVfxProbe.StartPosProp).AsVector2();
            var endPos = flight.Get(Sts2CardFlightVfxProbe.EndPosProp).AsVector2();
            var controlPointOffset = flight.Get(Sts2CardFlightVfxProbe.ControlPointOffsetProp).AsSingle();
            var duration = flight.Get(Sts2CardFlightVfxProbe.DurationProp).AsSingle();
            var speed = flight.Get(Sts2CardFlightVfxProbe.SpeedProp).AsSingle();
            var accel = flight.Get(Sts2CardFlightVfxProbe.AccelProp).AsSingle();
            // The host derives `_arcDir` itself; read the LIVE field rather than recomputing it, so a future
            // game-side change to that branch can't silently desync the mirror. Sts2CardFlightMath.ArcDir is the
            // documented/unit-tested twin and the fallback if the field ever stops being readable (0 ⇒ a flat arc,
            // which is wrong but bounded, so prefer the recompute when the read is not finite).
            var arcDir = flight.Get(Sts2CardFlightVfxProbe.ArcDirProp).AsSingle();
            if (!double.IsFinite(arcDir))
            {
                arcDir = (float)Sts2CardFlightMath.ArcDir(endPos.Y, controlPointOffset);
            }

            // The flight VFX's scene root is a Control; its scale is uniform (`Vector2.One * s`) throughout.
            var scale0 = flight.Get(Control.PropertyName.Scale).AsVector2().X;
            // Recorded for the request's audit trail only — the resolver re-reads this live, alongside the transform
            // it has to divide it out of. See Sts2CardFlightResolveRequest.NodeRotation.
            var rotation = flight.Get(Control.PropertyName.Rotation).AsSingle();

            var trail = flight.Get(Sts2CardFlightVfxProbe.TrailVfxProp).AsGodotObject() as Node;
            var trailId = trail is not null && GodotObject.IsInstanceValid(trail) ? trail.GetInstanceId() : 0UL;
            var strokeIds = trailId == 0 ? [] : Sts2CardFlightVfxProbe.CollectTrailStrokeIds(trail!);

            var control = Sts2CardFlightMath.ControlPoint(
                new Sts2CardFlightMath.Point2(startPos.X, startPos.Y),
                new Sts2CardFlightMath.Point2(endPos.X, endPos.Y),
                arcDir);

            var request = new Sts2CardFlightResolveRequest(
                FlightInstanceId: flight.GetInstanceId(),
                TrailInstanceId: trailId,
                TrailStrokeInstanceIds: strokeIds,
                StartGlobal: startPos,
                EndGlobal: endPos,
                ControlGlobal: new Vector2((float)control.X, (float)control.Y),
                Speed0: speed,
                Accel: accel,
                Duration: duration,
                Scale0: scale0,
                NodeRotation: rotation);

            // DEFERRED, like Sts2TweenRecorderHooks' finalize: `_Ready` runs mid-add, so the watcher's registry has
            // not seen this node (nor the trail attached two lines above it) yet, and resolving now would find
            // neither. The idle-frame callback runs on the same game main thread as the capture walk.
            Callable.From(() =>
            {
                try
                {
                    Publish(request);
                }
                catch
                {
                    // Telemetry must never disrupt the game — but a swallowed throw is exactly the failure that
                    // used to be indistinguishable from "declined", so leave a count behind.
                    Counters.RecordPublishFailed();
                }
            }).CallDeferred();
        }
        catch
        {
            // Telemetry must never disrupt the game (counted, same reason as above).
            Counters.RecordPublishFailed();
        }
    }

    private static void Publish(Sts2CardFlightResolveRequest request)
    {
        if (FlightResolver is not { } resolve || resolve(request) is not { } flightHint)
        {
            // No watcher, kill-switch off, unresolvable node, or a degenerate draw → keep streaming. Counted as a
            // resolve miss: by the time the deferred callback runs, the gates above already passed once, so this
            // bucket rising while the early-out buckets stay flat points at TARGETING, not at wiring.
            Counters.RecordResolveNull();
            return;
        }

        PublishResolvedHint(flightHint, request.FlightInstanceId);
    }

    // The hub-publish tail, shared by BOTH ways a flight reaches the wire: this hook's own deferred resolve, and the
    // watcher's R14 PARKED-RESOLVE RETRY (a resolve that missed the node registry is re-attempted right after the
    // next reconcile and published from there — see Sts2CardFlightResolvePark). The hub reference and the emit
    // counter live here, so the retry hands its finished hint back rather than cloning either.
    //
    // `Emitted` is incremented HERE, once, on whichever path got the hint out: it is the count of hints actually on
    // the wire, and the retry's own bucket (ResolveRetryHit) is what says how it got there.
    internal static void PublishResolvedHint(Embedding.CardFlightHint flightHint, ulong flightInstanceId)
    {
        // Ride the tween hub. `Property` is the synthetic marker (this is not a property tween); `DurationMs` is the
        // flight's WALL-CLOCK suppression lifetime, i.e. exactly how long the consumer owns these nodes' transforms,
        // so a consumer that reads only the common fields still knows when the producer resumes streaming.
        Embedding.EmbeddableAnimationHintHub.Shared.Publish(new Embedding.TweenAnimationHint(
            Scene: "vfx/vfx_card_shuffle_fly",
            NodePath: ".",
            Property: Embedding.CardFlightHint.PropertyMarker,
            To: null,
            DurationMs: Sts2CardFlightMath.SuppressWindowMs(flightHint.Duration),
            Trans: null,
            Ease: null,
            TargetInstanceId: flightInstanceId,
            CardFlight: flightHint));

        var count = Counters.RecordEmitted();
        if (count == 1 || count == 25 || count == 500 || count == 10000)
        {
            _log?.Write(
                BridgeLogLevel.Info,
                LogTarget,
                $"Card-flight hint emitted {count} time(s); per-frame flight transforms are suppressed for "
                + "the flight's lifetime.");
        }
    }
}

// One resolved card flight, as read off the live instance. Godot-typed (Vector2), so it lives here with the hook
// rather than in the Godot-free Sts2CardFlightMath. A struct record purely to keep the ~11 fields addressable by
// name at the two call sites instead of an unreadable positional delegate.
internal readonly record struct Sts2CardFlightResolveRequest(
    ulong FlightInstanceId,
    ulong TrailInstanceId,
    IReadOnlyList<ulong> TrailStrokeInstanceIds,
    Vector2 StartGlobal,
    Vector2 EndGlobal,
    Vector2 ControlGlobal,
    double Speed0,
    double Accel,
    double Duration,
    double Scale0,
    // INTENTIONALLY UNUSED by the resolver as of R14, and kept only as the spawn-time record of what the node's own
    // angle was. The hint's basis divides the mover's rotation out of its streamed transform, and BOTH must be read
    // at the same instant or the basis is skewed by whatever the animation wrote in between — which is exactly what
    // a parked resolve retried after a later reconcile would do with a value captured back at `_Ready`. So the
    // rotation is re-read live, next to the transform, in the watcher's tracked-resolve core.
    double NodeRotation);
