using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Perspective;
using Spirectl.Sts2.Core.Protocol;
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

        // Resolve compatibility first; optional failures leave the rest of the runtime available.
        Sts2GameApiProbe.EnsureCompatible(logStream);

        var hooks = new Sts2CompatibilityHookInstaller(Sts2CompatibilityReport.Process, logStream);
        hooks.Install(nameof(Sts2MonoModNativeDependencies), log => Sts2MonoModNativeDependencies.EnsureLoaded(log));
        hooks.Install(nameof(Sts2MultiplayerConnectionHooks), log => Sts2MultiplayerConnectionHooks.Install(log));
        hooks.Install(nameof(Sts2SyntheticLobbyNameHooks), log => Sts2SyntheticLobbyNameHooks.Install(log));
        hooks.Install(nameof(Sts2ChooseACardOverlayHooks), log => Sts2ChooseACardOverlayHooks.Install(log));
        hooks.Install(nameof(Sts2RewardsCaptureHooks), log => Sts2RewardsCaptureHooks.Install(log));
        hooks.Install(nameof(Sts2HandSelectionHooks), log => Sts2HandSelectionHooks.Install(log));
        hooks.Install(nameof(Sts2HostLocalSeatSyncWatcher), log => Sts2HostLocalSeatSyncWatcher.Install(log));
        hooks.Install(nameof(Sts2HostLocalSeatTurnWatcher), log => Sts2HostLocalSeatTurnWatcher.Install(log));
        hooks.Install(nameof(Sts2EndTurnReadinessHooks), log => Sts2EndTurnReadinessHooks.Install(log),
            Sts2EndTurnReadinessHooks.RequiredMembers);
        hooks.Install(nameof(Sts2DamageEventHooks), log => Sts2DamageEventHooks.Install(log));
        hooks.Install(nameof(Sts2CardUpgradeEventHooks), log => Sts2CardUpgradeEventHooks.Install(log));
        hooks.Install(nameof(Sts2VfxSpawnEventHooks), log => Sts2VfxSpawnEventHooks.Install(log));
        hooks.Install(nameof(Sts2ParticleRestartHooks), log => Sts2ParticleRestartHooks.Install(log));
        hooks.Install(nameof(Sts2SpineAnimationHooks), log => Sts2SpineAnimationHooks.Install(log),
            Sts2SpineAnimationHooks.RequiredMembers);
        hooks.Install(nameof(Sts2TweenRecorderHooks), log => Sts2TweenRecorderHooks.Install(log));
        hooks.Install(nameof(Sts2CardFlightHooks), log => Sts2CardFlightHooks.Install(log));
        hooks.Install(nameof(Sts2DiscardFlightHooks), log => Sts2DiscardFlightHooks.Install(log));
        hooks.Install(nameof(Sts2HandHolderHooks), log => Sts2HandHolderHooks.Install(log));

        Sts2CompatibilityReport.Process.WriteStartupSummary(logStream);

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
