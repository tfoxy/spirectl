using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Artifacts;
using Spirectl.Sts2.Core.Models;
using Spirectl.Sts2.Core.Reference;
using Spirectl.Sts2.Core.SceneInspection;

namespace Spirectl.Sts2.Embedding;

public interface IRuntimeCapabilitySource
{
    EmbeddableRuntimeCapabilities GetCapabilities();
}

public interface IRuntimeAssetSource
{
    ISpirectlAssetProvider Assets { get; }
    EmbeddableAssetBatchResult GetPresentationAssets(PresentationAssetBatchRequest request);
}

public interface IRuntimeStateSource
{
    /// <summary>Returns the current semantic state synchronously; callers needing changes should subscribe instead.</summary>
    CurrentStateResult GetCurrentState(CurrentStateRequest request);
    IDisposable SubscribeCurrentState(CurrentStateSubscriptionRequest request, Action<CurrentStateWatchEvent> onEvent, Action<EmbeddableRuntimeError>? onError = null);
    IAsyncEnumerable<CurrentStateWatchEvent> WatchCurrentStateAsync(CurrentStateSubscriptionRequest request, CancellationToken cancellationToken = default);
}

public interface IRuntimeMultiplayerConnectionSource
{
    /// <summary>Returns the latest native connection observation, or null when no connection has been observed.</summary>
    MultiplayerConnectionSnapshot? GetCurrentMultiplayerConnection();

    /// <summary>Callbacks run synchronously on the publishing thread and must not block.</summary>
    IDisposable SubscribeMultiplayerConnection(Action<MultiplayerConnectionSnapshot> onEvent);
}

public interface ICombatEventSource
{
    /// <summary>Callbacks may run on the game thread and must not block; resume is bounded by SinceSequence retention.</summary>
    IDisposable SubscribeCombatEvents(CombatEventSubscriptionRequest request, Action<CombatWatchEvent> onEvent, Action<EmbeddableRuntimeError>? onError = null);
    IAsyncEnumerable<CombatWatchEvent> WatchCombatEventsAsync(CombatEventSubscriptionRequest request, CancellationToken cancellationToken = default);
}

public interface IAnimationHintSource
{
    /// <summary>Hints are hot, non-buffered timing signals with no resume. First/last subscriptions enable/disable capture; callbacks must not block.</summary>
    IDisposable SubscribeAnimationHints(AnimationHintSubscriptionRequest request, Action<TweenAnimationHint> onHint, Action<EmbeddableRuntimeError>? onError = null);
    IAsyncEnumerable<TweenAnimationHint> WatchAnimationHintsAsync(AnimationHintSubscriptionRequest request, CancellationToken cancellationToken = default);
}

public interface IRuntimeSceneDeltaSource
{
    /// <summary>Streams a full keyframe followed by incremental deltas. Consumers own replay/resume handling and must dispose subscriptions.</summary>
    IDisposable SubscribeRuntimeSceneDelta(RuntimeSceneSubscriptionRequest request, Action<RuntimeSceneDelta> onDelta, Action<EmbeddableRuntimeError>? onError = null);
}

public interface IGameModelSource
{
    ModelCatalogOperationResult GetModels(ModelCatalogRequestSnapshot request);
}

public interface IGameReferenceSource
{
    ReferenceOperationResult GetReference(ReferenceRequestSnapshot request);
}

public interface ISpineCatalogSource
{
    SpineCatalogOperationResult GetSpineCatalog(SpineCatalogRequestSnapshot request);
}

public interface ISemanticActionSource
{
    EmbeddableActionResult ExecuteAction(EmbeddableActionRequest request);
}

/// <summary>Coupled scene-watch switches safe for an embedder to change at runtime.</summary>
public interface IRuntimeSceneWatchControls
{
    void SetTweenReplayEnabled(bool enabled);
    void SetCardFlightReplayEnabled(bool enabled);
    void SetHandTweenReplayEnabled(bool enabled);
    void SetTrailReplayEnabled(bool enabled);
}

public interface IRuntimeSceneWatchControlSource
{
    IRuntimeSceneWatchControls SceneWatchControls { get; }
}
