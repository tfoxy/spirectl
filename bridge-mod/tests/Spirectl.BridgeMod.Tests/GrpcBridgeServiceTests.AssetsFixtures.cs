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
    [Fact]
    public void AssetExtractReturnsNotImplementedForScaffoldRuntime()
    {
        var service = new GrpcBridgeService(BridgeRuntimeBootstrap.CreateScaffold());

        var result = service.HandleExtractAsset(new AssetExtractRequest
        {
            RequestId = "asset-1",
            SourceRoot = "resources",
            SourcePath = "ui/shared/HandPanel.tscn",
            LoadPath = "res://ui/shared/HandPanel.tscn",
            OutputFormat = "png",
        });

        Assert.NotNull(result.Error);
        Assert.Equal(BridgeErrorCode.NotImplemented, result.Error.Code);
    }

    [Fact]
    public void AssetExtractFailurePreservesProviderNotesAsErrorDetails()
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
            AssetExtractProvider = new FailingAssetExtractProvider(),
            ScreenshotProvider = new FixedScreenshotProvider(),
            BridgeHost = new FixedBridgeHost(new BridgeHostStatus("ipc", true, "Live IPC bridge host running.")),
        });
        var service = new GrpcBridgeService(runtime);

        var result = service.HandleExtractAsset(new AssetExtractRequest
        {
            RequestId = "asset-failure",
            SourceRoot = "virtual",
            SourcePath = "composed://encounters/kaiser_crab_boss/visual-state/rocket-charge-up/overlay/image",
            LoadPath = "composed://encounters/kaiser_crab_boss/visual-state/rocket-charge-up/overlay/image",
            OutputFormat = "png",
        });

        Assert.NotNull(result.Error);
        Assert.Equal(BridgeErrorCode.RuntimeFailure, result.Error.Code);
        Assert.Contains(result.Error.Details, detail =>
            detail.Field == "note"
            && detail.Note == "Resolved and kept catalog selector '%ArmBoneR' for visual part 'rocket'.");
        Assert.Contains(result.Error.Details, detail =>
            detail.Field == "scene"
            && detail.Note.Contains("fully transparent pixels", StringComparison.Ordinal));
        var diagnosticDetail = Assert.Single(result.Error.Details, detail => detail.Diagnostic is not null);
        Assert.Equal("asset-failure", diagnosticDetail.Diagnostic.Fields["requestId"].StringValue);
        Assert.Equal("overlay:rocket-charge-up", diagnosticDetail.Diagnostic.Fields["renderTargetId"].StringValue);
        Assert.True(diagnosticDetail.Diagnostic.Fields["readyHookRan"].BoolValue);
        Assert.Equal("rocket", diagnosticDetail.Diagnostic.Fields["keptPartIds"].ListValue.Values[0].StringValue);
        Assert.Equal(1, diagnosticDetail.Diagnostic.Fields["alphaEvidence"].StructValue.Fields["transparentPixelRatio"].NumberValue);
    }

    [Fact]
    public void AssetExtractReturnsStructuredRenderMetadata()
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
            AssetExtractProvider = new FixedAssetExtractProvider(),
            ScreenshotProvider = new FixedScreenshotProvider(),
            BridgeHost = new FixedBridgeHost(new BridgeHostStatus("ipc", true, "Live IPC bridge host running.")),
        });
        var service = new GrpcBridgeService(runtime);

        var result = service.HandleExtractAsset(new AssetExtractRequest
        {
            RequestId = "asset-2",
            SourceRoot = "resources",
            SourcePath = "ui/shared/HandPanel.tscn",
            LoadPath = "res://ui/shared/HandPanel.tscn",
            OutputFormat = "png",
        });

        Assert.NotNull(result.Success);
        Assert.Equal("asset-2", result.Success.RequestId);
        Assert.Equal(DataSource.Live, result.Success.Source);
        Assert.False(result.Success.Provisional);
        Assert.Equal("png", result.Success.Format);
        Assert.Equal((uint)2, result.Success.Width);
        Assert.Equal((uint)1, result.Success.Height);
        Assert.Equal("image/png", result.Success.ContentType);
        Assert.Equal("flattened-first-frame", result.Success.RenderMode);
        Assert.NotNull(result.Success.Provenance);
        Assert.Equal("resources", result.Success.Provenance.SourceRoot);
        Assert.Equal("ui/shared/HandPanel.tscn", result.Success.Provenance.SourcePath);
        Assert.Equal("res://ui/shared/HandPanel.tscn", result.Success.Provenance.LoadPath);
        Assert.Equal("flattened-first-frame", result.Success.Provenance.RenderMode);
        Assert.Equal("live", result.Success.Provenance.SourceKind);
        Assert.NotNull(result.Success.PlacementMetadata);
        Assert.Equal("res://ui/shared/HandPanel.tscn", result.Success.PlacementMetadata.SourceScenePath);
        Assert.Equal("/Root/Spine", result.Success.PlacementMetadata.SourceNodePath);
        Assert.Equal((uint)2, result.Success.PlacementMetadata.OutputWidth);
        Assert.Equal((uint)1, result.Success.PlacementMetadata.OutputHeight);
        Assert.Equal("flattened-first-frame", result.Success.PlacementMetadata.RenderMode);
        Assert.Equal(20, result.Success.PlacementMetadata.LocalBounds.Width);
        Assert.Equal(40, result.Success.PlacementMetadata.GlobalBounds.Height);
        Assert.Equal("Rendered first frame of animated resource.", result.Success.Notes[0]);
        Assert.NotEmpty(result.Success.Notices);
        Assert.Equal("asset_extract_notice", result.Success.Notices[0].Code);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4e, 0x47 }, result.Success.Contents.ToByteArray());
    }

    [Fact]
    public void AssetExtractReturnsStructuredTimelineMetadata()
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
            AssetExtractProvider = new FixedTimelineAssetExtractProvider(),
            ScreenshotProvider = new FixedScreenshotProvider(),
            BridgeHost = new FixedBridgeHost(new BridgeHostStatus("ipc", true, "Live IPC bridge host running.")),
        });
        var service = new GrpcBridgeService(runtime);

        var result = service.HandleExtractAsset(new AssetExtractRequest
        {
            RequestId = "asset-timeline",
            SourceRoot = "resources",
            SourcePath = "ui/shared/anim.tres",
            LoadPath = "res://ui/shared/anim.tres",
            OutputFormat = "png",
        });

        Assert.NotNull(result.Success);
        Assert.Equal(AssetArtifactKind.Timeline, result.Success.ArtifactKind);
        Assert.Equal("png", result.Success.Format);
        Assert.Equal((uint)2, result.Success.FrameCount);
        Assert.Equal((uint)175, result.Success.DurationMs);
        Assert.Equal("timeline-frames", result.Success.RenderMode);
        Assert.Equal("image/png", result.Success.ContentType);
        Assert.Equal("image/png", result.Success.Frames[0].ContentType);
        Assert.Equal("image/png", result.Success.Frames[1].ContentType);
        Assert.NotNull(result.Success.Provenance);
        Assert.Equal("resources", result.Success.Provenance.SourceRoot);
        Assert.Equal("res://ui/shared/anim.tres", result.Success.Provenance.LoadPath);
        Assert.Equal("timeline-frames", result.Success.Provenance.RenderMode);
        Assert.NotEmpty(result.Success.Notices);
        Assert.Equal((uint)100, result.Success.Frames[0].DurationMs);
        Assert.Equal((uint)75, result.Success.Frames[1].DurationMs);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4e, 0x47 }, result.Success.Frames[0].Contents.ToByteArray());
    }

    [Fact]
    public void AssetExplainReturnsNotImplementedForScaffoldRuntime()
    {
        var service = new GrpcBridgeService(BridgeRuntimeBootstrap.CreateScaffold());

        var result = service.HandleExplainAsset(new AssetExplainRequest
        {
            RequestId = "explain-1",
            SourceRoot = "resources",
            SourcePath = "scenes/backgrounds/overgrowth/overgrowth_background.tscn",
            LoadPath = "composed://combat-background/overgrowth/image",
        });

        Assert.NotNull(result.Error);
        Assert.Equal(BridgeErrorCode.NotImplemented, result.Error.Code);
    }

    [Fact]
    public void AssetExplainReturnsStructuredCompositionMetadata()
    {
        var snapshot = new GameStateSnapshot(
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
            Debug: null);
        var runtime = BridgeRuntime.Create(new BridgeRuntimeOptions
        {
            StateExtractor = new FixedSnapshotExtractor(snapshot),
            ActionHandler = new PlaceholderActionHandler(),
            LogStream = new InMemoryLogStream(),
            PerspectiveProvider = new DefaultPerspectiveProvider(),
            AssetExtractProvider = new FixedAssetExtractProvider(),
            ScreenshotProvider = new FixedScreenshotProvider(),
            BridgeHost = new FixedBridgeHost(new BridgeHostStatus("ipc", true, "Live IPC bridge host running.")),
        });
        var service = new GrpcBridgeService(runtime);

        var result = service.HandleExplainAsset(new AssetExplainRequest
        {
            RequestId = "explain-2",
            SourceRoot = "resources",
            SourcePath = "scenes/backgrounds/overgrowth/overgrowth_background.tscn",
            LoadPath = "composed://combat-background/overgrowth/image",
        });

        Assert.NotNull(result.Success);
        Assert.Equal("explain-2", result.Success.RequestId);
        Assert.Equal(DataSource.Live, result.Success.Source);
        Assert.Equal("overgrowth", result.Success.RootScene.BackgroundId);
        Assert.Equal("Layer_00", result.Success.Placeholders[0].Name);
        Assert.Equal("deterministic-first-sorted", result.Success.LayerGroups[0].SelectionSource);
        Assert.Equal("flattened-combat-background-composed", result.Success.Render.RenderMode);
        Assert.Equal("not-active", result.Success.ActiveScene.Status);
        Assert.Equal("missing-layer", result.Success.Warnings[0].Code);
    }

    [Fact]
    public void LoadFixtureReturnsNotImplementedForScaffoldRuntime()
    {
        var service = new GrpcBridgeService(BridgeRuntimeBootstrap.CreateScaffold());

        var result = service.HandleLoadFixture(new FixtureLoadRequest
        {
            RequestId = "fixture-1",
            SchemaVersion = "spirectl.fixture/v0",
            FixtureName = "basic-combat",
            SourcePath = "/repo/fixtures/basic-combat.sts2.fixture.yaml",
            FixtureJson = "{\"screen\":\"combat\"}",
        });

        Assert.NotNull(result.Error);
        Assert.Equal(BridgeErrorCode.NotImplemented, result.Error.Code);
        Assert.Contains(result.Error.Details, detail => detail.Field == "command" && detail.Value == "dev fixture load");
    }

    [Fact]
    public void LoadFixtureReturnsStructuredFixtureMetadata()
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
            FixtureLoader = new FixedFixtureLoader(),
            ScreenshotProvider = new FixedScreenshotProvider(),
            BridgeHost = new FixedBridgeHost(new BridgeHostStatus("ipc", true, "Live IPC bridge host running.")),
        });
        var service = new GrpcBridgeService(runtime);

        var result = service.HandleLoadFixture(new FixtureLoadRequest
        {
            RequestId = "fixture-1",
            SchemaVersion = "spirectl.fixture/v0",
            FixtureName = "basic-combat",
            SourcePath = "/repo/fixtures/basic-combat.sts2.fixture.yaml",
            FixtureJson = "{\"screen\":\"combat\"}",
        });

        Assert.NotNull(result.Success);
        Assert.Equal("fixture-1", result.Success.RequestId);
        Assert.Equal("basic-combat", result.Success.FixtureName);
        Assert.Equal("/repo/fixtures/basic-combat.sts2.fixture.yaml", result.Success.SourcePath);
        Assert.Equal(DataSource.Live, result.Success.Source);
        Assert.False(result.Success.Provisional);
        Assert.Equal("combat", result.Success.Screen.Id);
        Assert.Equal("screen:combat:live", result.Success.Screen.ScreenInstanceId);
        Assert.Equal(PerspectiveScope.Local, result.Success.ResolvedPerspective.Scope);
        Assert.Equal("p1", result.Success.ResolvedPerspective.PlayerId);
        Assert.Equal("fixture.loaded", result.Success.Notices[0].Code);
        Assert.Equal("combat-recipe", result.Success.RecipeReport.RecipeName);
        Assert.Equal("run.currentRoom.combat.encounterId", result.Success.RecipeReport.AppliedFields[0].FieldPath);
        Assert.Equal("applied_authored_field", result.Success.RecipeReport.AppliedFields[0].ReasonCode);
        Assert.Equal("run.seed", result.Success.RecipeReport.InferredFields[0].FieldPath);
        Assert.Equal("passed", result.Success.RecipeReport.BridgeValidation.Status);
    }

    [Fact]
    public void LoadFixtureRejectsUnsupportedSchemaVersionBeforeCallingLoader()
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
            FixtureLoader = new ThrowingFixtureLoader(),
            ScreenshotProvider = new FixedScreenshotProvider(),
            BridgeHost = new FixedBridgeHost(new BridgeHostStatus("ipc", true, "Live IPC bridge host running.")),
        });
        var service = new GrpcBridgeService(runtime);

        var result = service.HandleLoadFixture(new FixtureLoadRequest
        {
            RequestId = "fixture-1",
            SchemaVersion = "spirectl.fixture/unsupported",
            FixtureName = "basic-combat",
            SourcePath = "/repo/fixtures/basic-combat.sts2.fixture.yaml",
            FixtureJson = "{\"screen\":\"combat\"}",
        });

        Assert.NotNull(result.Error);
        Assert.Equal(BridgeErrorCode.InvalidFixture, result.Error.Code);
        Assert.Equal("schema_version", result.Error.Details[0].Field);
    }

    [Fact]
    public void LoadFixtureRejectsUnsupportedScreenBeforeCallingLoader()
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
            FixtureLoader = new ThrowingFixtureLoader(),
            ScreenshotProvider = new FixedScreenshotProvider(),
            BridgeHost = new FixedBridgeHost(new BridgeHostStatus("ipc", true, "Live IPC bridge host running.")),
        });
        var service = new GrpcBridgeService(runtime);

        var result = service.HandleLoadFixture(new FixtureLoadRequest
        {
            RequestId = "fixture-1",
            SchemaVersion = "spirectl.fixture/v0",
            FixtureName = "basic-combat",
            SourcePath = "/repo/fixtures/basic-combat.sts2.fixture.yaml",
            FixtureJson = "{\"screen\":\"unknown-screen\"}",
        });

        Assert.NotNull(result.Error);
        Assert.Equal(BridgeErrorCode.InvalidFixture, result.Error.Code);
        Assert.Equal("screen", result.Error.Details[0].Field);
    }

    [Fact]
    public void LoadFixturePropagatesStructuredLobbyValidationFailures()
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
            FixtureLoader = new StructuredInvalidFixtureLoader(),
            ScreenshotProvider = new FixedScreenshotProvider(),
            BridgeHost = new FixedBridgeHost(new BridgeHostStatus("ipc", true, "Live IPC bridge host running.")),
        });
        var service = new GrpcBridgeService(runtime);

        var result = service.HandleLoadFixture(new FixtureLoadRequest
        {
            RequestId = "fixture-1",
            SchemaVersion = "spirectl.fixture/v0",
            FixtureName = "basic-lobby",
            SourcePath = "/repo/fixtures/basic-lobby.sts2.fixture.yaml",
            FixtureJson = """
            {
              "schemaVersion": "spirectl.fixture/v0",
              "screen": "Screens.CharacterSelect.NCharacterSelectScreen",
              "name": "basic-lobby",
              "characterSelect": {
                "kind": "start-run",
                "lobby": {
                  "localPlayerId": "p:1",
                  "hostPlayerId": "p:1",
                  "players": [{ "id": "p:1", "characterId": "ironclad", "slotId": 0 }]
                },
                "characterButtons": [
                  { "characterId": "ironclad", "isLocked": true },
                  { "characterId": "missing-character", "isLocked": true }
                ]
              }
            }
            """,
        });

        Assert.NotNull(result.Error);
        Assert.Equal(BridgeErrorCode.InvalidFixture, result.Error.Code);
        Assert.Equal("characterSelect.characterButtons[1].characterId", result.Error.Details[0].Field);
        Assert.Equal("missing-character", result.Error.Details[0].Value);
        Assert.Equal("Valid values: IRONCLAD.", result.Error.Details[0].Note);
    }

    [Fact]
    public void LoadFixtureAllowsExpandedEventRoomOptionsThroughToLoader()
    {
        var loader = new RecordingFixtureLoader();
        var service = CreateFixtureService(loader);

        var result = service.HandleLoadFixture(new FixtureLoadRequest
        {
            RequestId = "fixture-1",
            SchemaVersion = "spirectl.fixture/v0",
            FixtureName = "basic-event-room",
            SourcePath = "/repo/fixtures/basic-event-room.sts2.fixture.yaml",
            FixtureJson = """
            {
              "schemaVersion": "spirectl.fixture/v0",
              "screen": "Rooms.NEventRoom",
              "name": "basic-event-room",
              "run": {
                "seed": "fixture-basic-event-room",
                "currentActIndex": 0,
                "actFloor": 7,
                "view": { "playerId": "p1" },
                "players": [{ "id": "p1", "characterId": "ironclad" }],
                "currentRoom": {
                  "event": {
                    "canonicalEventModelId": "golden-idol",
                    "playerStates": [
                      { "playerId": "p1", "options": [{ "id": "gain-gold", "titleText": "Take 75 Gold" }] }
                    ]
                  }
                }
              }
            }
            """,
        });

        Assert.NotNull(result.Success);
        Assert.Equal("basic-event-room", loader.LastRequest?.FixtureName);
        using var canonical = JsonDocument.Parse(loader.LastRequest!.FixtureJson);
        Assert.Equal("Rooms.NEventRoom", canonical.RootElement.GetProperty("screen").GetString());
        var options = canonical.RootElement
            .GetProperty("run")
            .GetProperty("currentRoom")
            .GetProperty("event")
            .GetProperty("playerStates")[0]
            .GetProperty("options")
            .EnumerateArray()
            .ToArray();
        Assert.Single(options);
        Assert.Equal("gain-gold", options[0].GetProperty("id").GetString());
        Assert.Equal("Take 75 Gold", options[0].GetProperty("titleText").GetString());
    }

    [Fact]
    public void LoadFixtureAllowsStateShapedEventOptionsThroughToLoader()
    {
        var loader = new RecordingFixtureLoader();
        var service = CreateFixtureService(loader);

        var result = service.HandleLoadFixture(new FixtureLoadRequest
        {
            RequestId = "fixture-1",
            SchemaVersion = "spirectl.fixture/v0",
            FixtureName = "event-crystal-sphere-grid",
            SourcePath = "/repo/fixtures/event-crystal-sphere-grid.sts2.fixture.yaml",
            FixtureJson = """
            {
              "schemaVersion": "spirectl.fixture/v0",
              "screen": "Events.Custom.CrystalSphere.NCrystalSphereScreen",
              "name": "event-crystal-sphere-grid",
              "run": {
                "seed": "event-crystal-sphere-grid",
                "currentActIndex": 0,
                "actFloor": 1,
                "view": { "playerId": "p:1" },
                "players": [{ "id": "p:1", "characterId": "IRONCLAD" }],
                "currentRoom": {
                  "event": {
                    "canonicalEventModelId": "CRYSTAL_SPHERE",
                    "playerStates": [
                      {
                        "options": [
                          { "textKey": "CRYSTAL_SPHERE.pages.INITIAL.options.UNCOVER_FUTURE", "wasChosen": false },
                          { "textKey": "CRYSTAL_SPHERE.pages.INITIAL.options.PAYMENT_PLAN", "wasChosen": true }
                        ]
                      }
                    ]
                  }
                }
              }
            }
            """,
        });

        Assert.NotNull(result.Success);
        using var canonical = JsonDocument.Parse(loader.LastRequest!.FixtureJson);
        Assert.Equal(
            "Events.Custom.CrystalSphere.NCrystalSphereScreen",
            canonical.RootElement.GetProperty("screen").GetString());
        var options = canonical.RootElement
            .GetProperty("run")
            .GetProperty("currentRoom")
            .GetProperty("event")
            .GetProperty("playerStates")[0]
            .GetProperty("options")
            .EnumerateArray()
            .ToArray();
        Assert.Equal("CRYSTAL_SPHERE.pages.INITIAL.options.UNCOVER_FUTURE", options[0].GetProperty("textKey").GetString());
        Assert.False(options[0].GetProperty("wasChosen").GetBoolean());
        Assert.Equal("CRYSTAL_SPHERE.pages.INITIAL.options.PAYMENT_PLAN", options[1].GetProperty("textKey").GetString());
        Assert.True(options[1].GetProperty("wasChosen").GetBoolean());
    }

    [Fact]
    public void LoadFixtureAllowsExpandedShopOverridesThroughToLoader()
    {
        var loader = new RecordingFixtureLoader();
        var service = CreateFixtureService(loader);

        var result = service.HandleLoadFixture(new FixtureLoadRequest
        {
            RequestId = "fixture-1",
            SchemaVersion = "spirectl.fixture/v0",
            FixtureName = "basic-shop",
            SourcePath = "/repo/fixtures/basic-shop.sts2.fixture.yaml",
            FixtureJson = """
            {
              "schemaVersion": "spirectl.fixture/v0",
              "screen": "Screens.Shops.NMerchantInventory",
              "name": "basic-shop",
              "run": {
                "seed": "fixture-basic-shop",
                "currentActIndex": 0,
                "actFloor": 5,
                "view": { "playerId": "p1" },
                "players": [{ "id": "p1", "characterId": "ironclad", "gold": 175 }],
                "currentRoom": {
                  "shop": {
                    "inventory": {
                      "characterCardEntries": [{ "card": { "modelId": "strike" } }],
                      "relicEntries": [{ "modelId": "anchor" }],
                      "potionEntries": [{ "modelId": "fire-potion" }],
                      "cardRemovalEntry": { "used": false }
                    }
                  }
                }
              }
            }
            """,
        });

        Assert.NotNull(result.Success);
        Assert.Equal("basic-shop", loader.LastRequest?.FixtureName);
        using var canonical = JsonDocument.Parse(loader.LastRequest!.FixtureJson);
        var run = canonical.RootElement.GetProperty("run");
        var inventory = run.GetProperty("currentRoom").GetProperty("shop").GetProperty("inventory");
        Assert.Equal(175, run.GetProperty("players")[0].GetProperty("gold").GetInt32());
        Assert.Equal("strike", inventory.GetProperty("characterCardEntries")[0].GetProperty("card").GetProperty("modelId").GetString());
        Assert.Equal("anchor", inventory.GetProperty("relicEntries")[0].GetProperty("modelId").GetString());
        Assert.Equal("fire-potion", inventory.GetProperty("potionEntries")[0].GetProperty("modelId").GetString());
        Assert.False(inventory.GetProperty("cardRemovalEntry").GetProperty("used").GetBoolean());
    }

    [Fact]
    public void LoadFixtureAllowsPlayerPotionIdsThroughToLoader()
    {
        var loader = new RecordingFixtureLoader();
        var service = CreateFixtureService(loader);

        var result = service.HandleLoadFixture(new FixtureLoadRequest
        {
            RequestId = "fixture-1",
            SchemaVersion = "spirectl.fixture/v0",
            FixtureName = "combat-potion-target",
            SourcePath = "/repo/fixtures/combat-potion-target.sts2.fixture.yaml",
            FixtureJson = """
            {
              "schemaVersion": "spirectl.fixture/v0",
              "screen": "combat",
              "name": "combat-potion-target",
              "run": {
                "seed": "fixture-combat-potion-target",
                "currentActIndex": 0,
                "actFloor": 1,
                "view": { "playerId": "p:1" },
                "players": [
                  { "id": "p:1", "characterId": "IRONCLAD", "potions": [{ "modelId": "FIRE_POTION" }, { "modelId": "POTION_OF_BINDING" }] },
                  { "id": "p:2", "characterId": "IRONCLAD" }
                ],
                "currentRoom": { "combat": { "encounterId": "NIBBITS_WEAK" } }
              }
            }
            """,
        });

        Assert.NotNull(result.Success);
        Assert.Equal("combat-potion-target", loader.LastRequest?.FixtureName);
        using var canonical = JsonDocument.Parse(loader.LastRequest!.FixtureJson);
        var players = canonical.RootElement.GetProperty("run").GetProperty("players").EnumerateArray().ToArray();
        var potions = players[0].GetProperty("potions").EnumerateArray().ToArray();
        Assert.Equal("FIRE_POTION", potions[0].GetProperty("modelId").GetString());
        Assert.Equal("POTION_OF_BINDING", potions[1].GetProperty("modelId").GetString());
        Assert.False(players[1].TryGetProperty("potions", out _));
    }

    [Fact]
    public void LoadFixtureAllowsGeneratedShopInventoryThroughToLoader()
    {
        var loader = new RecordingFixtureLoader();
        var service = CreateFixtureService(loader);

        var result = service.HandleLoadFixture(new FixtureLoadRequest
        {
            RequestId = "fixture-1",
            SchemaVersion = "spirectl.fixture/v0",
            FixtureName = "basic-shop",
            SourcePath = "/repo/fixtures/basic-shop.sts2.fixture.yaml",
            FixtureJson = """
            {
              "schemaVersion": "spirectl.fixture/v0",
              "screen": "Screens.Shops.NMerchantInventory",
              "name": "basic-shop",
              "run": {
                "seed": "fixture-basic-shop",
                "currentActIndex": 0,
                "actFloor": 5,
                "view": { "playerId": "p1" },
                "players": [{ "id": "p1", "characterId": "ironclad" }],
                "currentRoom": { "shop": {} }
              }
            }
            """,
        });

        Assert.NotNull(result.Success);
        Assert.Equal("basic-shop", loader.LastRequest?.FixtureName);
        using var canonical = JsonDocument.Parse(loader.LastRequest!.FixtureJson);
        Assert.Equal("Screens.Shops.NMerchantInventory", canonical.RootElement.GetProperty("screen").GetString());
        Assert.False(canonical.RootElement.GetProperty("run").GetProperty("currentRoom").GetProperty("shop").TryGetProperty("inventory", out _));
    }

    [Fact]
    public void LoadFixtureAllowsExpandedLoadRunLobbyThroughToLoader()
    {
        var loader = new RecordingFixtureLoader();
        var service = CreateFixtureService(loader);

        var result = service.HandleLoadFixture(new FixtureLoadRequest
        {
            RequestId = "fixture-1",
            SchemaVersion = "spirectl.fixture/v0",
            FixtureName = "basic-load-run-lobby",
            SourcePath = "/repo/fixtures/basic-load-run-lobby.sts2.fixture.yaml",
            FixtureJson = """
            {
              "schemaVersion": "spirectl.fixture/v0",
              "screen": "Screens.CharacterSelect.NMultiplayerLoadGameScreen",
              "name": "basic-load-run-lobby",
              "characterSelect": {
                "kind": "load-run",
                "view": { "playerId": "p:1" },
                "lobby": {
                  "localPlayerId": "p:1",
                  "hostPlayerId": "p:1",
                  "seed": "fixture-basic-load-run-lobby",
                  "players": [
                    { "id": "p:1", "characterId": "ironclad", "slotId": 0 },
                    { "id": "p:2", "characterId": "silent", "isReady": true, "slotId": 1 }
                  ]
                }
              }
            }
            """,
        });

        Assert.NotNull(result.Success);
        Assert.Equal("basic-load-run-lobby", loader.LastRequest?.FixtureName);
        using var canonical = JsonDocument.Parse(loader.LastRequest!.FixtureJson);
        var characterSelect = canonical.RootElement.GetProperty("characterSelect");
        var lobby = characterSelect.GetProperty("lobby");
        Assert.Equal("load-run", characterSelect.GetProperty("kind").GetString());
        Assert.Equal("p:1", lobby.GetProperty("hostPlayerId").GetString());
        Assert.Equal("silent", lobby.GetProperty("players")[1].GetProperty("characterId").GetString());
    }

    [Fact]
    public void LoadFixtureAllowsOpenedTreasureRoomProceedOverrideThroughToLoader()
    {
        var loader = new RecordingFixtureLoader();
        var service = CreateFixtureService(loader);

        var result = service.HandleLoadFixture(new FixtureLoadRequest
        {
            RequestId = "fixture-1",
            SchemaVersion = "spirectl.fixture/v0",
            FixtureName = "basic-opened-treasure-room",
            SourcePath = "/repo/fixtures/treasure-opened.sts2.fixture.yaml",
            FixtureJson = """
            {
              "schemaVersion": "spirectl.fixture/v0",
              "screen": "Rooms.NTreasureRoom",
              "name": "basic-opened-treasure-room",
              "run": {
                "seed": "fixture-basic-opened-treasure-room",
                "currentActIndex": 1,
                "actFloor": 9,
                "view": { "playerId": "p1" },
                "players": [{ "id": "p1", "characterId": "ironclad" }],
                "currentRoom": {
                  "treasure": {
                    "currentRelicsActive": true,
                    "canProceed": true
                  }
                }
              }
            }
            """,
        });

        Assert.NotNull(result.Success);
        Assert.Equal("basic-opened-treasure-room", loader.LastRequest?.FixtureName);
        using var canonical = JsonDocument.Parse(loader.LastRequest!.FixtureJson);
        var treasure = canonical.RootElement.GetProperty("run").GetProperty("currentRoom").GetProperty("treasure");
        Assert.True(treasure.GetProperty("currentRelicsActive").GetBoolean());
        Assert.True(treasure.GetProperty("canProceed").GetBoolean());
    }

    [Fact]
    public void LoadFixtureAllowsCardOverlayRecipeThroughToLoader()
    {
        var loader = new RecordingFixtureLoader();
        var service = CreateFixtureService(loader);

        var result = service.HandleLoadFixture(new FixtureLoadRequest
        {
            RequestId = "fixture-1",
            SchemaVersion = "spirectl.fixture/v0",
            FixtureName = "basic-card-overlay",
            SourcePath = "/repo/fixtures/basic-card-overlay.sts2.fixture.yaml",
            FixtureJson = """
            {
              "schemaVersion": "spirectl.fixture/v0",
              "screen": "card-overlay",
              "name": "basic-card-overlay",
              "run": {
                "seed": "fixture-basic-card-overlay",
                "currentActIndex": 0,
                "actFloor": 3,
                "view": { "playerId": "p1" },
                "players": [
                  {
                    "id": "p1",
                    "characterId": "ironclad",
                    "overlays": [
                      {
                        "cardOverlay": {
                          "policy": "blocking",
                          "sourceScreen": "combat",
                          "cards": [{ "modelId": "bash" }]
                        }
                      }
                    ]
                  }
                ]
              }
            }
            """,
        });

        Assert.NotNull(result.Success);
        Assert.Equal("basic-card-overlay", loader.LastRequest?.FixtureName);
        using var canonical = JsonDocument.Parse(loader.LastRequest!.FixtureJson);
        var overlay = canonical.RootElement
            .GetProperty("run")
            .GetProperty("players")[0]
            .GetProperty("overlays")[0]
            .GetProperty("cardOverlay");
        Assert.Equal("card-overlay", canonical.RootElement.GetProperty("screen").GetString());
        Assert.Equal("blocking", overlay.GetProperty("policy").GetString());
        Assert.Equal("bash", overlay.GetProperty("cards").EnumerateArray().Single().GetProperty("modelId").GetString());
    }

}
