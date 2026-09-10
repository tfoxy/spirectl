namespace Spirectl.Sts2.Embedding;

/// <summary>
/// The capability ids <see cref="SpirectlRuntimeFacade.GetCapabilities"/> publishes, as constants.
/// </summary>
/// <remarks>
/// <para>WHY THIS EXISTS. An embedder guards on a capability id BEFORE it calls the API behind it, so the id is
/// a contract in both directions — and until now the only place it was written down was inside the facade's
/// construction of the list, which means every consumer spelled it as a string literal of its own. A literal
/// that does not match publishes as "no such capability", and a guard against a capability the runtime never
/// advertises refuses every call, silently and for ever: an embedder shipped exactly that, carrying a fallback
/// for <see cref="SpineGeoClipBake"/> on the belief it was unpublished. Nothing type-checked either side.</para>
/// <para>WHAT IT DOES NOT DO. It does not say whether a capability is SUPPORTED — several of these are published
/// with <c>Supported: false</c> and an <c>UnsupportedReason</c> when the runtime behind them is a placeholder,
/// which is deliberate (an advertised-but-unsupported capability is a diagnosable state; an absent one is not).
/// Read the published list for that; use these constants to name the entry you are looking for.</para>
/// <para>THE LIST IS KEPT HONEST BY A TEST, not by discipline: <c>EmbeddableCapabilityIdsTests</c> asserts that
/// every id in the published list has a constant here and every constant here appears in the published list, so
/// a capability added to the facade without one fails the build's test leg rather than shipping half-named.</para>
/// </remarks>
public static class EmbeddableCapabilityIds
{
    /// <summary>Runtime metadata and capability discovery.</summary>
    public const string RuntimeMetadata = "runtime-metadata";

    /// <summary>Direct current semantic observation with timeout and runtime health metadata.</summary>
    public const string CurrentState = "current-state";

    /// <summary>Presentation runtime state envelope for browser scene evaluation.</summary>
    public const string State = "state";

    /// <summary>Reactive embedded semantic state subscriptions (callback and async-stream consumers).</summary>
    public const string StateSubscriptions = "state-subscriptions";

    /// <summary>Reactive state presentation envelope stream with duplicate suppression.</summary>
    public const string StateWatch = "state-watch";

    /// <summary>Ordered transient combat-event stream with sequence-based resume.</summary>
    public const string CombatEvents = "combat-events";

    /// <summary>Godot-tween timing hints for pre-arming CSS transitions.</summary>
    public const string AnimationHints = "animation-hints";

    /// <summary>Reactive live runtime scene-tree stream with duplicate suppression.</summary>
    public const string SceneWatch = "scene-watch";

    /// <summary>Typed controls for coupled scene-stream replay optimizations.</summary>
    public const string SceneWatchControls = "scene-watch-controls";

    /// <summary>Immutable game model metadata (characters, relics, and further model families).</summary>
    public const string GameModels = "game-models";

    /// <summary>Topic-addressed game reference data (palette, version/modding metadata, other constants).</summary>
    public const string GameReference = "game-reference";

    /// <summary>Legality-backed semantic action execution.</summary>
    public const string SemanticActions = "semantic-actions";

    /// <summary>Bridge-backed asset extraction entrypoint.</summary>
    public const string AssetExtraction = "asset-extraction";

    /// <summary>Enumerate canonical Spine scene, node, and playable animation entries.</summary>
    public const string SpineCatalog = "spine-catalog";

    /// <summary>Bake one Spine animation (or a single pose of it) to a geoclip artifact directory.</summary>
    public const string SpineGeoClipBake = "spine-geoclip-bake";

    /// <summary>Live STS2 host adapter integration.</summary>
    public const string LiveSts2Host = "live-sts2-host";

    /// <summary>Every id above, in the order the facade publishes them.</summary>
    /// <remarks>
    /// Ordered to match the published list so a diff between the two reads as a diff, not as a re-sort. Nothing
    /// depends on the order at runtime — the published list is looked up by id.
    /// </remarks>
    public static IReadOnlyList<string> All { get; } =
    [
        RuntimeMetadata,
        CurrentState,
        State,
        StateSubscriptions,
        StateWatch,
        CombatEvents,
        AnimationHints,
        SceneWatch,
        SceneWatchControls,
        GameModels,
        GameReference,
        SemanticActions,
        AssetExtraction,
        SpineCatalog,
        SpineGeoClipBake,
        LiveSts2Host,
    ];
}
