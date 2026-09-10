using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.ConsoleCommands;
using Spirectl.Sts2.Core.Debugging;
using Spirectl.Sts2.Core.HotReload;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Perspective;
using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2.Core.State;
using Spirectl.Sts2.Core.Transport;
using Spirectl.Sts2.Embedding;
using Spirectl.Sts2.Live.Debugging;

namespace Spirectl.Sts2.Live;

public static class Sts2BridgeRuntimeFactory
{
    public static BridgeRuntime CreateLiveRuntime(
        IBridgeHost host,
        InMemoryLogStream logStream)
    {
        var instrumentation = new Sts2BridgeRuntimeInstrumentation();
        var profilingScope = Sts2RuntimeInstrumentation.Install(instrumentation);
        var animationScope = Sts2AnimationHintInstrumentation.Install(new Sts2BridgeAnimationHintInstrumentation());
        var geoClipScope = Sts2GeoClipDiagnostics.Install(new Sts2BridgeGeoClipDiagnostics());
        var spineScope = Sts2SpineDiagnostics.Install(new Sts2BridgeSpineDiagnostics());
        BridgeRuntime? runtime = null;
        try
        {
            var shared = Sts2ReusableLiveCompositionFactory.Create(
                logStream, captureMainThreadDispatcher: false, instrumentation: instrumentation);

            // Env-gated one-shot diagnostic: with SPIRECTL_SPINE_GEOMETRY_PROBE unset this returns before doing
            // anything, so an unarmed host pays a single environment read and is otherwise unchanged.
            Sts2SpineGeometryProbe.Install(logStream);

            // Env-gated one-shot OFFLINE BAKE of per-slot spine geometry (the geoclip/0 artifact). Same deal: with
            // SPIRECTL_SPINE_GEOCLIP_BAKE unset this returns before doing anything.
            Sts2SpineGeoClipBaker.Install(logStream);

            // Asset-load crash guards are opt-in because their empty placeholders intentionally change rendering.
            if (Sts2AssetLoadGuardOptions.IsEnabled())
            {
                Sts2HeadlessTextureLoadHooks.Install(logStream);
                Sts2AncientEventHeadlessHooks.Install(logStream);
            }

            // Background throttling is an explicit developer override of the game's power-saving behavior.
            if (Sts2BackgroundThrottleOptions.IsEnabled())
            {
                Sts2BackgroundThrottleHooks.Install(logStream);
            }

            var fixtureLoader = new Sts2FixtureLoader(shared.ScreenLocator, logStream, shared.SelectedCardViewState);
            var runtimeSceneProvider = new Sts2RuntimeSceneProvider(shared.ScreenLocator, logStream);

            // The live incremental scene-delta watcher powers the high-frequency "mirror".
            runtime = new BridgeRuntime(new BridgeRuntimeServices(
                new BridgeRuntimeObservationServices(
                    shared.StateExtractor, shared.PerspectiveProvider,
                    shared.StateProvider, shared.Reference,
                    new Sts2CombatPreviewProvider(logStream), new Sts2MapDrawingsProvider(logStream)),
                new BridgeRuntimeActionServices(
                    new Sts2BridgeActionHandler(shared.ActionHandler, new Sts2DevelopmentActionHandler(shared.ActionHandler)), runtimeSceneProvider, shared.SceneWatcher),
                BridgeRuntimeAssetServices.FromProvider(
                    logStream, new Sts2ScreenshotProvider(shared.ScreenLocator, logStream), shared.Assets, shared.Models),
                new BridgeRuntimeDevelopmentServices(
                    fixtureLoader,
                    new LiveDebugControl(() => shared.StateExtractor.Extract(
                        new GameStateQuery(RequestedPerspective: null, IncludeDebug: false),
                        shared.PerspectiveProvider.GetDefaultPerspective()),
                    new Sts2LiveDebugRuntimeHooks()),
                    new Sts2ScenarioProvider(shared.StateExtractor, shared.PerspectiveProvider, fixtureLoader, logStream),
                    new Sts2RecordedFixtureProvider(shared.StateExtractor, shared.PerspectiveProvider, logStream),
                    new ReflectionHotReloadControl(logStream), new Sts2ConsoleCommandExecutor(logStream), new Sts2ModInspector(logStream)),
                new BridgeRuntimeLifecycleServices(host, new Sts2LifecycleControl(logStream), new Sts2MainThreadInvoker())));

            runtime.AttachLifetime(profilingScope, animationScope, geoClipScope, spineScope);
            return runtime;
        }
        catch
        {
            try
            {
                runtime?.Dispose();
            }
            finally
            {
                spineScope.Dispose();
                geoClipScope.Dispose();
                animationScope.Dispose();
                profilingScope.Dispose();
            }
            throw;
        }
    }
}
