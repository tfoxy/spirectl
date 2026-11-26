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
/// host-local seat watchers, state-only hooks and the combat-event hooks. The end-turn readiness
/// hook stays because it changes game behavior. The install list below is the full profile's minus those;
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

        // Resolve compatibility first; optional failures leave the rest of the runtime available.
        Sts2GameApiProbe.EnsureCompatible(resolvedLogStream);

        var hooks = new Sts2CompatibilityHookInstaller(Sts2CompatibilityReport.Process, resolvedLogStream);
        hooks.Install(nameof(Sts2MonoModNativeDependencies), log => Sts2MonoModNativeDependencies.EnsureLoaded(log));
        hooks.Install(nameof(Sts2MultiplayerConnectionHooks), log => Sts2MultiplayerConnectionHooks.Install(log));
        hooks.Install(nameof(Sts2SyntheticLobbyNameHooks), log => Sts2SyntheticLobbyNameHooks.Install(log));
        hooks.Install(nameof(Sts2EndTurnReadinessHooks), log => Sts2EndTurnReadinessHooks.Install(log),
            Sts2EndTurnReadinessHooks.RequiredMembers);
        hooks.Install(nameof(Sts2ParticleRestartHooks), log => Sts2ParticleRestartHooks.Install(log));
        hooks.Install(nameof(Sts2SpineAnimationHooks), log => Sts2SpineAnimationHooks.Install(log),
            Sts2SpineAnimationHooks.RequiredMembers);
        hooks.Install(nameof(Sts2TweenRecorderHooks), log => Sts2TweenRecorderHooks.Install(log));
        hooks.Install(nameof(Sts2CardFlightHooks), log => Sts2CardFlightHooks.Install(log));
        hooks.Install(nameof(Sts2DiscardFlightHooks), log => Sts2DiscardFlightHooks.Install(log));
        hooks.Install(nameof(Sts2HandHolderHooks), log => Sts2HandHolderHooks.Install(log));

        Sts2CompatibilityReport.Process.WriteStartupSummary(resolvedLogStream);

        var screen = new Sts2ScreenLocator();
        var perspective = new DefaultPerspectiveProvider();
        var actions = new Sts2ActionHandler(screen, resolvedLogStream, new Sts2SelectedCardViewState());
        var sceneWatcher = new Sts2RuntimeSceneWatcher(Sts2RuntimeInstrumentation.None);
        var assets = new Sts2AssetExtractProvider(resolvedLogStream);
        var models = new Sts2ModelCatalogProvider();

        // Reference data is a placeholder because no embedder asks the game for it.
        return new SpirectlRuntimeFacade(new SpirectlRuntimeServices(
            actions, resolvedLogStream, perspective,
            assets, assets, assets, assets, assets,
            models, new PlaceholderReferenceDataProvider(), sceneWatcher,
            Lifetime: sceneWatcher,
            Instrumentation: Sts2RuntimeInstrumentation.None,
            MultiplayerConnectionSupported: Sts2MultiplayerConnectionHooks.IsInstalled),
            new EmbeddableRuntimeOptions(true, null),
            new Sts2EmbeddableAssetProvider(assets, assets),
            Sts2RuntimeSceneWatchControls.Instance);
    }
}
