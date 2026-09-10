using System.Reflection;
using Godot;
using HarmonyLib;
using Spirectl.Sts2.Core.Logging;

namespace Spirectl.Sts2.Live;

/// <summary>
/// R13: the HAND→DISCARD CARD FLY producer, the twin of <see cref="Sts2CardFlightHooks"/>. A Harmony postfix on
/// <c>MegaCrit.Sts2.Core.Nodes.Vfx.NCardFlyVfx._Ready</c> reads the scalars the fly has just drawn, hands them to
/// the scene watcher to be mapped into streamed space (and to open the streaming-suppression windows), and
/// publishes ONE declarative <see cref="Embedding.CardFlightHint"/> with <c>Kind</c> <c>"discard"</c> on the SAME
/// <see cref="Embedding.EmbeddableAnimationHintHub"/> the tween recorder uses.
///
/// <para>WHAT IS DIFFERENT FROM THE SHUFFLE. The shuffle sweep flies a throwaway stand-in that pops out of
/// existence at the far end; this flies the REAL CARD — the node the mirror was already streaming and painting
/// while it sat in the player's hand — from wherever the player let go of it to the discard pile, and frees it when
/// it lands. So the hint TARGETS the card (not the VFX node, which never moves), and it carries the card's current
/// on-screen angle as the seed the replay turns out of, because a first-frame flick onto the curve's tangent would
/// read as a glitch on a card the player is still watching.</para>
///
/// <para>Discipline, matching every other telemetry hook here: installed unconditionally but INERT unless a hint
/// subscriber is attached (one volatile-bool read on the disabled path), never blocking, and never allowed to throw
/// into the game. The capture is DEFERRED to the next idle frame exactly like the shuffle hook's, because the
/// watcher's node registry has not seen the freshly-added flight node yet at <c>_Ready</c> time and because the
/// trail VFX is attached during the same <c>_Ready</c>.</para>
///
/// <para>Every call site that flies a card this way — a reward flying into the deck, an upgrade, an enchant, a shop
/// purchase, the played card leaving the hand — comes through this one <c>_Ready</c>, so the hook catches them all
/// and the resolver's registry/replayability gates are the fail-open filter for the exotic ones (a card the watcher
/// never tracked simply keeps streaming).</para>
/// </summary>
internal static class Sts2DiscardFlightHooks
{
    private const string LogTarget = "bridge.card-discard";
    private const string FlightTypeName = "MegaCrit.Sts2.Core.Nodes.Vfx.NCardFlyVfx";
    private const string FlightScene = "vfx/vfx_card_fly";

    private static readonly object Sync = new();
    private static bool _installed;
    private static ILogStream? _log;

    // Every decline below is silent by design (this runs on the game main thread, once per fly spawn), so the
    // counters are the only way to answer "which gate ate the hint?" from outside. One interlocked add per spawn.
    private static ISts2AnimationHintCounter Counters => Sts2AnimationHintInstrumentation.Current.CardDiscard;

    // Registered by Sts2RuntimeFactory, backed by the live scene watcher: maps the fly into the watcher's streamed
    // space AND opens the suppression windows. Null when no watcher is wired (tests / recording-only) — then no
    // hint is published at all, because a hint without its window is pure wire cost for a client that would still
    // be receiving every streamed frame.
    internal static Func<Sts2DiscardFlightResolveRequest, Embedding.CardFlightHint?>? FlightResolver { get => Sts2SceneAnimationCallbacks.DiscardResolver; set => Sts2SceneAnimationCallbacks.DiscardResolver = value; }

    public static long HintEmitCount => Sts2AnimationHintInstrumentation.Current.CardDiscard.Emitted;

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
            var postfix = typeof(Sts2DiscardFlightHooks).GetMethod(
                nameof(ReadyPostfix),
                BindingFlags.NonPublic | BindingFlags.Static);
            if (target is null || postfix is null)
            {
                logStream.Write(
                    BridgeLogLevel.Warn,
                    LogTarget,
                    $"Skipping card-discard hook; {FlightTypeName}._Ready was not found.");
                return;
            }

            try
            {
                new Harmony("spirectl.card-discard").Patch(target, postfix: new HarmonyMethod(postfix));
            }
            catch (Exception ex)
            {
                logStream.Write(
                    BridgeLogLevel.Warn,
                    LogTarget,
                    $"Skipping card-discard hook because Harmony patching failed: {ex.GetType().Name}: {ex.Message}");
                return;
            }

            _installed = true;
            logStream.Write(
                BridgeLogLevel.Info,
                LogTarget,
                "Installed card-discard hook; hand→discard card flies now ship as declarative hints "
                + "(SPIRECTL_SCENE_WATCH_DISCARD_FLIGHT=0 restores per-frame streaming).");
        }
    }

    // Harmony binds by name to the instance method, so `__instance` is the freshly-readied flight VFX. Everything
    // the fly needs has been drawn and its trail attached by the time this runs, and the card has not moved yet —
    // the animation's first frame is one process frame away.
    private static void ReadyPostfix(Node __instance)
    {
        // Inert unless someone is listening AND the feature is on. All plain volatile/bool reads. Checked one at a
        // time, in this order, purely so each decline lands in its OWN counter.
        if (!Embedding.EmbeddableAnimationHintHub.Shared.HasSubscribers)
        {
            Counters.RecordEarlyOutNoSubscribers();
            return;
        }

        // Both levers land in the SAME bucket: either one being off means "this consumer asked for per-frame
        // streaming", and splitting them would only make the diagnostic harder to read.
        if (!Sts2SceneWatchRuntimeSettings.CardFlightHints || !Sts2SceneWatchRuntimeSettings.DiscardFlightHints)
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
            // Read the LIVE arc direction rather than recomputing it, so a future game-side change to how it is
            // chosen can't silently desync the mirror. Sts2CardFlightMath.DiscardArcDir is the documented,
            // unit-tested twin and the fallback if the field ever stops being readable — it needs the viewport
            // height, because the up/down decision is taken against the middle of the screen the fly plays in.
            var arcDir = flight.Get(Sts2CardFlightVfxProbe.ArcDirProp).AsSingle();
            if (!double.IsFinite(arcDir))
            {
                var viewportHeight = flight is CanvasItem canvas ? canvas.GetViewportRect().Size.Y : 0f;
                arcDir = (float)Sts2CardFlightMath.DiscardArcDir(endPos.Y, controlPointOffset, viewportHeight);
            }

            // The MOVER. The flight node itself never moves; it drives the card, so the card is what the hint
            // targets and what the watcher freezes. No card ⇒ nothing to describe.
            var cardId = Sts2CardFlightVfxProbe.ReadNodeId(flight, Sts2CardFlightVfxProbe.CardProp);
            if (cardId == 0)
            {
                Counters.RecordResolveNull();
                return;
            }

            var trail = flight.Get(Sts2CardFlightVfxProbe.TrailVfxProp).AsGodotObject() as Node;
            var trailId = trail is not null && GodotObject.IsInstanceValid(trail) ? trail.GetInstanceId() : 0UL;
            var strokeIds = trailId == 0 ? [] : Sts2CardFlightVfxProbe.CollectTrailStrokeIds(trail!);

            // Identical arc geometry to the shuffle sweep — same midpoint-plus-arc control point.
            var control = Sts2CardFlightMath.ControlPoint(
                new Sts2CardFlightMath.Point2(startPos.X, startPos.Y),
                new Sts2CardFlightMath.Point2(endPos.X, endPos.Y),
                arcDir);

            var request = new Sts2DiscardFlightResolveRequest(
                CardInstanceId: cardId,
                TrailInstanceId: trailId,
                TrailStrokeInstanceIds: strokeIds,
                StartGlobal: startPos,
                EndGlobal: endPos,
                ControlGlobal: new Vector2((float)control.X, (float)control.Y),
                Speed0: speed,
                Accel: accel,
                Duration: duration);

            // DEFERRED, like the shuffle hook's: `_Ready` runs mid-add, so the watcher's registry has not seen this
            // node (nor the trail attached inside the same `_Ready`) yet. The idle-frame callback runs on the same
            // game main thread as the capture walk.
            Callable.From(() =>
            {
                try
                {
                    Publish(request);
                }
                catch
                {
                    // Telemetry must never disrupt the game — but a swallowed throw is exactly the failure that
                    // would otherwise be indistinguishable from "declined", so leave a count behind.
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

    private static void Publish(Sts2DiscardFlightResolveRequest request)
    {
        if (FlightResolver is not { } resolve || resolve(request) is not { } flightHint)
        {
            // No watcher, kill-switch off, an untracked card, or a degenerate draw → keep streaming. Counted as a
            // resolve miss: by the time the deferred callback runs, the gates above already passed once, so this
            // bucket rising while the early-out buckets stay flat points at TARGETING, not at wiring.
            Counters.RecordResolveNull();
            return;
        }

        PublishResolvedHint(flightHint, request.CardInstanceId);
    }

    // The hub-publish tail, shared by BOTH ways a fly reaches the wire: this hook's own deferred resolve, and the
    // watcher's R14 PARKED-RESOLVE RETRY (see Sts2CardFlightResolvePark and the shuffle twin). `Emitted` is
    // incremented HERE, once, on whichever path got the hint out.
    internal static void PublishResolvedHint(Embedding.CardFlightHint flightHint, ulong cardInstanceId)
    {
        // Ride the tween hub. `Property` is the synthetic marker (this is not a property tween); `DurationMs` is the
        // fly's WALL-CLOCK suppression lifetime, i.e. exactly how long the consumer owns the card's transform, so a
        // consumer that reads only the common fields still knows when the producer resumes streaming.
        Embedding.EmbeddableAnimationHintHub.Shared.Publish(new Embedding.TweenAnimationHint(
            Scene: FlightScene,
            NodePath: ".",
            Property: Embedding.CardFlightHint.PropertyMarker,
            To: null,
            DurationMs: Sts2CardFlightMath.SuppressWindowMs(flightHint.Duration),
            Trans: null,
            Ease: null,
            TargetInstanceId: cardInstanceId,
            CardFlight: flightHint));

        var count = Counters.RecordEmitted();
        if (count == 1 || count == 25 || count == 500 || count == 10000)
        {
            _log?.Write(
                BridgeLogLevel.Info,
                LogTarget,
                $"Card-discard hint emitted {count} time(s); the flying card's per-frame transforms are "
                + "suppressed for the fly's lifetime.");
        }
    }
}

// One resolved discard fly, as read off the live instance. Godot-typed (Vector2), so it lives here with the hook
// rather than in the Godot-free Sts2CardFlightMath. Note what is NOT here: the card's own scale and its current
// on-screen angle, which the resolver reads straight off the card node it has already looked up.
internal readonly record struct Sts2DiscardFlightResolveRequest(
    ulong CardInstanceId,
    ulong TrailInstanceId,
    IReadOnlyList<ulong> TrailStrokeInstanceIds,
    Vector2 StartGlobal,
    Vector2 EndGlobal,
    Vector2 ControlGlobal,
    double Speed0,
    double Accel,
    double Duration);
