using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Perspective;
using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2.Core.State;
using Spirectl.Sts2.Embedding;

namespace Spirectl.Sts2.Live;

/// <summary>Builds only the reusable embedded runtime; bridge-host composition lives in Sts2Host.</summary>
public static class Sts2RuntimeFactory
{
    public static ISpirectlRuntime CreateEmbeddableRuntime(
        InMemoryLogStream? logStream = null,
        bool captureMainThreadDispatcher = true)
    {
        var resolvedLogStream = logStream ?? new InMemoryLogStream(500, DataSourceKind.Live, false);
        var shared = Sts2ReusableLiveCompositionFactory.Create(
            resolvedLogStream, captureMainThreadDispatcher, Sts2RuntimeInstrumentation.None);
        return new SpirectlRuntimeFacade(new SpirectlRuntimeServices(
            shared.StateExtractor, shared.ActionHandler, resolvedLogStream, shared.PerspectiveProvider,
            shared.Assets, shared.Assets, shared.Assets, shared.Assets, shared.Assets,
            shared.Models, shared.Reference, shared.SceneWatcher, shared.StateProvider,
            Lifetime: shared.SceneWatcher,
            Instrumentation: Sts2RuntimeInstrumentation.None,
            MultiplayerConnectionSupported: Sts2MultiplayerConnectionHooks.IsInstalled),
            new EmbeddableRuntimeOptions(true, null),
            new Sts2EmbeddableAssetProvider(shared.Assets, shared.Assets),
            Sts2RuntimeSceneWatchControls.Instance);
    }
}
