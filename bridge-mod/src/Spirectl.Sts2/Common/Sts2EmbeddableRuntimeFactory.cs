using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Artifacts;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Perspective;
using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2.Core.State;
using Spirectl.Sts2.Embedding;

#pragma warning disable IDE0130
namespace Spirectl.Sts2;
#pragma warning restore IDE0130

public static class Sts2EmbeddableRuntimeFactory
{
    private const string LiveHostBuildOutsideGodotReason =
        "This live-host build is not running inside an initialized STS2/Godot process.";

    private const string NonLiveHostBuildReason =
        "This build was compiled without live STS2 host references.";

    public static ISpirectlRuntime Create(bool captureMainThreadDispatcher = true)
    {
#if ENABLE_STS2_LIVE_HOST
        if (!IsLikelyRunningInsideSts2GodotProcess())
        {
            return CreateUnsupportedEmbeddedRuntime(LiveHostBuildOutsideGodotReason);
        }

        if (!captureMainThreadDispatcher)
        {
            var logStream = new InMemoryLogStream(
                capacity: 500,
                source: DataSourceKind.Live,
                provisional: false);
            return SpirectlRuntimeFacade.FromFactory(
                new PlaceholderStateExtractor(),
                new PlaceholderActionHandler(),
                logStream,
                new DefaultPerspectiveProvider(),
                new Live.Sts2AssetExtractProvider(logStream));
        }

        return Live.Sts2RuntimeFactory.CreateEmbeddableRuntime(captureMainThreadDispatcher: captureMainThreadDispatcher);
#else
        return CreateUnsupportedEmbeddedRuntime(NonLiveHostBuildReason);
#endif
    }

    public static ISpirectlRuntime Create(
        Core.State.IGameStateExtractor stateExtractor,
        Core.Actions.IActionHandler actionHandler,
        Core.Logging.ILogStream logStream,
        Core.Perspective.IPerspectiveProvider perspectiveProvider,
        Core.Artifacts.IAssetExtractProvider assetExtractProvider)
    {
        return SpirectlRuntimeFacade.FromFactory(
            stateExtractor,
            actionHandler,
            logStream,
            perspectiveProvider,
            assetExtractProvider);
    }

    private static ISpirectlRuntime CreateUnsupportedEmbeddedRuntime(
        string liveHostUnsupportedReason)
        => SpirectlRuntimeFacade.FromFactory(
            new PlaceholderStateExtractor(),
            new PlaceholderActionHandler(),
            new InMemoryLogStream(),
            new DefaultPerspectiveProvider(),
            new PlaceholderAssetExtractProvider(),
            new EmbeddableRuntimeOptions(
                LiveSts2HostSupported: false,
                LiveSts2HostUnsupportedReason: liveHostUnsupportedReason));

    private static bool IsLikelyRunningInsideSts2GodotProcess()
    {
        var processName = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
        return processName.Contains("SlayTheSpire", StringComparison.OrdinalIgnoreCase)
            || processName.Contains("Godot", StringComparison.OrdinalIgnoreCase);
    }
}
