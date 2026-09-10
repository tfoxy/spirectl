using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Artifacts;
using Spirectl.Sts2.Core.ConsoleCommands;
using Spirectl.Sts2.Core.Debugging;
using Spirectl.Sts2.Core.Fixtures;
using Spirectl.Sts2.Core.HotReload;
using Spirectl.Sts2.Core.Lifecycle;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Map;
using Spirectl.Sts2.Core.Models;
using Spirectl.Sts2.Core.Mods;
using Spirectl.Sts2.Core.Perspective;
using Spirectl.Sts2.Core.Reference;
using Spirectl.Sts2.Core.SceneInspection;
using Spirectl.Sts2.Core.Scenarios;
using Spirectl.Sts2.Core.State;
using Spirectl.Sts2.Core.Transport;
using Spirectl.Sts2.Core.Combat;

namespace Spirectl.Sts2;


/// <summary>Observation collaborators. These are read-only from the runtime's perspective.</summary>
public sealed record BridgeRuntimeObservationServices(
    IGameStateExtractor StateExtractor,
    IPerspectiveProvider PerspectiveProvider,
    IStateProvider? StateProvider = null,
    IReferenceDataProvider? ReferenceDataProvider = null,
    ICombatPreviewProvider? CombatPreviewProvider = null,
    IMapDrawingsProvider? MapDrawingsProvider = null);

/// <summary>Semantic actions and the scene stream they act upon.</summary>
public sealed record BridgeRuntimeActionServices(
    IActionHandler ActionHandler,
    IRuntimeSceneProvider RuntimeSceneProvider,
    IRuntimeSceneWatcher RuntimeSceneWatcher);

/// <summary>Asset and artifact reads served by the bridge.</summary>
public sealed record BridgeRuntimeAssetServices(
    ILogStream LogStream,
    IScreenshotProvider ScreenshotProvider,
    IAssetExtractor Extractor,
    IAssetExplainer Explainer,
    IAssetCatalogProvider Catalog,
    ISpineCatalogProvider SpineCatalog,
    ISpineGeoClipBaker SpineBaker,
    IModelCatalogProvider? ModelCatalogProvider = null)
{
    /// <summary>Adapt the convenience aggregate once, at composition.</summary>
    public static BridgeRuntimeAssetServices FromProvider(ILogStream logStream, IScreenshotProvider screenshots,
        IAssetExtractProvider provider, IModelCatalogProvider? models = null)
        => new(logStream, screenshots, provider, provider, provider, provider, provider, models);
}

/// <summary>Explicitly opt-in development and fixture capabilities.</summary>
public sealed record BridgeRuntimeDevelopmentServices(
    IFixtureLoader FixtureLoader,
    IDebugControl DebugControl,
    IScenarioProvider ScenarioProvider,
    IRecordedFixtureProvider RecordedFixtureProvider,
    IHotReloadControl? HotReloadControl = null,
    IConsoleCommandExecutor? ConsoleCommandExecutor = null,
    IModInspector? ModInspector = null);

/// <summary>Host ownership and lifecycle dispatch.</summary>
public sealed record BridgeRuntimeLifecycleServices(
    IBridgeHost BridgeHost,
    ILifecycleControl LifecycleControl,
    IBridgeRuntimeMainThreadInvoker? MainThreadInvoker = null);

/// <summary>Immutable composition boundary for <see cref="BridgeRuntime"/>.</summary>
public sealed record BridgeRuntimeServices(
    BridgeRuntimeObservationServices Observation,
    BridgeRuntimeActionServices Actions,
    BridgeRuntimeAssetServices Assets,
    BridgeRuntimeDevelopmentServices Development,
    BridgeRuntimeLifecycleServices Lifecycle);
