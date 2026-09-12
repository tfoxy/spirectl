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
using Spirectl.Sts2.Core.Mods;
using Spirectl.Sts2.Core.Models;
using Spirectl.Sts2.Core.Perspective;
using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2.Core.Reference;
using Spirectl.Sts2.Core.Scenarios;
using Spirectl.Sts2.Core.SceneInspection;
using Spirectl.Sts2.Core.State;
using Spirectl.Sts2.Core.Transport;
using Spirectl.Sts2.Embedding;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;

namespace Spirectl.Sts2;

public sealed class BridgeRuntime : IDisposable
{
    private int _disposed;
    private readonly List<IDisposable> _ownedLifetimes = [];

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            (RuntimeSceneWatcher as IDisposable)?.Dispose();
        }
        finally
        {
            Exception? lifetimeFailure = null;
            for (var index = _ownedLifetimes.Count - 1; index >= 0; index--)
            {
                try
                {
                    _ownedLifetimes[index].Dispose();
                }
                catch (Exception exception)
                {
                    lifetimeFailure ??= exception;
                }
            }

            _ownedLifetimes.Clear();
            if (lifetimeFailure is not null)
            {
                ExceptionDispatchInfo.Capture(lifetimeFailure).Throw();
            }
        }
    }

    internal void AttachLifetime(params IDisposable[] lifetimes)
        => _ownedLifetimes.AddRange(lifetimes);

    /// <summary>Typed scaffold composition with explicit fallbacks for omitted optional capabilities.</summary>
    public static BridgeRuntime Create(BridgeRuntimeOptions options)
    {
        var fallbackAssets = options.AssetExtractProvider ?? new PlaceholderAssetExtractProvider();
        return new BridgeRuntime(new BridgeRuntimeServices(
            new BridgeRuntimeObservationServices(
                options.StateExtractor, options.PerspectiveProvider,
                options.StateProvider, options.ReferenceDataProvider, options.CombatPreviewProvider, options.MapDrawingsProvider),
            new BridgeRuntimeActionServices(
                options.ActionHandler, options.RuntimeSceneProvider ?? new PlaceholderRuntimeSceneProvider(),
                options.RuntimeSceneWatcher ?? new PlaceholderRuntimeSceneWatcher()),
            new BridgeRuntimeAssetServices(
                options.LogStream, options.ScreenshotProvider ?? new PlaceholderScreenshotProvider(),
                options.AssetExtractor ?? fallbackAssets, options.AssetExplainer ?? fallbackAssets,
                options.AssetCatalogProvider ?? fallbackAssets, options.SpineCatalogProvider ?? fallbackAssets,
                options.SpineGeoClipBaker ?? fallbackAssets, options.ModelCatalogProvider),
            new BridgeRuntimeDevelopmentServices(
                options.FixtureLoader ?? new PlaceholderFixtureLoader(), options.DebugControl ?? new PlaceholderDebugControl(),
                options.ScenarioProvider ?? new PlaceholderScenarioProvider(), options.RecordedFixtureProvider ?? new PlaceholderRecordedFixtureProvider(),
                options.HotReloadControl, options.ConsoleCommandExecutor, options.ModInspector),
            new BridgeRuntimeLifecycleServices(
                options.BridgeHost, options.LifecycleControl ?? new PlaceholderLifecycleControl(), options.MainThreadInvoker)));
    }

    public BridgeRuntime(BridgeRuntimeServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        StateExtractor = services.Observation.StateExtractor;
        PerspectiveProvider = services.Observation.PerspectiveProvider;
        StateProvider = services.Observation.StateProvider;
        ReferenceDataProvider = services.Observation.ReferenceDataProvider ?? new PlaceholderReferenceDataProvider();
        CombatPreviewProvider = services.Observation.CombatPreviewProvider ?? new PlaceholderCombatPreviewProvider();
        MapDrawingsProvider = services.Observation.MapDrawingsProvider ?? new PlaceholderMapDrawingsProvider();
        ActionHandler = services.Actions.ActionHandler;
        RuntimeSceneProvider = services.Actions.RuntimeSceneProvider;
        RuntimeSceneWatcher = services.Actions.RuntimeSceneWatcher;
        LogStream = services.Assets.LogStream;
        ScreenshotProvider = services.Assets.ScreenshotProvider;
        AssetExtractor = services.Assets.Extractor;
        AssetExplainer = services.Assets.Explainer;
        AssetCatalogProvider = services.Assets.Catalog;
        SpineCatalogProvider = services.Assets.SpineCatalog;
        SpineGeoClipBaker = services.Assets.SpineBaker;
        ModelCatalogProvider = services.Assets.ModelCatalogProvider ?? new PlaceholderModelCatalogProvider();
        FixtureLoader = services.Development.FixtureLoader;
        DebugControl = services.Development.DebugControl;
        ScenarioProvider = services.Development.ScenarioProvider;
        RecordedFixtureProvider = services.Development.RecordedFixtureProvider;
        HotReloadControl = services.Development.HotReloadControl ?? new PlaceholderHotReloadControl();
        ConsoleCommandExecutor = services.Development.ConsoleCommandExecutor ?? new PlaceholderConsoleCommandExecutor();
        ModInspector = services.Development.ModInspector ?? new PlaceholderModInspector();
        BridgeHost = services.Lifecycle.BridgeHost;
        LifecycleControl = services.Lifecycle.LifecycleControl;
        MainThreadInvoker = services.Lifecycle.MainThreadInvoker ?? InlineBridgeRuntimeMainThreadInvoker.Instance;
    }
    public IGameStateExtractor StateExtractor { get; }

    public IActionHandler ActionHandler { get; }

    public ILogStream LogStream { get; }

    public IPerspectiveProvider PerspectiveProvider { get; }

    public IFixtureLoader FixtureLoader { get; }

    public IScreenshotProvider ScreenshotProvider { get; }

    public IAssetExtractor AssetExtractor { get; }
    public IAssetExplainer AssetExplainer { get; }
    public IAssetCatalogProvider AssetCatalogProvider { get; }
    public ISpineCatalogProvider SpineCatalogProvider { get; }
    public ISpineGeoClipBaker SpineGeoClipBaker { get; }

    public IDebugControl DebugControl { get; }

    public IRuntimeSceneProvider RuntimeSceneProvider { get; }

    public IRuntimeSceneWatcher RuntimeSceneWatcher { get; }

    public ILifecycleControl LifecycleControl { get; }

    public IScenarioProvider ScenarioProvider { get; }

    public IRecordedFixtureProvider RecordedFixtureProvider { get; }

    public IHotReloadControl HotReloadControl { get; }

    public IConsoleCommandExecutor ConsoleCommandExecutor { get; }

    public IModInspector ModInspector { get; }

    public IModelCatalogProvider ModelCatalogProvider { get; }

    public ICombatPreviewProvider CombatPreviewProvider { get; }

    public IMapDrawingsProvider MapDrawingsProvider { get; }

    public IReferenceDataProvider ReferenceDataProvider { get; }

    public IStateProvider? StateProvider { get; }

    public IBridgeHost BridgeHost { get; }

    public IBridgeRuntimeMainThreadInvoker MainThreadInvoker { get; }

    internal SpirectlRuntimeFacade CreateEmbeddedFacade()
        => SpirectlRuntimeFacade.FromFactory(
            StateExtractor, ActionHandler, LogStream, PerspectiveProvider,
            AssetExtractor, AssetExplainer, AssetCatalogProvider, SpineCatalogProvider, SpineGeoClipBaker,
            ModelCatalogProvider, ReferenceDataProvider, RuntimeSceneWatcher, StateProvider,
            new EmbeddableRuntimeOptions(LiveSts2HostSupported: true, LiveSts2HostUnsupportedReason: null));

    public BridgeHandshakeSnapshot DescribeHandshake(BridgeHandshakeRequest request)
    {
        var defaultPerspective = PerspectiveProvider.GetDefaultPerspective();
        var transport = ResolveTransportSnapshot(
            requestedTransportKind: request.TransportKind,
            source: BridgeHost.DescribeStatus().TransportKind == "unbound"
                ? DataSourceKind.Stub
                : BridgeHost.DescribeStatus().TransportKind == "mock"
                    ? DataSourceKind.Stub
                    : DataSourceKind.Live);
        var playCardImplemented = transport.Source == DataSourceKind.Live;
        var dangerousMode = string.Equals(request.Mode, "dangerous", StringComparison.OrdinalIgnoreCase);

        return new BridgeHandshakeSnapshot(
            SchemaVersion: "spirectl/v0",
            GameVersion: ResolveLiveGameVersion(),
            BridgeVersion: BridgeBuildInfo.BridgeVersion,
            BuildIdentity: ToHandshakeBuildIdentity(BridgeBuildInfo.BuildIdentity),
            TransportKind: transport.TransportKind,
            AttachmentState: transport.AttachmentState,
            Source: transport.Source,
            Provisional: transport.Provisional,
            DefaultPerspective: defaultPerspective,
            Capabilities:
            [
                new BridgeCapabilitySnapshot("handshake", "Bridge metadata and capability negotiation.", Provisional: transport.Provisional),
                new BridgeCapabilitySnapshot("state", "Runtime state retrieval with perspective selection.", Provisional: transport.Provisional),
                new BridgeCapabilitySnapshot("state", "Experimental runtime-only state retrieval.", Provisional: true),
                new BridgeCapabilitySnapshot("state-watch", "Reactive runtime state observation stream with duplicate suppression.", Provisional: transport.Provisional),
                new BridgeCapabilitySnapshot("state-watch", "Reactive state presentation envelope stream with duplicate suppression.", Provisional: true),
                new BridgeCapabilitySnapshot("combat-events", "Ordered, non-deduplicated transient combat-event stream (floating damage numbers) with sequence-based resume.", Provisional: true),
                new BridgeCapabilitySnapshot("actions", "Runtime action execution contract, including dangerous raw-input fallback in dangerous mode.", Provisional: true),
                new BridgeCapabilitySnapshot("logs", "Structured bridge log retrieval.", Provisional: transport.Provisional),
                new BridgeCapabilitySnapshot("dev-console", "Explicit mode-gated in-game developer console command execution.", Provisional: transport.Provisional),
                new BridgeCapabilitySnapshot("debug-control", "Explicit dev-only debug status, stepping, and breakpoint scaffolding.", Provisional: true),
                new BridgeCapabilitySnapshot("runtime-scene-inspection", "Explicit dev-only live runtime scene-tree inspection.", Provisional: transport.Provisional),
                new BridgeCapabilitySnapshot("artifacts", "Bridge-backed screenshot capture for dev workflows.", Provisional: transport.Provisional),
                new BridgeCapabilitySnapshot("asset-extraction", "Bridge-backed asset extraction for composed and animated resources.", Provisional: transport.Provisional),
                new BridgeCapabilitySnapshot("game-models", "Live immutable game model metadata for downstream mods and CLI consumers.", Provisional: transport.Provisional),
                new BridgeCapabilitySnapshot(
                    "remote-client-orchestration",
                    "Reports whether this bridge can orchestrate remote multiplayer clients independently of local legality.",
                    Provisional: transport.Provisional,
                    RemoteOrchestration: new RemoteClientOrchestrationCapabilitySnapshot(
                        "local-only-degraded",
                        RemoteClientOrchestrationStateSnapshot.LocalOnlyDegraded,
                        "This local bridge can execute local-player actions only; independent remote clients require explicitly configured client bridges.",
                        Provisional: transport.Provisional)),
                new BridgeCapabilitySnapshot(
                    "host-local-seat-orchestration",
                    "Reports host-owned local multiplayer seats as actionable by the current host bridge without claiming independent remote-client control.",
                    Provisional: transport.Provisional,
                    RemoteOrchestration: new RemoteClientOrchestrationCapabilitySnapshot(
                        "host-local-seat",
                        RemoteClientOrchestrationStateSnapshot.HostLocalSeat,
                        "The current host bridge owns this local player seat and can execute its semantic actions.",
                        Provisional: transport.Provisional)),
            ],
            SupportedActions: Sts2ActionDescriptorCatalog.Build(playCardImplemented, dangerousMode));
    }

    private static BridgeBuildIdentitySnapshot ToHandshakeBuildIdentity(BridgeBuildIdentity identity)
        => new(
            identity.BridgeSemVer,
            identity.BridgeVersion,
            identity.AssemblyInformationalVersion,
            identity.BuiltAtUtc,
            identity.Sts2ApiLane,
            identity.BuiltAgainstGameVersion,
            identity.BuiltAgainstMainAssemblyHash);

    // The build that is actually RUNNING, as opposed to BuildIdentity's "the build this payload was
    // compiled for". A client compares the two to catch a bridge that survived a game update.
    // The reference-data provider already reads the game's own release info; the "version" topic is
    // its cheapest question, and it degrades to a stub result off the live host.
    private string ResolveLiveGameVersion()
    {
        try
        {
            var result = GetReference(new ReferenceRequestSnapshot("version", []));
            return result.Payload is GameVersionInfoSnapshot version && !string.IsNullOrEmpty(version.Version)
                ? version.Version
                : "unknown";
        }
        catch (Exception)
        {
            // A handshake must answer. An unreadable version is reported as unknown, never thrown:
            // "which build is this" is exactly the question a mis-bound bridge cannot answer.
            return "unknown";
        }
    }


    public GameStateSnapshot GetState(GameStateQuery query)
    {
        var resolvedPerspective = PerspectiveProvider.Resolve(query.RequestedPerspective);
        return StateExtractor.Extract(query, resolvedPerspective);
    }

    public StateSnapshot GetState(PerspectiveSelection? requestedPerspective = null)
    {
        var resolvedPerspective = PerspectiveProvider.Resolve(requestedPerspective);
        if (StateProvider is not null)
        {
            return StateProvider.Observe(resolvedPerspective);
        }

        var state = GetState(new GameStateQuery(RequestedPerspective: requestedPerspective, IncludeDebug: false));
        return new StateSnapshot(
            StateSnapshot.CurrentSchemaVersion,
            state.Language,
            "screens/main_menu",
            CharacterSelect: null,
            Run: null);
    }

    public ActionExecutionResult ExecuteAction(SemanticActionRequest request)
    {
        return ActionHandler.Execute(request);
    }

    public ConsoleCommandExecutionResult ExecuteConsoleCommand(ConsoleCommandRequestSnapshot request)
    {
        return ConsoleCommandExecutor.Execute(request);
    }

    public LogReadResult ReadLogs(LogQuery query)
    {
        return LogStream.Read(query);
    }

    public FixtureLoadResult LoadFixture(FixtureLoadRequestSnapshot request)
    {
        return FixtureLoader.Load(request);
    }

    public RecordedFixtureResult RecordFixture(RecordedFixtureRequestSnapshot request)
    {
        return RecordedFixtureProvider.Record(request);
    }

    public ScreenshotCaptureResult CaptureScreenshot(ScreenshotCaptureRequest request)
    {
        return ScreenshotProvider.Capture(request);
    }

    public RuntimeSceneTreeResult GetRuntimeSceneTree(RuntimeSceneQuery request)
    {
        return RuntimeSceneProvider.GetTree(request);
    }

    public RuntimeSceneNodeResult GetRuntimeSceneNode(RuntimeSceneQuery request)
    {
        return RuntimeSceneProvider.GetNode(request);
    }

    public RuntimeSceneSetVisibleResult SetRuntimeSceneNodeVisible(RuntimeSceneSetVisibleRequestSnapshot request)
    {
        return RuntimeSceneProvider.SetVisible(request);
    }

    public RuntimeSceneControlHoverResult HoverRuntimeSceneControl(RuntimeSceneControlHoverRequestSnapshot request)
    {
        if (!string.IsNullOrWhiteSpace(request.NodePath))
        {
            return RuntimeSceneProvider.HoverControl(request);
        }

        if (string.IsNullOrWhiteSpace(request.PresentationElementId))
        {
            return RuntimeSceneControlHoverResult.Failure(
                DataSourceKind.Live,
                provisional: false,
                RuntimeSceneFailureCode.InvalidNodePath,
                "runtime scene hover requests require a node path or presentation element id.",
                [
                    new RuntimeSceneDetail(
                        "target",
                        string.Empty,
                        "Provide --path with a normalized /root node path or --element-id with a current presentation element id."),
                ]);
        }

        return RuntimeSceneControlHoverResult.Failure(
            DataSourceKind.Live,
            provisional: false,
            RuntimeSceneFailureCode.InvalidNodePath,
            "presentation element id hover resolution is unavailable because legacy presentation was removed.",
            [
                new RuntimeSceneDetail(
                    "presentation_element_id",
                    request.PresentationElementId,
                    "Use --path with a normalized /root node path."),
            ]);
    }

    public RuntimeSceneControlUnhoverResult UnhoverRuntimeSceneControl(RuntimeSceneControlUnhoverRequestSnapshot request)
    {
        // A node path (or empty target for a global clear) goes straight to the provider. A bare
        // presentation element id can't be resolved (legacy presentation was removed), matching hover.
        if (!string.IsNullOrWhiteSpace(request.PresentationElementId) && string.IsNullOrWhiteSpace(request.NodePath))
        {
            return RuntimeSceneControlUnhoverResult.Failure(
                DataSourceKind.Live,
                provisional: false,
                RuntimeSceneFailureCode.InvalidNodePath,
                "presentation element id unhover resolution is unavailable because legacy presentation was removed.",
                [
                    new RuntimeSceneDetail(
                        "presentation_element_id",
                        request.PresentationElementId!,
                        "Use --path with a normalized /root node path, or omit the target for a global clear."),
                ]);
        }

        return RuntimeSceneProvider.UnhoverControl(request);
    }

    public RuntimeTransitionStatusResult GetRuntimeTransitionStatus(RuntimeTransitionStatusRequestSnapshot request)
    {
        return RuntimeSceneProvider.GetTransitionStatus(request);
    }

    /// <summary>
    /// Low-level resolved-request extraction primitive. Takes a fully-resolved
    /// <see cref="AssetExtractRequestSnapshot"/> (<c>{SourceRoot, SourcePath, LoadPath, OutputFormat}</c>)
    /// and runs the engine extraction. This is NOT the embedding consumer entrypoint —
    /// hosts hold opaque keys, not resolved requests, so they go through
    /// <see cref="Embedding.ISpirectlRuntime.Assets"/> (<c>GetAsset</c>/<c>GetAssets</c>),
    /// which resolves the key and then calls this. The only direct callers are the
    /// in-assembly <see cref="Embedding.BridgeEmbeddableAssetProvider"/> and the gRPC
    /// protocol adapter, both of which legitimately produce resolved requests.
    /// </summary>
    public AssetExtractOperationResult ExtractAsset(AssetExtractRequestSnapshot request)
    {
        return AssetExtractor.Extract(request);
    }

    public AssetExplainOperationResult ExplainAsset(AssetExplainRequestSnapshot request)
    {
        return AssetExplainer.Explain(request);
    }

    public AssetCatalogOperationResult GetAssetCatalog(AssetCatalogRequestSnapshot request)
    {
        return AssetCatalogProvider.Catalog(request);
    }

    public SpineCatalogOperationResult GetSpineCatalog(SpineCatalogRequestSnapshot request)
    {
        return SpineCatalogProvider.CatalogSpines(request);
    }

    /// <summary>
    /// Bake one spine pose (or clip) into a geoclip artifact directory. The provider marshals onto the Godot main
    /// thread itself and BLOCKS until the bake finishes, so a host calls this from a worker — see
    /// <see cref="SpineGeoClipBakeRequestSnapshot"/>.
    /// </summary>
    public SpineGeoClipBakeResultSnapshot BakeSpineGeoClip(SpineGeoClipBakeRequestSnapshot request)
    {
        return SpineGeoClipBaker.BakeSpineGeoClip(request);
    }

    public ModelCatalogOperationResult GetModels(ModelCatalogRequestSnapshot request)
    {
        // Model resolution formats game LocStrings via the game's shared (non-thread-safe)
        // SmartFormatter; it must not race the live game's own formatting on the main thread.
        return MainThreadInvoker.Invoke(() => ModelCatalogProvider.GetModels(request));
    }

    public CombatPreviewOperationResult GetCombatPreview(CombatPreviewRequestSnapshot request)
    {
        // The oracle reads live combat handles (CombatManager, creatures, card dynamic vars)
        // and calls the game's damage/block funnel; it must run on the game's main thread.
        return MainThreadInvoker.Invoke(() => CombatPreviewProvider.GetCombatPreview(request));
    }

    public MapDrawingsOperationResult GetMapDrawings(MapDrawingsRequestSnapshot request)
    {
        // Reads live map-screen handles (NMapScreen.Instance.Drawings, the run state players for
        // per-character colors); it must run on the game's main thread.
        return MainThreadInvoker.Invoke(() => MapDrawingsProvider.GetMapDrawings(request));
    }

    public ReferenceOperationResult GetReference(ReferenceRequestSnapshot request)
    {
        return ReferenceDataProvider.GetReference(request);
    }

    public ModListOperationResult GetMods(ModListRequestSnapshot request)
    {
        return ModInspector.ListMods(request);
    }

    public DebugStatusSnapshot GetDebugStatus(DebugStatusRequestSnapshot request)
    {
        return DebugControl.GetStatus(request);
    }

    public DebugSessionStartResultSnapshot StartDebugSession(DebugSessionStartRequestSnapshot request)
    {
        return DebugControl.StartSession(request);
    }

    public DebugSessionStatusResultSnapshot GetDebugSessionStatus(DebugSessionStatusRequestSnapshot request)
    {
        return DebugControl.GetSessionStatus(request);
    }

    public DebugSessionEndResultSnapshot EndDebugSession(DebugSessionEndRequestSnapshot request)
    {
        return DebugControl.EndSession(request);
    }

    public DebugOperationResultSnapshot PauseDebug(DebugSessionBoundRequestSnapshot request)
    {
        return DebugControl.Pause(request);
    }

    public DebugOperationResultSnapshot ResumeDebug(DebugSessionBoundRequestSnapshot request)
    {
        return DebugControl.Resume(request);
    }

    public DebugOperationResultSnapshot StepDebug(DebugStepRequestSnapshot request)
    {
        return DebugControl.Step(request);
    }

    public DebugWaitResultSnapshot WaitDebug(DebugWaitRequestSnapshot request)
    {
        return DebugControl.Wait(request);
    }

    public DebugBreakpointListResultSnapshot ListBreakpoints(DebugBreakpointListRequestSnapshot request)
    {
        return DebugControl.ListBreakpoints(request);
    }

    public DebugBreakpointAddResultSnapshot AddBreakpoint(DebugBreakpointAddRequestSnapshot request)
    {
        return DebugControl.AddBreakpoint(request);
    }

    public DebugBreakpointRemoveResultSnapshot RemoveBreakpoint(DebugBreakpointRemoveRequestSnapshot request)
    {
        return DebugControl.RemoveBreakpoint(request);
    }

    public DebugEventStreamResultSnapshot GetDebugEvents(DebugEventStreamRequestSnapshot request)
    {
        return DebugControl.GetEvents(request);
    }

    public HotReloadStatusResultSnapshot GetHotReloadStatus(HotReloadStatusRequestSnapshot request)
    {
        return HotReloadControl.GetStatus(request);
    }

    public Task<HotReloadOperationResult> RequestHotReloadAsync(HotReloadRequestSnapshot request)
    {
        return HotReloadControl.RequestReloadAsync(request);
    }

    public BridgeRuntimeStatus DescribeStatus()
    {
        var perspective = PerspectiveProvider.GetDefaultPerspective();
        var hostStatus = BridgeHost.DescribeStatus();

        return new BridgeRuntimeStatus(
            StateExtractor.GetType().Name,
            ActionHandler.GetType().Name,
            LogStream.GetType().Name,
            hostStatus,
            perspective);
    }

    private BridgeTransportSnapshot ResolveTransportSnapshot(string? requestedTransportKind, DataSourceKind source)
    {
        var hostStatus = BridgeHost.DescribeStatus();
        var transportKind = hostStatus.TransportKind == "unbound"
            ? string.IsNullOrWhiteSpace(requestedTransportKind) ? "mock" : requestedTransportKind!
            : hostStatus.TransportKind;

        var attachmentState = source switch
        {
            DataSourceKind.Live when hostStatus.IsRunning => RuntimeAttachmentState.Attached,
            DataSourceKind.Live => RuntimeAttachmentState.Detached,
            _ => RuntimeAttachmentState.Stubbed,
        };

        var provisional = source != DataSourceKind.Live || attachmentState != RuntimeAttachmentState.Attached;

        return new BridgeTransportSnapshot(
            TransportKind: transportKind,
            AttachmentState: attachmentState,
            Source: source,
            Provisional: provisional);
    }

    private static string Slug(string? value)
    {
        var input = string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
        var builder = new StringBuilder(input.Length);
        foreach (var ch in input)
        {
            builder.Append(char.IsAsciiLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-');
        }

        var slug = builder.ToString().Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        return string.IsNullOrWhiteSpace(slug) ? "unknown" : slug;
    }
}

public sealed record BridgeRuntimeStatus(
    string StateExtractor,
    string ActionHandler,
    string LogStream,
    BridgeHostStatus Host,
    PlayerPerspective DefaultPerspective);
