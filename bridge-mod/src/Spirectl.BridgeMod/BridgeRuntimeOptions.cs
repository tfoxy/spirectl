using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Artifacts;
using Spirectl.Sts2.Core.Combat;
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

namespace Spirectl.Sts2;


/// <summary>Named, typed inputs for scaffold and test runtime composition.</summary>
public sealed record BridgeRuntimeOptions
{
    public required IGameStateExtractor StateExtractor { get; init; }
    public required IActionHandler ActionHandler { get; init; }
    public required ILogStream LogStream { get; init; }
    public required IPerspectiveProvider PerspectiveProvider { get; init; }
    public required IBridgeHost BridgeHost { get; init; }
    public IFixtureLoader? FixtureLoader { get; init; }
    public IScreenshotProvider? ScreenshotProvider { get; init; }
    /// <summary>Optional aggregate shorthand. Individual operation ports below take precedence.</summary>
    public IAssetExtractProvider? AssetExtractProvider { get; init; }
    public IAssetExtractor? AssetExtractor { get; init; }
    public IAssetExplainer? AssetExplainer { get; init; }
    public IAssetCatalogProvider? AssetCatalogProvider { get; init; }
    public ISpineCatalogProvider? SpineCatalogProvider { get; init; }
    public ISpineGeoClipBaker? SpineGeoClipBaker { get; init; }
    public IDebugControl? DebugControl { get; init; }
    public IRuntimeSceneProvider? RuntimeSceneProvider { get; init; }
    public IRuntimeSceneWatcher? RuntimeSceneWatcher { get; init; }
    public ILifecycleControl? LifecycleControl { get; init; }
    public IScenarioProvider? ScenarioProvider { get; init; }
    public IRecordedFixtureProvider? RecordedFixtureProvider { get; init; }
    public IHotReloadControl? HotReloadControl { get; init; }
    public IConsoleCommandExecutor? ConsoleCommandExecutor { get; init; }
    public IModInspector? ModInspector { get; init; }
    public IModelCatalogProvider? ModelCatalogProvider { get; init; }
    public IBridgeRuntimeMainThreadInvoker? MainThreadInvoker { get; init; }
    public IStateProvider? StateProvider { get; init; }
    public IReferenceDataProvider? ReferenceDataProvider { get; init; }
    public ICombatPreviewProvider? CombatPreviewProvider { get; init; }
    public IMapDrawingsProvider? MapDrawingsProvider { get; init; }
}
