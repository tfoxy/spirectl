using Spirectl.Sts2;
using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Perspective;
using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2.Core.State;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

public sealed class RuntimeStateMapperTests
{
    private readonly RuntimeStateMapper _mapper = new();

    [Fact]
    public void MapsMainMenuObservationIntoScreenAwareState()
    {
        var observation = new BridgeRuntimeObservation(
            SchemaVersion: "spirectl/v0",
            GameVersion: "sts2-live",
            BridgeVersion: BridgeBuildInfo.BridgeVersion,
            Source: DataSourceKind.Live,
            Provisional: false,
            ScreenType: "main-menu",
            ScreenTitle: "Main Menu",
            ScreenInstanceId: "screen:main-menu:live",
            DefaultPlayerId: null,
            Menu: new MenuStateSnapshot("main-menu", "Main Menu"),
            Lobby: null,
            Run: null,
            Combat: null,
            Map: null,
            EventRoom: null,
            TreasureRoom: null,
            RelicSelection: null,
            RestSite: null,
            Shop: null,
            Rewards: null,
            CardSelection: null,
            SimpleCardSelection: null,
            DeckCardSelection: null,
            BundleSelection: null,
            MultiplayerLobby: null,
            Choices:
            [
                new ChoiceSnapshot("menu:start-run", "Start Run", "menu", Provisional: false),
            ],
            AvailableActions:
            [
                new AvailableActionSnapshot(
                    "action:menu:start-run",
                    SemanticActionKind.Choose,
                    "Choose the visible Start Run option.",
                    "sts2 act choose --choice menu:start-run",
                    Provisional: false,
                    Arguments: new ActionArgumentsSnapshot(null, null, null, "menu:start-run", null, null)),
            ],
            Notices:
            [
                new StateNoticeSnapshot("main-menu-live", "Main menu actions are derived from the visible menu screen.", false),
            ],
            Debug: null,
            Language: "eng");

        var state = _mapper.Map(observation, new PlayerPerspective(PlayerScope.Local, null, UsesDefault: true));

        Assert.Equal("main-menu", state.ScreenType);
        Assert.Equal("eng", state.Language);
        Assert.NotNull(state.Menu);
        Assert.Single(state.Notices);
        Assert.Equal("menu:start-run", state.AvailableActions[0].Arguments?.ChoiceId);
    }

    [Fact]
    public void RequestedEventRoomSectionKeepsEventRoomAndOmitsUnrelatedSections()
    {
        var state = _mapper.Map(
            NonCombatObservation(),
            new PlayerPerspective(PlayerScope.Local, null, UsesDefault: true),
            new HashSet<string>(StringComparer.Ordinal) { "eventRoom" });

        Assert.NotNull(state.EventRoom);
        Assert.Equal(2, state.EventRoom.Options.Count);
        Assert.Equal("event:local", state.EventRoom.Options[0].Id);
        Assert.Null(state.Map);
        Assert.Null(state.Combat);
        Assert.Null(state.Shop);
        Assert.Null(state.TreasureRoom);
    }

    [Fact]
    public void LocalPerspectiveFiltersCombatPlayersAndActionsToResolvedPlayer()
    {
        var observation = CombatObservation();

        var state = _mapper.Map(observation, new PlayerPerspective(PlayerScope.Local, null, UsesDefault: true));

        Assert.Equal("p1", state.ResolvedPerspective.PlayerId);
        Assert.Equal("p1", state.PlayerId);
        Assert.Equal("local:p1", state.Perspective);
        Assert.True(state.IsLocal);
        Assert.True(state.RemoteOrchestration is { Id: "local-only-degraded" });
        Assert.NotNull(state.Combat);
        Assert.Single(state.Combat.Players);
        Assert.Single(state.Combat.PlayersById);
        Assert.Equal("p1", state.Combat.Players[0].Id);
        Assert.True(state.Combat.PlayersById.ContainsKey("p1"));
        Assert.True(state.Run!.PlayersById["p1"].IsLocal);
        Assert.True(state.Run.PlayersById["p1"].IsHost);
        Assert.False(state.Run.PlayersById["p2"].IsLocal);
        Assert.True(state.Run.PlayersById["p2"].IsRemote);
        Assert.Equal("p1", state.Combat.Hand[0].OwnerPlayerId);
        Assert.True(state.Combat.Players[0].IsLocal);
        Assert.True(state.Combat.Players[0].IsHost);
        Assert.False(state.Combat.Players[0].IsRemote);
        Assert.Equal("p1", state.Choices[0].OwnerPlayerId);
        Assert.Single(state.AvailableActions);
        Assert.Equal("p1", state.AvailableActions[0].Arguments?.PlayerId);
        Assert.NotNull(state.Combat.EncounterVisuals);
        Assert.Equal("composed://encounters/kaiser_crab_boss/scene-package", state.Combat.EncounterVisuals.PackageId);
        Assert.Equal(3, state.Combat.EncounterVisuals.VisualParts.Count);
        Assert.Equal("rocket-charge-up", state.Combat.EncounterVisuals.RecentEvents[0].TransitionId);
    }

    [Fact]
    public void OmniscientPerspectiveRetainsAllCombatPlayers()
    {
        var observation = CombatObservation();

        var state = _mapper.Map(observation, new PlayerPerspective(PlayerScope.Omniscient, "p2", UsesDefault: false));

        Assert.NotNull(state.Run);
        Assert.True(state.Run.PlayersById["p1"].IsHost);
        Assert.True(state.Run.PlayersById["p2"].IsRemote);
        Assert.NotNull(state.Combat);
        Assert.Equal(2, state.Combat.Players.Count);
        Assert.True(state.Combat.PlayersById["p1"].IsLocal);
        Assert.True(state.Combat.PlayersById["p1"].IsHost);
        Assert.True(state.Combat.PlayersById["p2"].IsRemote);
        Assert.Equal("p2", state.ResolvedPerspective.PlayerId);
        Assert.Equal("omniscient:p2", state.Perspective);
        Assert.Equal(2, state.AvailableActions.Count);
        Assert.NotNull(state.Combat.EncounterVisuals);
        Assert.Equal("rocket", state.Combat.EncounterVisuals.VisualParts[1].PartId);
    }

    [Fact]
    public void LocalHostPerspectiveRetainsAuthoredHostLocalCombatSeats()
    {
        var host = new LobbyPlayerSnapshot("p1", "ready", null, "ironclad", true, 0, true, true, false);
        var hostLocalSeat = new LobbyPlayerSnapshot("p2", "ready", null, "ironclad", true, 1, false, false, false, IsHostLocalSeat: true);
        var observation = CombatObservation() with
        {
            Lobby = new LobbyStateSnapshot(
                "combat-fixture",
                "in-run",
                [host, hostLocalSeat],
                [new LobbyCharacterSnapshot("ironclad", "Ironclad", true)],
                "p1",
                "p1",
                "host",
                new Dictionary<string, LobbyPlayerSnapshot>(StringComparer.Ordinal)
                {
                    ["p1"] = host,
                    ["p2"] = hostLocalSeat,
                },
                new Dictionary<string, LobbyCharacterSnapshot>(StringComparer.Ordinal)
                {
                    ["ironclad"] = new LobbyCharacterSnapshot("ironclad", "Ironclad", true),
                }),
            Run = CombatObservation().Run! with
            {
                Players =
                [
                    new PlayerStateSnapshot("p1", "ironclad", 67, 80, IsLocal: true, IsHost: true, IsRemote: false),
                    new PlayerStateSnapshot("p2", "ironclad", 52, 75, IsHostLocalSeat: true),
                ],
                PlayersById = new Dictionary<string, PlayerStateSnapshot>(StringComparer.Ordinal)
                {
                    ["p1"] = new PlayerStateSnapshot("p1", "ironclad", 67, 80, IsLocal: true, IsHost: true, IsRemote: false),
                    ["p2"] = new PlayerStateSnapshot("p2", "ironclad", 52, 75, IsHostLocalSeat: true),
                },
            },
            Combat = CombatObservation().Combat! with
            {
                EncounterId = "nibbits-weak",
                EncounterLabel = "Nibbits",
                Players =
                [
                    new CombatPlayerStateSnapshot("p1", "ironclad", 67, 80, 0, 3, 3, [], IsLocal: true, IsHost: true, IsRemote: false),
                    new CombatPlayerStateSnapshot("p2", "ironclad", 52, 75, 0, 3, 3, [], IsHostLocalSeat: true),
                ],
                PlayersById = new Dictionary<string, CombatPlayerStateSnapshot>(StringComparer.Ordinal)
                {
                    ["p1"] = new CombatPlayerStateSnapshot("p1", "ironclad", 67, 80, 0, 3, 3, [], IsLocal: true, IsHost: true, IsRemote: false),
                    ["p2"] = new CombatPlayerStateSnapshot("p2", "ironclad", 52, 75, 0, 3, 3, [], IsHostLocalSeat: true),
                },
            },
        };

        var state = _mapper.Map(observation, new PlayerPerspective(PlayerScope.Local, "p1", UsesDefault: false));

        Assert.NotNull(state.Combat);
        Assert.Equal("nibbits-weak", state.Combat.EncounterId);
        Assert.Equal(["p1", "p2"], state.Combat.Players.Select(player => player.Id));
        Assert.Equal("ironclad", state.Combat.PlayersById["p1"].Character);
        Assert.Equal("ironclad", state.Combat.PlayersById["p2"].Character);
        Assert.Equal(67, state.Combat.PlayersById["p1"].Hp);
        Assert.Equal(80, state.Combat.PlayersById["p1"].MaxHp);
        Assert.Equal(52, state.Combat.PlayersById["p2"].Hp);
        Assert.Equal(75, state.Combat.PlayersById["p2"].MaxHp);
        Assert.True(state.Combat.PlayersById["p2"].IsHostLocalSeat);
        Assert.False(state.Combat.PlayersById["p2"].IsRemote);
    }

    [Fact]
    public void LobbyObservationCarriesByIdMapsAndPerspectiveFields()
    {
        var localPlayer = new LobbyPlayerSnapshot(
            "p:100",
            "not-ready",
            null,
            "ironclad",
            false,
            0,
            true,
            true,
            false);
        var remotePlayer = new LobbyPlayerSnapshot(
            "p:200",
            "ready",
            null,
            "silent",
            true,
            1,
            false,
            false,
            true);
        var observation = new BridgeRuntimeObservation(
            SchemaVersion: "spirectl/v0",
            GameVersion: "sts2-live",
            BridgeVersion: BridgeBuildInfo.BridgeVersion,
            Source: DataSourceKind.Live,
            Provisional: true,
            ScreenType: "Screens.CharacterSelect.NCharacterSelectScreen",
            ScreenTitle: "Character Select",
            ScreenInstanceId: "screen:lobby:1",
            DefaultPlayerId: "p1",
            Menu: null,
            Lobby: new LobbyStateSnapshot(
                "start-run",
                "selecting",
                [localPlayer, remotePlayer],
                [
                    new LobbyCharacterSnapshot("ironclad", "Ironclad", true),
                    new LobbyCharacterSnapshot("silent", "Silent", true),
                ],
                "p:100",
                "p:100",
                "host",
                new Dictionary<string, LobbyPlayerSnapshot>(StringComparer.Ordinal)
                {
                    ["p:100"] = localPlayer,
                    ["p:200"] = remotePlayer,
                },
                new Dictionary<string, LobbyCharacterSnapshot>(StringComparer.Ordinal)
                {
                    ["ironclad"] = new LobbyCharacterSnapshot("ironclad", "Ironclad", true),
                    ["silent"] = new LobbyCharacterSnapshot("silent", "Silent", true),
                }),
            Run: null,
            Combat: null,
            Choices: [],
            AvailableActions: [],
            Notices:
            [
                new StateNoticeSnapshot(
                    "lobby-partial",
                    "Lobby player readiness is not currently derivable from the active hooks.",
                    true),
            ],
            Debug: null);

        var state = _mapper.Map(observation, new PlayerPerspective(PlayerScope.Local, null, UsesDefault: true));

        Assert.NotNull(state.Lobby);
        Assert.Equal("p:100", state.ResolvedPerspective.PlayerId);
        Assert.Equal("p:100", state.Lobby.LocalPlayerId);
        Assert.Equal("p:100", state.Lobby.HostPlayerId);
        Assert.Equal("host", state.Lobby.LocalPlayerRole);
        Assert.Equal("ironclad", state.Lobby.PlayersById["p:100"].SelectedCharacterId);
        Assert.Equal("Silent", state.Lobby.AvailableCharactersById["silent"].Name);
        Assert.Single(state.Notices);
        Assert.True(state.Notices[0].Provisional);
    }

    [Fact]
    public void LocalPerspectiveFiltersNonCombatPresentationListsAndAddsStableNotices()
    {
        var observation = NonCombatObservation();

        var state = _mapper.Map(observation, new PlayerPerspective(PlayerScope.Local, null, UsesDefault: true));

        Assert.Equal("p1", state.ResolvedPerspective.PlayerId);
        AssertLocalOnly(state.Map!.Nodes, "map:local", "map:shared");
        AssertLocalOnly(state.EventRoom!.Options, "event:local", "event:shared");
        AssertLocalOnly(state.TreasureRoom!.Relics, "treasure:local", "treasure:shared");
        AssertLocalOnly(state.RelicSelection!.Relics, "relic-selection:local", "relic-selection:shared");
        AssertLocalOnly(state.Shop!.PurchasableItems, "shop:local", "shop:shared");
        AssertLocalOnly(state.Rewards!.Rewards, "reward:local", "reward:shared");
        AssertLocalOnly(state.CardSelection!.Cards, "card-selection:local", "card-selection:shared");
        AssertLocalOnly(state.SimpleCardSelection!.Choices, "simple-card-selection:local", "simple-card-selection:shared");
        AssertLocalOnly(state.DeckCardSelection!.DeckCards, "deck-card-selection:local", "deck-card-selection:shared");
        AssertLocalOnly(state.BundleSelection!.Bundles, "bundle-selection:local", "bundle-selection:shared");
        Assert.Equal(["rest:local", "rest:shared"], state.RestSite!.Controls.Select(control => control.Id));
        Assert.Equal(["overlay:local", "overlay:shared", "overlay:ambiguous"], state.CardOverlay!.Cards.Select(card => card.Id));
        Assert.DoesNotContain(state.CardOverlay.Cards, card => card.OwnerPlayerId == "p2");
        Assert.Equal(
            ["card-overlay:confirm:local", "card-overlay:confirm:shared", "card-overlay:confirm:ambiguous"],
            state.CardOverlay.FollowThroughControls!.Select(control => control.Id));
        Assert.DoesNotContain(state.CardOverlay.FollowThroughControls!, control => control.OwnerPlayerId == "p2");

        var noticePaths = state.Notices
            .Where(notice => notice.Code == "player-presentation-filtered")
            .Select(notice => notice.Path ?? string.Empty)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            [
                "bundleSelection.bundles",
                "cardOverlay.cards",
                "cardOverlay.followThroughControls",
                "cardSelection.cards",
                "deckCardSelection.deckCards",
                "eventRoom.options",
                "lobby.actions",
                "map.nodes",
                "relicSelection.relics",
                "restSite.controls",
                "rewards.rewards",
                "shop.purchasableItems",
                "simpleCardSelection.choices",
                "treasureRoom.relics",
            ],
            noticePaths);
        Assert.All(
            state.Notices.Where(notice => notice.Code == "player-presentation-filtered"),
            notice =>
            {
                Assert.False(notice.Provisional);
                Assert.Equal("info", notice.Severity);
                Assert.Equal(nameof(RuntimeStateMapper), notice.Source);
                Assert.Equal("stable", notice.Stability);
                Assert.Equal("local:p1", notice.Perspective);
            });
    }

    [Fact]
    public void LocalPerspectivePreservesLobbyPlayerIdentitiesButFiltersLobbyAndCompatibilityActions()
    {
        var observation = NonCombatObservation();

        var state = _mapper.Map(observation, new PlayerPerspective(PlayerScope.Local, null, UsesDefault: true));

        Assert.NotNull(state.MultiplayerLobby);
        Assert.Equal("local:p1", state.MultiplayerLobby.Perspective);
        Assert.Equal("local-only-degraded", state.MultiplayerLobby.RemoteOrchestration?.Id);
        Assert.Equal(["lobby:player:p1", "lobby:player:p2"], state.MultiplayerLobby.Players.Select(player => player.Id));
        Assert.Equal(["lobby:action:ready", "lobby:action:shared"], state.MultiplayerLobby.Actions.Select(action => action.Id));
        Assert.DoesNotContain(state.MultiplayerLobby.Actions, action => action.OwnerPlayerId == "p2");

        Assert.Equal(["action:local-args", "action:local-owner", "action:shared"], state.AvailableActions.Select(action => action.Id));
        Assert.DoesNotContain(state.AvailableActions, action => action.OwnerPlayerId == "p2");
        Assert.DoesNotContain(state.AvailableActions, action => action.Arguments?.PlayerId == "p2");
    }

    [Fact]
    public void HostPerspectiveRetainsHostLocalSeatActionsAndFiltersTrueRemoteDetails()
    {
        var hostLocalSeat = new LobbyPlayerSnapshot(
            "p2",
            "ready",
            "Silent",
            "silent",
            true,
            1,
            IsLocal: true,
            IsHost: false,
            IsRemote: false,
            IsHostLocalSeat: true);
        var remoteClient = new LobbyPlayerSnapshot(
            "p3",
            "ready",
            "Defect",
            "defect",
            true,
            2,
            IsLocal: false,
            IsHost: false,
            IsRemote: true);
        var lobby = new LobbyStateSnapshot(
            "start-run",
            "selecting",
            [
                new LobbyPlayerSnapshot("p1", "ready", "Ironclad", "ironclad", true, 0, IsLocal: true, IsHost: true, IsRemote: false),
                hostLocalSeat,
                remoteClient,
            ],
            [],
            LocalPlayerId: "p1",
            HostPlayerId: "p1",
            LocalPlayerRole: "host",
            PlayersById: new Dictionary<string, LobbyPlayerSnapshot>(StringComparer.Ordinal)
            {
                ["p1"] = new LobbyPlayerSnapshot("p1", "ready", "Ironclad", "ironclad", true, 0, IsLocal: true, IsHost: true, IsRemote: false),
                ["p2"] = hostLocalSeat,
                ["p3"] = remoteClient,
            },
            AvailableCharactersById: new Dictionary<string, LobbyCharacterSnapshot>(StringComparer.Ordinal));
        var observation = NonCombatObservation() with
        {
            Lobby = lobby,
            Map = new MapStateSnapshot(
            [
                new VisibleItemStateSnapshot("map:host", "Host", OwnerPlayerId: "p1", PlayerId: "p1"),
                new VisibleItemStateSnapshot("map:host-local-seat", "Host Local Seat", OwnerPlayerId: "p2", PlayerId: "p2"),
                new VisibleItemStateSnapshot("map:true-remote", "True Remote", OwnerPlayerId: "p3", PlayerId: "p3"),
            ]),
            MultiplayerLobby = new MultiplayerLobbyStateSnapshot(
                [
                    new VisibleItemStateSnapshot("lobby:player:p1", "Ironclad", OwnerPlayerId: "p1", PlayerId: "p1"),
                    new VisibleItemStateSnapshot("lobby:player:p2", "Silent", OwnerPlayerId: "p2", PlayerId: "p2"),
                    new VisibleItemStateSnapshot("lobby:player:p3", "Defect", OwnerPlayerId: "p3", PlayerId: "p3"),
                ],
                [
                    new VisibleActionReferenceSnapshot("lobby:action:p1:ready", "Ready", true, OwnerPlayerId: "p1"),
                    new VisibleActionReferenceSnapshot("lobby:action:p2:ready", "Ready", true, OwnerPlayerId: "p2"),
                    new VisibleActionReferenceSnapshot("lobby:action:p3:ready", "Ready", true, OwnerPlayerId: "p3"),
                ]),
            Choices =
            [
                new ChoiceSnapshot("choice:p1", "Host", "test", false, OwnerPlayerId: "p1"),
                new ChoiceSnapshot("choice:p2", "Host Local Seat", "test", false, OwnerPlayerId: "p2"),
                new ChoiceSnapshot("choice:p3", "True Remote", "test", false, OwnerPlayerId: "p3"),
            ],
            AvailableActions =
            [
                new AvailableActionSnapshot(
                    "action:p1",
                    SemanticActionKind.Choose,
                    "Host action.",
                    "sts2 act choose --choice p1",
                    false,
                    new ActionArgumentsSnapshot("p1", null, null, "choice:p1", null, null),
                    OwnerPlayerId: "p1"),
                new AvailableActionSnapshot(
                    "action:p2",
                    SemanticActionKind.Choose,
                    "Host-local-seat action.",
                    "sts2 act choose --choice p2",
                    false,
                    new ActionArgumentsSnapshot("p2", null, null, "choice:p2", null, null),
                    OwnerPlayerId: "p2"),
                new AvailableActionSnapshot(
                    "action:p3",
                    SemanticActionKind.Choose,
                    "True remote action.",
                    "sts2 act choose --choice p3",
                    false,
                    new ActionArgumentsSnapshot("p3", null, null, "choice:p3", null, null),
                    OwnerPlayerId: "p3",
                    RemoteOrchestration: OwnershipMetadata.LocalOnlyDegraded()),
            ],
        };

        var state = _mapper.Map(observation, new PlayerPerspective(PlayerScope.Local, null, UsesDefault: true));

        Assert.Equal(["map:host", "map:host-local-seat"], state.Map!.Nodes.Select(node => node.Id));
        var hostLocalSeatNode = Assert.Single(state.Map.Nodes, node => node.OwnerPlayerId == "p2");
        Assert.True(hostLocalSeatNode.IsHostLocalSeat);
        Assert.Equal("host-local-seat", hostLocalSeatNode.RemoteOrchestration?.Id);
        Assert.Equal(["choice:p1", "choice:p2"], state.Choices.Select(choice => choice.Id));
        Assert.Equal(["action:p1", "action:p2"], state.AvailableActions.Select(action => action.Id));
        Assert.Equal("host-local-seat", state.AvailableActions.Single(action => action.Id == "action:p2").RemoteOrchestration?.Id);
        Assert.Equal(["lobby:action:p1:ready", "lobby:action:p2:ready"], state.MultiplayerLobby!.Actions.Select(action => action.Id));
        Assert.DoesNotContain(state.Map.Nodes, node => node.OwnerPlayerId == "p3");
        Assert.DoesNotContain(state.AvailableActions, action => action.OwnerPlayerId == "p3");
    }

    private static BridgeRuntimeObservation CombatObservation()
    {
        return new BridgeRuntimeObservation(
            SchemaVersion: "spirectl/v0",
            GameVersion: "sts2-live",
            BridgeVersion: BridgeBuildInfo.BridgeVersion,
            Source: DataSourceKind.Live,
            Provisional: false,
            ScreenType: "combat",
            ScreenTitle: "Combat",
            ScreenInstanceId: "screen:combat:live",
            DefaultPlayerId: "p1",
            Menu: null,
            Lobby: null,
            Run: new RunStateSnapshot(
                "LIVE-SEED",
                5,
                1,
                [
                    new PlayerStateSnapshot("p1", "ironclad", 70, 80, IsLocal: true, IsHost: true, IsRemote: false),
                    new PlayerStateSnapshot("p2", "silent", 68, 70, IsLocal: false, IsHost: false, IsRemote: true),
                ],
                new Dictionary<string, PlayerStateSnapshot>(StringComparer.Ordinal)
                {
                    ["p1"] = new PlayerStateSnapshot("p1", "ironclad", 70, 80, IsLocal: true, IsHost: true, IsRemote: false),
                    ["p2"] = new PlayerStateSnapshot("p2", "silent", 68, 70, IsLocal: false, IsHost: false, IsRemote: true),
                }),
            Combat: new CombatStateSnapshot(
                Turn: 3,
                ActivePlayerId: "p1",
                IsPlayerTurn: true,
                Hand:
                [
                    new CardStateSnapshot("c_1", "Jab", 1, "p1", true, null, ["e_1"], false),
                ],
                Players:
                [
                    new CombatPlayerStateSnapshot(
                        "p1",
                        "ironclad",
                        70,
                        80,
                        0,
                        3,
                        3,
                        [
                            new CardStateSnapshot("c_1", "Jab", 1, "p1", true, null, ["e_1"], false),
                        ],
                        IsLocal: true,
                        IsHost: true,
                        IsRemote: false),
                    new CombatPlayerStateSnapshot(
                        "p2",
                        "silent",
                        68,
                        70,
                        5,
                        2,
                        3,
                        [
                            new CardStateSnapshot("c_9", "Defend", 1, "p2", true, null, [], false),
                        ],
                        IsLocal: false,
                        IsHost: false,
                        IsRemote: true),
                ],
                Enemies:
                [
                    new EnemyStateSnapshot(
                        "e_1",
                        "Gnash Grub",
                        38,
                        "attack+block",
                        42,
                        0,
                        true,
                        [
                            new EnemyIntentSnapshot("attack", 11, 1, 11),
                        ]),
                ],
                new Dictionary<string, CombatPlayerStateSnapshot>(StringComparer.Ordinal)
                {
                    ["p1"] = new CombatPlayerStateSnapshot(
                        "p1",
                        "ironclad",
                        70,
                        80,
                        0,
                        3,
                        3,
                        [
                            new CardStateSnapshot("c_1", "Jab", 1, "p1", true, null, ["e_1"], false),
                        ],
                        IsLocal: true,
                        IsHost: true,
                        IsRemote: false),
                    ["p2"] = new CombatPlayerStateSnapshot(
                        "p2",
                        "silent",
                        68,
                        70,
                        5,
                        2,
                        3,
                        [
                            new CardStateSnapshot("c_9", "Defend", 1, "p2", true, null, [], false),
                        ],
                        IsLocal: false,
                        IsHost: false,
                        IsRemote: true),
                },
                EncounterId: "kaiser_crab_boss",
                EncounterLabel: "Kaiser Crab",
                EncounterVisuals: new EncounterVisualsStateSnapshot(
                    PackageId: "composed://encounters/kaiser_crab_boss/scene-package",
                    EncounterId: "kaiser_crab_boss",
                    Provisional: true,
                    VisualParts:
                    [
                        new EncounterVisualPartStateSnapshot("crusher", "crusher", "default", "left", "left"),
                        new EncounterVisualPartStateSnapshot("rocket", "rocket", "rocket-charge-up", "right", "right"),
                        new EncounterVisualPartStateSnapshot("body", string.Empty, "default", "center", "body"),
                    ],
                    RecentEvents:
                    [
                        new EncounterVisualTransitionEventSnapshot(
                            1,
                            "composed://encounters/kaiser_crab_boss/scene-package",
                            "rocket-charge-up",
                            ["rocket"],
                            "rocket-charge-up",
                            "NKaiserCrabBossBackground.PlayRightSideChargeUpAnim"),
                    ],
                    Notices:
                    [
                        new StateNoticeSnapshot(
                            "encounter-visuals-provisional",
                            "Encounter visual state is provisional and shared across multiplayer perspectives.",
                            true,
                            Path: "combat.encounterVisuals",
                            Severity: "info",
                            Source: nameof(RuntimeStateMapperTests)),
                    ])),
            Map: null,
            EventRoom: null,
            TreasureRoom: null,
            RelicSelection: null,
            RestSite: null,
            Shop: null,
            Rewards: null,
            CardSelection: null,
            SimpleCardSelection: null,
            DeckCardSelection: null,
            BundleSelection: null,
            MultiplayerLobby: null,
            Choices:
            [
                new ChoiceSnapshot("target:e_1", "Gnash Grub", "target", Provisional: false, OwnerPlayerId: "p1"),
            ],
            AvailableActions:
            [
                new AvailableActionSnapshot(
                    "action:p1:play-card",
                    SemanticActionKind.PlayCard,
                    "Play Jab at Gnash Grub.",
                    "sts2 act play-card --card c_1 --target e_1",
                    Provisional: false,
                    Arguments: new ActionArgumentsSnapshot("p1", "c_1", "e_1", null, null, null)),
                new AvailableActionSnapshot(
                    "action:p2:end-turn",
                    SemanticActionKind.EndTurn,
                    "End turn for player p2.",
                    "sts2 act end-turn --player-id p2",
                    Provisional: true,
                    Arguments: new ActionArgumentsSnapshot("p2", null, null, null, null, null)),
            ],
            Notices: [],
            Debug: null);
    }

    private static BridgeRuntimeObservation NonCombatObservation()
    {
        return new BridgeRuntimeObservation(
            SchemaVersion: "spirectl/v0",
            GameVersion: "sts2-live",
            BridgeVersion: BridgeBuildInfo.BridgeVersion,
            Source: DataSourceKind.Live,
            Provisional: false,
            ScreenType: "map",
            ScreenTitle: "Map",
            ScreenInstanceId: "screen:map:live",
            DefaultPlayerId: "p1",
            Menu: null,
            Lobby: null,
            Run: null,
            Combat: null,
            Map: new MapStateSnapshot(OwnedItems("map")),
            EventRoom: new EventRoomStateSnapshot(OwnedItems("event")),
            TreasureRoom: new TreasureRoomStateSnapshot(OwnedItems("treasure")),
            RelicSelection: new RelicSelectionStateSnapshot(OwnedItems("relic-selection")),
            RestSite: new RestSiteStateSnapshot(
            [
                new VisibleControlStateSnapshot("rest:local", "Rest", true, "p1"),
                new VisibleControlStateSnapshot("rest:remote", "Smith", true, "p2"),
                new VisibleControlStateSnapshot("rest:shared", "Proceed", true),
            ]),
            Shop: new ShopStateSnapshot(OwnedItems("shop")),
            Rewards: new RewardsStateSnapshot(OwnedItems("reward")),
            CardSelection: new CardSelectionStateSnapshot(OwnedItems("card-selection")),
            SimpleCardSelection: new SimpleCardSelectionStateSnapshot(OwnedItems("simple-card-selection")),
            DeckCardSelection: new DeckCardSelectionStateSnapshot(OwnedItems("deck-card-selection")),
            BundleSelection: new BundleSelectionStateSnapshot(OwnedItems("bundle-selection")),
            MultiplayerLobby: new MultiplayerLobbyStateSnapshot(
                [
                    new VisibleItemStateSnapshot("lobby:player:p1", "Ironclad", OwnerPlayerId: "p1", PlayerId: "p1", IsLocal: true, IsHost: true, HostPlayerId: "p1", Perspective: "local:p1"),
                    new VisibleItemStateSnapshot("lobby:player:p2", "Silent", OwnerPlayerId: "p2", PlayerId: "p2", IsRemote: true, HostPlayerId: "p1", Perspective: "local:p2"),
                ],
                [
                    new VisibleActionReferenceSnapshot("lobby:action:ready", "Ready", true, "p1"),
                    new VisibleActionReferenceSnapshot("lobby:action:remote-ready", "Remote Ready", true, "p2"),
                    new VisibleActionReferenceSnapshot("lobby:action:shared", "Start", true),
                ]),
            Choices: [],
            AvailableActions:
            [
                new AvailableActionSnapshot(
                    "action:local-args",
                    SemanticActionKind.Choose,
                    "Local argument-owned action.",
                    "sts2 act choose --choice local",
                    Provisional: false,
                    Arguments: new ActionArgumentsSnapshot("p1", null, null, "local", null, null)),
                new AvailableActionSnapshot(
                    "action:remote-args",
                    SemanticActionKind.Choose,
                    "Remote argument-owned action.",
                    "sts2 act choose --choice remote",
                    Provisional: false,
                    Arguments: new ActionArgumentsSnapshot("p2", null, null, "remote", null, null)),
                new AvailableActionSnapshot(
                    "action:local-owner",
                    SemanticActionKind.Choose,
                    "Local owner-owned action.",
                    "sts2 act choose --choice local-owner",
                    Provisional: false,
                    Arguments: new ActionArgumentsSnapshot(null, null, null, "local-owner", null, null),
                    OwnerPlayerId: "p1"),
                new AvailableActionSnapshot(
                    "action:remote-owner",
                    SemanticActionKind.Choose,
                    "Remote owner-owned action.",
                    "sts2 act choose --choice remote-owner",
                    Provisional: false,
                    Arguments: new ActionArgumentsSnapshot(null, null, null, "remote-owner", null, null),
                    OwnerPlayerId: "p2"),
                new AvailableActionSnapshot(
                    "action:shared",
                    SemanticActionKind.Choose,
                    "Shared action.",
                    "sts2 act choose --choice shared",
                    Provisional: false,
                    Arguments: new ActionArgumentsSnapshot(null, null, null, "shared", null, null)),
            ],
            Notices: [],
            Debug: null,
            CardOverlay: new CardOverlayStateSnapshot(
                [
                    new CardStateSnapshot("overlay:local", "Local Overlay", 1, "p1", false, "inspection-only", [], false),
                    new CardStateSnapshot("overlay:remote", "Remote Overlay", 1, "p2", false, "inspection-only", [], false),
                    new CardStateSnapshot("overlay:shared", "Shared Overlay", 1, null, false, "inspection-only", [], false),
                    new CardStateSnapshot("overlay:ambiguous", "Ambiguous Overlay", 1, null, false, "inspection-only", [], false),
                ],
                OwnerPlayerId: null,
                Perspective: null,
                FollowThroughControls:
                [
                    new OverlayAffordanceSnapshot(
                        "card-overlay:confirm:local",
                        "Confirm Local",
                        true,
                        OwnerPlayerId: "p1",
                        ChoiceKind: "card-overlay-control",
                        IntentKind: "choose-overlay-control",
                        PreferredAction: "choose",
                        Perspective: "local"),
                    new OverlayAffordanceSnapshot(
                        "card-overlay:confirm:remote",
                        "Confirm Remote",
                        true,
                        OwnerPlayerId: "p2",
                        ChoiceKind: "card-overlay-control",
                        IntentKind: "choose-overlay-control",
                        PreferredAction: "choose",
                        Perspective: "local"),
                    new OverlayAffordanceSnapshot(
                        "card-overlay:confirm:shared",
                        "Confirm Shared",
                        true,
                        ChoiceKind: "card-overlay-control",
                        IntentKind: "choose-overlay-control",
                        PreferredAction: "choose"),
                    new OverlayAffordanceSnapshot(
                        "card-overlay:confirm:ambiguous",
                        "Confirm Ambiguous",
                        true,
                        ChoiceKind: "card-overlay-control",
                        IntentKind: "choose-overlay-control",
                        PreferredAction: "choose"),
                ]));
    }

    private static IReadOnlyList<VisibleItemStateSnapshot> OwnedItems(string prefix)
        =>
        [
            new VisibleItemStateSnapshot($"{prefix}:local", "Local", OwnerPlayerId: "p1", PlayerId: "p1", IsLocal: true, IsHost: true, HostPlayerId: "p1", Perspective: "local:p1"),
            new VisibleItemStateSnapshot($"{prefix}:remote", "Remote", OwnerPlayerId: "p2", PlayerId: "p2", IsRemote: true, HostPlayerId: "p1", Perspective: "local:p2"),
            new VisibleItemStateSnapshot($"{prefix}:shared", "Shared"),
        ];

    private static void AssertLocalOnly(IReadOnlyList<VisibleItemStateSnapshot> items, string localId, string sharedId)
    {
        Assert.Equal([localId, sharedId], items.Select(item => item.Id));
        Assert.DoesNotContain(items, item => item.OwnerPlayerId == "p2");
    }
}
