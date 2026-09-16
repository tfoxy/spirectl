using Spirectl.Sts2;
using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Artifacts;
using Spirectl.Sts2.Core.ConsoleCommands;
using Spirectl.Sts2.Core.Debugging;
using Spirectl.Sts2.Core.Fixtures;
using Spirectl.Sts2.Core.HotReload;
using Spirectl.Sts2.Core.Lifecycle;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Perspective;
using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2.Core.Scenarios;
using Spirectl.Sts2.Core.SceneInspection;
using Spirectl.Sts2.Core.State;
using Spirectl.Sts2.Core.Transport;
using Spirectl.BridgeMod.Services;
using Spirectl.Sts2.Common;
using Spirectl.Proto.V0;
using Google.Protobuf;
using System.Text.Json;
using Xunit;
using RestoreLobbyCharacterSnapshot = Spirectl.Sts2.Core.Restore.LobbyCharacterSnapshot;
using RestoreMultiplayerLobbySnapshot = Spirectl.Sts2.Core.Restore.MultiplayerLobbySnapshot;
using RestoreMultiplayerPlayerSnapshot = Spirectl.Sts2.Core.Restore.MultiplayerPlayerSnapshot;
using RestoreMultiplayerRestoreModeSnapshot = Spirectl.Sts2.Core.Restore.MultiplayerRestoreModeSnapshot;
using RestoreMultiplayerRestoreResultSnapshot = Spirectl.Sts2.Core.Restore.MultiplayerRestoreResultSnapshot;
using RestoreMultiplayerRestoreSnapshot = Spirectl.Sts2.Core.Restore.MultiplayerRestoreSnapshot;

namespace Spirectl.BridgeMod.Tests;

public sealed partial class GrpcBridgeServiceTests
{
    private static GrpcBridgeService CreateFixtureService(IFixtureLoader loader)
    {
        var runtime = BridgeRuntime.Create(new BridgeRuntimeOptions
        {
            StateExtractor = new FixedSnapshotExtractor(
                new GameStateSnapshot(
                    SchemaVersion: "spirectl/v0",
                    GameVersion: "sts2-live",
                    BridgeVersion: BridgeBuildInfo.BridgeVersion,
                    Source: DataSourceKind.Live,
                    Provisional: false,
                    ScreenType: "combat",
                    ScreenTitle: "Combat",
                    ScreenInstanceId: "screen:combat:live",
                    ResolvedPerspective: new PlayerPerspective(PlayerScope.Local, "p1", UsesDefault: true),
                    Menu: null,
                    Lobby: null,
                    Run: null,
                    Combat: null,
                    Choices: [],
                    AvailableActions: [],
                    Notices: [],
                    Debug: null)),
            ActionHandler = new PlaceholderActionHandler(),
            LogStream = new InMemoryLogStream(),
            PerspectiveProvider = new DefaultPerspectiveProvider(),
            FixtureLoader = loader,
            ScreenshotProvider = new FixedScreenshotProvider(),
            BridgeHost = new FixedBridgeHost(new BridgeHostStatus("ipc", true, "Live IPC bridge host running.")),
        });
        return new GrpcBridgeService(runtime);
    }

    private static BridgeRuntime RuntimeWithLifecycle(ILifecycleControl lifecycleControl)
    {
        return BridgeRuntime.Create(new BridgeRuntimeOptions
        {
            StateExtractor = new FixedSnapshotExtractor(
                new GameStateSnapshot(
                    SchemaVersion: "spirectl/v0",
                    GameVersion: "sts2-live",
                    BridgeVersion: BridgeBuildInfo.BridgeVersion,
                    Source: DataSourceKind.Live,
                    Provisional: false,
                    ScreenType: "main-menu",
                    ScreenTitle: "Main Menu",
                    ScreenInstanceId: "screen:main-menu:test",
                    ResolvedPerspective: new PlayerPerspective(PlayerScope.Local, null, UsesDefault: true),
                    Menu: new MenuStateSnapshot("main-menu", "Main Menu"),
                    Lobby: null,
                    Run: null,
                    Combat: null,
                    Choices: [],
                    AvailableActions: [],
                    Notices: [],
                    Debug: null)),
            ActionHandler = new PlaceholderActionHandler(),
            LogStream = new InMemoryLogStream(),
            PerspectiveProvider = new DefaultPerspectiveProvider(),
            FixtureLoader = new PlaceholderFixtureLoader(),
            ScreenshotProvider = new FixedScreenshotProvider(),
            AssetExtractProvider = new PlaceholderAssetExtractProvider(),
            DebugControl = new PlaceholderDebugControl(),
            RuntimeSceneProvider = new FixedRuntimeSceneProvider(),
            LifecycleControl = lifecycleControl,
            BridgeHost = new FixedBridgeHost(new BridgeHostStatus("ipc", true, "Live IPC bridge host running.")),
        });
    }

    private static BridgeRuntime RuntimeWithConsoleExecutor(IConsoleCommandExecutor consoleCommandExecutor)
    {
        return BridgeRuntime.Create(new BridgeRuntimeOptions
        {
            StateExtractor = new FixedSnapshotExtractor(
                new GameStateSnapshot(
                    SchemaVersion: "spirectl/v0",
                    GameVersion: "sts2-live",
                    BridgeVersion: BridgeBuildInfo.BridgeVersion,
                    Source: DataSourceKind.Live,
                    Provisional: false,
                    ScreenType: "combat",
                    ScreenTitle: "Combat",
                    ScreenInstanceId: "screen:combat:live",
                    ResolvedPerspective: new PlayerPerspective(PlayerScope.Local, "p1", UsesDefault: true),
                    Menu: null,
                    Lobby: null,
                    Run: null,
                    Combat: null,
                    Choices: [],
                    AvailableActions: [],
                    Notices: [],
                    Debug: null)),
            ActionHandler = new PlaceholderActionHandler(),
            LogStream = new InMemoryLogStream(),
            PerspectiveProvider = new DefaultPerspectiveProvider(),
            FixtureLoader = new PlaceholderFixtureLoader(),
            ScreenshotProvider = new FixedScreenshotProvider(),
            AssetExtractProvider = new PlaceholderAssetExtractProvider(),
            DebugControl = new PlaceholderDebugControl(),
            RuntimeSceneProvider = new FixedRuntimeSceneProvider(),
            LifecycleControl = new PlaceholderLifecycleControl(),
            ScenarioProvider = new PlaceholderScenarioProvider(),
            RecordedFixtureProvider = new PlaceholderRecordedFixtureProvider(),
            BridgeHost = new FixedBridgeHost(new BridgeHostStatus("ipc", true, "Live IPC bridge host running.")),
            HotReloadControl = new PlaceholderHotReloadControl(),
            ConsoleCommandExecutor = consoleCommandExecutor,
        });
    }

    private static BridgeRuntime RuntimeWithStateProvider(StateSnapshot snapshot)
    {
        return BridgeRuntime.Create(new BridgeRuntimeOptions
        {
            StateExtractor = new FixedSnapshotExtractor(
                new GameStateSnapshot(
                    SchemaVersion: "spirectl/v0",
                    GameVersion: "sts2-live",
                    BridgeVersion: BridgeBuildInfo.BridgeVersion,
                    Source: DataSourceKind.Live,
                    Provisional: false,
                    ScreenType: "main-menu",
                    ScreenTitle: "Main Menu",
                    ScreenInstanceId: "screen:main-menu:live",
                    ResolvedPerspective: new PlayerPerspective(PlayerScope.Local, null, UsesDefault: true),
                    Menu: null,
                    Lobby: null,
                    Run: null,
                    Combat: null,
                    Choices: [],
                    AvailableActions: [],
                    Notices: [],
                    Debug: null,
                    Language: "eng")),
            ActionHandler = new PlaceholderActionHandler(),
            LogStream = new InMemoryLogStream(),
            PerspectiveProvider = new DefaultPerspectiveProvider(),
            FixtureLoader = new PlaceholderFixtureLoader(),
            ScreenshotProvider = new FixedScreenshotProvider(),
            AssetExtractProvider = new PlaceholderAssetExtractProvider(),
            DebugControl = new PlaceholderDebugControl(),
            RuntimeSceneProvider = new FixedRuntimeSceneProvider(),
            LifecycleControl = new PlaceholderLifecycleControl(),
            ScenarioProvider = new PlaceholderScenarioProvider(),
            RecordedFixtureProvider = new PlaceholderRecordedFixtureProvider(),
            BridgeHost = new FixedBridgeHost(new BridgeHostStatus("ipc", true, "Live IPC bridge host running.")),
            StateProvider = new FixedStateProvider(snapshot),
        });
    }

    private static BridgeRuntime RuntimeWithScenarioProvider(IScenarioProvider scenarioProvider)
    {
        return BridgeRuntime.Create(new BridgeRuntimeOptions
        {
            StateExtractor = new FixedSnapshotExtractor(
                new GameStateSnapshot(
                    SchemaVersion: "spirectl/v0",
                    GameVersion: "sts2-live",
                    BridgeVersion: BridgeBuildInfo.BridgeVersion,
                    Source: DataSourceKind.Live,
                    Provisional: false,
                    ScreenType: "shop",
                    ScreenTitle: "Shop",
                    ScreenInstanceId: "screen:shop:live",
                    ResolvedPerspective: new PlayerPerspective(PlayerScope.Local, "p1", UsesDefault: true),
                    Menu: null,
                    Lobby: null,
                    Run: null,
                    Combat: null,
                    Choices: [],
                    AvailableActions: [],
                    Notices: [],
                    Debug: null)),
            ActionHandler = new PlaceholderActionHandler(),
            LogStream = new InMemoryLogStream(),
            PerspectiveProvider = new DefaultPerspectiveProvider(),
            FixtureLoader = new PlaceholderFixtureLoader(),
            ScreenshotProvider = new FixedScreenshotProvider(),
            AssetExtractProvider = new PlaceholderAssetExtractProvider(),
            DebugControl = new PlaceholderDebugControl(),
            RuntimeSceneProvider = new FixedRuntimeSceneProvider(),
            LifecycleControl = new PlaceholderLifecycleControl(),
            ScenarioProvider = scenarioProvider,
            BridgeHost = new FixedBridgeHost(new BridgeHostStatus("ipc", true, "Live IPC bridge host running.")),
        });
    }

    private static BridgeRuntime RuntimeWithRecordedFixtureProvider(IRecordedFixtureProvider recordedFixtureProvider)
    {
        return BridgeRuntime.Create(new BridgeRuntimeOptions
        {
            StateExtractor = new FixedSnapshotExtractor(
                new GameStateSnapshot(
                    SchemaVersion: "spirectl/v0",
                    GameVersion: "sts2-live",
                    BridgeVersion: BridgeBuildInfo.BridgeVersion,
                    Source: DataSourceKind.Live,
                    Provisional: false,
                    ScreenType: "combat",
                    ScreenTitle: "Combat",
                    ScreenInstanceId: "screen:combat:live",
                    ResolvedPerspective: new PlayerPerspective(PlayerScope.Local, "p1", UsesDefault: true),
                    Menu: null,
                    Lobby: null,
                    Run: null,
                    Combat: null,
                    Choices: [],
                    AvailableActions: [],
                    Notices: [],
                    Debug: null)),
            ActionHandler = new PlaceholderActionHandler(),
            LogStream = new InMemoryLogStream(),
            PerspectiveProvider = new DefaultPerspectiveProvider(),
            FixtureLoader = new PlaceholderFixtureLoader(),
            ScreenshotProvider = new FixedScreenshotProvider(),
            AssetExtractProvider = new PlaceholderAssetExtractProvider(),
            DebugControl = new PlaceholderDebugControl(),
            RuntimeSceneProvider = new FixedRuntimeSceneProvider(),
            LifecycleControl = new PlaceholderLifecycleControl(),
            ScenarioProvider = new PlaceholderScenarioProvider(),
            RecordedFixtureProvider = recordedFixtureProvider,
            BridgeHost = new FixedBridgeHost(new BridgeHostStatus("ipc", true, "Live IPC bridge host running.")),
        });
    }

    private sealed class FixedSnapshotExtractor(GameStateSnapshot snapshot) : IGameStateExtractor
    {
        public GameStateSnapshot Extract(GameStateQuery query, PlayerPerspective perspective)
        {
            return snapshot with { ResolvedPerspective = perspective };
        }
    }

    private sealed class FixedStateProvider(StateSnapshot snapshot) : IStateProvider
    {
        public StateSnapshot Observe(PlayerPerspective perspective)
        {
            _ = perspective;
            return snapshot;
        }
    }

    private sealed class FixedActionHandler(ActionExecutionResult result) : IActionHandler
    {
        public SemanticActionRequest? LastRequest { get; private set; }

        public ActionExecutionResult Execute(SemanticActionRequest request)
        {
            LastRequest = request;
            return result with { Kind = request.Kind };
        }
    }

    private sealed class FixedFixtureLoader : IFixtureLoader
    {
        public Spirectl.Sts2.Core.Fixtures.FixtureLoadResult Load(FixtureLoadRequestSnapshot request)
        {
            return Spirectl.Sts2.Core.Fixtures.FixtureLoadResult.Success(
                requestId: request.RequestId,
                fixtureName: request.FixtureName,
                sourcePath: request.SourcePath,
                source: DataSourceKind.Live,
                provisional: false,
                screenType: "combat",
                screenInstanceId: "screen:combat:live",
                resolvedPerspective: new PlayerPerspective(PlayerScope.Local, "p1", UsesDefault: false),
                notices:
                [
                    new FixtureLoadNotice(
                        Code: "fixture.loaded",
                        Message: "Loaded fixture into combat."),
                ],
                recipeReport: new Spirectl.Sts2.Core.Fixtures.FixtureRecipeRestoreReport(
                    RecipeName: "combat-recipe",
                    AppliedFields:
                    [
                        new Spirectl.Sts2.Core.Fixtures.FixtureRecipeFieldReport(
                            FieldPath: "run.currentRoom.combat.encounterId",
                            ValueSummary: "nibbits-normal",
                            ReasonCode: "applied_authored_field",
                            Message: "Applied authored encounter id."),
                    ],
                    InferredFields:
                    [
                        new Spirectl.Sts2.Core.Fixtures.FixtureRecipeFieldReport(
                            FieldPath: "run.seed",
                            ValueSummary: "basic-combat",
                            ReasonCode: "inferred_default",
                            Message: "Inferred deterministic fixture seed."),
                    ],
                    OmittedFields: [],
                    UnsupportedFields: [],
                    DegradedMultiplayerFields: [],
                    BridgeValidation: new Spirectl.Sts2.Core.Fixtures.FixtureBridgeValidationResult(
                        Status: "passed",
                        Details: [])));
        }
    }

    private sealed class FixedRecordedFixtureProvider : IRecordedFixtureProvider
    {
        public Spirectl.Sts2.Core.Fixtures.RecordedFixtureResult Record(RecordedFixtureRequestSnapshot request)
            => Spirectl.Sts2.Core.Fixtures.RecordedFixtureResult.Succeeded(new RecordedFixtureSuccess(
                request.RequestId,
                FixtureYaml: string.Empty,
                FixtureJson: """{"screen":"combat"}""",
                Metadata: new RecordedFixtureMetadataSnapshot(
                    SchemaVersion: "spirectl.recorded-fixture/v0",
                    RecordedAt: "2026-04-24T00:00:00Z",
                    Screen: new RecordedFixtureScreenSnapshot("combat", "Combat", "screen:combat:1"),
                    GameVersion: "sts2-test",
                    BridgeVersion: BridgeBuildInfo.BridgeVersion,
                    SpirectlVersion: "0.0.0",
                    RestoreQuality: RecordedFixtureRestoreQuality.Partial,
                    KnownOmissions:
                    [
                        new RecordedFixtureCompatibilityNoteSnapshot(
                            "rng-continuation-omitted",
                            "Runtime RNG continuation is not recorded.",
                            "run.rng"),
                    ],
                    Notices:
                    [
                        new RecordedFixtureNoticeSnapshot(
                            "screen-entry-fixture",
                            "This artifact recreates the screen-entry recipe, not the current runtime frame."),
                    ]),
                Source: DataSourceKind.Live,
                Provisional: false));
    }

    private sealed class FixedLifecycleControl(GameCloseOperationResult result) : ILifecycleControl
    {
        public GameCloseOperationResult CloseGame(string requestId)
        {
            if (result.Error is not null)
            {
                return result;
            }

            return result with
            {
                Accepted = result.Accepted,
            };
        }
    }

    private sealed class FixedConsoleCommandExecutor(bool success) : IConsoleCommandExecutor
    {
        public ConsoleCommandExecutionResult Execute(ConsoleCommandRequestSnapshot request)
        {
            if (success)
            {
                return ConsoleCommandExecutionResult.SuccessResult(
                    request.RequestId,
                    request.Command,
                    request.Args,
                    request.Line,
                    success: true,
                    output: "draw <n>",
                    outputLines: ["draw <n>"],
                    DataSourceKind.Live,
                    provisional: false,
                    notices:
                    [
                        new ConsoleCommandNoticeSnapshot(
                            "console-networked",
                            "Executed through the public networked console path.",
                            Provisional: false),
                    ]);
            }

            return ConsoleCommandExecutionResult.SuccessResult(
                request.RequestId,
                request.Command,
                request.Args,
                request.Line,
                success: false,
                output: "The command 'nope' does not exist.",
                outputLines: ["The command 'nope' does not exist."],
                DataSourceKind.Live,
                provisional: false);
        }
    }

    private sealed class ThrowingFixtureLoader : IFixtureLoader
    {
        public Spirectl.Sts2.Core.Fixtures.FixtureLoadResult Load(FixtureLoadRequestSnapshot request)
        {
            throw new Xunit.Sdk.XunitException("Fixture loader should not run for invalid fixture requests.");
        }
    }

    private sealed class RecordingFixtureLoader : IFixtureLoader
    {
        public FixtureLoadRequestSnapshot? LastRequest { get; private set; }

        public Spirectl.Sts2.Core.Fixtures.FixtureLoadResult Load(FixtureLoadRequestSnapshot request)
        {
            LastRequest = request;
            return Spirectl.Sts2.Core.Fixtures.FixtureLoadResult.Success(
                requestId: request.RequestId,
                fixtureName: request.FixtureName,
                sourcePath: request.SourcePath,
                source: DataSourceKind.Live,
                provisional: false,
                screenType: "fixture-screen",
                screenInstanceId: "screen:fixture:test",
                resolvedPerspective: new PlayerPerspective(PlayerScope.Local, "p1", UsesDefault: false),
                notices:
                [
                    new FixtureLoadNotice(
                        Code: "fixture.loaded",
                        Message: "Fixture reached recording loader."),
                ]);
        }
    }

    private sealed class StructuredInvalidFixtureLoader : IFixtureLoader
    {
        public Spirectl.Sts2.Core.Fixtures.FixtureLoadResult Load(FixtureLoadRequestSnapshot request)
        {
            return Spirectl.Sts2.Core.Fixtures.FixtureLoadResult.Failure(
                requestId: request.RequestId,
                fixtureName: request.FixtureName,
                sourcePath: request.SourcePath,
                source: DataSourceKind.Live,
                provisional: false,
                code: Spirectl.Sts2.Core.Fixtures.FixtureLoadFailureCode.InvalidFixture,
                message: "The live fixture loader could not realize the requested fixture.",
                details:
                [
                    new Spirectl.Sts2.Core.Fixtures.FixtureLoadDetail(
                        Field: "characterSelect.characterButtons[1].characterId",
                        Value: "missing-character",
                        Note: "Valid values: IRONCLAD."),
                ]);
        }
    }

    private sealed class FixedScreenshotProvider : IScreenshotProvider
    {
        public ScreenshotCaptureResult Capture(ScreenshotCaptureRequest request)
        {
            return ScreenshotCaptureResult.Success(
                source: DataSourceKind.Live,
                provisional: false,
                format: "png",
                width: 2,
                height: 1,
                contents: [0x89, 0x50, 0x4e, 0x47],
                screenType: "combat",
                screenInstanceId: "screen:combat:live");
        }
    }

    private sealed class ViewportAwareScreenshotProvider : IScreenshotProvider
    {
        public ScreenshotCaptureRequest? LastRequest { get; private set; }

        public ScreenshotCaptureResult Capture(ScreenshotCaptureRequest request)
        {
            LastRequest = request;
            return ScreenshotCaptureResult.Success(
                source: DataSourceKind.Live,
                provisional: false,
                format: "png",
                width: 1280,
                height: 720,
                contents: [0x89, 0x50, 0x4e, 0x47],
                screenType: "combat",
                screenInstanceId: "screen:combat:live",
                requestedViewportWidth: request.ViewportWidth,
                requestedViewportHeight: request.ViewportHeight,
                appliedViewportWidth: 1280,
                appliedViewportHeight: 720,
                restoredViewport: true,
                restoredViewportWidth: 1920,
                restoredViewportHeight: 1080);
        }
    }

    private sealed class FixedAssetExtractProvider : AssetOperationFallbackPorts, IAssetExtractProvider
    {
        public AssetExtractOperationResult Extract(AssetExtractRequestSnapshot request)
        {
            return AssetExtractOperationResult.Success(
                requestId: request.RequestId,
                source: DataSourceKind.Live,
                provisional: false,
                format: request.OutputFormat,
                width: 2,
                height: 1,
                contents: [0x89, 0x50, 0x4e, 0x47],
                renderMode: "flattened-first-frame",
                notes:
                [
                    "Rendered first frame of animated resource.",
                ]) with
            {
                Provenance = AssetExtractProvenanceSnapshot.FromRequest(
                    request.RequestId,
                    request.SourceRoot,
                    request.SourcePath,
                    request.LoadPath,
                    "flattened-first-frame",
                    DataSourceKind.Live),
                Notices =
                [
                    new AssetExtractNoticeSnapshot("asset_extract_notice", "info", "Fixed test extractor emitted metadata.")
                ],
                PlacementMetadata = new AssetExtractPlacementMetadataSnapshot(
                    "res://ui/shared/HandPanel.tscn",
                    "/Root/Spine",
                    new AssetCompositionRectSnapshot(0, 0, 20, 30),
                    new AssetCompositionRectSnapshot(1, 2, 30, 40),
                    2,
                    1,
                    "flattened-first-frame")
            };
        }

        public AssetExplainOperationResult Explain(AssetExplainRequestSnapshot request)
        {
            return AssetExplainOperationResult.Success(
                requestId: request.RequestId,
                source: DataSourceKind.Live,
                provisional: false,
                rootScene: new AssetCompositionRootSceneSnapshot(
                    BackgroundId: "overgrowth",
                    Path: "res://scenes/backgrounds/overgrowth/overgrowth_background.tscn",
                    LoadSource: "live-host"),
                placeholders:
                [
                    new AssetCompositionPlaceholderSnapshot(
                        Name: "Layer_00",
                        NodePath: "/OvergrowthBackground/Layer_00",
                        Order: 0,
                        Matched: true),
                ],
                layerGroups:
                [
                    new AssetCompositionLayerGroupSnapshot(
                        Name: "Layer_00",
                        Placeholder: "Layer_00",
                        CandidateCount: 2,
                        Candidates:
                        [
                            "res://scenes/backgrounds/overgrowth/layers/overgrowth_bg_00_a.tscn",
                            "res://scenes/backgrounds/overgrowth/layers/overgrowth_bg_00_b.tscn",
                        ],
                        SelectedPath: "res://scenes/backgrounds/overgrowth/layers/overgrowth_bg_00_a.tscn",
                        SelectionSource: "deterministic-first-sorted",
                        Order: 0),
                ],
                selectedLayers:
                [
                    new AssetCompositionSelectedLayerSnapshot(
                        Placeholder: "Layer_00",
                        Path: "res://scenes/backgrounds/overgrowth/layers/overgrowth_bg_00_a.tscn",
                        Order: 0,
                        LoadStatus: "loaded",
                        TextureRefs:
                        [
                            "res://scenes/backgrounds/overgrowth/layers/overgrowth_bg_00_a.webp",
                        ],
                        LocalBounds: new AssetCompositionRectSnapshot(0, 0, 1920, 1080),
                        VisibleBounds: new AssetCompositionRectSnapshot(0, 0, 1880, 1012)),
                ],
                bounds: new AssetCompositionBoundsSnapshot(
                    Viewport: new AssetCompositionRectSnapshot(0, 0, 1920, 1080),
                    FinalComposed: new AssetCompositionRectSnapshot(0, 0, 1920, 1080),
                    FinalVisible: new AssetCompositionRectSnapshot(0, 0, 1880, 1012),
                    TransparentPixelRatio: 0.07),
                render: new AssetCompositionRenderPlanSnapshot(
                    RenderMode: "flattened-combat-background-composed",
                    WarmupFrames: 3,
                    TrimTransparentBounds: false,
                    TransparentCropPadding: 0,
                    Notes:
                    [
                        "Rendered composed combat background at viewport framing using runtime BgContainer placement: centered, x-offset 23, scale 0.9, preserved root Control size.",
                    ]),
                activeScene: new AssetCompositionActiveSceneSnapshot(
                    Status: "not-active",
                    MatchedRoot: false,
                    ObservedLayerPaths: [],
                    Differences: []),
                warnings:
                [
                    new AssetCompositionWarningSnapshot(
                        Code: "missing-layer",
                        Severity: "warning",
                        Message: "Placeholder Layer_02 did not have a matching deterministic layer scene.",
                        Details: new Dictionary<string, string>
                        {
                            ["placeholder"] = "Layer_02",
                        }),
                ]);
        }
    }

    private sealed class FailingAssetExtractProvider : AssetOperationFallbackPorts, IAssetExtractProvider
    {
        public AssetExtractOperationResult Extract(AssetExtractRequestSnapshot request)
            => AssetExtractOperationResult.Failure(
                request.RequestId,
                DataSourceKind.Live,
                provisional: false,
                AssetExtractFailureCode.RuntimeFailure,
                "The live bridge could not render the requested asset.",
                [
                    new AssetExtractDetail(
                        "scene",
                        request.SourcePath,
                        "The live bridge rendered only fully transparent pixels while flattening the scene.",
                        new Dictionary<string, object?>
                        {
                            ["requestId"] = request.RequestId,
                            ["renderTargetId"] = "overlay:rocket-charge-up",
                            ["renderMode"] = "flattened-encounter-visual-overlay",
                            ["readyHookRan"] = true,
                            ["spinePreviewSetupRan"] = false,
                            ["keptPartIds"] = new List<object?> { "rocket" },
                            ["hiddenPartIds"] = new List<object?> { "crusher" },
                            ["captureTimingNotes"] = new List<object?> { "Warmup frames completed." },
                            ["alphaEvidence"] = new Dictionary<string, object?>
                            {
                                ["transparentPixelRatio"] = 1d,
                                ["hasVisiblePixels"] = false,
                            },
                        }),
                ]) with
            {
                Notes =
                [
                    "Rendered encounter special visual scene 'res://scenes/creature_visuals/kaiser_crab_boss_setup.tscn'.",
                    "Resolved and kept catalog selector '%ArmBoneR' for visual part 'rocket'.",
                ],
            };

        public AssetExplainOperationResult Explain(AssetExplainRequestSnapshot request)
            => AssetExplainOperationResult.Failure(
                request.RequestId,
                DataSourceKind.Live,
                provisional: false,
                AssetExtractFailureCode.NotImplemented,
                "not implemented",
                []);
    }

    private sealed class FixedTimelineAssetExtractProvider : AssetOperationFallbackPorts, IAssetExtractProvider
    {
        public AssetExtractOperationResult Extract(AssetExtractRequestSnapshot request)
        {
            return AssetExtractOperationResult.SuccessTimeline(
                requestId: request.RequestId,
                source: DataSourceKind.Live,
                provisional: false,
                format: request.OutputFormat,
                width: 2,
                height: 1,
                renderMode: "timeline-frames",
                durationMs: 175,
                frames:
                [
                    new AssetExtractFrame(0, request.OutputFormat, "image/png", 2, 1, [0x89, 0x50, 0x4e, 0x47], 100),
                    new AssetExtractFrame(1, request.OutputFormat, "image/png", 2, 1, [0x89, 0x50, 0x4e, 0x47], 75),
                ],
                notes:
                [
                    "Rendered animation timeline.",
                ]) with
            {
                Provenance = AssetExtractProvenanceSnapshot.FromRequest(
                    request.RequestId,
                    request.SourceRoot,
                    request.SourcePath,
                    request.LoadPath,
                    "timeline-frames",
                    DataSourceKind.Live),
                Notices =
                [
                    new AssetExtractNoticeSnapshot("asset_extract_notice", "info", "Fixed timeline extractor emitted metadata.")
                ]
            };
        }

        public AssetExplainOperationResult Explain(AssetExplainRequestSnapshot request)
        {
            return AssetExplainOperationResult.Failure(
                request.RequestId,
                DataSourceKind.Stub,
                provisional: true,
                AssetExtractFailureCode.NotImplemented,
                "asset composition explanation is not implemented by this fixed timeline provider.",
                []);
        }
    }

    private sealed class FixedRuntimeSceneProvider : IRuntimeSceneProvider
    {
        public RuntimeSceneQuery? LastQuery { get; private set; }

        public RuntimeSceneSetVisibleRequestSnapshot? LastSetVisibleRequest { get; private set; }

        public Spirectl.Sts2.Core.SceneInspection.RuntimeSceneTreeResult GetTree(RuntimeSceneQuery request)
        {
            LastQuery = request;
            return Spirectl.Sts2.Core.SceneInspection.RuntimeSceneTreeResult.Success(
                source: DataSourceKind.Live,
                provisional: false,
                screenType: "combat",
                screenTitle: "Combat",
                screenInstanceId: "screen:combat:live",
                rootNodePath: "/root",
                nodes:
                [
                    CombatScreenNode(request),
                    HandPanelNode(request),
                ],
                notes: []);
        }

        public Spirectl.Sts2.Core.SceneInspection.RuntimeSceneNodeResult GetNode(RuntimeSceneQuery request)
        {
            LastQuery = request;
            return Spirectl.Sts2.Core.SceneInspection.RuntimeSceneNodeResult.Success(
                source: DataSourceKind.Live,
                provisional: false,
                screenType: "combat",
                screenTitle: "Combat",
                screenInstanceId: "screen:combat:live",
                node: CombatScreenNode(request),
                children:
                [
                    HandPanelNode(request),
                ],
                notes:
                [
                    "Runtime node ids are stable only for the current screen instance.",
                ]);
        }

        public Spirectl.Sts2.Core.SceneInspection.RuntimeSceneSetVisibleResult SetVisible(RuntimeSceneSetVisibleRequestSnapshot request)
        {
            LastSetVisibleRequest = request;
            var query = new RuntimeSceneQuery(
                request.NodePath,
                IncludeProperties: true,
                IncludeComputedTransform: request.IncludeComputedTransform);
            return Spirectl.Sts2.Core.SceneInspection.RuntimeSceneSetVisibleResult.Success(
                source: DataSourceKind.Live,
                provisional: false,
                screenType: "combat",
                screenTitle: "Combat",
                screenInstanceId: "screen:combat:live",
                node: CombatScreenNode(query, visible: request.Visible),
                previousVisible: true,
                requestedVisible: request.Visible,
                changed: true,
                notes: []);
        }

        public RuntimeSceneControlHoverRequestSnapshot? LastHoverRequest { get; private set; }

        /// <summary>Set false to stand in for a provider that reports no hover visibility at all.</summary>
        public bool ReportVisibility { get; init; } = true;

        public Spirectl.Sts2.Core.SceneInspection.RuntimeSceneControlHoverResult HoverControl(RuntimeSceneControlHoverRequestSnapshot request)
        {
            LastQuery = new RuntimeSceneQuery(
                request.NodePath,
                IncludeProperties: true,
                IncludeComputedTransform: true);
            LastHoverRequest = request;
            return Spirectl.Sts2.Core.SceneInspection.RuntimeSceneControlHoverResult.Success(
                source: DataSourceKind.Live,
                provisional: false,
                screenType: "combat",
                screenTitle: "Combat",
                screenInstanceId: "screen:combat:live",
                node: CombatScreenNode(LastQuery),
                resolvedNodePath: request.NodePath,
                presentationElementId: request.PresentationElementId,
                hovered: true,
                hoverPosition: new RuntimeSceneVector2Snapshot(320, 180),
                hoverTip: request.IncludeHoverTip
                    ? new RuntimeSceneHoverTipSnapshot(
                        Visible: true,
                        NodePath: "/root/HoverTip",
                        Title: "Defect",
                        Text: "Locked.",
                        Labels:
                        [
                            new RuntimeSceneHoverLabelSnapshot("/root/HoverTip/Title", "Title", "Defect"),
                            new RuntimeSceneHoverLabelSnapshot("/root/HoverTip/Body", "Body", "Locked."),
                        ],
                        Notes: [])
                    : null,
                notes: [],
                visibility: !ReportVisibility ? null : new RuntimeSceneHoverVisibilitySnapshot(
                    FullyVisible: false,
                    ControlRect: Rect(0, 100, 640, 160),
                    VisibleRect: Rect(0, 100, 640, 60),
                    ClippedBy:
                    [
                        new RuntimeSceneHoverClipSnapshot(
                            "/root/CombatScreen/Scroll",
                            "Godot.ScrollContainer",
                            RuntimeSceneHoverClipReasons.ScrollContainer,
                            Rect(0, 0, 640, 160)),
                    ],
                    Scrolled:
                    [
                        new RuntimeSceneHoverScrollSnapshot("/root/CombatScreen/Scroll", 0, 240, 0, 80),
                    ]));
        }

        private static RuntimeSceneRect2Snapshot Rect(double x, double y, double width, double height)
            => new(
                new RuntimeSceneVector2Snapshot(x, y),
                new RuntimeSceneVector2Snapshot(width, height));

        public Spirectl.Sts2.Core.SceneInspection.RuntimeSceneControlUnhoverResult UnhoverControl(RuntimeSceneControlUnhoverRequestSnapshot request)
        {
            var hasTarget = !string.IsNullOrWhiteSpace(request.NodePath);
            LastQuery = hasTarget
                ? new RuntimeSceneQuery(request.NodePath!, IncludeProperties: true, IncludeComputedTransform: true)
                : LastQuery;
            return Spirectl.Sts2.Core.SceneInspection.RuntimeSceneControlUnhoverResult.Success(
                source: DataSourceKind.Live,
                provisional: false,
                screenType: "combat",
                screenTitle: "Combat",
                screenInstanceId: "screen:combat:live",
                node: hasTarget ? CombatScreenNode(LastQuery!) : null,
                resolvedNodePath: request.NodePath ?? string.Empty,
                presentationElementId: request.PresentationElementId,
                hovered: false,
                pointerPosition: new RuntimeSceneVector2Snapshot(0, 0),
                notes: []);
        }

        public Spirectl.Sts2.Core.SceneInspection.RuntimeTransitionStatusResult GetTransitionStatus(RuntimeTransitionStatusRequestSnapshot request)
            => Spirectl.Sts2.Core.SceneInspection.RuntimeTransitionStatusResult.Success(
                source: DataSourceKind.Live,
                provisional: false,
                screenType: "combat",
                screenTitle: "Combat",
                screenInstanceId: "screen:combat:live",
                quiescent: true,
                blockingCount: 0,
                ignoredInfiniteCount: 1,
                blockers: [],
                notes: ["Ignored running infinite animation loops while determining transition quiescence."]);

        private static RuntimeSceneNodeSnapshot CombatScreenNode(RuntimeSceneQuery request, bool visible = true)
        {
            return new RuntimeSceneNodeSnapshot(
                NodeId: "node:screen:combat:live:/root/CombatScreen",
                NodePath: "/root/CombatScreen",
                Name: "CombatScreen",
                NodeType: "MegaCrit.Sts2.CombatScreen",
                ParentNodePath: "/root",
                OwnerPath: "/root/CombatScreen",
                SceneFilePath: "res://ui/CombatScreen.tscn",
                AttachedScriptPath: "res://scripts/CombatScreenController.cs",
                AttachedScriptType: "MegaCrit.Sts2.CombatScreenController",
                ChildCount: 1,
                Notes: [],
                Properties: request.IncludeProperties ? FixedProperties(visible) : null,
                ComputedTransform: request.IncludeComputedTransform ? FixedComputedTransform() : null);
        }

        private static RuntimeSceneNodeSnapshot HandPanelNode(RuntimeSceneQuery request)
        {
            return new RuntimeSceneNodeSnapshot(
                NodeId: "node:screen:combat:live:/root/CombatScreen/HandPanel",
                NodePath: "/root/CombatScreen/HandPanel",
                Name: "HandPanel",
                NodeType: "MegaCrit.Sts2.HandPanel",
                ParentNodePath: "/root/CombatScreen",
                OwnerPath: "/root/CombatScreen",
                SceneFilePath: null,
                AttachedScriptPath: null,
                AttachedScriptType: null,
                ChildCount: 0,
                Notes: [],
                Properties: request.IncludeProperties ? FixedProperties() : null,
                ComputedTransform: request.IncludeComputedTransform ? FixedComputedTransform() : null);
        }

        private static RuntimeSceneNodePropertiesSnapshot FixedProperties(bool visible = true)
        {
            return new RuntimeSceneNodePropertiesSnapshot(
                Visible: visible,
                EffectiveVisible: visible,
                Position: new RuntimeSceneVector2Snapshot(10, 20),
                GlobalPosition: new RuntimeSceneVector2Snapshot(10, 20),
                Scale: new RuntimeSceneVector2Snapshot(1, 1),
                RotationRadians: 0,
                Size: new RuntimeSceneVector2Snapshot(640, 360),
                PivotOffset: new RuntimeSceneVector2Snapshot(0, 0),
                Anchors: new RuntimeSceneAnchorsSnapshot(0, 0, 1, 1),
                Offsets: new RuntimeSceneOffsetsSnapshot(0, 0, 0, 0),
                ZIndex: 0,
                Modulate: new RuntimeSceneColorSnapshot(1, 1, 1, 1, "#ffffffff"),
                SelfModulate: new RuntimeSceneColorSnapshot(1, 1, 1, 1, "#ffffffff"),
                EffectiveModulate: new RuntimeSceneColorSnapshot(1, 1, 1, 1, "#ffffffff"),
                ClipContents: true,
                TextureRect: new RuntimeSceneTextureRectPropertiesSnapshot(
                    StretchMode: "keep-aspect-centered",
                    ExpandMode: "ignore-size",
                    FlipH: false,
                    FlipV: true),
                Material: new RuntimeSceneMaterialPropertiesSnapshot(
                    Material: new RuntimeSceneResourceRefSnapshot(
                        Field: "Material",
                        ResourcePath: "res://materials/lobby/dim.tres",
                        ResourceType: "Godot.ShaderMaterial",
                        ResourceName: "dim"),
                    UseParentMaterial: false,
                    Shader: new RuntimeSceneResourceRefSnapshot(
                        Field: "Shader",
                        ResourcePath: "res://shaders/ui_dim.gdshader",
                        ResourceType: "Godot.Shader",
                        ResourceName: "ui_dim"),
                    ShaderParameters:
                    [
                        new RuntimeSceneShaderParameterSnapshot(
                            Name: "darken",
                            ValueKind: "number",
                            StringValue: null,
                            NumberValue: 0.5,
                            BoolValue: null,
                            ColorValue: null,
                            Vector2Value: null,
                            ResourceValue: null),
                    ]),
                Layout: null,
                TextureRefs:
                [
                    new RuntimeSceneResourceRefSnapshot(
                        Field: "Texture",
                        ResourcePath: "res://combat/backgrounds/overgrowth/layer.png",
                        ResourceType: "Godot.Texture2D",
                        ResourceName: "layer"),
                ],
                Text: new RuntimeSceneTextPropertiesSnapshot(
                    Text: "A spire label",
                    RawText: "A {0} label",
                    RichTextEnabled: true,
                    Source: "mega-rich-text-label",
                    DiagnosticSurface: "dev.runtime_scene.text",
                    Font: new RuntimeSceneResourceRefSnapshot(
                        Field: "Font",
                        ResourcePath: "res://fonts/kreon_regular.ttf",
                        ResourceType: "Godot.FontFile",
                        ResourceName: "kreon_regular"),
                    FontSize: 24,
                    LineHeight: 28,
                    TextColor: new RuntimeSceneColorSnapshot(1, 1, 1, 1, "#FFFFFFFF"),
                    OutlineColor: new RuntimeSceneColorSnapshot(0, 0, 0, 1, "#000000FF"),
                    OutlineSize: 2,
                    Shadow: new RuntimeSceneTextShadowSnapshot(
                        Color: new RuntimeSceneColorSnapshot(0, 0, 0, 0.5, "#00000080"),
                        Offset: new RuntimeSceneVector2Snapshot(2, 3),
                        Size: 4,
                        OutlineSize: 1,
                        Source: "theme:font_shadow_color",
                        StackedShadows:
                        [
                            new RuntimeSceneStackedTextShadowSnapshot(
                                Index: 0,
                                Color: new RuntimeSceneColorSnapshot(0, 0, 0, 0.25, "#00000040"),
                                Offset: new RuntimeSceneVector2Snapshot(1, 1),
                                OutlineSize: 0.5,
                                Source: "label-settings"),
                        ]),
                    RichTextSpans:
                    [
                        new RuntimeSceneRichTextSpanSnapshot("red", "spire", null),
                    ],
                    Notices:
                    [
                        new RuntimeScenePropertyNoticeSnapshot(
                            Code: "dev_scene_text",
                            Field: "properties.text",
                            Message: "Text diagnostics are developer runtime scene internals."),
                    ],
                    FontWeight: "700",
                    FontStyle: "italic",
                    Recipe: new RuntimeSceneTextRecipeSnapshot(
                        Source: "mega-rich-text-label",
                        AutoSizeEnabled: true,
                        MinFontSizePx: 8,
                        MaxFontSizePx: 100,
                        NominalFontSizePx: 24,
                        RichTextEnabled: true,
                        WrapMode: null,
                        BreakFlags: "mandatory,word-bound",
                        JustificationFlags: "kashida,word-bound",
                        TextOverrunBehavior: "trim-char",
                        HorizontallyBound: false,
                        VerticallyBound: true),
                    FontSizeSource: "mega-text._lastSetSize",
                    AppliedFontSize: 24,
                    ThemeFontSize: 28,
                    ConfiguredMinFontSize: 8,
                    ConfiguredMaxFontSize: 100),
                NinePatch: new RuntimeSceneNinePatchPropertiesSnapshot(
                    Texture: new RuntimeSceneResourceRefSnapshot(
                        Field: "Texture",
                        ResourcePath: "res://combat/backgrounds/overgrowth/layer.png",
                        ResourceType: "Godot.Texture2D",
                        ResourceName: "layer"),
                    DrawCenter: true,
                    PatchMargins: new RuntimeScenePatchMarginsSnapshot(12, 13, 14, 15),
                    AxisStretchHorizontal: "tile-fit",
                    AxisStretchVertical: "stretch",
                    EffectiveModulate: new RuntimeSceneColorSnapshot(0, 0, 0, 0.36, "#0000005c")),
                Notices: [],
                ShowBehindParent: true,
                ZAsRelative: false,
                ClipChildrenMode: 1,
                MouseFilter: 2,
                FocusMode: 1,
                MouseDefaultCursorShape: 3);
        }

        private static RuntimeSceneComputedTransformSnapshot FixedComputedTransform()
        {
            var transform = new RuntimeSceneTransform2DSnapshot(
                XAxis: new RuntimeSceneVector2Snapshot(1, 0),
                YAxis: new RuntimeSceneVector2Snapshot(0, 1),
                Origin: new RuntimeSceneVector2Snapshot(10, 20));

            return new RuntimeSceneComputedTransformSnapshot(
                LocalTransform: transform,
                GlobalTransform: transform,
                GlobalRect: new RuntimeSceneRect2Snapshot(
                    Position: new RuntimeSceneVector2Snapshot(10, 20),
                    Size: new RuntimeSceneVector2Snapshot(640, 360)),
                ViewportClippedRect: new RuntimeSceneRect2Snapshot(
                    Position: new RuntimeSceneVector2Snapshot(10, 20),
                    Size: new RuntimeSceneVector2Snapshot(640, 360)),
                Notices:
                [
                    new RuntimeScenePropertyNoticeSnapshot(
                        Code: "not_applicable",
                        Field: "computedTransform.extraBounds",
                        Message: "extra bounds are not available for this fixture node."),
                ]);
        }
    }

    private sealed class UnsupportedRuntimeSceneProvider : IRuntimeSceneProvider
    {
        public Spirectl.Sts2.Core.SceneInspection.RuntimeSceneTreeResult GetTree(RuntimeSceneQuery request)
            => Spirectl.Sts2.Core.SceneInspection.RuntimeSceneTreeResult.Failure(
                DataSourceKind.Live,
                provisional: false,
                RuntimeSceneFailureCode.NotImplemented,
                "not exercised",
                []);

        public Spirectl.Sts2.Core.SceneInspection.RuntimeSceneNodeResult GetNode(RuntimeSceneQuery request)
            => Spirectl.Sts2.Core.SceneInspection.RuntimeSceneNodeResult.Failure(
                DataSourceKind.Live,
                provisional: false,
                RuntimeSceneFailureCode.NotImplemented,
                "not exercised",
                []);

        public Spirectl.Sts2.Core.SceneInspection.RuntimeSceneSetVisibleResult SetVisible(RuntimeSceneSetVisibleRequestSnapshot request)
            => Spirectl.Sts2.Core.SceneInspection.RuntimeSceneSetVisibleResult.Failure(
                DataSourceKind.Live,
                provisional: false,
                RuntimeSceneFailureCode.InvalidNodePath,
                "Live runtime scene node '/root/PlainNode' does not support visibility mutation.",
                [new RuntimeSceneDetail("node_type", "Godot.Node", "Only Godot CanvasItem nodes expose runtime visibility.")]);

        public Spirectl.Sts2.Core.SceneInspection.RuntimeSceneControlHoverResult HoverControl(RuntimeSceneControlHoverRequestSnapshot request)
            => Spirectl.Sts2.Core.SceneInspection.RuntimeSceneControlHoverResult.Failure(
                DataSourceKind.Live,
                provisional: false,
                RuntimeSceneFailureCode.InvalidNodePath,
                "Live runtime scene node '/root/PlainNode' is not a hoverable Godot Control.",
                [new RuntimeSceneDetail("node_type", "Godot.Node", "Only Godot Control nodes expose pointer hover geometry for dev scene hover.")]);

        public Spirectl.Sts2.Core.SceneInspection.RuntimeSceneControlUnhoverResult UnhoverControl(RuntimeSceneControlUnhoverRequestSnapshot request)
            => Spirectl.Sts2.Core.SceneInspection.RuntimeSceneControlUnhoverResult.Failure(
                DataSourceKind.Live,
                provisional: false,
                RuntimeSceneFailureCode.InvalidNodePath,
                "Live runtime scene node '/root/PlainNode' is not a hoverable Godot Control.",
                [new RuntimeSceneDetail("node_type", "Godot.Node", "Only Godot Control nodes expose pointer hover geometry for dev scene unhover.")]);

        public Spirectl.Sts2.Core.SceneInspection.RuntimeTransitionStatusResult GetTransitionStatus(RuntimeTransitionStatusRequestSnapshot request)
            => Spirectl.Sts2.Core.SceneInspection.RuntimeTransitionStatusResult.Failure(
                DataSourceKind.Live,
                provisional: false,
                RuntimeSceneFailureCode.NotImplemented,
                "not exercised",
                []);
    }

    private sealed class DiagnosticHoverRuntimeSceneProvider : IRuntimeSceneProvider
    {
        public Spirectl.Sts2.Core.SceneInspection.RuntimeSceneTreeResult GetTree(RuntimeSceneQuery request)
            => Spirectl.Sts2.Core.SceneInspection.RuntimeSceneTreeResult.Failure(
                DataSourceKind.Live,
                provisional: false,
                RuntimeSceneFailureCode.NotImplemented,
                "not exercised",
                []);

        public Spirectl.Sts2.Core.SceneInspection.RuntimeSceneNodeResult GetNode(RuntimeSceneQuery request)
            => Spirectl.Sts2.Core.SceneInspection.RuntimeSceneNodeResult.Failure(
                DataSourceKind.Live,
                provisional: false,
                RuntimeSceneFailureCode.NotImplemented,
                "not exercised",
                []);

        public Spirectl.Sts2.Core.SceneInspection.RuntimeSceneSetVisibleResult SetVisible(RuntimeSceneSetVisibleRequestSnapshot request)
            => Spirectl.Sts2.Core.SceneInspection.RuntimeSceneSetVisibleResult.Failure(
                DataSourceKind.Live,
                provisional: false,
                RuntimeSceneFailureCode.NotImplemented,
                "not exercised",
                []);

        public Spirectl.Sts2.Core.SceneInspection.RuntimeSceneControlHoverResult HoverControl(RuntimeSceneControlHoverRequestSnapshot request)
            => Spirectl.Sts2.Core.SceneInspection.RuntimeSceneControlHoverResult.Failure(
                DataSourceKind.Live,
                provisional: false,
                RuntimeSceneFailureCode.RuntimeFailure,
                "failed to hover the requested live runtime scene control.",
                [
                    new RuntimeSceneDetail("outer_exception_type", typeof(System.Reflection.TargetInvocationException).FullName!, "Exception type caught by the hover dispatcher."),
                    new RuntimeSceneDetail("outer_exception_message", "Exception has been thrown by the target of an invocation.", "Exception message caught by the hover dispatcher."),
                    new RuntimeSceneDetail("root_exception_type", typeof(InvalidOperationException).FullName!, "Innermost non-TargetInvocationException type."),
                    new RuntimeSceneDetail("root_exception_message", "hover focus handler failed", "Innermost non-TargetInvocationException message."),
                    new RuntimeSceneDetail("first_stack_frame", "at Spirectl.Sts2.Live.Sts2RuntimeSceneProvider.HoverControlOnMainThread(RuntimeSceneControlHoverRequestSnapshot request)", "First stack frame outside reflection/invocation wrappers."),
                    new RuntimeSceneDetail("requested_node_path", request.NodePath, "Node path requested by dev scene hover."),
                    new RuntimeSceneDetail("resolved_node_path", request.NodePath, "Normalized node path resolved before the hover failure."),
                    new RuntimeSceneDetail("hover_position", "320,180", "Pointer position computed before the hover failure."),
                ]);

        public Spirectl.Sts2.Core.SceneInspection.RuntimeSceneControlUnhoverResult UnhoverControl(RuntimeSceneControlUnhoverRequestSnapshot request)
            => Spirectl.Sts2.Core.SceneInspection.RuntimeSceneControlUnhoverResult.Failure(
                DataSourceKind.Live,
                provisional: false,
                RuntimeSceneFailureCode.RuntimeFailure,
                "failed to unhover the requested live runtime scene control.",
                [
                    new RuntimeSceneDetail("exception", typeof(InvalidOperationException).Name, "unfocus handler failed"),
                ]);

        public Spirectl.Sts2.Core.SceneInspection.RuntimeTransitionStatusResult GetTransitionStatus(RuntimeTransitionStatusRequestSnapshot request)
            => Spirectl.Sts2.Core.SceneInspection.RuntimeTransitionStatusResult.Failure(
                DataSourceKind.Live,
                provisional: false,
                RuntimeSceneFailureCode.NotImplemented,
                "not exercised",
                []);
    }

    private sealed class FixedScenarioProvider : IScenarioProvider
    {
        public ScenarioRestoreRequestSnapshot? LastRestoreRequest { get; private set; }

        public ScenarioCaptureResultSnapshot Capture(ScenarioCaptureRequestSnapshot request)
        {
            return ScenarioCaptureResultSnapshot.Success(
                request.RequestId,
                new ScenarioDocumentSnapshot(
                    SchemaVersion: "spirectl.scenario/v0",
                    Name: "shop-anchor",
                    Description: "Captured shop repro.",
                    CreatedAt: "2026-04-24T00:00:00Z",
                    Source: new ScenarioSourceSnapshot(
                        GameVersion: "sts2-live",
                        BridgeVersion: BridgeBuildInfo.BridgeVersion,
                        SpirectlVersion: "0.0.0",
                        Screen: new ScenarioScreenSnapshot("shop", "Shop", "screen:shop:live"),
                        Perspective: new ScenarioPerspectiveSnapshot("local", "p1")),
                    Restore: new ScenarioRestoreSnapshot(
                        Mode: ScenarioRestoreMode.Sparse,
                        Quality: ScenarioRestoreQuality.Partial,
                        ExactBundle: null,
                        CompatibilityNotes: [new ScenarioCompatibilityNoteSnapshot("runtime-queues-omitted", "Hidden runtime queues were not captured.", "")],
                        FieldReports: [CombatTurnReport()]),
                    Run: null,
                    ScreenStateJson: "{\"shop\":{\"gold\":123}}",
                    Notices: [new ScenarioNoticeSnapshot("sparse-export", "Static model defaults are referenced by stable id.", Provisional: false)],
                    Multiplayer: MultiplayerMetadata()),
                DataSourceKind.Live,
                provisional: false,
                new ScenarioExactBundlePayloadSnapshot("bundle.json", "spirectl.scenario.bundle/v0", "application/json", [4, 5, 6]));
        }

        public ScenarioRestoreResultSnapshot Restore(ScenarioRestoreRequestSnapshot request)
        {
            LastRestoreRequest = request;
            return ScenarioRestoreResultSnapshot.Success(
                request.RequestId,
                ScenarioRestoreQuality.Degraded,
                new ScenarioScreenSnapshot("shop", "Shop", "screen:shop:live"),
                new ScenarioPerspectiveSnapshot("local", "p1"),
                [new ScenarioNoticeSnapshot("exact-restore-failed-fallback-used", "Exact bundle restore failed; sparse restore succeeded.", Provisional: false)],
                exactBundleUsed: true,
                sparseFallbackUsed: true,
                [new ScenarioCompatibilityNoteSnapshot("runtime-queues-omitted", "Hidden runtime queues were not restored.", "")],
                verification: FailedVerification(),
                multiplayerRestore: MultiplayerRestoreResult());
        }
    }

    private static RestoreMultiplayerRestoreSnapshot MultiplayerMetadata()
        => new(
            IsMultiplayer: true,
            RestoreMode: RestoreMultiplayerRestoreModeSnapshot.LobbyOnly,
            LocalPlayerId: "p:100",
            HostPlayerId: "p:100",
            LocalPlayerRole: "host",
            Players:
            [
                new RestoreMultiplayerPlayerSnapshot(
                    Id: "p:100",
                    NetId: "100",
                    SlotId: 0,
                    DisplayName: string.Empty,
                    SelectedCharacterId: "ironclad",
                    IsReady: false,
                    IsLocal: true,
                    IsHost: true,
                    IsRemote: false,
                    Character: "ironclad"),
                new RestoreMultiplayerPlayerSnapshot(
                    Id: "p:200",
                    NetId: "200",
                    SlotId: 1,
                    DisplayName: string.Empty,
                    SelectedCharacterId: "silent",
                    IsReady: true,
                    IsLocal: false,
                    IsHost: false,
                    IsRemote: true,
                    Character: "silent"),
            ],
            Lobby: new RestoreMultiplayerLobbySnapshot(
                LobbyId: "start-run",
                Phase: "selecting",
                AvailableCharacters:
                [
                    new RestoreLobbyCharacterSnapshot("ironclad", "Ironclad", IsUnlocked: true),
                ]),
            RequiresRemoteClients: false,
            DegradedLocalOnlyAvailable: false,
            Limitations: []);

    private static RestoreMultiplayerRestoreResultSnapshot MultiplayerRestoreResult()
        => new(
            Mode: RestoreMultiplayerRestoreModeSnapshot.DegradedLocalOnly,
            RemotePlayerMode: "omitted",
            LocalPlayerId: "p:100",
            HostPlayerId: "p:100",
            RestoredPlayerIds: ["p:100"],
            OmittedRemotePlayerIds: ["p:200"],
            RequiresRemoteClients: true);

    private static Spirectl.Sts2.Core.Restore.RestoreFieldReportSnapshot CombatTurnReport()
        => new(
            "combat.turn",
            Spirectl.Sts2.Core.Restore.RestoreFieldFidelity.Exact,
            Spirectl.Sts2.Core.Restore.RestoreFieldFidelity.Exact,
            ValidationKey: true,
            "combat-round-number",
            "Round number is verified after restore.");

    private static Spirectl.Sts2.Core.Restore.RestoreVerificationSnapshot FailedVerification()
        => new(
            Spirectl.Sts2.Core.Restore.RestoreVerificationStatus.Failed,
            "degraded",
            ["combat.turn"],
            [new Spirectl.Sts2.Core.Restore.RestoreMismatchSnapshot("combat.turn", "1", "2", "failed")],
            "{\"combat\":{\"turn\":1}}",
            "{\"combat\":{\"turn\":2}}");

    private sealed class FixedBridgeHost(BridgeHostStatus status) : IBridgeHost
    {
        public BridgeHostStatus DescribeStatus() => status;
    }

    private sealed class WarningHotReloadControl : IHotReloadControl
    {
        public HotReloadStatusResultSnapshot GetStatus(HotReloadStatusRequestSnapshot request)
            => HotReloadStatusResultSnapshot.Success(Status(), []);

        public Task<HotReloadOperationResult> RequestReloadAsync(HotReloadRequestSnapshot request)
            => Task.FromResult(HotReloadOperationResult.Success(
                Status(),
                new HotReloadReportSnapshot(
                    Status: "loaded",
                    Generation: 2,
                    RequestedAt: "2026-04-24T00:00:00Z",
                    SourceAssemblyPath: request.LogicArtifactPath,
                    ShadowAssemblyPath: "/tmp/CouchCoop.Logic.dll",
                    ContractVersion: request.ExpectedContractVersion,
                    LogicAssemblyName: "CouchCoop.Logic",
                    EntryType: "CouchCoop.LogicEntry",
                    PreviousGeneration: 1,
                    PreviousRemainsActive: false,
                    PreviousDisposed: true,
                    PreviousUnloadRequested: true,
                    PreviousCollected: true,
                    DurationMs: 12,
                    Error: null,
                    Warnings: [new HotReloadWarningSnapshot("reload_scheduled", null!, "Reload was scheduled on the main thread.")]),
                accepted: true,
                notices: []));

        private static HotReloadShellStatusSnapshot Status()
            => new(
                Supported: true,
                Protocol: new HotReloadProtocolSnapshot("spirectl.m57.hot-reload-shell", 0),
                ShellModId: "warning-shell",
                ShellProtocolVersion: 0,
                ActiveGeneration: 1,
                ExpectedLogicArtifactPath: "/tmp/MyHotMod.Logic.dll",
                ContractVersion: 0,
                ReloadInProgress: false,
                LastReloadReport: null,
                RestartRequired: false,
                Notices: []);
    }

}
