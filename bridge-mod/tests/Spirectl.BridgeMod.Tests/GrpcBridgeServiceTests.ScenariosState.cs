using Spirectl.Sts2;
using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Artifacts;
using Spirectl.Sts2.Core.ConsoleCommands;
using Spirectl.Sts2.Core.Debugging;
using Spirectl.Sts2.Core.Fixtures;
using Spirectl.Sts2.Core.HotReload;
using Spirectl.Sts2.Core.Lifecycle;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Models;
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
    public void ScenarioCaptureUsesPlaceholderProviderInScaffoldRuntime()
    {
        var service = new GrpcBridgeService(BridgeRuntimeBootstrap.CreateScaffold());

        var result = service.HandleCaptureScenario(new ScenarioCaptureRequest
        {
            RequestId = "scenario-1",
            SchemaVersion = "spirectl.scenario/v0",
        });

        Assert.NotNull(result.Error);
        Assert.Equal(BridgeErrorCode.NotImplemented, result.Error.Code);
        Assert.Contains("requires the live STS2 bridge host", result.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.Error.Details, detail => detail.Field == "command" && detail.Value == "dev scenario export");
    }

    [Fact]
    public void ScenarioCaptureMapsProviderSuccess()
    {
        var runtime = RuntimeWithScenarioProvider(new FixedScenarioProvider());
        var service = new GrpcBridgeService(runtime);

        var result = service.HandleCaptureScenario(new ScenarioCaptureRequest
        {
            RequestId = "scenario-capture-2",
            SchemaVersion = "spirectl.scenario/v0",
            IncludeExact = true,
        });

        Assert.NotNull(result.Success);
        Assert.Equal("scenario-capture-2", result.Success.RequestId);
        Assert.Equal(DataSource.Live, result.Success.Source);
        Assert.False(result.Success.Provisional);
        Assert.Equal("shop-anchor", result.Success.Scenario.Name);
        Assert.Equal(RestoreQuality.Partial, result.Success.Scenario.Restore.Quality);
        Assert.Contains(result.Success.Scenario.Restore.FieldReports, report =>
            report.Path == "combat.turn"
            && report.Capture == RestoreFieldFidelity.Exact
            && report.Restore == RestoreFieldFidelity.Exact
            && report.ValidationKey);
        Assert.Equal("bundle.json", result.Success.ExactBundlePayload.Path);
        Assert.Equal([4, 5, 6], result.Success.ExactBundlePayload.Data.ToByteArray());
    }

    [Fact]
    public void ScenarioCaptureMapsMultiplayerMetadata()
    {
        var runtime = RuntimeWithScenarioProvider(new FixedScenarioProvider());
        var service = new GrpcBridgeService(runtime);

        var result = service.HandleCaptureScenario(new ScenarioCaptureRequest
        {
            RequestId = "scenario-capture-mp",
            SchemaVersion = "spirectl.scenario/v0",
        });

        Assert.NotNull(result.Success);
        Assert.NotNull(result.Success.Scenario.Multiplayer);
        Assert.True(result.Success.Scenario.Multiplayer.IsMultiplayer);
        Assert.Equal(MultiplayerRestoreMode.LobbyOnly, result.Success.Scenario.Multiplayer.RestoreMode);
        Assert.Equal("p:100", result.Success.Scenario.Multiplayer.LocalPlayerId);
        Assert.Equal("p:100", result.Success.Scenario.Multiplayer.HostPlayerId);
        Assert.Equal("host", result.Success.Scenario.Multiplayer.LocalPlayerRole);
        Assert.False(result.Success.Scenario.Multiplayer.RequiresRemoteClients);
        Assert.False(result.Success.Scenario.Multiplayer.DegradedLocalOnlyAvailable);
        Assert.Equal("start-run", result.Success.Scenario.Multiplayer.Lobby.LobbyId);
        Assert.Equal("selecting", result.Success.Scenario.Multiplayer.Lobby.Phase);
        Assert.Contains(result.Success.Scenario.Multiplayer.Lobby.AvailableCharacters, character =>
            character.Id == "ironclad" && character.Name == "Ironclad" && character.IsUnlocked);
        Assert.Contains(result.Success.Scenario.Multiplayer.Players, player =>
            player.Id == "p:200"
            && player.NetId == "200"
            && player.SlotId == 1
            && player.SelectedCharacterId == "silent"
            && player.Character == "silent"
            && player.IsReady
            && player.IsRemote);
    }

    [Fact]
    public void RecordFixtureMapsProviderSuccess()
    {
        var runtime = RuntimeWithRecordedFixtureProvider(new FixedRecordedFixtureProvider());
        var service = new GrpcBridgeService(runtime);

        var result = service.HandleRecordFixture(new RecordedFixtureRequest
        {
            RequestId = "record-fixture-1",
            Perspective = new PerspectiveSelector { Scope = PerspectiveScope.Local, PlayerId = "p1" },
        });

        Assert.NotNull(result.Success);
        Assert.Equal("record-fixture-1", result.Success.RequestId);
        Assert.Equal(DataSource.Live, result.Success.Source);
        Assert.False(result.Success.Provisional);
        Assert.Equal("""{"screen":"combat"}""", result.Success.FixtureJson);
        Assert.Equal("spirectl.recorded-fixture/v0", result.Success.Metadata.SchemaVersion);
        Assert.Equal("combat", result.Success.Metadata.Screen.Id);
        Assert.Equal(RestoreQuality.Partial, result.Success.Metadata.RestoreQuality);
        Assert.Contains(result.Success.Metadata.KnownOmissions, note => note.Code == "rng-continuation-omitted");
        Assert.Contains(result.Success.Metadata.Notices, notice => notice.Code == "screen-entry-fixture");
    }

    [Fact]
    public void RecordFixtureUsesPlaceholderProviderInScaffoldRuntime()
    {
        var service = new GrpcBridgeService(BridgeRuntimeBootstrap.CreateScaffold());

        var result = service.HandleRecordFixture(new RecordedFixtureRequest
        {
            RequestId = "record-fixture-placeholder",
        });

        Assert.NotNull(result.Error);
        Assert.Equal(BridgeErrorCode.NotImplemented, result.Error.Code);
        Assert.Contains(result.Error.Details, detail => detail.Field == "command" && detail.Value == "dev fixture record");
    }

    [Fact]
    public void ScenarioRestoreMapsProviderTelemetry()
    {
        var runtime = RuntimeWithScenarioProvider(new FixedScenarioProvider());
        var service = new GrpcBridgeService(runtime);

        var result = service.HandleRestoreScenario(new ScenarioRestoreRequest
        {
            RequestId = "scenario-restore-1",
            SchemaVersion = "spirectl.scenario/v0",
            Scenario = new ScenarioDocument { SchemaVersion = "spirectl.scenario/v0", Name = "shop-anchor" },
            ExactBundlePayload = new ExactBundlePayload { Path = "bundle.json", Data = Google.Protobuf.ByteString.CopyFrom([4, 5, 6]) },
            AllowSparseFallback = true,
            AllowDegradedLocalMultiplayer = true,
        });

        Assert.NotNull(result.Success);
        Assert.Equal("scenario-restore-1", result.Success.RequestId);
        Assert.Equal(RestoreQuality.Degraded, result.Success.Quality);
        Assert.True(result.Success.ExactBundleUsed);
        Assert.True(result.Success.SparseFallbackUsed);
        Assert.Equal("shop", result.Success.Screen.Id);
        Assert.Equal("p1", result.Success.ResolvedPerspective.PlayerId);
        Assert.Contains(result.Success.Notices, notice => notice.Code == "exact-restore-failed-fallback-used");
        Assert.Contains(result.Success.CompatibilityNotes, note => note.Code == "runtime-queues-omitted");
        Assert.Equal(RestoreVerificationStatus.Failed, result.Success.Verification.Status);
        Assert.Equal(RestoreQuality.Degraded, result.Success.Verification.Quality);
        Assert.Equal("combat.turn", result.Success.Verification.CheckedFields[0]);
        Assert.Equal("combat.turn", result.Success.Verification.Mismatches[0].Path);
        Assert.NotNull(result.Success.MultiplayerRestore);
        Assert.Equal(MultiplayerRestoreMode.DegradedLocalOnly, result.Success.MultiplayerRestore.Mode);
        Assert.Equal("omitted", result.Success.MultiplayerRestore.RemotePlayerMode);
        Assert.Equal(["p:100"], result.Success.MultiplayerRestore.RestoredPlayerIds);
        Assert.Equal(["p:200"], result.Success.MultiplayerRestore.OmittedRemotePlayerIds);
        Assert.True(result.Success.MultiplayerRestore.RequiresRemoteClients);
        Assert.True(((FixedScenarioProvider)runtime.ScenarioProvider).LastRestoreRequest?.AllowDegradedLocalMultiplayer);
    }

    [Fact]
    public void RemovedCheckpointRpcsCollapseToRemovedError()
    {
        var service = new GrpcBridgeService(BridgeRuntimeBootstrap.CreateScaffold());

        var capture = service.HandleCaptureCheckpoint(new CheckpointCaptureRequest { RequestId = "checkpoint-capture-1", Name = "test" });
        var restore = service.HandleRestoreCheckpoint(new CheckpointRestoreRequest { RequestId = "checkpoint-restore-1", Name = "test" });
        var list = service.HandleListCheckpoints(new CheckpointListRequest());
        var delete = service.HandleDeleteCheckpoint(new CheckpointDeleteRequest());

        foreach (var error in new[] { capture.Error, restore.Error, list.Error, delete.Error })
        {
            Assert.NotNull(error);
            Assert.Equal(BridgeErrorCode.NotImplemented, error.Code);
            Assert.Contains("was removed", error.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void RemovedPresentationInspectorRpcsCollapseToRemovedError()
    {
        var service = new GrpcBridgeService(BridgeRuntimeBootstrap.CreateScaffold());

        var scenes = service.HandleInspectPresentationResourceScenes(new PresentationResourceSceneRequest { RequestId = "p-1" });
        var loc = service.HandleInspectPresentationLocalization(new PresentationLocalizationRequest { RequestId = "p-2" });

        foreach (var error in new[] { scenes.Error, loc.Error })
        {
            Assert.NotNull(error);
            Assert.Equal(BridgeErrorCode.NotImplemented, error.Code);
            Assert.Contains("was removed", error.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void CloseGameMapsLifecycleSuccess()
    {
        var runtime = RuntimeWithLifecycle(new FixedLifecycleControl(
            new GameCloseOperationResult(
                Accepted: true,
                Notices: ["Requested SceneTree.Quit()."],
                Error: null)));
        var service = new GrpcBridgeService(runtime);

        var result = service.HandleCloseGame(new GameCloseRequest { RequestId = "close-2" });

        Assert.NotNull(result.Success);
        Assert.Equal("close-2", result.Success.RequestId);
        Assert.True(result.Success.Accepted);
        Assert.Equal(["Requested SceneTree.Quit()."], result.Success.Notices);
    }

    [Fact]
    public void CloseGameMapsLifecycleFailure()
    {
        var runtime = RuntimeWithLifecycle(new FixedLifecycleControl(
            new GameCloseOperationResult(
                Accepted: false,
                Notices: [],
                Error: new LifecycleFailure(
                    LifecycleFailureCode.RuntimeFailure,
                    "SceneTree quit failed.",
                    [new LifecycleFailureDetail("main_loop", "null", "Engine.GetMainLoop() did not return a SceneTree.")]))));
        var service = new GrpcBridgeService(runtime);

        var result = service.HandleCloseGame(new GameCloseRequest { RequestId = "close-3" });

        Assert.NotNull(result.Error);
        Assert.Equal(BridgeErrorCode.RuntimeFailure, result.Error.Code);
        Assert.Equal("SceneTree quit failed.", result.Error.Message);
        Assert.Contains(result.Error.Details, detail => detail.Field == "main_loop" && detail.Value == "null");
    }

    [Fact]
    public void BreakpointAddAndListReturnStructuredStoredBreakpoints()
    {
        var service = new GrpcBridgeService(BridgeRuntimeBootstrap.CreateScaffold());

        var add = service.HandleAddBreakpoint(new DebugBreakpointAddRequest
        {
            QueryPath = "screen.id",
            Name = "menu-break",
            Predicate = new DebugPredicate
            {
                Operator = DebugPredicateOperator.Equals,
                ExpectedJson = "\"main-menu\"",
            },
        });
        var list = service.HandleListBreakpoints(new DebugBreakpointListRequest());

        Assert.NotNull(add.Success);
        Assert.True(add.Success.Added);
        Assert.Equal("screen.id", add.Success.Breakpoint.QueryPath);
        Assert.Equal("menu-break", add.Success.Breakpoint.Name);
        Assert.Equal(DebugPredicateOperator.Equals, add.Success.Breakpoint.Predicate.Operator);
        Assert.Equal("\"main-menu\"", add.Success.Breakpoint.Predicate.ExpectedJson);

        Assert.NotNull(list.Success);
        Assert.Single(list.Success.Breakpoints);
        Assert.Equal(add.Success.Breakpoint.Id, list.Success.Breakpoints[0].Id);
        Assert.Equal(DebugExecutionState.Unsupported, list.Success.Status.ExecutionState);
    }

    [Fact]
    public void BreakpointAddRejectsInvalidQueryFilterSyntax()
    {
        var service = new GrpcBridgeService(BridgeRuntimeBootstrap.CreateScaffold());

        var result = service.HandleAddBreakpoint(new DebugBreakpointAddRequest
        {
            QueryPath = "choices[=]",
            Predicate = new DebugPredicate
            {
                Operator = DebugPredicateOperator.Equals,
                ExpectedJson = "\"menu:start-run\"",
            },
        });

        Assert.NotNull(result.Error);
        Assert.Equal(BridgeErrorCode.InvalidQueryFilter, result.Error.Code);
        Assert.Equal(
            "State query path 'choices[=]' uses an invalid filter expression '[=]'.",
            result.Error.Message);
    }

    [Fact]
    public void StateSerializesCharacterSelectRuntimeContract()
    {
        var runtime = RuntimeWithStateProvider(new StateSnapshot(
            StateSnapshot.CurrentSchemaVersion,
            Language: "es",
            RootScene: "screens/character_select_screen",
            CharacterSelect: new StateCharacterSelectSnapshot(
                new StateCharacterSelectLobbySnapshot(
                    NetGameType: "host",
                    LocalPlayerId: "p:100",
                    HostPlayerId: "p:100",
                    ConnectingPlayerCount: 1,
                    Ascension: 3,
                    MaxAscension: 7,
                    Act1: "random",
                    Seed: null,
                    ModifierIds: ["daily"],
                    Players:
                    [
                        new StateCharacterSelectPlayerSnapshot(
                            "p:100",
                            SlotId: 0,
                            CharacterId: "ironclad",
                            IsReady: false,
                            MaxMultiplayerAscensionUnlocked: 7,
                            DisplayName: "Test Host"),
                    ]),
                CharacterButtons:
                [
                    new StateCharacterButtonSnapshot("ironclad", "ironclad", IsLocked: false),
                    new StateCharacterButtonSnapshot("silent", "silent", IsLocked: true),
                ],
                View: new StateCharacterSelectViewSnapshot("p:100", "silent")),
            Run: null));
        var service = new GrpcBridgeService(runtime);

        var result = service.HandleGetState(new StateRequest());
        var roundTrip = StateResult.Parser.ParseFrom(result.ToByteArray());

        Assert.NotNull(roundTrip.Success);
        Assert.Equal("spirectl.state/v0", roundTrip.Success.SchemaVersion);
        Assert.Equal("es", roundTrip.Success.Language);
        Assert.Equal("screens/character_select_screen", roundTrip.Success.RootScene);
        Assert.NotNull(roundTrip.Success.CharacterSelect);
        Assert.Equal("host", roundTrip.Success.CharacterSelect.Lobby.NetGameType);
        Assert.Equal((uint)1, roundTrip.Success.CharacterSelect.Lobby.ConnectingPlayerCount);
        Assert.Equal("p:100", roundTrip.Success.CharacterSelect.Lobby.Players[0].Id);
        Assert.Equal(0, roundTrip.Success.CharacterSelect.Lobby.Players[0].SlotId);
        Assert.Equal("p:100", roundTrip.Success.CharacterSelect.View.PlayerId);
        Assert.Equal("silent", roundTrip.Success.CharacterSelect.View.SelectedCharacterButtonId);
        Assert.True(roundTrip.Success.CharacterSelect.CharacterButtons[1].IsLocked);
        Assert.Empty(roundTrip.Success.CharacterSelect.Lobby.Seed);
        // Start-run lobbies carry no saved run; the saved_run summary stays unset.
        Assert.Null(roundTrip.Success.CharacterSelect.Lobby.SavedRun);
        Assert.Null(roundTrip.Success.Run);
    }

    [Fact]
    public void StateSerializesLoadRunLobbySavedRunSummary()
    {
        var runtime = RuntimeWithStateProvider(new StateSnapshot(
            StateSnapshot.CurrentSchemaVersion,
            Language: "es",
            RootScene: "screens/multiplayer_load_game_screen",
            CharacterSelect: new StateCharacterSelectSnapshot(
                new StateCharacterSelectLobbySnapshot(
                    NetGameType: "host",
                    LocalPlayerId: "p:1",
                    HostPlayerId: "p:1",
                    ConnectingPlayerCount: 0,
                    Ascension: 0,
                    MaxAscension: 0,
                    Act1: null,
                    Seed: "fixture-basic-load-run-lobby",
                    ModifierIds: [],
                    Players:
                    [
                        new StateCharacterSelectPlayerSnapshot(
                            "p:1",
                            SlotId: 0,
                            CharacterId: "IRONCLAD",
                            IsReady: false,
                            MaxMultiplayerAscensionUnlocked: 0,
                            DisplayName: "Test Host",
                            IsConnected: true),
                        new StateCharacterSelectPlayerSnapshot(
                            "p:2",
                            SlotId: 1,
                            CharacterId: "SILENT",
                            IsReady: false,
                            MaxMultiplayerAscensionUnlocked: 0,
                            DisplayName: "2",
                            IsConnected: false),
                    ],
                    SavedRun: new StateCharacterSelectSavedRunSnapshot(
                        CurrentActIndex: 0,
                        ActFloor: 0,
                        Players:
                        [
                            new StateCharacterSelectSavedRunPlayerSnapshot("p:1", CurrentHp: 80, MaxHp: 80, Gold: 99),
                            new StateCharacterSelectSavedRunPlayerSnapshot("p:2", CurrentHp: 70, MaxHp: 70, Gold: 99),
                        ])),
                CharacterButtons: [],
                View: new StateCharacterSelectViewSnapshot("p:1", null)),
            Run: null));
        var service = new GrpcBridgeService(runtime);

        var result = service.HandleGetState(new StateRequest());
        var roundTrip = StateResult.Parser.ParseFrom(result.ToByteArray());

        Assert.NotNull(roundTrip.Success);
        Assert.Equal("screens/multiplayer_load_game_screen", roundTrip.Success.RootScene);
        var lobby = roundTrip.Success.CharacterSelect.Lobby;
        Assert.NotNull(lobby.SavedRun);
        Assert.Equal(0, lobby.SavedRun.CurrentActIndex);
        Assert.Equal(0, lobby.SavedRun.ActFloor);
        // Connection state mirrors the game: local/host connected, the not-yet-joined remote
        // player disconnected (drives the remote-row disconnected indicator).
        Assert.True(lobby.Players[0].IsConnected);
        Assert.False(lobby.Players[1].IsConnected);
        Assert.Equal(2, lobby.SavedRun.Players.Count);
        Assert.Equal("p:1", lobby.SavedRun.Players[0].Id);
        Assert.Equal(80, lobby.SavedRun.Players[0].CurrentHp);
        Assert.Equal(80, lobby.SavedRun.Players[0].MaxHp);
        Assert.Equal(99, lobby.SavedRun.Players[0].Gold);
        Assert.Equal(70, lobby.SavedRun.Players[1].CurrentHp);
        // Saved-run player ids reuse the lobby NetIds, so they match the lobby players the
        // catalog binds against (view.playerId -> localSavedRunPlayer).
        Assert.Equal(lobby.Players[0].Id, lobby.SavedRun.Players[0].Id);
        Assert.Equal(lobby.Players[1].Id, lobby.SavedRun.Players[1].Id);
    }

    [Fact]
    public void StateSerializesShopViewOpenState()
    {
        var runtime = RuntimeWithStateProvider(new StateSnapshot(
            StateSnapshot.CurrentSchemaVersion,
            Language: "en",
            RootScene: "run",
            CharacterSelect: null,
            Run: new StateRunSnapshot(
                SourceType: "MegaCrit.Sts2.Core.Runs.RunState",
                ManagerSourceType: "MegaCrit.Sts2.Core.Runs.RunManager",
                NetGameType: "host",
                GameMode: "Standard",
                Seed: "seed-1",
                AscensionLevel: 0,
                ActId: "act_1",
                CurrentActIndex: 0,
                ActFloor: 1,
                TotalFloor: 1,
                BossEncounterId: null,
                SecondBossEncounterId: null,
                CurrentMapCoord: null,
                CurrentMapPointId: null,
                VisitedMapCoords: [],
                Players: [],
                Map: null,
                CurrentRoom: new StateRunCurrentRoomSnapshot(
                    SourceType: "MegaCrit.Sts2.Core.Rooms.MerchantRoom",
                    RoomType: "Merchant",
                    Scene: "rooms/merchant_room",
                    ModelId: null,
                    Event: null,
                    Shop: new StateRunShopRoomSnapshot(
                        Inventory: new StateShopInventorySnapshot(
                            SourceType: "MegaCrit.Sts2.Core.Nodes.Screens.Shops.NMerchantInventory",
                            PlayerId: "p:100",
                            CharacterCardEntries: [],
                            ColorlessCardEntries: [],
                            RelicEntries: [],
                            PotionEntries: [],
                            CardRemovalEntry: null),
                        Notices: [],
                        View: new StateShopViewSnapshot(IsOpen: true))),
                Notices: [])));
        var service = new GrpcBridgeService(runtime);

        var result = service.HandleGetState(new StateRequest());
        var roundTrip = StateResult.Parser.ParseFrom(result.ToByteArray());

        Assert.NotNull(roundTrip.Success.Run.CurrentRoom.Shop.Inventory);
        Assert.True(roundTrip.Success.Run.CurrentRoom.Shop.View.IsOpen);
    }

    [Fact]
    public async Task WatchStateStreamsInitialEnvelopeAndStopsAtMaxEvents()
    {
        var runtime = RuntimeWithStateProvider(new StateSnapshot(
            StateSnapshot.CurrentSchemaVersion,
            Language: "en",
            RootScene: "screens/character_select_screen",
            CharacterSelect: null,
            Run: null));
        var service = new GrpcBridgeService(runtime);

        var events = new List<StateWatchEvent>();
        await foreach (var evt in service.HandleWatchState(new StateWatchRequest
        {
            MaxEvents = 1,
            MinCaptureIntervalMs = 0,
        }))
        {
            events.Add(StateWatchEvent.Parser.ParseFrom(evt.ToByteArray()));
        }

        var initial = Assert.Single(events);
        Assert.Equal(StateWatchEventType.Initial, initial.Type);
        Assert.Equal(1UL, initial.Sequence);
        Assert.NotEmpty(initial.Fingerprint);
        Assert.NotNull(initial.State);
        Assert.Equal("en", initial.State.Language);
        Assert.Equal("screens/character_select_screen", initial.State.RootScene);
    }

    [Fact]
    public void StateSerializesRunRuntimeContract()
    {
        var runtime = RuntimeWithStateProvider(new StateSnapshot(
            StateSnapshot.CurrentSchemaVersion,
            Language: "en",
            RootScene: "run",
            CharacterSelect: null,
            Run: new StateRunSnapshot(
                SourceType: "MegaCrit.Sts2.Core.Runs.RunState",
                ManagerSourceType: "MegaCrit.Sts2.Core.Runs.RunManager",
                NetGameType: "host",
                GameMode: "Standard",
                Seed: "seed-1",
                AscensionLevel: 10,
                ActId: "act_2",
                CurrentActIndex: 1,
                ActFloor: 4,
                TotalFloor: 21,
                BossEncounterId: "VANTOM_BOSS",
                SecondBossEncounterId: null,
                CurrentMapCoord: new StateMapCoordSnapshot(3, 1),
                CurrentMapPointId: "map-point:3:1",
                VisitedMapCoords:
                [
                    new StateMapCoordSnapshot(0, 3),
                    new StateMapCoordSnapshot(3, 1),
                ],
                Players:
                [
                    new StateRunPlayerSnapshot(
                        Id: "p:100",
                        SourceType: "MegaCrit.Sts2.Core.Entities.Players.Player",
                        NetId: "100",
                        DisplayName: "Host",
                        CharacterId: "ironclad",
                        IsLocal: true,
                        IsHost: true,
                        IsRemote: false,
                        Creature: new StateRunCreatureSnapshot(
                            "MegaCrit.Sts2.Core.Entities.Creatures.Creature",
                            CurrentHp: 52,
                            MaxHp: 80),
                        Gold: 123,
                        Deck: new StateCardPileSnapshot(
                            "MegaCrit.Sts2.Core.Entities.Cards.CardPile",
                            "deck:p:100",
                            "Deck",
                            Count: 1,
                            Cards:
                            [
                                new StateCardSnapshot(
                                    "card:p:100:deck:0",
                                    "strike_red",
                                    UpgradeLevel: 1,
                                    DynamicVars: new Dictionary<string, int>(StringComparer.Ordinal)
                                    {
                                        ["damage"] = 9,
                                    },
                                    NextDynamicVars: new Dictionary<string, int>(StringComparer.Ordinal)
                                    {
                                        ["damage"] = 12,
                                    }),
                            ],
                            OrderObservable: true),
                        Relics: [new StateRelicSnapshot("relic:p:100:0:burning_blood", "burning_blood", SlotIndex: 0, HasCounter: false, Counter: 0)],
                        InventoryComplete: true,
                        Notices: [],
                        Potions:
                        [
                            new StateCombatPotionSnapshot(0, "fire-potion", IsQueued: false, PassesUsabilityCheck: true),
                            new StateCombatPotionSnapshot(1, null, IsQueued: false, PassesUsabilityCheck: true),
                            new StateCombatPotionSnapshot(2, "block-potion", IsQueued: true, PassesUsabilityCheck: false),
                            new StateCombatPotionSnapshot(3, null, IsQueued: false, PassesUsabilityCheck: true),
                        ],
                        Overlays:
                        [
                            new StateRunOverlaySnapshot(
                                Id: "overlay:p:100:choose-a-card:1",
                                ScreenType: "CardSelection",
                                ScreenId: "Screens.CardSelection.NChooseACardSelectionScreen",
                                Scene: "screens/card_selection/choose_a_card_selection_screen",
                                ChooseACard: new StateChooseACardOverlaySnapshot(
                                    CanSkip: true,
                                    Cards:
                                    [
                                        new StateCardSnapshot("card:p:100:choose-a-card:1:0", "DEMONIC_SHIELD", UpgradeLevel: 0),
                                    ])),
                            new StateRunOverlaySnapshot(
                                Id: "overlay:p:100:rewards:visible",
                                ScreenType: "Rewards",
                                ScreenId: "Screens.NRewardsScreen",
                                Scene: "screens/rewards_screen",
                                ChooseACard: null,
                                Rewards: new StateRewardsOverlaySnapshot(
                                    Flow: new StateRewardFlowSnapshot("skip", Enabled: false),
                                    Items:
                                    [
                                        new StateRewardItemSnapshot(
                                            Id: "reward:p:100:visible:0",
                                            SourceType: "MegaCrit.Sts2.Core.Rewards.RelicReward",
                                            Description: new StateLocRefSnapshot("relics", "CURSED_PEARL.title"),
                                            Relic: "CURSED_PEARL"),
                                        new StateRewardItemSnapshot(
                                            Id: "reward:p:100:visible:1",
                                            SourceType: "MegaCrit.Sts2.Core.Rewards.RelicReward",
                                            Description: new StateLocRefSnapshot("relics", "GOLDEN_PEARL.title"),
                                            Relic: "GOLDEN_PEARL"),
                                    ],
                                    Notices: [])),
                            new StateRunOverlaySnapshot(
                                Id: "overlay:p:100:crystal-sphere:visible",
                                ScreenType: "CrystalSphere",
                                ScreenId: Sts2SupportedScreenIds.CrystalSphereScreenId,
                                Scene: "screens/crystal_sphere_screen",
                                ChooseACard: null,
                                CrystalSphere: new StateCrystalSphereOverlaySnapshot(
                                    DivinationsRemaining: 6,
                                    SelectedTool: "big",
                                    IsFinished: false,
                                    Cells:
                                    [
                                        new StateCrystalSphereCellSnapshot(
                                            Id: "crystal-sphere:cell:1:2",
                                            X: 1,
                                            Y: 2,
                                            IsHidden: true,
                                            IsHighlighted: true,
                                            IsHovered: false,
                                            Enabled: true),
                                        new StateCrystalSphereCellSnapshot(
                                            Id: "crystal-sphere:cell:3:4",
                                            X: 3,
                                            Y: 4,
                                            IsHidden: false,
                                            IsHighlighted: false,
                                            IsHovered: false,
                                            Enabled: false),
                                    ],
                                    RevealedItems:
                                    [
                                        new StateCrystalSphereItemSnapshot(
                                            Id: "crystal-sphere:item:3:4:2:1",
                                            X: 3,
                                            Y: 4,
                                            WidthCells: 2,
                                            HeightCells: 1,
                                            IconAssetKey: "res://images/events/crystal_sphere/crystal_sphere_relic.png",
                                            ShowsCard: false),
                                    ])),
                        ]),
                ],
                Map: new StateRunMapSnapshot(
                    SourceType: "MegaCrit.Sts2.Core.Map.ActMap",
                    RowCount: 15,
                    ColumnCount: 7,
                    StartingMapPointId: "map-point:0:3",
                    BossMapPointId: "map-point:14:3",
                    SecondBossMapPointId: null,
                    MapPointHistory:
                    [
                        new StateMapPointHistoryActSnapshot(
                        [
                            new StateMapPointHistoryEntrySnapshot(1, new StateMapCoordSnapshot(0, 3), "Monster"),
                        ]),
                    ],
                    Points:
                    [
                        new StateMapPointSnapshot(
                            "map-point:3:1",
                            "MegaCrit.Sts2.Core.Map.MapPoint",
                            new StateMapCoordSnapshot(3, 1),
                            "Monster",
                            CanBeModified: true,
                            ParentIds: ["map-point:2:1"],
                            ChildIds: ["map-point:4:1"]),
                    ],
                    View: new StateRunMapViewSnapshot(IsOpen: true)),
                CurrentRoom: new StateRunCurrentRoomSnapshot(
                    SourceType: "MegaCrit.Sts2.Core.Rooms.EventRoom",
                    RoomType: "Event",
                    Scene: "rooms/event_room",
                    ModelId: "Neow",
                    Event: new StateRunEventRoomSnapshot(
                        Scene: null,
                        CanonicalEventModelId: "Neow",
                        CanonicalSourceType: "MegaCrit.Sts2.Core.Events.AncientEventModel",
                        IsPreFinished: false,
                        IsShared: true,
                        PlayerStates:
                        [
                            new StateRunEventPlayerStateSnapshot(
                                PlayerId: "p:100",
                                EventModelId: "Neow",
                                CanonicalEventModelId: "Neow",
                                SourceType: "MegaCrit.Sts2.Core.Events.AncientEventModel",
                                OwnerPlayerId: "p:100",
                                LayoutType: "Ancient",
                                IsFinished: false,
                                DescriptionLoc: new StateLocRefSnapshot("events", "NEOW.pages.INITIAL.description"),
                                Options:
                                [
                                    new StateRunEventOptionSnapshot(
                                        Id: "event-room:arcanescroll:0",
                                        Index: 0,
                                        TextKey: "ArcaneScroll",
                                        TitleLoc: new StateLocRefSnapshot("relics", "ARCANE_SCROLL.title"),
                                        DescriptionLoc: new StateLocRefSnapshot("relics", "ARCANE_SCROLL.eventDescription"),
                                        TitleText: "Arcane Scroll",
                                        DescriptionText: "Obtain a rare relic.",
                                        IsLocked: false,
                                        IsProceed: false,
                                        WasChosen: false,
                                        RelicId: "ARCANE_SCROLL",
                                        ShouldSaveChoiceToHistory: true,
                                        ShouldSaveVariablesToHistory: false,
                                        HoverTips:
                                        [
                                            new ModelHoverTipSnapshot(
                                                Title: "Arcane Scroll",
                                                Description: "Obtain a rare relic.",
                                                IsDebuff: false,
                                                IconAssetKey: null),
                                            new ModelHoverTipSnapshot(
                                                Title: "Cursed",
                                                Description: "A curse may follow.",
                                                IsDebuff: true,
                                                IconAssetKey: "model://powers/cursed/icon"),
                                        ]),
                                ],
                                Ancient: new StateRunEventAncientSnapshot(
                                    HealedAmount: 6,
                                    View: new StateRunEventAncientViewSnapshot(
                                        new StateRunEventAncientVisibleDialogueSnapshot(
                                            SourceType: "MegaCrit.Sts2.Core.Nodes.Events.NAncientEventLayout",
                                            DialogueId: "NEOW.talk.ANY.4",
                                            CurrentLineIndex: 0,
                                            CurrentLineLocKey: "NEOW.talk.ANY.4-0.ancient",
                                            LineLocKeys:
                                            [
                                                "NEOW.talk.ANY.4-0.ancient",
                                                "NEOW.talk.ANY.4-1.char",
                                            ])))),
                        ],
                        SharedVotes:
                        [
                            new StateRunEventSharedVoteSnapshot("p:100", HasOptionIndex: false, OptionIndex: 0),
                        ],
                        Notices: []),
                    Id: 42),
                Notices: [])));
        var service = new GrpcBridgeService(runtime);

        var result = service.HandleGetState(new StateRequest());
        var roundTrip = StateResult.Parser.ParseFrom(result.ToByteArray());

        Assert.NotNull(roundTrip.Success);
        var run = Assert.IsType<StateRun>(roundTrip.Success.Run);
        Assert.Equal("host", run.NetGameType);
        Assert.Equal("Standard", run.GameMode);
        Assert.Equal(10, run.AscensionLevel);
        Assert.Equal("act_2", run.ActId);
        Assert.Equal("VANTOM_BOSS", run.BossEncounterId);
        Assert.Empty(run.SecondBossEncounterId);
        Assert.Equal(3, run.CurrentMapCoord.Row);
        Assert.Equal("map-point:3:1", run.CurrentMapPointId);
        Assert.Equal("Host", run.Players[0].DisplayName);
        Assert.Equal(52, run.Players[0].Creature.CurrentHp);
        Assert.Equal(80, run.Players[0].Creature.MaxHp);
        Assert.False(run.Players[0].Creature.HasBlock);
        Assert.False(run.Players[0].Creature.HasIsHittable);
        Assert.Null(run.Players[0].Creature.NextMove);
        Assert.Equal("strike_red", run.Players[0].Deck.Cards[0].ModelId);
        Assert.Equal(1, run.Players[0].Deck.Cards[0].UpgradeLevel);
        Assert.Equal(9, run.Players[0].Deck.Cards[0].DynamicVars["damage"]);
        Assert.Equal(12, run.Players[0].Deck.Cards[0].NextDynamicVars["damage"]);
        Assert.Equal("burning_blood", run.Players[0].Relics[0].ModelId);
        Assert.Equal(["fire-potion", "", "block-potion", ""], run.Players[0].Potions.Select(potion => potion.ModelId));
        Assert.Equal([0, 1, 2, 3], run.Players[0].Potions.Select(potion => potion.Id));
        Assert.True(run.Players[0].Potions[2].IsQueued);
        Assert.False(run.Players[0].Potions[2].PassesUsabilityCheck);
        Assert.True(run.Players[0].Potions[0].PassesUsabilityCheck);
        Assert.Equal(3, run.Players[0].Overlays.Count);
        Assert.Equal("CardSelection", run.Players[0].Overlays[0].ScreenType);
        Assert.Equal("Screens.CardSelection.NChooseACardSelectionScreen", run.Players[0].Overlays[0].ScreenId);
        Assert.True(run.Players[0].Overlays[0].ChooseACard.CanSkip);
        Assert.Equal("DEMONIC_SHIELD", run.Players[0].Overlays[0].ChooseACard.Cards[0].ModelId);
        Assert.Empty(run.Players[0].Overlays[0].ChooseACard.Cards[0].DynamicVars);
        Assert.Empty(run.Players[0].Overlays[0].ChooseACard.Cards[0].NextDynamicVars);
        Assert.Equal("Rewards", run.Players[0].Overlays[1].ScreenType);
        Assert.Equal("Screens.NRewardsScreen", run.Players[0].Overlays[1].ScreenId);
        Assert.Equal("skip", run.Players[0].Overlays[1].Rewards.Flow.Mode);
        Assert.False(run.Players[0].Overlays[1].Rewards.Flow.Enabled);
        Assert.Equal(["CURSED_PEARL", "GOLDEN_PEARL"], run.Players[0].Overlays[1].Rewards.Items.Select(item => item.Relic));
        Assert.Equal("relics", run.Players[0].Overlays[1].Rewards.Items[0].Description.Table);
        Assert.Equal("CURSED_PEARL.title", run.Players[0].Overlays[1].Rewards.Items[0].Description.Key);
        Assert.Equal("CrystalSphere", run.Players[0].Overlays[2].ScreenType);
        Assert.Equal(Sts2SupportedScreenIds.CrystalSphereScreenId, run.Players[0].Overlays[2].ScreenId);
        Assert.Equal("screens/crystal_sphere_screen", run.Players[0].Overlays[2].Scene);
        Assert.Equal(6, run.Players[0].Overlays[2].CrystalSphere.DivinationsRemaining);
        Assert.Equal("big", run.Players[0].Overlays[2].CrystalSphere.SelectedTool);
        Assert.False(run.Players[0].Overlays[2].CrystalSphere.IsFinished);
        Assert.Equal(2, run.Players[0].Overlays[2].CrystalSphere.Cells.Count);
        Assert.Equal("crystal-sphere:cell:1:2", run.Players[0].Overlays[2].CrystalSphere.Cells[0].Id);
        Assert.Equal(1, run.Players[0].Overlays[2].CrystalSphere.Cells[0].X);
        Assert.Equal(2, run.Players[0].Overlays[2].CrystalSphere.Cells[0].Y);
        Assert.True(run.Players[0].Overlays[2].CrystalSphere.Cells[0].IsHidden);
        Assert.True(run.Players[0].Overlays[2].CrystalSphere.Cells[0].IsHighlighted);
        Assert.True(run.Players[0].Overlays[2].CrystalSphere.Cells[0].Enabled);
        Assert.False(run.Players[0].Overlays[2].CrystalSphere.Cells[1].Enabled);
        var revealedItem = Assert.Single(run.Players[0].Overlays[2].CrystalSphere.RevealedItems);
        Assert.Equal("crystal-sphere:item:3:4:2:1", revealedItem.Id);
        Assert.Equal(2, revealedItem.WidthCells);
        Assert.Equal("res://images/events/crystal_sphere/crystal_sphere_relic.png", revealedItem.IconAssetKey);
        Assert.True(run.Players[0].InventoryComplete);
        Assert.Null(run.Players[0].Combat);
        Assert.Equal(15, run.Map.RowCount);
        Assert.Equal("map-point:14:3", run.Map.BossMapPointId);
        Assert.True(run.Map.View.IsOpen);
        Assert.Equal("Monster", run.Map.MapPointHistory[0].Entries[0].MapPointType);
        Assert.Equal("map-point:4:1", run.Map.Points[0].ChildIds[0]);
        Assert.True(run.CurrentRoom.HasId);
        Assert.Equal(42, run.CurrentRoom.Id);
        Assert.Equal("Event", run.CurrentRoom.RoomType);
        Assert.Equal("rooms/event_room", run.CurrentRoom.Scene);
        Assert.Empty(run.CurrentRoom.Event.Scene);
        Assert.Equal("Neow", run.CurrentRoom.Event.CanonicalEventModelId);
        Assert.Equal("p:100", run.CurrentRoom.Event.PlayerStates[0].PlayerId);
        Assert.Equal("ARCANE_SCROLL", run.CurrentRoom.Event.PlayerStates[0].Options[0].RelicId);
        Assert.Equal("relics", run.CurrentRoom.Event.PlayerStates[0].Options[0].TitleLoc.Table);
        Assert.Equal(2, run.CurrentRoom.Event.PlayerStates[0].Options[0].HoverTips.Count);
        Assert.Equal("Arcane Scroll", run.CurrentRoom.Event.PlayerStates[0].Options[0].HoverTips[0].Title);
        Assert.Empty(run.CurrentRoom.Event.PlayerStates[0].Options[0].HoverTips[0].IconAssetKey);
        Assert.True(run.CurrentRoom.Event.PlayerStates[0].Options[0].HoverTips[1].IsDebuff);
        Assert.Equal("model://powers/cursed/icon", run.CurrentRoom.Event.PlayerStates[0].Options[0].HoverTips[1].IconAssetKey);
        Assert.Equal("NEOW.talk.ANY.4-0.ancient", run.CurrentRoom.Event.PlayerStates[0].Ancient.View.VisibleDialogue.CurrentLineLocKey);
        Assert.False(run.CurrentRoom.Event.SharedVotes[0].HasOptionIndex);
    }

    [Fact]
    public void StateRunMapViewIsOmittedForRemotePerspective()
    {
        var runtime = RuntimeWithStateProvider(new StateSnapshot(
            StateSnapshot.CurrentSchemaVersion,
            Language: "en",
            RootScene: "run",
            CharacterSelect: null,
            Run: new StateRunSnapshot(
                SourceType: "MegaCrit.Sts2.Core.Runs.RunState",
                ManagerSourceType: "MegaCrit.Sts2.Core.Runs.RunManager",
                NetGameType: "host",
                GameMode: "Standard",
                Seed: "seed-1",
                AscensionLevel: 0,
                ActId: "act_1",
                CurrentActIndex: 0,
                ActFloor: 1,
                TotalFloor: 1,
                BossEncounterId: null,
                SecondBossEncounterId: null,
                CurrentMapCoord: null,
                CurrentMapPointId: null,
                VisitedMapCoords: [],
                Players: [],
                Map: new StateRunMapSnapshot(
                    SourceType: "MegaCrit.Sts2.Core.Map.ActMap",
                    RowCount: 15,
                    ColumnCount: 7,
                    StartingMapPointId: null,
                    BossMapPointId: null,
                    SecondBossMapPointId: null,
                    MapPointHistory: [],
                    Points: [],
                    View: null),
                CurrentRoom: null,
                Notices:
                [
                    new StateNoticeSnapshot(
                        "state-run-map-view-local-only",
                        "Run map view is attached-client transient UI and is omitted for the requested remote presentation perspective.",
                        Provisional: true,
                        Path: "run.map.view",
                        Severity: "partial",
                        Source: "Sts2StateProvider"),
                ])));
        var service = new GrpcBridgeService(runtime);

        var result = service.HandleGetState(new StateRequest());
        var roundTrip = StateResult.Parser.ParseFrom(result.ToByteArray());

        Assert.Null(roundTrip.Success.Run.Map.View);
        var notice = Assert.Single(roundTrip.Success.Run.Notices);
        Assert.Equal("run.map.view", notice.Path);
        Assert.Equal("state-run-map-view-local-only", notice.Code);
    }

    [Fact]
    public void StateSerializesDirectDeckCapstoneView()
    {
        var run = RoundTripStateRunView(new StateRunViewSnapshot(
            "p:100",
            new StateRunCapstoneViewSnapshot(
                "screens/deck_view_screen",
                "MegaCrit.Sts2.Core.Nodes.Screens.NDeckViewScreen",
                Stack: [],
                DeckView: new StateDeckViewSnapshot(
                    [
                        new StateDeckViewSortSnapshot("obtained", "ascending"),
                        new StateDeckViewSortSnapshot("type", "descending"),
                    ],
                    ShowUpgrades: true))));

        Assert.Equal("screens/deck_view_screen", run.View.Capstone.Scene);
        Assert.Equal("p:100", run.View.PlayerId);
        Assert.Equal("MegaCrit.Sts2.Core.Nodes.Screens.NDeckViewScreen", run.View.Capstone.SourceType);
        Assert.Empty(run.View.Capstone.Stack);
        Assert.NotNull(run.View.Capstone.DeckView);
        Assert.True(run.View.Capstone.DeckView.ShowUpgrades);
        Assert.Equal("obtained", run.View.Capstone.DeckView.Sort[0].By);
        Assert.Equal("ascending", run.View.Capstone.DeckView.Sort[0].Direction);
        Assert.Equal("type", run.View.Capstone.DeckView.Sort[1].By);
        Assert.Equal("descending", run.View.Capstone.DeckView.Sort[1].Direction);
    }

    [Fact]
    public void StateSerializesSelectedCardView()
    {
        var run = RoundTripStateRunView(new StateRunViewSnapshot(
            "p:100",
            Capstone: null,
            SelectedCard: new StateSelectedCardSnapshot("card:p:100:hand:0", "p:100")));

        Assert.Null(run.View.Capstone);
        Assert.NotNull(run.View.SelectedCard);
        Assert.Equal("card:p:100:hand:0", run.View.SelectedCard.CardId);
        Assert.Equal("p:100", run.View.SelectedCard.PlayerId);
    }

    [Fact]
    public void StateSerializesInspectRelicView()
    {
        var run = RoundTripStateRunView(new StateRunViewSnapshot(
            "p:100",
            Capstone: null,
            InspectRelic: new StateInspectRelicViewSnapshot("BURNING_BLOOD", Index: 1, Count: 3)));

        Assert.Null(run.View.Capstone);
        Assert.NotNull(run.View.InspectRelic);
        Assert.Equal("BURNING_BLOOD", run.View.InspectRelic.RelicModelId);
        Assert.Equal(1, run.View.InspectRelic.Index);
        Assert.Equal(3, run.View.InspectRelic.Count);
    }

    [Fact]
    public void StateNormalizesDeckViewSortOrders()
    {
        Assert.True(StateDeckViewSortNormalizer.TryNormalize("Ascending", out var obtained));
        Assert.Equal("obtained", obtained.By);
        Assert.Equal("ascending", obtained.Direction);

        Assert.True(StateDeckViewSortNormalizer.TryNormalize("TypeDescending", out var type));
        Assert.Equal("type", type.By);
        Assert.Equal("descending", type.Direction);

        Assert.True(StateDeckViewSortNormalizer.TryNormalize("CostAscending", out var cost));
        Assert.Equal("cost", cost.By);
        Assert.Equal("ascending", cost.Direction);

        Assert.True(StateDeckViewSortNormalizer.TryNormalize("AlphabetDescending", out var alphabet));
        Assert.Equal("alphabet", alphabet.By);
        Assert.Equal("descending", alphabet.Direction);

        Assert.False(StateDeckViewSortNormalizer.TryNormalize("Unexpected", out _));
    }

    [Fact]
    public void StateSerializesPauseMenuCapstoneStackView()
    {
        var run = RoundTripStateRunView(new StateRunViewSnapshot(
            "p:100",
            new StateRunCapstoneViewSnapshot(
                "screens/capstone_submenu_stack",
                "MegaCrit.Sts2.Core.Nodes.Screens.NCapstoneSubmenuStack",
                Stack:
                [
                    new StateRunStackEntrySnapshot(
                        "screens/pause_menu/pause_menu",
                        "MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu.NPauseMenu"),
                ])));

        Assert.Equal("screens/capstone_submenu_stack", run.View.Capstone.Scene);
        Assert.Single(run.View.Capstone.Stack);
        Assert.Equal("screens/pause_menu/pause_menu", run.View.Capstone.Stack[0].Scene);
        Assert.Equal("MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu.NPauseMenu", run.View.Capstone.Stack[0].SourceType);
    }

    [Fact]
    public void StateSerializesNestedSettingsCapstoneStackViewBottomToTop()
    {
        var run = RoundTripStateRunView(new StateRunViewSnapshot(
            "p:100",
            new StateRunCapstoneViewSnapshot(
                "screens/capstone_submenu_stack",
                "MegaCrit.Sts2.Core.Nodes.Screens.NCapstoneSubmenuStack",
                Stack:
                [
                    new StateRunStackEntrySnapshot(
                        "screens/pause_menu/pause_menu",
                        "MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu.NPauseMenu"),
                    new StateRunStackEntrySnapshot(
                        "screens/settings_screen",
                        "MegaCrit.Sts2.Core.Nodes.Screens.Settings.NSettingsScreen"),
                ])));

        Assert.Equal("screens/capstone_submenu_stack", run.View.Capstone.Scene);
        Assert.Equal("screens/pause_menu/pause_menu", run.View.Capstone.Stack[0].Scene);
        Assert.Equal("screens/settings_screen", run.View.Capstone.Stack[1].Scene);
        Assert.Equal("MegaCrit.Sts2.Core.Nodes.Screens.Settings.NSettingsScreen", run.View.Capstone.Stack[^1].SourceType);
    }

    [Fact]
    public void StateSerializesTreasureVoteRuntimeContract()
    {
        var runtime = RuntimeWithStateProvider(new StateSnapshot(
            StateSnapshot.CurrentSchemaVersion,
            Language: "en",
            RootScene: "run",
            CharacterSelect: null,
            Run: new StateRunSnapshot(
                SourceType: "MegaCrit.Sts2.Core.Runs.RunState",
                ManagerSourceType: "MegaCrit.Sts2.Core.Runs.RunManager",
                NetGameType: "host",
                GameMode: "Standard",
                Seed: "seed-1",
                AscensionLevel: 0,
                ActId: "act_1",
                CurrentActIndex: 0,
                ActFloor: 2,
                TotalFloor: 2,
                BossEncounterId: null,
                SecondBossEncounterId: null,
                CurrentMapCoord: null,
                CurrentMapPointId: null,
                VisitedMapCoords: [],
                Players: [],
                Map: null,
                CurrentRoom: new StateRunCurrentRoomSnapshot(
                    SourceType: "MegaCrit.Sts2.Core.Rooms.TreasureRoom",
                    RoomType: "Treasure",
                    Scene: "rooms/treasure_room",
                    ModelId: null,
                    Event: null,
                    Treasure: new StateRunTreasureRoomSnapshot(
                        CurrentRelicsActive: true,
                        CurrentRelics:
                        [
                            new StateTreasureRelicSnapshot("treasure-relic:0:anchor", "anchor"),
                        ],
                        PlayerVotes:
                        [
                            new StateTreasurePlayerVoteSnapshot("p:100", Index: null, VoteReceived: false),
                            new StateTreasurePlayerVoteSnapshot("p:200", Index: 0, VoteReceived: true),
                            new StateTreasurePlayerVoteSnapshot("p:300", Index: null, VoteReceived: true),
                        ],
                        Notices: [],
                        CanProceed: true),
                    Id: 7),
                Notices: [])));
        var service = new GrpcBridgeService(runtime);

        var result = service.HandleGetState(new StateRequest());
        var roundTrip = StateResult.Parser.ParseFrom(result.ToByteArray());

        var treasure = Assert.IsType<StateRun>(roundTrip.Success.Run).CurrentRoom.Treasure;
        Assert.True(roundTrip.Success.Run.CurrentRoom.HasId);
        Assert.Equal(7, roundTrip.Success.Run.CurrentRoom.Id);
        Assert.Equal("rooms/treasure_room", roundTrip.Success.Run.CurrentRoom.Scene);
        Assert.True(treasure.CurrentRelicsActive);
        Assert.True(treasure.CanProceed);
        Assert.Equal("anchor", treasure.CurrentRelics[0].ModelId);
        Assert.False(treasure.PlayerVotes[0].HasIndex);
        Assert.False(treasure.PlayerVotes[0].VoteReceived);
        Assert.True(treasure.PlayerVotes[1].HasIndex);
        Assert.Equal(0, treasure.PlayerVotes[1].Index);
        Assert.True(treasure.PlayerVotes[1].VoteReceived);
        Assert.False(treasure.PlayerVotes[2].HasIndex);
        Assert.True(treasure.PlayerVotes[2].VoteReceived);
    }

    private static StateRun RoundTripStateRunView(StateRunViewSnapshot view)
    {
        var runtime = RuntimeWithStateProvider(new StateSnapshot(
            StateSnapshot.CurrentSchemaVersion,
            Language: "en",
            RootScene: "run",
            CharacterSelect: null,
            Run: new StateRunSnapshot(
                SourceType: "MegaCrit.Sts2.Core.Runs.RunState",
                ManagerSourceType: "MegaCrit.Sts2.Core.Runs.RunManager",
                NetGameType: "host",
                GameMode: "Standard",
                Seed: "seed-1",
                AscensionLevel: 0,
                ActId: "act_1",
                CurrentActIndex: 0,
                ActFloor: 1,
                TotalFloor: 1,
                BossEncounterId: null,
                SecondBossEncounterId: null,
                CurrentMapCoord: null,
                CurrentMapPointId: null,
                VisitedMapCoords: [],
                Players: [],
                Map: null,
                CurrentRoom: null,
                Notices: [],
                View: view)));
        var service = new GrpcBridgeService(runtime);

        var result = service.HandleGetState(new StateRequest());
        var roundTrip = StateResult.Parser.ParseFrom(result.ToByteArray());

        return Assert.IsType<StateRun>(roundTrip.Success.Run);
    }

    [Fact]
    public void StateSerializesCombatRoomRuntimeContract()
    {
        var playerCreature = new StateRunCreatureSnapshot(
            "MegaCrit.Sts2.Core.Entities.Creatures.Creature",
            CurrentHp: 67,
            MaxHp: 80,
            Id: "creature:1",
            ModelId: "ironclad",
            Side: "Player",
            SlotName: null,
            Block: 5,
            IsHittable: true,
            PowerInstances:
            [
                new StateCombatPowerInstanceSnapshot(
                    Id: "power:creature:1:0:strength",
                    ModelId: "strength",
                    SourceType: "MegaCrit.Sts2.Core.Models.PowerModel",
                    Amount: 2,
                    DisplayAmount: 2,
                    AmountOnTurnStart: 0,
                    Type: "Buff",
                    TypeForCurrentAmount: "Buff",
                    StackType: "Intensity",
                    IsVisible: true,
                    SkipNextDurationTick: false,
                    AmountLabelColor: "rgb(239 200 81)",
                    OwnerCreatureId: "creature:1",
                    TargetCreatureId: "creature:1",
                    ApplierCreatureId: "creature:2",
                    HoverTips:
                    [
                        new ModelHoverTipSnapshot(
                            Title: "Strength",
                            Description: "Deal 2 more attack damage.",
                            IsDebuff: false,
                            IconAssetKey: "model://powers/strength/icon"),
                    ]),
            ]);
        var petCreature = new StateRunCreatureSnapshot(
            "MegaCrit.Sts2.Core.Entities.Creatures.Creature",
            CurrentHp: 12,
            MaxHp: 12,
            Id: "creature:3",
            ModelId: "osty",
            Side: "Player",
            SlotName: null,
            Block: 0,
            IsHittable: true,
            PowerInstances: []);
        var enemyCreature = new StateRunCreatureSnapshot(
            "MegaCrit.Sts2.Core.Entities.Creatures.Creature",
            CurrentHp: 38,
            MaxHp: 44,
            Id: "creature:2",
            ModelId: "jaw_worm",
            Side: "Enemy",
            SlotName: "A",
            Block: 0,
            IsHittable: true,
            PowerInstances: [],
            NextMove: new StateCombatNextMoveSnapshot(
                Id: "ATTACK",
                Intents:
                [
                    new StateCombatIntentSnapshot("Attack", new StateCombatAttackIntentSnapshot(Damage: 11, Hits: 1, Repeats: 1)),
                    new StateCombatIntentSnapshot("Defend", Attack: null),
                ]));
        var runtime = RuntimeWithStateProvider(new StateSnapshot(
            StateSnapshot.CurrentSchemaVersion,
            Language: "en",
            RootScene: "run",
            CharacterSelect: null,
            Run: new StateRunSnapshot(
                SourceType: "MegaCrit.Sts2.Core.Runs.RunState",
                ManagerSourceType: "MegaCrit.Sts2.Core.Runs.RunManager",
                NetGameType: "host",
                GameMode: "Standard",
                Seed: "seed-1",
                AscensionLevel: 0,
                ActId: "act_1",
                CurrentActIndex: 0,
                ActFloor: 1,
                TotalFloor: 1,
                BossEncounterId: null,
                SecondBossEncounterId: null,
                CurrentMapCoord: null,
                CurrentMapPointId: null,
                VisitedMapCoords: [],
                Players:
                [
                    new StateRunPlayerSnapshot(
                        Id: "p:100",
                        SourceType: "MegaCrit.Sts2.Core.Entities.Players.Player",
                        NetId: "100",
                        DisplayName: "Host",
                        CharacterId: "ironclad",
                        IsLocal: true,
                        IsHost: true,
                        IsRemote: false,
                        Creature: playerCreature,
                        Gold: 99,
                        Deck: null,
                        Relics: [],
                        InventoryComplete: true,
                        Notices: [],
                        Combat: new StateRunPlayerCombatSnapshot(
                            SourceType: "MegaCrit.Sts2.Core.Entities.Players.PlayerCombatState",
                            Energy: 2,
                            MaxEnergy: 3,
                            Stars: 1,
                            Hand: new StateCombatCardPileSnapshot(
                                "MegaCrit.Sts2.Core.Entities.Cards.CardPile",
                                "pile:p:100:hand",
                                "Hand",
                                Cards:
                                [
                                    new StateCombatCardSnapshot(
                                        Id: "7",
                                        ModelId: "strike_red",
                                        UpgradeLevel: 1,
                                        CurrentTargetCreatureId: "creature:2",
                                        ExhaustOnNextPlay: false,
                                        HasSingleTurnRetain: false,
                                        HasSingleTurnSly: false,
                                        ShouldRetainThisTurn: false,
                                        EnergyCost: 1,
                                        StarCost: 1,
                                        UnplayableReason: ["EnergyCostTooHigh"],
                                        ShouldGlowGold: true,
                                        ShouldGlowRed: false),
                                ]),
                            DrawPile: new StateCombatCardPileSnapshot("MegaCrit.Sts2.Core.Entities.Cards.CardPile", "pile:p:100:draw", "Draw", Cards: []),
                            DiscardPile: new StateCombatCardPileSnapshot("MegaCrit.Sts2.Core.Entities.Cards.CardPile", "pile:p:100:discard", "Discard", Cards: []),
                            ExhaustPile: new StateCombatCardPileSnapshot("MegaCrit.Sts2.Core.Entities.Cards.CardPile", "pile:p:100:exhaust", "Exhaust", Cards: []),
                            PlayPile: new StateCombatCardPileSnapshot("MegaCrit.Sts2.Core.Entities.Cards.CardPile", "pile:p:100:play", "Play", Cards: []),
                            PetCreatures: [petCreature],
                            OrbQueue: new StateCombatOrbQueueSnapshot(
                                "MegaCrit.Sts2.Core.Entities.Orbs.OrbQueue",
                                Capacity: 3,
                                Orbs:
                                [
                                    new StateCombatOrbSnapshot(
                                        Id: "orb:p:100:0:lightning",
                                        ModelId: "lightning",
                                        PassiveVal: 3,
                                        EvokeVal: 8,
                                        OwnerPlayerId: "p:100",
                                        HasBeenRemovedFromState: false),
                                ]),
                            Notices: [])),
                ],
                Map: null,
                CurrentRoom: new StateRunCurrentRoomSnapshot(
                    SourceType: "MegaCrit.Sts2.Core.Rooms.CombatRoom",
                    RoomType: "Monster",
                    Scene: "rooms/combat_room",
                    ModelId: "act1_monster",
                    Event: null,
                    Combat: new StateRunCombatRoomSnapshot(
                        EncounterId: "jaw_worm",
                        ParentEventId: null,
                        GoldProportion: 1,
                        IsPreFinished: false,
                        ShouldCreateCombat: true,
                        ShouldResumeParentEventAfterCombat: false,
                        CombatState: new StateCombatStateSnapshot(
                            SourceType: "MegaCrit.Sts2.Core.Combat.CombatState",
                            CurrentSide: "Player",
                            RoundNumber: 2,
                            ModifierIds: ["daily_mirror"],
                            EscapedCreatureIds: [],
                            Enemies: [enemyCreature]),
                        Notices: [])),
                Notices: [])));
        var service = new GrpcBridgeService(runtime);

        var result = service.HandleGetState(new StateRequest());
        var roundTrip = StateResult.Parser.ParseFrom(result.ToByteArray());

        var run = Assert.IsType<StateRun>(roundTrip.Success.Run);
        Assert.Null(run.CurrentRoom.Event);
        Assert.Equal("rooms/combat_room", run.CurrentRoom.Scene);
        Assert.NotNull(run.CurrentRoom.Combat);
        Assert.Equal("jaw_worm", run.CurrentRoom.Combat.EncounterId);
        Assert.Single(run.CurrentRoom.Combat.CombatState.Enemies);
        Assert.Equal("jaw_worm", run.CurrentRoom.Combat.CombatState.Enemies[0].ModelId);
        Assert.Equal("creature:2", run.CurrentRoom.Combat.CombatState.Enemies[0].Id);
        Assert.Equal("ATTACK", run.CurrentRoom.Combat.CombatState.Enemies[0].NextMove.Id);
        Assert.Equal(2, run.CurrentRoom.Combat.CombatState.Enemies[0].NextMove.Intents.Count);
        Assert.Equal("Attack", run.CurrentRoom.Combat.CombatState.Enemies[0].NextMove.Intents[0].Type);
        Assert.Equal(11, run.CurrentRoom.Combat.CombatState.Enemies[0].NextMove.Intents[0].Attack.Damage);
        Assert.Equal(1, run.CurrentRoom.Combat.CombatState.Enemies[0].NextMove.Intents[0].Attack.Hits);
        Assert.Equal("Defend", run.CurrentRoom.Combat.CombatState.Enemies[0].NextMove.Intents[1].Type);
        Assert.Null(run.CurrentRoom.Combat.CombatState.Enemies[0].NextMove.Intents[1].Attack);
        Assert.NotNull(run.Players[0].Combat);
        Assert.True(run.Players[0].Creature.HasBlock);
        Assert.True(run.Players[0].Creature.HasIsHittable);
        Assert.Null(run.Players[0].Creature.NextMove);
        Assert.Equal(2, run.Players[0].Combat.Energy);
        Assert.Equal("pile:p:100:hand", run.Players[0].Combat.Hand.Id);
        Assert.Equal("7", run.Players[0].Combat.Hand.Cards[0].Id);
        Assert.Equal("strike_red", run.Players[0].Combat.Hand.Cards[0].ModelId);
        Assert.Equal("creature:2", run.Players[0].Combat.Hand.Cards[0].CurrentTargetCreatureId);
        Assert.Equal(1, run.Players[0].Combat.Hand.Cards[0].EnergyCost);
        Assert.Equal(1, run.Players[0].Combat.Hand.Cards[0].StarCost);
        Assert.Equal(["EnergyCostTooHigh"], run.Players[0].Combat.Hand.Cards[0].UnplayableReason);
        Assert.True(run.Players[0].Combat.Hand.Cards[0].ShouldGlowGold);
        Assert.False(run.Players[0].Combat.Hand.Cards[0].ShouldGlowRed);
        Assert.Empty(run.Players[0].Overlays);
        Assert.Equal("creature:3", run.Players[0].Combat.PetCreatures[0].Id);
        Assert.Equal("lightning", run.Players[0].Combat.OrbQueue.Orbs[0].ModelId);
        Assert.Equal("strength", run.Players[0].Creature.PowerInstances[0].ModelId);
        Assert.Single(run.Players[0].Creature.PowerInstances[0].HoverTips);
        Assert.Equal("Strength", run.Players[0].Creature.PowerInstances[0].HoverTips[0].Title);
        Assert.Equal("model://powers/strength/icon", run.Players[0].Creature.PowerInstances[0].HoverTips[0].IconAssetKey);
    }

}
