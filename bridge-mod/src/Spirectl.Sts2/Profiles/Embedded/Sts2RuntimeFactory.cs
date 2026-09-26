using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Perspective;
using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2.Core.Reference;
using Spirectl.Sts2.Core.State;
using Spirectl.Sts2.Embedding;
using Spirectl.Sts2.Live.GameApi;

namespace Spirectl.Sts2.Live;

/// <summary>
/// The embedded profile's runtime factory (see <c>Sts2Profile</c> in the project file). It stands in for the
/// full profile's <c>Live/Sts2RuntimeFactory.cs</c> and <c>Live/Sts2ReusableLiveComposition.cs</c>, and it
/// neither constructs nor installs what an in-process embedder cannot reach: the legacy state extractor, its
/// observation provider and the extractor slot of the runtime services, the reference-data provider, the
/// host-local seat watchers and the VFX-spawn hook. The install list below is the full profile's minus those;
/// Sts2ProfileCompositionTests keeps the two lists from drifting apart.
/// </summary>
public static class Sts2RuntimeFactory
{
    public static ISpirectlRuntime CreateEmbeddableRuntime(
        InMemoryLogStream? logStream = null,
        bool captureMainThreadDispatcher = true)
    {
        var resolvedLogStream = logStream ?? new InMemoryLogStream(500, DataSourceKind.Live, false);

        if (captureMainThreadDispatcher && !Sts2MainThreadDispatcher.DescribeStatus().HasCapturedContext)
        {
            Sts2MainThreadDispatcher.Capture(Sts2GodotMainThreadPump.Install());
        }

        // Before anything binds to the game: prove the members this build's API lane promises are really
        // there. A miss throws, because every alternative is a runtime that starts and then reports wrong
        // numbers.
        Sts2GameApiProbe.EnsureCompatible(resolvedLogStream);

        Sts2MonoModNativeDependencies.EnsureLoaded(resolvedLogStream);
        Sts2MultiplayerConnectionHooks.Install(resolvedLogStream);
        Sts2SyntheticLobbyNameHooks.Install(resolvedLogStream);
        Sts2ChooseACardOverlayHooks.Install(resolvedLogStream);
        Sts2RewardsCaptureHooks.Install(resolvedLogStream);
        Sts2HandSelectionHooks.Install(resolvedLogStream);
        Sts2EndTurnReadinessHooks.Install(resolvedLogStream);
        Sts2DamageEventHooks.Install(resolvedLogStream);
        Sts2CardUpgradeEventHooks.Install(resolvedLogStream);
        Sts2ParticleRestartHooks.Install(resolvedLogStream);
        Sts2SpineAnimationHooks.Install(resolvedLogStream);
        Sts2TweenRecorderHooks.Install(resolvedLogStream);
        Sts2CardFlightHooks.Install(resolvedLogStream);
        Sts2DiscardFlightHooks.Install(resolvedLogStream);
        Sts2HandHolderHooks.Install(resolvedLogStream);

        var screen = new Sts2ScreenLocator();
        var perspective = new DefaultPerspectiveProvider();
        var selected = new Sts2SelectedCardViewState();
        var stateProvider = new Sts2StateProvider(selected);
        var actions = new Sts2ActionHandler(screen, resolvedLogStream, selected);
        var sceneWatcher = new Sts2RuntimeSceneWatcher(Sts2RuntimeInstrumentation.None);
        var assets = new Sts2AssetExtractProvider(resolvedLogStream);
        var models = new Sts2ModelCatalogProvider();

        // The facade reads state through the state provider only, so this profile's services record has no
        // extractor slot at all; reference data is a placeholder because no embedder asks the game for it.
        return new SpirectlRuntimeFacade(new SpirectlRuntimeServices(
            actions, resolvedLogStream, perspective,
            assets, assets, assets, assets, assets,
            models, new PlaceholderReferenceDataProvider(), sceneWatcher, stateProvider,
            Lifetime: sceneWatcher,
            Instrumentation: Sts2RuntimeInstrumentation.None,
            MultiplayerConnectionSupported: Sts2MultiplayerConnectionHooks.IsInstalled),
            new EmbeddableRuntimeOptions(true, null),
            new Sts2EmbeddableAssetProvider(assets, assets),
            Sts2RuntimeSceneWatchControls.Instance);
    }
}
