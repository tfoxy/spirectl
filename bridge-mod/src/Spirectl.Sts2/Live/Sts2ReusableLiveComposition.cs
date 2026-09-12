using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Perspective;
using Spirectl.Sts2.Core.State;
using Spirectl.Sts2.Live.GameApi;

namespace Spirectl.Sts2.Live;

/// <summary>Shared live composition used by both the embedded runtime and the bridge host.</summary>
internal sealed record Sts2ReusableLiveComposition(
    Sts2ScreenLocator ScreenLocator,
    InMemoryLogStream LogStream,
    DefaultPerspectiveProvider PerspectiveProvider,
    Sts2SelectedCardViewState SelectedCardViewState,
    ObservedGameStateExtractor StateExtractor,
    Sts2StateProvider StateProvider,
    Sts2ActionHandler ActionHandler,
    Sts2RuntimeSceneWatcher SceneWatcher,
    Sts2AssetExtractProvider Assets,
    Sts2ModelCatalogProvider Models,
    Sts2ReferenceDataProvider Reference);

internal static class Sts2ReusableLiveCompositionFactory
{
    internal static Sts2ReusableLiveComposition Create(
        InMemoryLogStream logStream,
        bool captureMainThreadDispatcher,
        ISts2RuntimeInstrumentation? instrumentation = null)
    {
        if (captureMainThreadDispatcher && !Sts2MainThreadDispatcher.DescribeStatus().HasCapturedContext)
        {
            Sts2MainThreadDispatcher.Capture(Sts2GodotMainThreadPump.Install());
        }

        // Before anything binds to the game: prove the members this build's API lane promises are really
        // there. A miss throws, because every alternative is a bridge that starts and then reports wrong
        // numbers — the by-name reader answers null, and the projection turns null into 0/false.
        Sts2GameApiProbe.EnsureCompatible(logStream);

        Sts2MonoModNativeDependencies.EnsureLoaded(logStream);
        Sts2SyntheticLobbyNameHooks.Install(logStream);
        Sts2ChooseACardOverlayHooks.Install(logStream);
        Sts2RewardsCaptureHooks.Install(logStream);
        Sts2HandSelectionHooks.Install(logStream);
        Sts2HostLocalSeatSyncWatcher.Install(logStream);
        Sts2HostLocalSeatTurnWatcher.Install(logStream);
        Sts2EndTurnReadinessHooks.Install(logStream);
        Sts2DamageEventHooks.Install(logStream);
        Sts2CardUpgradeEventHooks.Install(logStream);
        Sts2VfxSpawnEventHooks.Install(logStream);
        Sts2ParticleRestartHooks.Install(logStream);
        Sts2SpineAnimationHooks.Install(logStream);
        Sts2TweenRecorderHooks.Install(logStream);
        Sts2CardFlightHooks.Install(logStream);
        Sts2DiscardFlightHooks.Install(logStream);
        Sts2HandHolderHooks.Install(logStream);

        var screen = new Sts2ScreenLocator();
        var perspective = new DefaultPerspectiveProvider();
        var selected = new Sts2SelectedCardViewState();
        var extractor = new ObservedGameStateExtractor(new Sts2RuntimeObservationProvider(screen, logStream), new RuntimeStateMapper());
        return new Sts2ReusableLiveComposition(
            screen, logStream, perspective, selected, extractor, new Sts2StateProvider(selected),
            new Sts2ActionHandler(screen, logStream, selected), new Sts2RuntimeSceneWatcher(instrumentation),
            new Sts2AssetExtractProvider(logStream), new Sts2ModelCatalogProvider(), new Sts2ReferenceDataProvider(logStream));
    }
}
