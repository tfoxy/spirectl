using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Artifacts;
using Spirectl.Sts2.Core.Models;
using Spirectl.Sts2.Core.Perspective;
using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2.Core.Reference;
using Spirectl.Sts2.Core.SceneInspection;
using Spirectl.Sts2.Core.State;

namespace Spirectl.Sts2.Embedding;

public sealed record CurrentStateRequest(
    TimeSpan? Timeout = null,
    PerspectiveSelection? Perspective = null);

public sealed record CurrentStateResult(
    bool Success,
    StateSnapshot? State,
    EmbeddableRuntimeError? Error,
    EmbeddableRuntimeHealthSnapshot? Health = null);

/// <summary>
/// A semantic state subscription. <paramref name="MinCaptureInterval"/> is the FLOOR (the fastest the hub will
/// re-walk state for this subscriber, default 50 ms); <paramref name="MaxIdleInterval"/> and
/// <paramref name="IdleBackoff"/> control how far it slows down when nothing is changing.
/// </summary>
/// <param name="MaxIdleInterval">
/// Ceiling the capture interval doubles toward while the state fingerprint is unchanged. Null uses the process
/// default (<see cref="Live.Sts2StateWatchRuntimeSettings.MaxIdleInterval"/>, seeded from
/// <c>SPIRECTL_STATE_WATCH_MAX_IDLE_MS</c>, 400 ms). Always clamped into
/// <c>[MinCaptureInterval, Sts2StateWatchRuntimeSettings.MaxIdleCeilingMs]</c>: backoff may make an idle host
/// cheap, but it may not make a change arrive late.
/// </param>
/// <param name="IdleBackoff">
/// Opt this subscription out of idle backoff (<c>false</c> = capture at <paramref name="MinCaptureInterval"/>
/// forever, the pre-backoff pacing exactly). The process-wide
/// <see cref="Live.Sts2StateWatchRuntimeSettings.IdleBackoff"/> lever can disable backoff regardless; this field
/// can only ever turn it off, never force it on.
/// </param>
public sealed record CurrentStateSubscriptionRequest(
    TimeSpan? StateTimeout = null,
    bool EmitInitial = true,
    TimeSpan? MinCaptureInterval = null,
    int BufferCapacity = 16,
    PerspectiveSelection? Perspective = null,
    TimeSpan? MaxIdleInterval = null,
    bool IdleBackoff = true);

public sealed record CurrentStateWatchEvent(
    CurrentStateWatchEventType Type,
    ulong Sequence,
    DateTimeOffset ObservedAtUtc,
    string? SemanticFingerprint,
    ulong? SemanticRevision,
    StateSnapshot? State,
    EmbeddableRuntimeError? Error,
    EmbeddableRuntimeHealthSnapshot? Health);

public enum CurrentStateWatchEventType
{
    Initial,
    Changed,
    Error,
}

public sealed record CombatEventSubscriptionRequest(
    ulong SinceSequence = 0,
    ulong MaxEvents = 0,
    int BufferCapacity = 256);

public enum CombatWatchEventType
{
    Damage,
    Error,
    CardUpgrade,
    Vfx,
}

public sealed record CombatDamagePayload(
    string TargetCreatureId,
    int Amount,
    string? DealerCreatureId,
    string? SourceCardModelId);

public sealed record CombatCardUpgradePayload(
    string CardId,
    string CardModelId,
    string? SourceRelicModelId);

// Generic native-VFX backbone: one combat-event names the game scene a VFX node instances
// (e.g. "vfx/hit_spark_vfx"), captured at the common spawn seam. Anchor empty = centered.
public sealed record CombatVfxPayload(
    string ScenePath,
    string? AnchorCreatureId,
    int Amount);

public sealed record CombatWatchEvent(
    CombatWatchEventType Type,
    ulong Sequence,
    DateTimeOffset ObservedAtUtc,
    CombatDamagePayload? Damage,
    EmbeddableRuntimeError? Error,
    CombatCardUpgradePayload? CardUpgrade = null,
    CombatVfxPayload? Vfx = null);

public sealed record AnimationHintSubscriptionRequest(int BufferCapacity = 256);

/// <summary>
/// A lightweight timing hint emitted when the game CREATES a Godot property tween (one hint per animated
/// property). The consumer uses it ONLY to pre-arm a CSS transition's timing (duration + easing) for the
/// addressed node/property — it is NEVER a tween to replay. <c>Property</c> is the raw Godot property
/// string (<c>"modulate:a"</c>, <c>"scale"</c>, <c>"position"</c>, …); the bridge does NOT map Godot
/// props to CSS channels — the consumer does. <c>To</c> is the tween's target value as compact JSON, or
/// null (hints are timing-first). <c>Trans</c>/<c>Ease</c> are raw Godot <c>TransitionType</c>/<c>EaseType</c>
/// enum names (the per-tweener value, falling back to the tween-level default), or null.
/// <c>TargetInstanceId</c> is the tween target's <c>GodotObject.GetInstanceId()</c> — the SAME id space the
/// scene watcher keys nodes by (<c>RuntimeSceneNodeDelta.Id</c>) — so a consumer that renders off the scene
/// stream (the CouchCoop mirror) can join a hint to a live node by exact id instead of a scene-path suffix.
/// It is 0 when the target could not be resolved to a Godot object.
///
/// <para><c>EndTransform</c>/<c>EndOpacity</c> upgrade a timing hint into a REPLAYABLE one (Part C): the target
/// node's END GLOBAL <c>Transform2D</c> as a 6-tuple <c>[a,b,c,d,tx,ty]</c> in the watcher's streamed space
/// (a consumer rigidly propagates it across the target's subtree), or its END <c>modulate.a</c>. <c>Group</c>
/// ties one Godot tween's hints together. All null when the producer has no endpoint resolver, or for looping /
/// unsupported tweens — then the hint stays timing-only.</para>
///
/// <para><c>CardFlight</c> (WS-3) carries a DECLARATIVE, non-tween animation on the same fan-out: the
/// discard→draw shuffle card flight (<c>NCardFlyShuffleVfx</c>) is a per-frame async loop, not a Godot
/// <c>Tween</c>, so it has no tweener for the recorder to capture — but it IS closed-form, so the producer
/// ships its parameters and the consumer integrates it locally. It rides this record (rather than a second
/// hub + a second subscription API) because it is the same kind of signal with the same delivery guarantees:
/// hot, ephemeral, non-buffered, joined to a node by <c>TargetInstanceId</c>. When it is non-null the hint's
/// <c>Property</c> is the synthetic marker <see cref="CardFlightHint.PropertyMarker"/> and the
/// <c>EndTransform</c>/<c>EndOpacity</c> channels are unused.</para>
/// </summary>
public sealed record TweenAnimationHint(
    string Scene,
    string NodePath,
    string Property,
    string? To,
    double DurationMs,
    string? Trans,
    string? Ease,
    ulong TargetInstanceId = 0,
    IReadOnlyList<double>? EndTransform = null,
    double? EndOpacity = null,
    string? Group = null,
    // Fix 2: the tween's DECLARED start (`.From(...)`), same shape/space as End*; the consumer PRIMES the node here
    // (transition-less) before transitioning to the endpoint, so a re-anchored/primed node replays from its real
    // start (no 1-frame transient). Null unless the tween declared a start for the resolved channel.
    IReadOnlyList<double>? StartTransform = null,
    double? StartOpacity = null,
    // WS-3: a declarative, closed-form NON-tween animation (the discard→draw shuffle card flight). Null for every
    // ordinary tween hint. See CardFlightHint.
    CardFlightHint? CardFlight = null);

/// <summary>
/// The discard→draw shuffle card flight, as a declarative description the consumer REPLAYS on its own clock
/// instead of receiving ~60 streamed transform deltas per second per shuffled card. One short-lived "card soul"
/// node flies per shuffled card, advanced once per RENDERED frame rather than by a Godot <c>Tween</c>, so there is
/// nothing for the tween recorder to capture — but each soul's parameters are fixed when it spawns and never
/// change, which makes the whole ~3s animation exactly reproducible.
///
/// <para>SPACE. <c>Start</c>/<c>End</c>/<c>Control</c> are <c>[x, y]</c> points and <c>Basis</c> is an
/// <c>[a, b, c, d]</c> 2x2 column-major basis, all already mapped into the scene watcher's STREAMED space (the
/// same space <c>RuntimeSceneNodeDelta.Transform</c> and <c>TweenAnimationHint.EndTransform</c> live in) and always
/// GLOBAL — never re-based to a parent, even when the watcher is emitting local transforms, because the flight is
/// authored in Godot global space and its two piles sit under different parents.</para>
///
/// <para>REPLAY. Integrate the same two phases the host does (all in its pseudo-time unit, NOT wall clock — see
/// <c>Duration</c>): phase 1 steps <c>time += speed*dt; speed += accel*dt</c> and places the node at
/// <c>Bezier(Start, End, Control, time/Duration)</c> rotated to the curve's tangent plus a quarter turn; phase 2
/// steps <c>time += speed*dt</c> with the speed frozen and scales the node by
/// <c>max(lerp(0.1, -0.1, time/Duration), 0) / Scale0</c> at <c>End</c>; then the node's own
/// <c>modulate:a</c> fade (a real tween, delivered as an ordinary hint) takes it out. The composed streamed global
/// is <c>[Basis · R(rotation) · scale, position]</c>.</para>
///
/// <para>The producer opens a streaming-suppression window of <c>2*Duration + 0.8</c>s over the flight node's
/// subtree and over the trail's two <c>NCardTrail</c> strokes, so those nodes stop streaming per-frame transforms
/// for the whole animation. A consumer that ignores this hint therefore sees the CARD freeze — the two must be
/// switched together (see the <c>SPIRECTL_SCENE_WATCH_CARD_FLIGHT</c> /
/// <c>Sts2SceneWatchRuntimeSettings.CardFlightHints</c> kill-switch, which gates the hint and the window as one).
/// The trail VFX ROOT is frozen only in its OWN transform — its spark branch keeps streaming, so the comet follows
/// the card even for a consumer that drops the hint. See Sts2RuntimeSceneWatcher.ResolveCardFlight.</para>
///
/// <para>KIND. <c>Kind</c> says WHICH flight these numbers describe, so one fan-out carries both. Null — the
/// default, and everything above — is the shuffle sweep, whose flier is a throwaway visual that pops out of
/// existence at the far end. <c>"discard"</c> is the hand→discard fly, and its one structural difference is that
/// the moving node is the REAL CARD the player just played — the same element that was sitting in the hand a frame
/// ago, not a stand-in. On screen the card face leaves the hand, arcs to the discard pile shrinking as it goes, and
/// TURNS SMOOTHLY out of the angle it was already resting at (<c>Rot0</c>) into the curve's tangent instead of
/// snapping onto it: the player is watching the very card they just dragged, so a first-frame flick to a new angle
/// reads as a glitch — whereas the shuffle's flier appears from nothing and has no prior pose to preserve, which is
/// why <c>Rot0</c> is meaningless (and 0) there. A consumer that does not recognise a kind should replay it as the
/// shuffle flight it already knows.</para>
/// </summary>
public sealed record CardFlightHint(
    // `NCardTrailVfx` (the comet behind the card). The consumer does NOT drive this node — it keeps streaming — but
    // it needs the id to find the two `NCardTrail` strokes underneath it, whose synthesized ribbon it feeds from the
    // locally-integrated head instead of from the (now suppressed) streamed motion. 0 when the game made no trail
    // (test mode / no combat room).
    ulong TrailInstanceId,
    IReadOnlyList<double> Start,
    IReadOnlyList<double> End,
    // The quadratic control point, already resolved from the arc's drawn offset, its up/down branch and the two
    // anchors' midpoint. Resolved PRODUCER-side because that arithmetic (including its `endY < 540` branch) is
    // expressed in Godot's global design space, which the consumer does not have — see Sts2CardFlightMath.
    IReadOnlyList<double> Control,
    // The flight node's streamed 2x2 basis with its OWN rotation factored out and its resting scale folded in, so
    // the consumer composes `Basis · R(rotation) · (scale/Scale0)`.
    IReadOnlyList<double> Basis,
    // The flight's three drawn scalars, unchanged. `Duration` is in the integrator's pseudo-time unit (`time`
    // accumulates `speed*dt` with speed ≈ 1.1…1.25 and rising), NOT seconds.
    double Speed0,
    double Accel,
    double Duration,
    // The flight node's own RESTING uniform scale (the scene authors 1.0). Phase 2 ASSIGNS an absolute scale,
    // so the consumer's multiplier is `popScale / Scale0`.
    double Scale0,
    // Which flight this is: null (the default) = the shuffle sweep documented above; "discard" = the hand→discard
    // fly, whose mover is the real card. Appended with a default so every existing construction site is unchanged,
    // and left as a STRING (not an enum) because it crosses to the wire and a consumer must be free to ignore a
    // kind it does not know.
    string? Kind = null,
    // The target's on-screen rotation in radians at the moment of capture, in the same STREAMED space as everything
    // else here. It is the rotation-smoothing SEED of the "discard" replay: the card turns out of this pose into the
    // curve's tangent over the first part of the arc instead of jumping to it. Unused (0) for the shuffle flight.
    double Rot0 = 0)
{
    /// <summary>
    /// The synthetic <c>TweenAnimationHint.Property</c> a card-flight hint carries. Not a Godot property — the
    /// flight is not a property tween — so a consumer that only knows about real tween properties ignores it.
    /// </summary>
    public const string PropertyMarker = "card-flight";
}

// Subscription request for the live scene-delta stream. The watcher self-throttles (~60Hz) and
// self-dedups (it emits only when something changed), so this is reserved for future tuning knobs.
public sealed record RuntimeSceneSubscriptionRequest(
    bool EmitInitial = true,
    TimeSpan? MinCaptureInterval = null,
    int BufferCapacity = 16);

public sealed record EmbeddableRuntimeHealthSnapshot(
    bool HasCapturedMainThreadContext,
    int? CapturedThreadId,
    int CurrentThreadId,
    bool IsOnCapturedThread,
    int? DispatcherQueueDepth = null,
    DateTimeOffset? LastQueueDrainAtUtc = null,
    DateTimeOffset? LastSuccessfulRefreshAtUtc = null,
    ulong? LastSuccessfulRevision = null,
    string? ActiveRefreshOperationId = null,
    DateTimeOffset? ActiveRefreshStartedAtUtc = null,
    TimeSpan? ActiveRefreshElapsed = null,
    string? ActiveRefreshProjection = null,
    string? LastFailureReason = null,
    DateTimeOffset? LastFailureAtUtc = null,
    bool RetryAllowed = true,
    DateTimeOffset? RetryAfterUtc = null,
    bool? IsApplicationFocused = null,
    DateTimeOffset? LastFocusChangedAtUtc = null,
    string? DispatcherLiveness = null,
    string? Status = null,
    bool? Healthy = null,
    string? Message = null);

public sealed record EmbeddableActionRequest(
    string RequestId,
    SemanticActionKind Kind,
    string? PlayerId = null,
    string? CardId = null,
    string? TargetId = null,
    string? ChoiceId = null,
    string? CharacterId = null,
    string? MapNodeId = null,
    string? PotionId = null,
    int? MouseX = null,
    int? MouseY = null,
    RawMouseButtonKind? MouseButton = null,
    string? DisplayName = null,
    IReadOnlyDictionary<string, string>? Values = null,
    // Card ids to stage before confirming (confirm-selection / confirm-hand-selection) when the
    // client kept the selection device-local and folds staging + confirm into one call. Arrays can't
    // ride the scalar Values dict, so they need this dedicated field. Maps to SemanticActionRequest.CardIds.
    IReadOnlyList<string>? CardIds = null,
    // Element-addressed pointer input (hover-element / mouse-click by element): stable node instance id +
    // optional normalized 0..1 offset within its rect. Maps to SemanticActionRequest.ElementId/OffsetX/OffsetY.
    string? ElementId = null,
    double? OffsetX = null,
    double? OffsetY = null,
    // Keyboard input (key-input): browser KeyboardEvent.code, optional comma-separated modifiers, and the
    // pressed state (true=down, false=up, null=full press+release). Maps to SemanticActionRequest.Key/*.
    string? Key = null,
    string? KeyModifiers = null,
    bool? KeyPressed = null,
    // Mouse press state for mouse-click (drag support): true=button DOWN only (hold), false=button UP only
    // (release), null=a full click (down+up, the default). A held button turns the subsequent hover/move stream
    // into a drag. Maps to SemanticActionRequest.MousePressed.
    bool? MousePressed = null);

public sealed record EmbeddableActionResult(
    bool Success,
    ActionExecutionResult? Result,
    EmbeddableRuntimeError? Error);

public sealed record EmbeddableRuntimeError(
    string Code,
    string Message,
    string? Field = null,
    string? Value = null,
    bool Retryable = false,
    string? SuggestedNextStep = null);

/// <summary>
/// Convenience aggregate returned by runtime factories. Consumers should depend on the focused ports they use.
/// </summary>
public interface ISpirectlRuntime :
    IRuntimeCapabilitySource,
    IRuntimeAssetSource,
    IRuntimeStateSource,
    ICombatEventSource,
    IAnimationHintSource,
    IRuntimeSceneDeltaSource,
    IGameModelSource,
    IGameReferenceSource,
    ISpineCatalogSource,
    ISpineGeoClipBaker,
    ISemanticActionSource,
    IRuntimeSceneWatchControlSource
{
}
