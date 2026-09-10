using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Artifacts;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Models;
using Spirectl.Sts2.Core.Perspective;
using Spirectl.Sts2.Core.Reference;
using Spirectl.Sts2.Core.SceneInspection;
using Spirectl.Sts2.Core.State;

namespace Spirectl.Sts2.Embedding;

/// <summary>
/// Internal composition for the embeddable facade. It intentionally contains only the
/// reusable ports consumed by an in-process embedder; bridge transport, fixtures,
/// diagnostics, screenshots, and lifecycle ownership stay in BridgeMod.
/// </summary>
internal sealed record SpirectlRuntimeServices(
    IGameStateExtractor StateExtractor,
    IActionHandler ActionHandler,
    ILogStream LogStream,
    IPerspectiveProvider PerspectiveProvider,
    IAssetExtractor AssetExtractor,
    IAssetExplainer AssetExplainer,
    IAssetCatalogProvider AssetCatalogProvider,
    ISpineCatalogProvider SpineCatalogProvider,
    ISpineGeoClipBaker SpineGeoClipBaker,
    IModelCatalogProvider ModelCatalogProvider,
    IReferenceDataProvider ReferenceDataProvider,
    IRuntimeSceneWatcher RuntimeSceneWatcher,
    IStateProvider? StateProvider = null,
    bool Provisional = false,
    IDisposable? Lifetime = null,
    IReadOnlyList<Core.Actions.ActionDescriptorSnapshot>? SupportedActions = null,
    Live.ISts2RuntimeInstrumentation? Instrumentation = null);
