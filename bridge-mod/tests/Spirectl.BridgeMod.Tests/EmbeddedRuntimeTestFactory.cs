using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Artifacts;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Models;
using Spirectl.Sts2.Core.Perspective;
using Spirectl.Sts2.Core.Reference;
using Spirectl.Sts2.Core.SceneInspection;
using Spirectl.Sts2.Core.State;
using Spirectl.Sts2.Embedding;

namespace Spirectl.BridgeMod.Tests;

internal static class EmbeddedRuntimeTestFactory
{
    internal static SpirectlRuntimeFacade CreateExtractionOnly(IAssetExtractor assetExtractor)
    {
        var fallback = new PlaceholderAssetExtractProvider();
        return new SpirectlRuntimeFacade(new SpirectlRuntimeServices(
            new PlaceholderStateExtractor(),
            new PlaceholderActionHandler(),
            new InMemoryLogStream(),
            new DefaultPerspectiveProvider(),
            assetExtractor,
            fallback,
            fallback,
            fallback,
            fallback,
            new PlaceholderModelCatalogProvider(),
            new PlaceholderReferenceDataProvider(),
            new PlaceholderRuntimeSceneWatcher(),
            Provisional: true));
    }

    internal static SpirectlRuntimeFacade Create(
        IAssetExtractProvider? assetExtractProvider = null,
        IRuntimeSceneWatchControls? sceneWatchControls = null)
    {
        var assets = assetExtractProvider ?? new PlaceholderAssetExtractProvider();
        return new SpirectlRuntimeFacade(new SpirectlRuntimeServices(
            new PlaceholderStateExtractor(),
            new PlaceholderActionHandler(),
            new InMemoryLogStream(),
            new DefaultPerspectiveProvider(),
            assets,
            assets,
            assets,
            assets,
            assets,
            new PlaceholderModelCatalogProvider(),
            new PlaceholderReferenceDataProvider(),
            new PlaceholderRuntimeSceneWatcher(),
            Provisional: true),
            sceneWatchControls: sceneWatchControls);
    }
}
