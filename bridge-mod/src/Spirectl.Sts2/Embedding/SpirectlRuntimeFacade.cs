using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Artifacts;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Models;
using Spirectl.Sts2.Core.Perspective;
using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2.Core.Reference;
using Spirectl.Sts2.Core.SceneInspection;
using Spirectl.Sts2.Core.State;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Spirectl.Sts2.Live;

namespace Spirectl.Sts2.Embedding;

public sealed class SpirectlRuntimeFacade : ISpirectlRuntime, IDisposable
{
    internal SpirectlRuntimeFacade(
        SpirectlRuntimeServices services,
        EmbeddableRuntimeOptions? options = null,
        ISpirectlAssetProvider? assets = null,
        IRuntimeSceneWatchControls? sceneWatchControls = null)
    {
        _services = services;
        _options = options ?? new EmbeddableRuntimeOptions();
        Assets = assets ?? CreateDefaultAssetProvider(services);
        SceneWatchControls = sceneWatchControls ?? UnsupportedRuntimeSceneWatchControls.Instance;
    }

    public ISpirectlAssetProvider Assets { get; }

    public IRuntimeSceneWatchControls SceneWatchControls { get; }

    private readonly RuntimeSubscriptionScope _subscriptions = new();
    private readonly CancellationTokenSource _lifetime = new();
    private int _disposed;
    private readonly SpirectlRuntimeServices _services;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        try { _subscriptions.Dispose(); }
        finally { _services.Lifetime?.Dispose(); }
    }

    private readonly EmbeddableRuntimeOptions _options;
    private EmbeddableStateSubscriptionHub<StateSnapshot, CurrentStateWatchEvent>? _stateSubscriptionHub;

    public EmbeddableRuntimeCapabilities GetCapabilities()
    {
        var actionSupported = _services.ActionHandler is not PlaceholderActionHandler;
        var assetSupported = _services.AssetExtractor is not PlaceholderAssetExtractProvider;
        var spineCatalogSupported = _services.SpineCatalogProvider is not PlaceholderAssetExtractProvider;
        var spineBakeSupported = _services.SpineGeoClipBaker is not PlaceholderAssetExtractProvider;
        var capabilities = new List<EmbeddableRuntimeCapability>
        {
            new("runtime-metadata", "Runtime metadata and capability discovery.", true, _services.Provisional, null),
            new("current-state", "Direct current semantic observation with timeout and runtime health metadata.", true, _services.Provisional, null),
            new("state", "Presentation runtime state envelope for browser scene evaluation.", true, _services.Provisional, null),
            new("state-subscriptions", "Reactive embedded semantic state subscriptions with callback and async-stream consumers.", true, _services.Provisional, null),
            new("state-watch", "Reactive state presentation envelope stream with duplicate suppression.", true, _services.Provisional, null),
            new(EmbeddableCapabilityIds.MultiplayerConnection, "Ordered native multiplayer connection observations for embedded hosts.", _services.MultiplayerConnectionSupported, !_services.MultiplayerConnectionSupported || _services.Provisional, _services.MultiplayerConnectionSupported ? null : "The runtime does not expose native multiplayer connection hooks."),
            new("combat-events", "Ordered, non-deduplicated transient combat-event stream (floating damage numbers) with sequence-based resume.", true, _services.Provisional, null),
            new("animation-hints", "Lightweight Godot-tween timing hints (scene/node/property + duration/easing) for pre-arming CSS transitions; subscribing enables the producer's cheap lite capture path.", true, _services.Provisional, null),
            new("scene-watch", "Reactive live runtime scene-tree stream (full /root subtree with live properties and computed transforms) with duplicate suppression.", true, _services.Provisional, null),
            new("scene-watch-controls", "Coupled controls for scene-stream replay optimizations.", SceneWatchControls is not UnsupportedRuntimeSceneWatchControls, SceneWatchControls is UnsupportedRuntimeSceneWatchControls || _services.Provisional, SceneWatchControls is UnsupportedRuntimeSceneWatchControls ? "The runtime does not expose live scene-watch controls." : null),
            new("game-models", "Immutable game model metadata for characters, relics, and future model families.", true, _services.Provisional, null),
            new("game-reference", "Topic-addressed game reference data (the StsColors palette, version/modding metadata, and other non-model constants).", true, _services.Provisional, null),
            new("semantic-actions", "Legality-backed semantic action execution.", actionSupported, !actionSupported || _services.Provisional, actionSupported ? null : "The runtime uses PlaceholderActionHandler."),
            new("asset-extraction", "Bridge-backed asset extraction entrypoint.", assetSupported, !assetSupported || _services.Provisional, assetSupported ? null : "The runtime uses PlaceholderAssetExtractProvider."),
            new("spine-catalog", "Enumerate canonical Spine scene, node, and playable animation entries.", spineCatalogSupported, !spineCatalogSupported || _services.Provisional, spineCatalogSupported ? null : "The runtime uses PlaceholderAssetExtractProvider."),
            new("spine-geoclip-bake", "Bake one Spine animation (or a single pose of it) to a geoclip mesh-geometry artifact directory.", spineBakeSupported, !spineBakeSupported || _services.Provisional, spineBakeSupported ? null : "The runtime uses PlaceholderAssetExtractProvider."),
            new("live-sts2-host", "Live STS2 host adapter integration.", _options.LiveSts2HostSupported, !_options.LiveSts2HostSupported || _services.Provisional, _options.LiveSts2HostSupported ? null : _options.LiveSts2HostUnsupportedReason),
        };

        return new EmbeddableRuntimeCapabilities(
            StateSnapshot.CurrentSchemaVersion,
            string.Empty,
            BridgeBuildInfo.BridgeVersion,
            "embedded",
            RuntimeAttachmentState.Attached,
            _services.Provisional ? DataSourceKind.Stub : DataSourceKind.Live,
            _services.Provisional,
            capabilities,
            actionSupported
                ? _services.SupportedActions ?? Sts2ActionDescriptorCatalog.Build(!_services.Provisional, dangerousMode: false)
                : []);
    }

    public CurrentStateResult GetCurrentState(CurrentStateRequest request)
    {
        try
        {
            var resolvedPerspective = _services.PerspectiveProvider.Resolve(request.Perspective);
            // A runtime composed without a state provider cannot observe the game, and says so below. It used to
            // fabricate a snapshot instead — root scene "screens/main_menu", no character select, no run — which
            // is byte-identical to what the live provider emits at a REAL main menu, so a composition failure was
            // indistinguishable from a game sitting at its title screen. A consumer that trusted it concluded
            // "not in a lobby, not in a run" forever; the only honest answer is the state-unavailable failure.
            var state = _services.StateProvider?.Observe(resolvedPerspective);
            var health = CaptureRuntimeHealth();
            if (state is null)
            {
                return new CurrentStateResult(
                    false,
                    null,
                    CurrentObservationError(
                        health,
                        "state-unavailable",
                        "Runtime state is unavailable."),
                    health);
            }

            return new CurrentStateResult(true, state, null, health);
        }
        catch (Exception ex)
        {
            var health = CaptureRuntimeHealth();
            return new CurrentStateResult(
                false,
                null,
                CurrentObservationError(
                    health,
                    "runtime-state-failed",
                    ex.Message),
                health);
        }
    }

    public MultiplayerConnectionSnapshot? GetCurrentMultiplayerConnection()
        => _services.MultiplayerConnectionSupported
            ? EmbeddableMultiplayerConnectionHub.Shared.GetCurrent()
            : null;

    public IDisposable SubscribeMultiplayerConnection(Action<MultiplayerConnectionSnapshot> onEvent)
    {
        ArgumentNullException.ThrowIfNull(onEvent);
        return _services.MultiplayerConnectionSupported
            ? _subscriptions.Subscribe(() => EmbeddableMultiplayerConnectionHub.Shared.Subscribe(onEvent))
            : NoopDisposable.Instance;
    }

    public IDisposable SubscribeCurrentState(
        CurrentStateSubscriptionRequest request,
        Action<CurrentStateWatchEvent> onEvent,
        Action<EmbeddableRuntimeError>? onError = null)
        => _subscriptions.Subscribe(() => StateSubscriptionHub.Subscribe(
            new StateProjection(request.Perspective, request.StateTimeout),
            request.EmitInitial,
            request.MinCaptureInterval,
            request.MaxIdleInterval,
            request.IdleBackoff,
            onEvent,
            onError));

    public async IAsyncEnumerable<CurrentStateWatchEvent> WatchCurrentStateAsync(
        CurrentStateSubscriptionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var capacity = request.BufferCapacity <= 0 ? 16 : request.BufferCapacity;
        var channel = Channel.CreateBounded<CurrentStateWatchEvent>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        using var runtimeRegistration = _lifetime.Token.Register(() => channel.Writer.TryComplete());
        using var registration = cancellationToken.Register(() => channel.Writer.TryComplete());
        using var subscription = SubscribeCurrentState(
            request,
            evt => channel.Writer.TryWrite(evt),
            error => channel.Writer.TryWrite(new CurrentStateWatchEvent(
                CurrentStateWatchEventType.Error,
                0,
                DateTimeOffset.UtcNow,
                BuildErrorFingerprint(error),
                null,
                null,
                error,
                CaptureRuntimeHealth())));

        await foreach (var evt in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return evt;
        }
    }

    public IDisposable SubscribeCombatEvents(
        CombatEventSubscriptionRequest request,
        Action<CombatWatchEvent> onEvent,
        Action<EmbeddableRuntimeError>? onError = null)
        => _subscriptions.Subscribe(() => EmbeddableCombatEventHub.Shared.Subscribe(request.SinceSequence, onEvent));

    public async IAsyncEnumerable<CombatWatchEvent> WatchCombatEventsAsync(
        CombatEventSubscriptionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var capacity = request.BufferCapacity <= 0 ? 256 : request.BufferCapacity;
        var channel = Channel.CreateBounded<CombatWatchEvent>(new BoundedChannelOptions(capacity)
        {
            // Drop oldest under extreme backpressure rather than block the game thread; the
            // ring buffer + SinceSequence resume (and Phase-3's per-client queue) carry the
            // non-loss guarantee.
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        using var runtimeRegistration = _lifetime.Token.Register(() => channel.Writer.TryComplete());
        using var registration = cancellationToken.Register(() => channel.Writer.TryComplete());
        using var subscription = SubscribeCombatEvents(request, evt => channel.Writer.TryWrite(evt));

        var emitted = 0UL;
        await foreach (var evt in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return evt;
            emitted++;
            if (request.MaxEvents > 0 && emitted >= request.MaxEvents)
            {
                yield break;
            }
        }
    }

    public IDisposable SubscribeAnimationHints(
        AnimationHintSubscriptionRequest request,
        Action<TweenAnimationHint> onHint,
        Action<EmbeddableRuntimeError>? onError = null)
    {
        try
        {
            return _subscriptions.Subscribe(() => EmbeddableAnimationHintHub.Shared.Subscribe(onHint));
        }
        catch (Exception ex)
        {
            onError?.Invoke(new EmbeddableRuntimeError("animation-hints-subscribe-failed", ex.Message));
            return NoopDisposable.Instance;
        }
    }

    public async IAsyncEnumerable<TweenAnimationHint> WatchAnimationHintsAsync(
        AnimationHintSubscriptionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var capacity = request.BufferCapacity <= 0 ? 256 : request.BufferCapacity;
        var channel = Channel.CreateBounded<TweenAnimationHint>(new BoundedChannelOptions(capacity)
        {
            // Drop oldest under extreme backpressure rather than block the game thread. Hints are a
            // pre-arm signal with no non-loss guarantee (no ring/resume), so a dropped hint just means
            // one transition isn't pre-armed — the live scene stream still carries the actual visual.
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        using var runtimeRegistration = _lifetime.Token.Register(() => channel.Writer.TryComplete());
        using var registration = cancellationToken.Register(() => channel.Writer.TryComplete());
        using var subscription = SubscribeAnimationHints(request, hint => channel.Writer.TryWrite(hint));

        await foreach (var hint in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return hint;
        }
    }

    public IDisposable SubscribeRuntimeSceneDelta(
        RuntimeSceneSubscriptionRequest request,
        Action<RuntimeSceneDelta> onDelta,
        Action<EmbeddableRuntimeError>? onError = null)
        => _subscriptions.Subscribe(() => _services.RuntimeSceneWatcher.Subscribe(onDelta));

    public ModelCatalogOperationResult GetModels(ModelCatalogRequestSnapshot request)
    {
        try
        {
            return _services.ModelCatalogProvider.GetModels(request);
        }
        catch (Exception ex)
        {
            return ModelCatalogOperationResult.Failure(
                DataSourceKind.Stub,
                provisional: true,
                request.Family,
                language: null,
                ModelCatalogStatus.Unavailable,
                "model-catalog-failed",
                ex.Message,
                [new ModelCatalogNoticeSnapshot("model-catalog-failed", "error", ex.Message, "models")]);
        }
    }

    public SpineCatalogOperationResult GetSpineCatalog(SpineCatalogRequestSnapshot request)
    {
        try
        {
            return _services.SpineCatalogProvider.CatalogSpines(request);
        }
        catch (Exception ex)
        {
            return SpineCatalogOperationResult.Failure(
                DataSourceKind.Live,
                provisional: false,
                AssetExtractFailureCode.RuntimeFailure,
                "The embedded runtime failed while enumerating the live Spine catalog.",
                [new AssetExtractDetail("exception", ex.GetType().Name, ex.Message)]);
        }
    }

    public SpineGeoClipBakeResultSnapshot BakeSpineGeoClip(SpineGeoClipBakeRequestSnapshot request)
    {
        try
        {
            return _services.SpineGeoClipBaker.BakeSpineGeoClip(request);
        }
        catch (Exception ex)
        {
            return SpineGeoClipBakeResultSnapshot.Failure(
                AssetExtractFailureCode.RuntimeFailure,
                "The embedded runtime failed while baking a Spine geoclip.",
                [new AssetExtractDetail("exception", ex.GetType().Name, ex.Message)]);
        }
    }

    public ReferenceOperationResult GetReference(ReferenceRequestSnapshot request)
    {
        try
        {
            return _services.ReferenceDataProvider.GetReference(request);
        }
        catch (Exception ex)
        {
            return ReferenceOperationResult.Failure(
                DataSourceKind.Stub,
                provisional: true,
                request.Topic,
                ReferenceStatus.Unavailable,
                "reference-failed",
                ex.Message,
                [new ReferenceNoticeSnapshot("reference-failed", "error", ex.Message, "reference")]);
        }
    }

    public EmbeddableAssetBatchResult GetPresentationAssets(PresentationAssetBatchRequest request)
    {
        return Assets.GetAssets(new EmbeddableAssetBatchRequest(
            [.. request.Keys
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Distinct(StringComparer.Ordinal)
                .Select(key => new EmbeddableAssetRequest(key, request.Format))],
            request.FailFast));
    }

    public EmbeddableActionResult ExecuteAction(EmbeddableActionRequest request)
    {
        try
        {
            var validationError = ValidateActionRequest(request);
            if (validationError is not null)
            {
                return validationError;
            }

            var result = _services.ActionHandler.Execute(new SemanticActionRequest(
                request.RequestId,
                request.Kind,
                request.CardId,
                request.PotionId,
                request.TargetId,
                request.ChoiceId,
                request.CharacterId,
                request.MapNodeId,
                request.MouseX,
                request.MouseY,
                request.MouseButton,
                ToPerspective(request.PlayerId),
                request.DisplayName,
                request.Values,
                request.CardIds,
                IsEraser: null,
                StrokePoints: null,
                ElementId: request.ElementId,
                OffsetX: request.OffsetX,
                OffsetY: request.OffsetY,
                Key: request.Key,
                KeyModifiers: request.KeyModifiers,
                KeyPressed: request.KeyPressed,
                MousePressed: request.MousePressed));
            // HoverElement is a high-frequency cursor move (continuous targeting) that changes no committed
            // game state — refreshing the state hub on every hover would be a per-frame broadcast storm. The
            // live SCENE watcher still streams the resulting visual deltas (targeting arrow, tooltips), so the
            // mirror updates without the state-hub refresh.
            // set-scroll-offset joins hover-element in the no-refresh set for the same reason: a client that leads
            // a scroll locally sends one per gesture frame, and a state-hub broadcast per frame is the storm this
            // exclusion exists to prevent. The scene watcher still streams the moved container.
            if (result.Accepted
                && request.Kind != SemanticActionKind.HoverElement
                && request.Kind != SemanticActionKind.SetScrollOffset)
            {
                _stateSubscriptionHub?.RequestRefresh(force: true);
            }

            return new EmbeddableActionResult(result.Accepted, result, result.Error is null ? null : new EmbeddableRuntimeError(result.Error.Code.ToString(), result.Error.Message));
        }
        catch (Exception ex)
        {
            return new EmbeddableActionResult(false, null, new EmbeddableRuntimeError("runtime-action-failed", ex.Message));
        }
    }

    private static EmbeddableActionResult? ValidateActionRequest(EmbeddableActionRequest request)
    {
        var missingField = request.Kind switch
        {
            SemanticActionKind.PlayCard when string.IsNullOrWhiteSpace(request.CardId) => nameof(request.CardId),
            SemanticActionKind.Choose when string.IsNullOrWhiteSpace(request.ChoiceId) => nameof(request.ChoiceId),
            SemanticActionKind.SelectCharacter when string.IsNullOrWhiteSpace(request.CharacterId) => nameof(request.CharacterId),
            // select-map-node addresses a point EITHER by stable map node id ("map-node:{row}:{col}") OR by
            // ElementId (a live map point's scene-node instance id — the only addressing a mirror client has, since
            // it renders scene nodes and cannot derive row/col). Requiring MapNodeId alone would reject the
            // element-addressed form before the handler ever saw it.
            SemanticActionKind.SelectMapNode
                when string.IsNullOrWhiteSpace(request.MapNodeId) && string.IsNullOrWhiteSpace(request.ElementId)
                => nameof(request.MapNodeId),
            SemanticActionKind.UsePotion when string.IsNullOrWhiteSpace(request.PotionId) => nameof(request.PotionId),
            SemanticActionKind.JoinLobbyPlayer when string.IsNullOrWhiteSpace(request.DisplayName) => nameof(request.DisplayName),
            SemanticActionKind.LeaveLobbyPlayer when string.IsNullOrWhiteSpace(request.PlayerId) => nameof(request.PlayerId),
            SemanticActionKind.DisconnectClient when string.IsNullOrWhiteSpace(request.PlayerId) => nameof(request.PlayerId),
            // SetClientName requires the target netId via PlayerId; DisplayName is optional (empty = clear override).
            SemanticActionKind.SetClientName when string.IsNullOrWhiteSpace(request.PlayerId) => nameof(request.PlayerId),
            // Dev-only host-side heal: needs a positive Values["amount"] or Values["full"]="true"
            // (TargetId is optional — empty means the acting seat's player creature).
            SemanticActionKind.Heal when !HasHealPayload(request.Values) => nameof(request.Values),
            // set-scroll-offset addresses the scroll container by ElementId only (a scrollable surface has no
            // stable semantic id of its own), and carries the wanted Y in Values["offsetY"].
            SemanticActionKind.SetScrollOffset when string.IsNullOrWhiteSpace(request.ElementId)
                => nameof(request.ElementId),
            _ => null
        };
        if (missingField is null)
        {
            return null;
        }

        static bool HasHealPayload(IReadOnlyDictionary<string, string>? values)
        {
            if (values is null)
            {
                return false;
            }

            if (values.TryGetValue("full", out var full)
                && string.Equals(full?.Trim(), "true", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return values.TryGetValue("amount", out var amount)
                && decimal.TryParse(amount, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                && parsed > 0m;
        }

        var message = $"Action {request.Kind} requires {missingField}.";
        var failure = ActionExecutionResult.Failure(
            request.Kind,
            ActionFailureCode.InvalidAction,
            message,
            [new ActionFailureDetail(missingField, string.Empty, "missing-required-field")],
            actionInstanceId: request.RequestId);
        return new EmbeddableActionResult(false, failure, new EmbeddableRuntimeError(ActionFailureCode.InvalidAction.ToString(), message, missingField));
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();

        public void Dispose()
        {
        }
    }

    private static ISpirectlAssetProvider CreateDefaultAssetProvider(SpirectlRuntimeServices services)
        => services.AssetExtractor is PlaceholderAssetExtractProvider
            ? new PlaceholderEmbeddableAssetProvider()
            : new BridgeEmbeddableAssetProvider(services.AssetExtractor);

    private static PerspectiveSelection ToPerspective(string? playerId)
        => ToPerspective(null, playerId);

    private static PerspectiveSelection ToPerspective(PerspectiveSelection? perspective, string? fallbackPlayerId)
        => perspective ?? new(PlayerScope.Local, string.IsNullOrWhiteSpace(fallbackPlayerId) ? null : fallbackPlayerId);

    // Constant: every field is a literal, and CaptureRuntimeHealth() runs once per state capture. It was a fresh
    // allocation per capture for a value that never differs.
    private static readonly RefreshOperationState IdleRefreshOperationState = new(
        null,
        null,
        null,
        null,
        null,
        false,
        null,
        null,
        null,
        null,
        null,
        true,
        0,
        3);

    private static EmbeddableRuntimeHealthSnapshot CaptureRuntimeHealth()
        => CaptureRuntimeHealth(IdleRefreshOperationState);

    private static EmbeddableRuntimeHealthSnapshot CaptureRuntimeHealth(RefreshOperationState refresh)
    {
        var dispatcher = Sts2MainThreadDispatcher.DescribeStatus();
        var dispatcherLiveness = dispatcher.DispatcherLiveness;
        var healthy = dispatcherLiveness is "running";
        var retryAllowed = refresh.RetryAllowed && dispatcherLiveness is not "not-installed";
        return new EmbeddableRuntimeHealthSnapshot(
            dispatcher.HasCapturedContext,
            dispatcher.CapturedThreadId,
            dispatcher.CurrentThreadId,
            dispatcher.IsOnCapturedThread,
            DispatcherQueueDepth: dispatcher.QueueDepth,
            LastQueueDrainAtUtc: dispatcher.LastQueueDrainAtUtc,
            LastSuccessfulRefreshAtUtc: refresh.LastSuccessfulRefreshAtUtc,
            LastSuccessfulRevision: refresh.LastSuccessfulRevision,
            ActiveRefreshOperationId: refresh.ActiveRefreshOperationId,
            ActiveRefreshStartedAtUtc: refresh.ActiveRefreshStartedAtUtc,
            ActiveRefreshElapsed: refresh.ActiveRefreshElapsed,
            ActiveRefreshProjection: refresh.ActiveRefreshProjection,
            LastFailureReason: refresh.LastFailureReason,
            LastFailureAtUtc: refresh.LastFailureAtUtc,
            RetryAllowed: retryAllowed,
            RetryAfterUtc: refresh.CooldownUntilUtc,
            IsApplicationFocused: dispatcher.IsApplicationFocused,
            LastFocusChangedAtUtc: dispatcher.LastFocusChangedAtUtc,
            DispatcherLiveness: dispatcherLiveness,
            Status: RuntimeHealthStatus(dispatcherLiveness),
            Healthy: healthy,
            Message: DispatcherHealthMessage(dispatcherLiveness));
    }

    private static EmbeddableRuntimeError CurrentObservationError(
        EmbeddableRuntimeHealthSnapshot health,
        string fallbackCode,
        string fallbackMessage)
    {
        return health.DispatcherLiveness switch
        {
            "backgrounded" => new EmbeddableRuntimeError(
                "current-observation-backgrounded",
                "Current game state cannot be observed while STS2 is backgrounded. Retry after the game regains focus.",
                "dispatcherLiveness",
                "backgrounded",
                Retryable: true,
                SuggestedNextStep: "Bring STS2 to the foreground and retry the same current-state observation."),
            "stalled" => new EmbeddableRuntimeError(
                "current-observation-dispatcher-stalled",
                "Current game state cannot be observed because the STS2 main-thread dispatcher is stalled. Retry this observation before treating the runtime as terminal.",
                "dispatcherLiveness",
                "stalled",
                Retryable: true,
                SuggestedNextStep: "Retry the same current-state observation; collect diagnostics if the dispatcher remains stalled."),
            _ => new EmbeddableRuntimeError(
                fallbackCode,
                fallbackMessage,
                Retryable: false,
                SuggestedNextStep: "Collect runtime diagnostics if this failure persists.")
        };
    }

    private static string RuntimeHealthStatus(string dispatcherLiveness)
        => dispatcherLiveness switch
        {
            "running" => "healthy",
            "backgrounded" or "stalled" => "retryable",
            "not-installed" => "unavailable",
            _ => "unknown"
        };

    private static string DispatcherHealthMessage(string dispatcherLiveness)
        => dispatcherLiveness switch
        {
            "backgrounded" => "The STS2 main-thread dispatcher is not draining because the game appears to be in the background.",
            "stalled" => "The STS2 main-thread dispatcher has queued work but has not drained recently.",
            "not-installed" => "The STS2 main-thread dispatcher has not been installed.",
            _ => "The STS2 main-thread dispatcher is draining queued work."
        };

    private EmbeddableStateSubscriptionHub<StateSnapshot, CurrentStateWatchEvent> StateSubscriptionHub
        => _stateSubscriptionHub ??= new EmbeddableStateSubscriptionHub<StateSnapshot, CurrentStateWatchEvent>(
            CaptureCurrentStateForSubscription,
            BuildStateFingerprint,
            BuildErrorFingerprint,
            (type, sequence, observedAt, fingerprint, revision, state, error, health)
                => new CurrentStateWatchEvent(type, sequence, observedAt, fingerprint, revision, state, error, health),
            instrumentation: _services.Instrumentation ?? Sts2RuntimeInstrumentation.None);

    private StateCaptureOutcome<StateSnapshot> CaptureCurrentStateForSubscription(StateProjection projection)
    {
        var result = GetCurrentState(new CurrentStateRequest(projection.StateTimeout, projection.Perspective));
        return new StateCaptureOutcome<StateSnapshot>(result.Success, result.State, result.Error, result.Health);
    }

    // The watch hub's change detector, run once per capture (several times a second, for as long as a mirror is
    // attached). It used to serialize the whole snapshot to a string and SHA-256 it; both halves were pure
    // overhead — nothing authenticates a fingerprint, it is only ever compared for equality — so it now streams
    // UTF-8 straight into a pooled buffer and hashes the bytes. Values gained an `xxh64:` prefix in the process,
    // so a value from before the change can never be mistaken for one from after it.
    internal static string BuildStateFingerprint(StateSnapshot state)
        => EmbeddableStateFingerprint.Compute(state, WatchJsonOptions);

    internal static string BuildErrorFingerprint(EmbeddableRuntimeError error)
        => EmbeddableStateFingerprint.Compute(
            string.Join(
                "\u001f",
                "current-state-error",
                error.Code,
                error.Message,
                error.Field ?? string.Empty,
                error.Value ?? string.Empty));

    private static readonly JsonSerializerOptions WatchJsonOptions = new(JsonSerializerDefaults.Web);

    public static SpirectlRuntimeFacade FromFactory(
        Core.State.IGameStateExtractor stateExtractor,
        IActionHandler actionHandler,
        Core.Logging.ILogStream logStream,
        Core.Perspective.IPerspectiveProvider perspectiveProvider,
        IAssetExtractProvider assetExtractProvider,
        EmbeddableRuntimeOptions? options = null)
    {
        return new SpirectlRuntimeFacade(new SpirectlRuntimeServices(
            stateExtractor, actionHandler, logStream, perspectiveProvider,
            assetExtractProvider, assetExtractProvider, assetExtractProvider,
            assetExtractProvider, assetExtractProvider, new PlaceholderModelCatalogProvider(),
            new PlaceholderReferenceDataProvider(), new PlaceholderRuntimeSceneWatcher(),
            Provisional: true,
            Instrumentation: Sts2RuntimeInstrumentation.None), options);
    }

    /// <summary>Bridge-facing adapter path; callers supply only the reusable embedded ports.</summary>
    internal static SpirectlRuntimeFacade FromFactory(
        IGameStateExtractor stateExtractor,
        IActionHandler actionHandler,
        ILogStream logStream,
        IPerspectiveProvider perspectiveProvider,
        IAssetExtractor assetExtractor,
        IAssetExplainer assetExplainer,
        IAssetCatalogProvider assetCatalogProvider,
        ISpineCatalogProvider spineCatalogProvider,
        ISpineGeoClipBaker spineGeoClipBaker,
        IModelCatalogProvider modelCatalogProvider,
        IReferenceDataProvider referenceDataProvider,
        IRuntimeSceneWatcher runtimeSceneWatcher,
        IStateProvider? stateProvider = null,
        EmbeddableRuntimeOptions? options = null,
        bool provisional = false,
        ISts2RuntimeInstrumentation? instrumentation = null)
        => new(new SpirectlRuntimeServices(
            stateExtractor, actionHandler, logStream, perspectiveProvider,
            assetExtractor, assetExplainer, assetCatalogProvider, spineCatalogProvider, spineGeoClipBaker,
            modelCatalogProvider, referenceDataProvider, runtimeSceneWatcher, stateProvider, provisional,
            Instrumentation: instrumentation ?? Sts2RuntimeInstrumentation.Current), options);
}
