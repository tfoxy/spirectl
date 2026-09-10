using Spirectl.Sts2;
using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Perspective;
using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2.Core.State;
using Spirectl.Sts2.Live;
using Spirectl.Sts2.Live.EncounterVisuals;
using Spirectl.Proto.V0;
using Google.Protobuf;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Serialization;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

extern alias GodotLive;
#if ENABLE_STS2_LIVE_HOST && ENABLE_STALE_STS2_SCREEN_FIXTURE_TESTS
extern alias Sts2Live;
#endif

using GodotLive::Godot;
#if ENABLE_STS2_LIVE_HOST && ENABLE_STALE_STS2_SCREEN_FIXTURE_TESTS
using Sts2Live::MegaCrit.Sts2.Core.Entities.Players;
using Sts2Live::MegaCrit.Sts2.Core.Map;
using Sts2Live::MegaCrit.Sts2.Core.Models;
using Sts2Live::MegaCrit.Sts2.Core.Models.Cards.Mocks;
using Sts2Live::MegaCrit.Sts2.Core.Rooms;
using Sts2Live::MegaCrit.Sts2.Core.Runs;
using Sts2Live::MegaCrit.Sts2.Core.Runs.History;
#endif

public sealed partial class Sts2HostTests
{
    private static async Task WriteHandshakeRequestAsync(Stream stream, TransportKind transportKind)
    {
        var requestPayload = new HandshakeRequest
        {
            CliVersion = "0.0.0",
            RequestedSchemaVersion = "spirectl/v0",
            Mode = "normal",
            TransportKind = transportKind,
        }.ToByteArray();
        var requestHeader = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(requestHeader, 0x4c545053);
        BinaryPrimitives.WriteUInt16LittleEndian(requestHeader.AsSpan(4), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(requestHeader.AsSpan(6), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(
            requestHeader.AsSpan(8),
            checked((uint)requestPayload.Length));
        await stream.WriteAsync(requestHeader);
        await stream.WriteAsync(requestPayload);
        await stream.FlushAsync();
    }

    private static async Task<HandshakeResult> ReadHandshakeResponseAsync(Stream stream)
    {
        var responseHeader = new byte[12];
        await stream.ReadExactlyAsync(responseHeader);
        Assert.Equal(0x4c545053u, BinaryPrimitives.ReadUInt32LittleEndian(responseHeader));
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(responseHeader.AsSpan(4)));
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(responseHeader.AsSpan(6)));
        var responseLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(responseHeader.AsSpan(8)));
        var responsePayload = new byte[responseLength];
        await stream.ReadExactlyAsync(responsePayload);
        return HandshakeResult.Parser.ParseFrom(responsePayload);
    }

    private static async Task WriteBridgeRequestAsync(Stream stream, ushort method, IMessage request)
    {
        var requestPayload = request.ToByteArray();
        var requestHeader = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(requestHeader, 0x4c545053);
        BinaryPrimitives.WriteUInt16LittleEndian(requestHeader.AsSpan(4), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(requestHeader.AsSpan(6), method);
        BinaryPrimitives.WriteUInt32LittleEndian(
            requestHeader.AsSpan(8),
            checked((uint)requestPayload.Length));
        await stream.WriteAsync(requestHeader);
        await stream.WriteAsync(requestPayload);
        await stream.FlushAsync();
    }

    private static async Task<byte[]> ReadSuccessBridgeResponseAsync(Stream stream)
    {
        var responseHeader = new byte[12];
        await stream.ReadExactlyAsync(responseHeader);
        Assert.Equal(0x4c545053u, BinaryPrimitives.ReadUInt32LittleEndian(responseHeader));
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(responseHeader.AsSpan(4)));
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(responseHeader.AsSpan(6)));
        var responseLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(responseHeader.AsSpan(8)));
        var responsePayload = new byte[responseLength];
        await stream.ReadExactlyAsync(responsePayload);
        return responsePayload;
    }

    [Fact]
    public void SavedRunSnapshotResolverBuildsLoadRunLobbyRunState()
    {
        var helperType = GetLoadableTypes(typeof(Sts2ActionCatalog).Assembly)
            .SingleOrDefault(type => string.Equals(type.Name, "Sts2SavedRunSnapshotResolver", StringComparison.Ordinal));
        Assert.NotNull(helperType);

        var method = helperType!.GetMethod(
            "Resolve",
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);

        var savedRun = new TestSavedRun
        {
            CurrentActIndex = 2,
            Players =
            [
                new TestSavedPlayer
                {
                    NetId = 100,
                    CharacterId = new TestModelId("ironclad"),
                    CurrentHp = 71,
                    MaxHp = 80,
                },
                new TestSavedPlayer
                {
                    NetId = 200,
                    CharacterId = new TestModelId("silent"),
                    CurrentHp = 52,
                    MaxHp = 70,
                },
            ],
            SerializableRng = new TestSavedRunRng
            {
                Seed = "LOAD-RUN-SEED",
            },
            MapPointHistory =
            [
                [new TestMapPointHistoryEntry(), new TestMapPointHistoryEntry()],
                [new TestMapPointHistoryEntry()],
            ],
        };

        var snapshot = Assert.IsType<RunStateSnapshot>(method!.Invoke(null, [savedRun]));

        Assert.Equal("LOAD-RUN-SEED", snapshot.Seed);
        Assert.Equal(3, snapshot.Floor);
        Assert.Equal(3, snapshot.Act);
        Assert.Collection(
            snapshot.Players,
            player =>
            {
                Assert.Equal("p:100", player.Id);
                Assert.Equal("ironclad", player.Character);
                Assert.Equal(71, player.Hp);
                Assert.Equal(80, player.MaxHp);
            },
            player =>
            {
                Assert.Equal("p:200", player.Id);
                Assert.Equal("silent", player.Character);
                Assert.Equal(52, player.Hp);
                Assert.Equal(70, player.MaxHp);
            });
        Assert.Equal("silent", snapshot.PlayersById["p:200"].Character);
    }

#if ENABLE_STS2_LIVE_HOST && ENABLE_STALE_STS2_SCREEN_FIXTURE_TESTS
    [Fact]
    public void LoadRunLobbyObservationUsesSavedRunSnapshotResolver()
    {
        var runtimeObservationProviderType = GetLoadableTypes(typeof(Sts2ActionCatalog).Assembly)
            .SingleOrDefault(type => string.Equals(type.Name, "Sts2RuntimeObservationProvider", StringComparison.Ordinal));
        Assert.NotNull(runtimeObservationProviderType);

        var method = runtimeObservationProviderType!.GetMethod(
            "ResolveLoadRunLobbyRunSnapshot",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var savedRun = new TestSavedRun
        {
            CurrentActIndex = 1,
            Players =
            [
                new TestSavedPlayer
                {
                    NetId = 100,
                    CharacterId = new TestModelId("ironclad"),
                    CurrentHp = 68,
                    MaxHp = 80,
                },
            ],
            SerializableRng = new TestSavedRunRng
            {
                Seed = "OBSERVED-LOAD-RUN-SEED",
            },
            MapPointHistory =
            [
                [new TestMapPointHistoryEntry()],
                [new TestMapPointHistoryEntry(), new TestMapPointHistoryEntry()],
            ],
        };

        var snapshot = Assert.IsType<RunStateSnapshot>(method!.Invoke(null, [savedRun]));

        Assert.Equal("OBSERVED-LOAD-RUN-SEED", snapshot.Seed);
        Assert.Equal(3, snapshot.Floor);
        Assert.Equal(2, snapshot.Act);
        var player = Assert.Single(snapshot.Players);
        Assert.Equal("p:100", player.Id);
        Assert.Equal("ironclad", player.Character);
    }

    [Fact]
    public void CardSelectionInspectorRecognizesSimpleCardGridScreens()
    {
        var screen = new Sts2Live::MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NSimpleCardSelectScreen();

        Assert.True(InvokeIsSupportedCardSelectionScreen(screen));
    }

    [Fact]
    public void CardSelectionInspectorResolvesVisibleSimpleCardGridChoicesAsExecutable()
    {
        var model = TestCardSelectionModel.Create("bash");
        var screen = new Sts2Live::MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NSimpleCardSelectScreen
        {
            _cardRow = new TestUiContainer(),
        };
        ((TestUiContainer)screen._cardRow).AddChild(new TestCardHolder(model));

        var notices = new List<StateNoticeSnapshot>();
        var choices = InvokeResolveCardSelectionChoices(screen, "p1", notices);

        var choice = Assert.Single(choices);
        Assert.Equal($"card-selection:card:{model.Id.Entry}:0", ReadChoiceSnapshotString(choice, "Id"));
        Assert.Equal("card-selection-card", ReadChoiceSnapshotString(choice, "Kind"));
        Assert.Equal("p1", ReadChoiceSnapshotString(choice, "OwnerPlayerId"));
        Assert.True(ReadChoiceBool(choice, "IsExecutable"));
        Assert.Empty(notices);
    }

    [Fact]
    public void CardSelectionInspectorResolvesVisibleDeckCardGridChoicesAsExecutable()
    {
        var model = TestCardSelectionModel.Create("defend");
        var screen = new Sts2Live::MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NDeckUpgradeSelectScreen
        {
            _cardRow = new TestUiContainer(),
        };
        ((TestUiContainer)screen._cardRow).AddChild(new TestCardHolder(model));

        var choices = InvokeResolveCardSelectionChoices(screen, "p1", notices: null);

        var choice = Assert.Single(choices);
        Assert.Equal($"card-selection:card:{model.Id.Entry}:0", ReadChoiceSnapshotString(choice, "Id"));
        Assert.True(ReadChoiceBool(choice, "IsExecutable"));
    }

    [Fact]
    public void CardSelectionInspectorRecognizesBundleSelectionScreens()
    {
        var screen = new Sts2Live::MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NChooseABundleSelectionScreen();

        Assert.True(InvokeIsSupportedCardSelectionScreen(screen));
    }

    [Fact]
    public void CardSelectionInspectorResolvesVisibleBundleChoicesAsExecutable()
    {
        var firstBundle = new[]
        {
            TestCardSelectionModel.Create("bash"),
            TestCardSelectionModel.Create("defend"),
        };
        var secondBundle = new[]
        {
            TestCardSelectionModel.Create("zap"),
        };
        var screen = new Sts2Live::MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NChooseABundleSelectionScreen
        {
            _bundleRow = new TestUiContainer(),
        };
        ((TestUiContainer)screen._bundleRow).AddChild(new TestCardBundle(firstBundle));
        ((TestUiContainer)screen._bundleRow).AddChild(new TestCardBundle(secondBundle));

        var choices = InvokeResolveCardSelectionChoices(screen, "p1", notices: null);

        Assert.Equal(2, choices.Count);
        var firstChoice = choices[0];
        Assert.Equal("card-selection:bundle:bash+defend:0", ReadChoiceSnapshotString(firstChoice, "Id"));
        Assert.Equal("Bundle 1: bash, defend", ReadChoiceSnapshotString(firstChoice, "Label"));
        Assert.Equal("card-selection-bundle", ReadChoiceSnapshotString(firstChoice, "Kind"));
        Assert.Equal("p1", ReadChoiceSnapshotString(firstChoice, "OwnerPlayerId"));
        Assert.True(ReadChoiceBool(firstChoice, "IsExecutable"));

        var secondChoice = choices[1];
        Assert.Equal("card-selection:bundle:zap:1", ReadChoiceSnapshotString(secondChoice, "Id"));
        Assert.Equal("Bundle 2: zap", ReadChoiceSnapshotString(secondChoice, "Label"));
        Assert.True(ReadChoiceBool(secondChoice, "IsExecutable"));
    }

    [Fact]
    public void CardSelectionBundleExecutionInvokesBundlePickThenConfirm()
    {
        var firstBundle = new[]
        {
            TestCardSelectionModel.Create("bash"),
            TestCardSelectionModel.Create("defend"),
        };
        var screen = new TestBundleSelectionExecutionScreen();
        var bundle = new TestCardBundle(firstBundle);
        screen.BundleRow.AddChild(bundle);

        var choice = Assert.Single(InvokeResolveCardSelectionChoices(screen, "p1", notices: null));

        Assert.True(InvokeExecuteBundleSelectionChoice(screen, choice));
        Assert.Same(bundle, screen.ClickedBundle);
        Assert.Same(screen.PreviewConfirmButton, screen.ConfirmedButton);
    }
#endif

    [Fact]
    public void TreeSearchFindDescendantsReturnsNestedMatches()
    {
        var root = new TestTreeNode(
            "root",
            new TestTreeNode(
                "branch",
                new TestTreeNode("button:first"),
                new TestTreeNode(
                    "nested",
                    new TestTreeNode("button:second"))),
            new TestTreeNode("leaf"));

        var matches = InvokeFindDescendants(
            root,
            node => node.Children,
            node => node.Name.StartsWith("button:", StringComparison.Ordinal));

        Assert.Collection(
            matches,
            match => Assert.Equal("button:first", match.Name),
            match => Assert.Equal("button:second", match.Name));
    }

    [Fact]
    public void ActionCatalogOnlyAdvertisesExecutableMapNodeActions()
    {
        var actions = Sts2ActionCatalog.MapActions(
            localPlayerId: "p1",
            nodes:
            [
                new Sts2MapNodeSnapshot("map-node:3:1", "Monster (3,1)", "map-node", Travelable: true),
                new Sts2MapNodeSnapshot("map-node:4:2", "Shop (4,2)", "map-node", Travelable: false),
            ]);

        var action = Assert.Single(actions);
        Assert.Equal(SemanticActionKind.SelectMapNode, action.Kind);
        Assert.Equal("p1", action.Arguments?.PlayerId);
        Assert.Equal("map-node:3:1", action.Arguments?.MapNodeId);
        Assert.Equal("sts2 act select-map-node --node map-node:3:1", action.CliCommandHint);
    }

    [Fact]
    public void MapIdsExposeStableBackChoiceId()
    {
        Assert.Equal("map:back", Sts2MapIds.BackChoiceId());
    }

    [Fact]
    public void ActionCatalogAdvertisesExecutableMapBackChoice()
    {
        var actions = Sts2ActionCatalog.MapActions(
            localPlayerId: "p1",
            nodes:
            [
                new Sts2MapNodeSnapshot("map-node:3:1", "Monster (3,1)", "map-node", Travelable: true),
            ],
            flowChoices:
            [
                new ChoiceSnapshot(Sts2MapIds.BackChoiceId(), "Back", "map-flow", Provisional: false, OwnerPlayerId: "p1"),
            ]);

        Assert.Collection(
            actions,
            action =>
            {
                Assert.Equal(SemanticActionKind.SelectMapNode, action.Kind);
                Assert.Equal("map-node:3:1", action.Arguments?.MapNodeId);
            },
            action =>
            {
                Assert.Equal(SemanticActionKind.Choose, action.Kind);
                Assert.Equal("action:map:choose:map:back", action.Id);
                Assert.Equal("sts2 act choose --choice map:back", action.CliCommandHint);
                Assert.Equal("map:back", action.Arguments?.ChoiceId);
                Assert.Equal("p1", action.Arguments?.PlayerId);
            });
    }

    [Fact]
    public void ActionCatalogKeepsMapBackChoiceExecutableWhenTravelIsDisabled()
    {
        var actions = Sts2ActionCatalog.MapActions(
            localPlayerId: "p1",
            nodes:
            [
                new Sts2MapNodeSnapshot("map-node:3:1", "Monster (3,1)", "map-node", Travelable: true),
            ],
            isTravelEnabled: false,
            flowChoices:
            [
                new ChoiceSnapshot(Sts2MapIds.BackChoiceId(), "Back", "map-flow", Provisional: false, OwnerPlayerId: "p1"),
            ]);

        var action = Assert.Single(actions);
        Assert.Equal(SemanticActionKind.Choose, action.Kind);
        Assert.Equal("map:back", action.Arguments?.ChoiceId);
        Assert.Null(action.Arguments?.MapNodeId);
    }

    [Fact]
    public void ScreenPriorityLetsOpenMapOverrideRetainedScreens()
    {
        Assert.True(Sts2ScreenPriority.ShouldOpenMapOverrideScreen("crystal-sphere", isMapOpen: true, overlayPolicy: null));
        Assert.True(Sts2ScreenPriority.ShouldOpenMapOverrideScreen("event-room", isMapOpen: true, overlayPolicy: null));
        Assert.True(Sts2ScreenPriority.ShouldOpenMapOverrideScreen("treasure-room", isMapOpen: true, overlayPolicy: null));
        Assert.True(Sts2ScreenPriority.ShouldOpenMapOverrideScreen("rewards", isMapOpen: true, overlayPolicy: null));
        Assert.True(Sts2ScreenPriority.ShouldOpenMapOverrideScreen("combat", isMapOpen: true, overlayPolicy: null));
        Assert.True(Sts2ScreenPriority.ShouldOpenMapOverrideScreen("card-selection", isMapOpen: true, overlayPolicy: null));
        Assert.False(Sts2ScreenPriority.ShouldOpenMapOverrideScreen("crystal-sphere", isMapOpen: false));
    }

    [Fact]
    public void OverlayPriorityLetsBlockingOverlayOverrideRetainedScreens()
    {
        Assert.True(Sts2ScreenPriority.ShouldOverlayOverrideScreen("blocking", "event-room"));
        Assert.True(Sts2ScreenPriority.ShouldOverlayOverrideScreen("blocking", "combat"));
        Assert.False(Sts2ScreenPriority.ShouldRetainUnderlyingScreen("blocking", "event-room"));
        Assert.False(Sts2ScreenPriority.ShouldOpenMapOverrideScreen("event-room", isMapOpen: true, overlayPolicy: "blocking"));
    }

    [Fact]
    public void OverlayPriorityLetsPassiveOverlayRetainUnderlyingScreens()
    {
        Assert.False(Sts2ScreenPriority.ShouldOverlayOverrideScreen("passive", "combat"));
        Assert.True(Sts2ScreenPriority.ShouldRetainUnderlyingScreen("passive", "combat"));
        Assert.True(Sts2ScreenPriority.ShouldOpenMapOverrideScreen("combat", isMapOpen: true, overlayPolicy: "passive"));
    }

    [Fact]
    public void OverlayPriorityTreatsUnsupportedOverlaysAsBlockingNotices()
    {
        Assert.True(Sts2ScreenPriority.ShouldOverlayOverrideScreen("unsupported", "treasure-room"));
        Assert.False(Sts2ScreenPriority.ShouldRetainUnderlyingScreen("unsupported", "treasure-room"));

        var notice = Sts2UnsupportedScreenNotice.Create(
            "unsupported-overlay",
            "Experimental Info Overlay",
            "overlay",
            "MegaCrit.Sts2.Core.Nodes.Screens.Overlays.NExperimentalInfoOverlay");

        Assert.Equal("unsupported-overlay-unsupported", notice.Code);
        Assert.Contains("Experimental Info Overlay", notice.Message, StringComparison.Ordinal);
        Assert.Contains("NExperimentalInfoOverlay", notice.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ActionCatalogAdvertisesExecutableRewardChoices()
    {
        var actions = Sts2ActionCatalog.RewardActions(
            rewardChoices:
            [
                new ChoiceSnapshot("reward:p1:0", "Claim 30 Gold", "reward", Provisional: false),
                new ChoiceSnapshot("reward:p2:1", "Take Anchor", "reward", Provisional: false),
            ],
            choiceOwnersById: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["reward:p1:0"] = "p1",
                ["reward:p2:1"] = "p2",
            });

        Assert.Equal(2, actions.Count);
        Assert.Equal(SemanticActionKind.Choose, actions[0].Kind);
        Assert.Equal("p1", actions[0].Arguments?.PlayerId);
        Assert.Equal("reward:p1:0", actions[0].Arguments?.ChoiceId);
        Assert.Equal("sts2 act choose --choice reward:p1:0", actions[0].CliCommandHint);
    }

    [Fact]
    public void RewardIdsExposeStableFlowChoiceIds()
    {
        Assert.Equal("reward-flow:skip", Sts2RewardIds.FlowChoiceId(isSkip: true));
        Assert.Equal("reward-flow:proceed", Sts2RewardIds.FlowChoiceId(isSkip: false));
    }

    [Fact]
    public void RewardIdsParseVisibleStateChoiceIds()
    {
        var id = Sts2RewardIds.VisibleChoiceId("p:100", 2);

        Assert.Equal("reward:p:100:visible:2", id);
        Assert.True(Sts2RewardIds.TryParseVisibleRewardChoiceId(id, out var playerId, out var visibleIndex));
        Assert.Equal("p:100", playerId);
        Assert.Equal(2, visibleIndex);
    }

    [Fact]
    public void CardSelectionIdsExposeStableChoiceIds()
    {
        Assert.Equal("card-selection:card:bash:0", Sts2CardSelectionIds.CardChoiceId("bash", 0));
        Assert.Equal("card-selection:bundle:bash+defend:1", Sts2CardSelectionIds.BundleChoiceId("bash+defend", 1));
        Assert.Equal("card-selection:skip", Sts2CardSelectionIds.SkipChoiceId());
        Assert.Equal(
            "card-selection:alternative:reroll:1",
            Sts2CardSelectionIds.AlternativeChoiceId("reroll", 1));
    }

    [Fact]
    public void ActionCatalogAdvertisesExecutableCardSelectionChoices()
    {
        var actions = Sts2ActionCatalog.CardSelectionActions(
            playerId: "p1",
            choices:
            [
                new ChoiceSnapshot("card-selection:card:bash:0", "Take Bash", "card-selection-card", Provisional: false),
                new ChoiceSnapshot("card-selection:skip", "Skip", "card-selection-skip", Provisional: false),
            ]);

        Assert.Equal(2, actions.Count);
        Assert.All(actions, action => Assert.Equal(SemanticActionKind.Choose, action.Kind));
        Assert.Equal("card-selection:card:bash:0", actions[0].Arguments?.ChoiceId);
        Assert.Equal("p1", actions[0].Arguments?.PlayerId);
        Assert.Equal("card-selection:skip", actions[1].Arguments?.ChoiceId);
        Assert.Equal("p1", actions[1].Arguments?.PlayerId);
    }

    [Fact]
    public void LobbyNameResolverReturnsPlatformLookupNamesWithoutNotices()
    {
        var notices = new List<StateNoticeSnapshot>();

        var name = InvokeResolveLobbyPlayerName(
            platform: "steam",
            playerId: 42,
            notices,
            static (_, playerId) => $"Player {playerId}");

        Assert.Equal("Player 42", name);
        Assert.Empty(notices);
    }

    [Fact]
    public void LobbyNameResolverDeduplicatesUnavailableNotices()
    {
        var notices = new List<StateNoticeSnapshot>();

        Assert.Null(InvokeResolveLobbyPlayerName(
            platform: "steam",
            playerId: 42,
            notices,
            static (_, _) => " "));
        Assert.Null(InvokeResolveLobbyPlayerName(
            platform: null,
            playerId: 99,
            notices,
            lookup: null));

        var notice = Assert.Single(notices);
        Assert.Equal("lobby-player-names-unavailable", notice.Code);
    }

    [Fact]
    public void LobbyHostResolverReturnsLocalPlayerForHostServices()
    {
        var notices = new List<StateNoticeSnapshot>();

        var hostPlayerId = InvokeResolveLobbyHostPlayerId(
            new TestLobbyNetService("Host", null),
            localPlayerId: "p:100",
            notices);

        Assert.Equal("p:100", hostPlayerId);
        Assert.Empty(notices);
    }

    [Fact]
    public void RunOverlayRegistryTreatsHostLocalSeatsAsObservableLocalOwners()
    {
        Sts2HostLocalSeatRegistry.ReplaceHostLocalSeats([200]);
        try
        {
            Assert.True(Sts2RunOverlayRegistry.IsObservableLocalOwner(100, 100));
            Assert.True(Sts2RunOverlayRegistry.IsObservableLocalOwner(200, 100));
            Assert.False(Sts2RunOverlayRegistry.IsObservableLocalOwner(300, 100));
        }
        finally
        {
            Sts2HostLocalSeatRegistry.ReplaceHostLocalSeats([]);
            Sts2RunOverlayRegistry.ClearForTest();
        }
    }

    [Fact]
    public void RunOverlayRegistryStoresChooseACardOverlaysByPlayer()
    {
        Sts2RunOverlayRegistry.ClearForTest();
        try
        {
            Sts2RunOverlayRegistry.Register(new StateRunOverlaySnapshot(
                Id: "overlay:p:100:choose-a-card:1",
                ScreenType: "CardSelection",
                ScreenId: "Screens.CardSelection.NChooseACardSelectionScreen",
                Scene: "screens/card_selection/choose_a_card_selection_screen",
                ChooseACard: new StateChooseACardOverlaySnapshot(
                    CanSkip: true,
                    Cards:
                    [
                        new StateCardSnapshot("card:p:100:choose-a-card:1:0", "DEMONIC_SHIELD", 0),
                        new StateCardSnapshot("card:p:100:choose-a-card:1:1", "COORDINATE", 0),
                        new StateCardSnapshot("card:p:100:choose-a-card:1:2", "BELIEVE_IN_YOU", 0),
                    ])));

            var localOverlays = Sts2RunOverlayRegistry.GetForPlayer("p:100");
            var remoteOverlays = Sts2RunOverlayRegistry.GetForPlayer("p:300");

            Assert.Single(localOverlays);
            Assert.Equal("CardSelection", localOverlays[0].ScreenType);
            Assert.True(localOverlays[0].ChooseACard?.CanSkip);
            Assert.Equal(["DEMONIC_SHIELD", "COORDINATE", "BELIEVE_IN_YOU"], localOverlays[0].ChooseACard?.Cards.Select(card => card.ModelId));
            Assert.Empty(remoteOverlays);
        }
        finally
        {
            Sts2RunOverlayRegistry.ClearForTest();
        }
    }

    [Fact]
    public void RewardsOverlayInspectorKeepsSameKindRelicRewards()
    {
        var player = new PlayerDouble(100);
        var screen = new RewardScreenDouble(
            new ProceedButtonDouble(IsSkip: true, IsEnabled: false),
            [
                new RewardButtonDouble(new RelicReward(
                    player,
                    new LocStringDouble("relics", "CURSED_PEARL.title"),
                    new ModelDouble("CURSED_PEARL"))),
                new RewardButtonDouble(new RelicReward(
                    player,
                    new LocStringDouble("relics", "GOLDEN_PEARL.title"),
                    new ModelDouble("GOLDEN_PEARL"))),
            ]);

        var overlay = Sts2RewardsOverlayInspector.ResolveVisibleRewardsOverlay("p:100", screen);

        Assert.NotNull(overlay);
        Assert.Equal("Rewards", overlay.ScreenType);
        Assert.Equal("Screens.NRewardsScreen", overlay.ScreenId);
        Assert.Equal("skip", overlay.Rewards?.Flow?.Mode);
        Assert.False(overlay.Rewards?.Flow?.Enabled);
        Assert.Equal(
            ["reward:p:100:visible:0", "reward:p:100:visible:1"],
            overlay.Rewards?.Items.Select(item => item.Id));
        Assert.Equal(["CURSED_PEARL", "GOLDEN_PEARL"], overlay.Rewards?.Items.Select(item => item.Relic));
        Assert.Equal("relics", overlay.Rewards?.Items[0].Description?.Table);
        Assert.Equal("CURSED_PEARL.title", overlay.Rewards?.Items[0].Description?.Key);
    }

    [Fact]
    public void RewardsOverlayInspectorSkipsRewardsForDifferentPlayer()
    {
        var screen = new RewardScreenDouble(
            new ProceedButtonDouble(IsSkip: true, IsEnabled: true),
            [
                new RewardButtonDouble(new RelicReward(
                    new PlayerDouble(200),
                    new LocStringDouble("relics", "GOLDEN_PEARL.title"),
                    new ModelDouble("GOLDEN_PEARL"))),
            ]);

        var overlay = Sts2RewardsOverlayInspector.ResolveVisibleRewardsOverlay("p:100", screen);

        // The overlay stays alive on flow alone: the rewards screen is still open and its Skip/Proceed
        // control must remain reachable even when every claimable item belongs to another player.
        Assert.NotNull(overlay);
        Assert.Equal("skip", overlay.Rewards?.Flow?.Mode);
        Assert.True(overlay.Rewards?.Flow?.Enabled);
        Assert.Empty(overlay.Rewards?.Items ?? []);
    }

    [Fact]
    public void RewardsOverlayInspectorFiltersLinkedRewardsForDifferentPlayer()
    {
        var localPlayer = new PlayerDouble(100);
        var remotePlayer = new PlayerDouble(200);
        var screen = new RewardScreenDouble(
            new ProceedButtonDouble(IsSkip: false, IsEnabled: true),
            [
                new RewardButtonDouble(new LinkedRewardSet([
                    new RelicReward(
                        localPlayer,
                        new LocStringDouble("relics", "CURSED_PEARL.title"),
                        new ModelDouble("CURSED_PEARL")),
                    new RelicReward(
                        remotePlayer,
                        new LocStringDouble("relics", "GOLDEN_PEARL.title"),
                        new ModelDouble("GOLDEN_PEARL")),
                ])),
            ]);

        var overlay = Sts2RewardsOverlayInspector.ResolveVisibleRewardsOverlay("p:100", screen);

        Assert.NotNull(overlay);
        var item = Assert.Single(overlay.Rewards?.Items ?? []);
        var linked = Assert.Single(item.Linked ?? []);
        Assert.Equal("CURSED_PEARL", linked.Relic);
    }

    [Fact]
    public void LobbyHostResolverReturnsRemoteHostIdForClientServices()
    {
        var notices = new List<StateNoticeSnapshot>();

        var hostPlayerId = InvokeResolveLobbyHostPlayerId(
            new TestLobbyNetService("Client", 777UL),
            localPlayerId: "p:100",
            notices);

        Assert.Equal("p:777", hostPlayerId);
        Assert.Empty(notices);
    }

    [Fact]
    public void LobbyHostResolverAddsNoticeWhenClientHostIdIsMissing()
    {
        var notices = new List<StateNoticeSnapshot>();

        var hostPlayerId = InvokeResolveLobbyHostPlayerId(
            new TestLobbyNetService("Client", null),
            localPlayerId: "p:100",
            notices);

        Assert.Null(hostPlayerId);
        var notice = Assert.Single(notices);
        Assert.Equal("lobby-host-player-unknown", notice.Code);
    }

#if ENABLE_STS2_LIVE_HOST
    [Fact]
    public void MultiplayerOwnershipLegalityRejectsRemoteOwnedSemanticActionWithStructuredDetails()
    {
        var result = InvokeOwnershipGuard(
            new SemanticActionRequest(
                "req-1",
                SemanticActionKind.SelectMapNode,
                CardId: null,
                PotionId: null,
                TargetId: null,
                ChoiceId: null,
                CharacterId: null,
                MapNodeId: "map-node:1:0",
                MouseX: null,
                MouseY: null,
                MouseButton: null,
                Perspective: new PerspectiveSelection(PlayerScope.Local, "p:remote")),
            requestedPlayerId: "p:remote",
            resolvedOwnerPlayerId: "p:remote",
            localPlayerId: "p:local",
            hostPlayerId: "p:local",
            screen: "map",
            action: "select-map-node");

        Assert.False(result.Accepted);
        Assert.NotNull(result.Error);
        Assert.Equal(ActionFailureCode.UnsupportedPerspective, result.Error!.Code);
        Assert.Equal("p:remote", result.Error.RequestedPlayerId);
        Assert.Equal("p:remote", result.Error.ResolvedOwnerPlayerId);
        Assert.Equal("p:local", result.Error.LocalPlayerId);
        Assert.Equal("p:local", result.Error.HostPlayerId);
        Assert.Equal(MultiplayerRoleSnapshot.Host, result.Error.LocalRole);
        Assert.Equal("map", result.Error.Screen);
        Assert.Equal("select-map-node", result.Error.Action);
        Assert.Equal(RemoteClientOrchestrationStateSnapshot.LocalOnlyDegraded, result.Error.RemoteOrchestration?.State);
        var detail = Assert.Single(result.Error.Details);
        Assert.Equal("p:remote", detail.RequestedPlayerId);
        Assert.Equal("p:remote", detail.ResolvedOwnerPlayerId);
        Assert.Equal("p:local", detail.LocalPlayerId);
        Assert.Equal("p:local", detail.HostPlayerId);
        Assert.Equal(MultiplayerRoleSnapshot.Host, detail.LocalRole);
        Assert.Equal("select-map-node", detail.Action);
        Assert.Equal(RemoteClientOrchestrationStateSnapshot.LocalOnlyDegraded, detail.RemoteOrchestration?.State);
    }

    [Fact]
    public void MultiplayerOwnershipLegalityRejectsWrongOwnerBeforeActionSpecificHooks()
    {
        var result = InvokeOwnershipGuard(
            new SemanticActionRequest(
                "req-2",
                SemanticActionKind.ClaimReward,
                CardId: null,
                PotionId: null,
                TargetId: null,
                ChoiceId: null,
                CharacterId: null,
                MapNodeId: null,
                MouseX: null,
                MouseY: null,
                MouseButton: null,
                Perspective: new PerspectiveSelection(PlayerScope.Local, "p:2")),
            requestedPlayerId: "p:2",
            resolvedOwnerPlayerId: "p:1",
            localPlayerId: "p:1",
            hostPlayerId: "p:host",
            screen: "rewards",
            action: "claim-reward");

        Assert.False(result.Accepted);
        Assert.NotNull(result.Error);
        Assert.Equal(ActionFailureCode.WrongPlayer, result.Error!.Code);
        Assert.Equal("p:2", result.Error.RequestedPlayerId);
        Assert.Equal("p:1", result.Error.ResolvedOwnerPlayerId);
        Assert.Equal("p:1", result.Error.LocalPlayerId);
        Assert.Equal("p:host", result.Error.HostPlayerId);
        Assert.Equal(MultiplayerRoleSnapshot.Local, result.Error.LocalRole);
        Assert.Equal("rewards", result.Error.Screen);
        Assert.Equal("claim-reward", result.Error.Action);
        Assert.Equal(RemoteClientOrchestrationStateSnapshot.LocalOnlyDegraded, result.Error.RemoteOrchestration?.State);
    }

    [Fact]
    public void MultiplayerOwnershipLegalityPermitsHostLocalSeatOwnedByCurrentHost()
    {
        var result = InvokeOwnershipGuard(
            new SemanticActionRequest(
                "req-host-local",
                SemanticActionKind.EndTurn,
                CardId: null,
                PotionId: null,
                TargetId: null,
                ChoiceId: null,
                CharacterId: null,
                MapNodeId: null,
                MouseX: null,
                MouseY: null,
                MouseButton: null,
                Perspective: new PerspectiveSelection(PlayerScope.Local, "p:200")),
            requestedPlayerId: "p:200",
            resolvedOwnerPlayerId: "p:200",
            localPlayerId: "p:100",
            hostPlayerId: "p:100",
            screen: "combat",
            action: "end-turn",
            hostLocalPlayerIds: ["p:200"],
            expectRejected: false);

        Assert.True(result.Accepted);
        Assert.Null(result.Error);
        Assert.True(Sts2ActionCatalog.CanEndTurn("combat", "p:200", isPlayerTurn: true, requestedPlayerId: "p:200", activeOwnerIsHostLocalSeat: true));
        Assert.True(Sts2ActionCatalog.CanPlayCard("combat", "p:200", isPlayerTurn: true, requestedPlayerId: "p:200", activeOwnerIsHostLocalSeat: true));
        Assert.True(Sts2ActionCatalog.CanUsePotion("combat", "p:200", isPlayerTurn: true, potionOwnerPlayerId: "p:200", requestedPlayerId: "p:200", activeOwnerIsHostLocalSeat: true));
    }

    [Fact]
    public void MultiplayerOwnershipLegalityRejectsHostLocalWrongPlayerWithStructuredDetails()
    {
        var result = InvokeOwnershipGuard(
            new SemanticActionRequest(
                "req-host-local-wrong-player",
                SemanticActionKind.EndTurn,
                CardId: null,
                PotionId: null,
                TargetId: null,
                ChoiceId: null,
                CharacterId: null,
                MapNodeId: null,
                MouseX: null,
                MouseY: null,
                MouseButton: null,
                Perspective: new PerspectiveSelection(PlayerScope.Local, "p:100")),
            requestedPlayerId: "p:100",
            resolvedOwnerPlayerId: "p:200",
            localPlayerId: "p:100",
            hostPlayerId: "p:100",
            screen: "combat",
            action: "end-turn",
            hostLocalPlayerIds: ["p:200"]);

        Assert.False(result.Accepted);
        Assert.NotNull(result.Error);
        Assert.Equal(ActionFailureCode.WrongPlayer, result.Error!.Code);
        Assert.Equal("p:100", result.Error.RequestedPlayerId);
        Assert.Equal("p:200", result.Error.ResolvedOwnerPlayerId);
        Assert.Equal("p:100", result.Error.LocalPlayerId);
        Assert.Equal("p:100", result.Error.HostPlayerId);
        Assert.Equal(MultiplayerRoleSnapshot.Host, result.Error.LocalRole);
        Assert.Equal("combat", result.Error.Screen);
        Assert.Equal("end-turn", result.Error.Action);
        Assert.Equal(RemoteClientOrchestrationStateSnapshot.LocalOnlyDegraded, result.Error.RemoteOrchestration?.State);
        var detail = Assert.Single(result.Error.Details);
        Assert.Equal("p:100", detail.RequestedPlayerId);
        Assert.Equal("p:200", detail.ResolvedOwnerPlayerId);
        Assert.Equal("p:100", detail.LocalPlayerId);
        Assert.Equal("p:100", detail.HostPlayerId);
        Assert.Equal(MultiplayerRoleSnapshot.Host, detail.LocalRole);
        Assert.Equal("end-turn", detail.Action);
        Assert.Equal(RemoteClientOrchestrationStateSnapshot.LocalOnlyDegraded, detail.RemoteOrchestration?.State);
    }
#endif

    [Fact]
    public void LoadRunCharacterButtonExecutionPrefersSelectOverOnPress()
    {
        var helperType = GetLoadableTypes(typeof(Sts2ActionCatalog).Assembly)
            .SingleOrDefault(type => string.Equals(type.Name, "Sts2LobbyCharacterButtonInvoker", StringComparison.Ordinal));
        Assert.NotNull(helperType);

        var method = helperType!.GetMethod(
            "TryExecute",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var button = new TestCharacterSelectButton();

        var executed = Assert.IsType<bool>(method!.Invoke(null, [button]));

        Assert.True(executed);
        Assert.True(button.SelectCalled);
        Assert.False(button.OnPressCalled);
    }

    [Fact]
    public void ActionCatalogAdvertisesExecutableRewardFlowChoice()
    {
        var flowChoiceId = Sts2RewardIds.FlowChoiceId(isSkip: true);
        var actions = Sts2ActionCatalog.RewardActions(
            rewardChoices:
            [
                new ChoiceSnapshot(flowChoiceId, "Skip Rewards", "reward-flow", Provisional: false),
            ],
            choiceOwnersById: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [flowChoiceId] = "p1",
            });

        var action = Assert.Single(actions);
        Assert.Equal(SemanticActionKind.Choose, action.Kind);
        Assert.Equal("p1", action.Arguments?.PlayerId);
        Assert.Equal(flowChoiceId, action.Arguments?.ChoiceId);
        Assert.Equal($"sts2 act choose --choice {flowChoiceId}", action.CliCommandHint);
    }

    [Fact]
    public void ShopIdsExposeStableChoiceIds()
    {
        Assert.Equal("shop:p1:card:strike:0", Sts2ShopIds.ChoiceId("p1", "card", "strike", 0));
        Assert.Equal("shop:p1:card-removal:0", Sts2ShopIds.CardRemovalChoiceId("p1", 0));
        Assert.Equal("shop:leave", Sts2ShopIds.LeaveChoiceId());
        Assert.Equal("shop:close-inventory", Sts2ShopIds.CloseInventoryChoiceId());
        Assert.True(Sts2ShopIds.TryParseShopItemChoiceId("shop:p1:card:strike:0", out var playerId, out var kind, out var stableId, out var slotIndex));
        Assert.Equal("p1", playerId);
        Assert.Equal("card", kind);
        Assert.Equal("strike", stableId);
        Assert.Equal(0, slotIndex);
        Assert.True(Sts2ShopIds.TryParseCardRemovalChoiceId("shop:p1:card-removal:0", out playerId, out slotIndex));
        Assert.Equal("p1", playerId);
        Assert.Equal(0, slotIndex);
        Assert.True(Sts2ShopIds.IsLeaveChoiceId("shop:leave"));
        Assert.True(Sts2ShopIds.IsCloseInventoryChoiceId("shop:close-inventory"));
    }

    [Fact]
    public void ShopIdsExposeCanonicalInventoryEntryIds()
    {
        // The canonical, browser-facing id minted from the inventory model. This is the id the state
        // snapshot puts on run.currentRoom.shop.inventory.*Entries[].id and the id the action handler
        // resolves a purchase against.
        Assert.Equal("shop:character-card:0", Sts2ShopIds.InventoryEntryId(Sts2ShopIds.CharacterCardCollection, 0));
        Assert.Equal("shop:colorless-card:1", Sts2ShopIds.InventoryEntryId(Sts2ShopIds.ColorlessCardCollection, 1));
        Assert.Equal("shop:relic:2", Sts2ShopIds.InventoryEntryId(Sts2ShopIds.RelicCollection, 2));
        Assert.Equal("shop:potion:3", Sts2ShopIds.InventoryEntryId(Sts2ShopIds.PotionCollection, 3));
        Assert.Equal("shop:card-removal", Sts2ShopIds.CardRemovalEntryIdValue);

        Assert.True(Sts2ShopIds.TryParseInventoryEntryId("shop:character-card:0", out var collection, out var index));
        Assert.Equal(Sts2ShopIds.CharacterCardCollection, collection);
        Assert.Equal(0, index);
        Assert.True(Sts2ShopIds.TryParseInventoryEntryId("shop:relic:4", out collection, out index));
        Assert.Equal(Sts2ShopIds.RelicCollection, collection);
        Assert.Equal(4, index);

        // The card-removal constant parses to its own collection at index 0.
        Assert.True(Sts2ShopIds.TryParseInventoryEntryId(Sts2ShopIds.CardRemovalEntryIdValue, out collection, out index));
        Assert.Equal(Sts2ShopIds.CardRemovalCollection, collection);
        Assert.Equal(0, index);

        // The 5-part screen-slot id is NOT a canonical inventory id.
        Assert.False(Sts2ShopIds.TryParseInventoryEntryId("shop:p1:card:strike:0", out _, out _));
        Assert.False(Sts2ShopIds.TryParseInventoryEntryId("shop:made-up:0", out _, out _));

        // Collection -> action slot kind: both card collections buy through "card".
        Assert.Equal("card", Sts2ShopIds.KindForCollection(Sts2ShopIds.CharacterCardCollection));
        Assert.Equal("card", Sts2ShopIds.KindForCollection(Sts2ShopIds.ColorlessCardCollection));
        Assert.Equal("relic", Sts2ShopIds.KindForCollection(Sts2ShopIds.RelicCollection));
        Assert.Equal("potion", Sts2ShopIds.KindForCollection(Sts2ShopIds.PotionCollection));
        Assert.Equal("card-removal", Sts2ShopIds.KindForCollection(Sts2ShopIds.CardRemovalCollection));

        // Canonical ids are recognized as shop choice ids alongside the screen-slot and flow ids.
        Assert.True(Sts2ShopIds.IsShopChoiceId("shop:character-card:0"));
        Assert.True(Sts2ShopIds.IsShopChoiceId(Sts2ShopIds.CardRemovalEntryIdValue));
    }

#if ENABLE_STS2_LIVE_HOST
    [Fact]
    public void ResolveInventoryChoicesMintsCanonicalIdsMatchingStateSnapshot()
    {
        // A pure-POCO inventory double — Sts2LiveIntrospection.GetMemberValue reads it via reflection,
        // so no live game/Godot host is required for the inventory-model path.
        var inventory = new FakeMerchantInventoryDouble
        {
            CharacterCardEntries =
            {
                new FakeShopCardEntry { Cost = 49, CreationResult = new FakeShopCreationResult { Card = new FakeShopModel { Id = "BREAKTHROUGH" } } },
                new FakeShopCardEntry { Cost = 79, CreationResult = new FakeShopCreationResult { Card = new FakeShopModel { Id = "FORGOTTEN_RITUAL" } } },
            },
            ColorlessCardEntries =
            {
                new FakeShopCardEntry { Cost = 82, CreationResult = new FakeShopCreationResult { Card = new FakeShopModel { Id = "PRODUCTION" } } },
            },
            RelicEntries =
            {
                new FakeShopModelEntry { Cost = 177, Model = new FakeShopModel { Id = "RED_SKULL" } },
            },
            PotionEntries =
            {
                new FakeShopModelEntry { Cost = 104, Model = new FakeShopModel { Id = "LUCKY_TONIC" } },
                new FakeShopModelEntry { Cost = 76, Model = new FakeShopModel { Id = "LUCKY_TONIC" } },
            },
            CardRemovalEntry = new FakeShopCardRemovalEntry { Cost = 75 },
        };

        var ids = Sts2ShopScreenInspector.ResolveInventoryChoices(inventory, "p1")
            .Select(choice => choice.Snapshot.Id)
            .ToArray();

        // Per-collection indices + collection-specific prefixes, exactly matching the state snapshot
        // (Sts2StateProvider.ResolveShopInventory) — the ids the browser sends back.
        Assert.Equal(
            new[]
            {
                "shop:character-card:0",
                "shop:character-card:1",
                "shop:colorless-card:0",
                "shop:relic:0",
                "shop:potion:0",
                "shop:potion:1",
                "shop:card-removal",
                Sts2ShopIds.LeaveChoiceId(),
            },
            ids);
    }
#endif

    [Fact]
    public void LiveIntrospectionInvokesPrivateParameterlessMethodsDeclaredOnBaseTypes()
    {
        var inventory = new DerivedInventoryForReflectionTest();

        var helperType = typeof(Sts2ActionCatalog).Assembly.GetType("Spirectl.Sts2.Live.Sts2LiveIntrospection");
        Assert.NotNull(helperType);
        var method = helperType!.GetMethod("TryInvokeParameterlessMethod", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);

        var invoked = (bool)method!.Invoke(null, [inventory, new[] { "Close" }])!;

        Assert.True(invoked);
        Assert.True(inventory.WasClosed);
    }

    [Fact]
    public void ActionCatalogAdvertisesExecutableShopChoices()
    {
        var actions = Sts2ActionCatalog.ShopActions(
            shopChoices:
            [
                new ChoiceSnapshot("shop:p1:card:strike:0", "Buy Strike (50 gold)", "shop-card", Provisional: false, PreferredAction: "buy-card"),
                new ChoiceSnapshot("shop:p1:card-removal:0", "Remove a Card (100 gold)", "shop-card-removal", Provisional: false, PreferredAction: "remove-card"),
                new ChoiceSnapshot("shop:leave", "Leave Shop", "shop-flow", Provisional: false, PreferredAction: "leave-shop"),
            ],
            choiceOwnersById: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["shop:p1:card:strike:0"] = "p1",
                ["shop:p1:card-removal:0"] = "p1",
                ["shop:leave"] = "p1",
            });

        Assert.Equal(3, actions.Count);
        Assert.Equal(SemanticActionKind.BuyCard, actions[0].Kind);
        Assert.Equal("p1", actions[0].Arguments?.PlayerId);
        Assert.Equal("shop:p1:card:strike:0", actions[0].Arguments?.Values?["shopItemId"]);
        Assert.Equal("sts2 act buy-card --shop-item shop:p1:card:strike:0", actions[0].CliCommandHint);
        Assert.Equal(SemanticActionKind.RemoveCard, actions[1].Kind);
        Assert.Equal("shop:p1:card-removal:0", actions[1].Arguments?.Values?["shopItemId"]);
        Assert.Equal(SemanticActionKind.LeaveShop, actions[2].Kind);
        Assert.Equal("leave-shop", actions[2].IntentKind);
    }

    [Fact]
    public void RestSiteIdsExposeStableChoiceIds()
    {
        var helperType = GetLoadableTypes(typeof(Sts2ActionCatalog).Assembly)
            .SingleOrDefault(type => string.Equals(type.Name, "Sts2RestSiteIds", StringComparison.Ordinal));
        Assert.NotNull(helperType);

        var choiceMethod = helperType!.GetMethod(
            "ChoiceId",
            BindingFlags.Public | BindingFlags.Static);
        var proceedMethod = helperType.GetMethod(
            "ProceedChoiceId",
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(choiceMethod);
        Assert.NotNull(proceedMethod);

        Assert.Equal("rest-site:p1:heal:0", choiceMethod!.Invoke(null, ["p1", "heal", 0]));
        Assert.Equal("rest-site:proceed", proceedMethod!.Invoke(null, []));
        Assert.True(Sts2RestSiteIds.TryParseOptionChoiceId("rest-site:p1:heal:0", out var playerId, out var optionId, out var optionIndex));
        Assert.Equal("p1", playerId);
        Assert.Equal("heal", optionId);
        Assert.Equal(0, optionIndex);
        Assert.True(Sts2RestSiteIds.IsProceedChoiceId("rest-site:proceed"));
    }

    [Fact]
    public void ActionCatalogAdvertisesExecutableRestSiteChoices()
    {
        var method = typeof(Sts2ActionCatalog).GetMethod(
            "RestSiteActions",
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);

        var actions = Assert.IsAssignableFrom<IReadOnlyList<AvailableActionSnapshot>>(method!.Invoke(null,
            [
                new[]
                {
                    new ChoiceSnapshot("rest-site:p1:heal:0", "Heal", "rest-site-option", Provisional: false, PreferredAction: "rest", Arguments: new ActionArgumentsSnapshot("p1", null, null, null, null, null, IntentKind: "rest", Values: new Dictionary<string, string> { ["restOptionId"] = "heal" })),
                    new ChoiceSnapshot("rest-site:proceed", "Proceed", "rest-site-flow", Provisional: false, PreferredAction: "proceed-rest-site"),
                },
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["rest-site:p1:heal:0"] = "p1",
                    ["rest-site:proceed"] = "p1",
                },
            ]));

        Assert.Equal(2, actions.Count);
        Assert.Equal(SemanticActionKind.Rest, actions[0].Kind);
        Assert.Equal(SemanticActionKind.ProceedRestSite, actions[1].Kind);
        Assert.Equal("p1", actions[0].Arguments?.PlayerId);
        Assert.Equal("heal", actions[0].Arguments?.Values?["restOptionId"]);
        Assert.Equal("sts2 act rest", actions[0].CliCommandHint);
        Assert.Null(actions[1].Arguments?.ChoiceId);
    }

}

#if ENABLE_STS2_LIVE_HOST && ENABLE_STALE_STS2_SCREEN_FIXTURE_TESTS
file sealed class TestCardHolder
{
    public TestCardHolder(CardModel model)
    {
        CardModel = model;
    }

    public CardModel CardModel { get; }

    public bool Visible { get; } = true;
}

file sealed class TestCardBundle
{
    public TestCardBundle(IReadOnlyList<CardModel> bundle)
    {
        Bundle = bundle;
        Hitbox = new TestHitbox();
    }

    public IReadOnlyList<CardModel> Bundle { get; }

    public object Hitbox { get; }

    public bool Visible { get; } = true;
}

file sealed class TestBundleSelectionExecutionScreen : Sts2Live::MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NChooseABundleSelectionScreen
{
    public TestBundleSelectionExecutionScreen()
    {
        BundleRow = new TestUiContainer();
        PreviewConfirmButton = new TestHitbox();
        _bundleRow = BundleRow;
    }

    public TestUiContainer BundleRow { get; }

    public object PreviewConfirmButton { get; }

    public TestCardBundle? ClickedBundle { get; private set; }

    public object? ConfirmedButton { get; private set; }

    public void OnBundleClicked(TestCardBundle bundle)
    {
        ClickedBundle = bundle;
    }

    public void ConfirmSelection(object button)
    {
        ConfirmedButton = button;
    }
}

file sealed class TestUiContainer
{
    private readonly List<object> _children = [];

    public bool Visible { get; set; } = true;

    public IReadOnlyList<object> Children => _children;

    public void AddChild(object child) => _children.Add(child);
}

file sealed class TestHitbox
{
    public bool Visible { get; set; } = true;

    public bool IsEnabled { get; set; } = true;
}

file sealed class TestCrystalSphereMinigame
{
    public int DivinationCount { get; set; }

    public bool IsFinished { get; set; }

    public object? CrystalSphereTool { get; set; } = "Big";

    public TestCrystalSphereCell[] cells { get; set; } = [];

    public TestCrystalSphereItem[] Items { get; set; } = [];
}

file sealed class TestCrystalSphereItem(int x, int y, int width, int height, string? texture)
{
    public TestCrystalSpherePoint Position { get; } = new(x, y);

    public TestCrystalSpherePoint Size { get; } = new(width, height);

    public string? Texture { get; } = texture;

    public bool ShowsCard { get; init; }
}

file sealed class TestCrystalSpherePoint(int x, int y)
{
    public int X { get; } = x;

    public int Y { get; } = y;
}

file sealed class TestCrystalSphereCell(int x, int y, bool isHidden)
{
    public int X { get; } = x;

    public int Y { get; } = y;

    public bool IsHidden { get; } = isHidden;

    public bool IsHighlighted { get; init; }

    public bool IsHovered { get; init; }

    public object? Item { get; init; }
}

file sealed class TestCrystalSphereCellControl(object entity)
{
    public object Entity { get; } = entity;

    public bool Visible { get; set; } = true;
}

file sealed class TestCrystalSphereExecutionScreen : Sts2Live::MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere.NCrystalSphereScreen
{
    public object? BigDivinationButton { get; private set; }

    public object? SmallDivinationButton { get; private set; }

    public object? ClickedCell { get; private set; }

    public object? ProceedButton { get; private set; }

    public void SetBigDivination(object button)
    {
        BigDivinationButton = button;
    }

    public void SetSmallDivination(object button)
    {
        SmallDivinationButton = button;
    }

    public void OnCellClicked(object cell)
    {
        ClickedCell = cell;
    }

    public void OnProceedButtonPressed(object button)
    {
        ProceedButton = button;
    }
}

file sealed class TestMapScreenWithBack
{
    public object? _backButton;
}

file sealed class TestMapBackButton
{
    public bool Visible { get; set; } = true;

    public bool Disabled { get; set; }

    public bool Released { get; private set; }

    public void OnRelease()
    {
        Released = true;
    }
}

file sealed class TestCardSelectionModel : MockCardModel
{
    public static TestCardSelectionModel Create(string entry)
    {
        var model = (TestCardSelectionModel)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(TestCardSelectionModel));
        typeof(AbstractModel)
            .GetField("<Id>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(model, new ModelId("test", entry));
        return model;
    }

    public override MockCardModel MockBlock(int block) => this;

    protected override int GetBaseBlock() => 0;
}

file sealed class TestParticles2DObject
{
    public bool Emitting { get; set; }
}
#endif

internal sealed class TestRichTextEventRoom
{
    private object? _event;

    public object? Event
    {
        set => _event = value;
    }

    public object? Layout { get; init; }
}

internal sealed class TestEventLayoutWithRichText
{
    private object? _title;
    private object? _description;
    private object? _sharedEventLabel;

    public object? TitleLabel
    {
        set => _title = value;
    }

    public object? DescriptionLabel
    {
        set => _description = value;
    }

    public object? SharedEventLabel
    {
        set => _sharedEventLabel = value;
    }

    public IReadOnlyList<object> OptionButtons { get; init; } = [];
}

internal sealed class TestAncientEventLayoutWithRichText
{
    private object? _ancientEvent;
    private object? _ancientNameBanner;
    private int _currentDialogueLine;
    private object? _dialogueContainer;
    private object? _fakeNextButtonLabel;

    public object? AncientEvent
    {
        set => _ancientEvent = value;
    }

    public object? NameBanner
    {
        set => _ancientNameBanner = value;
    }

    public int CurrentDialogueLine
    {
        set => _currentDialogueLine = value;
    }

    public object? DialogueContainer
    {
        set => _dialogueContainer = value;
    }

    public object? FakeNextButtonLabel
    {
        set => _fakeNextButtonLabel = value;
    }

    public IReadOnlyList<object> OptionButtons { get; init; } = [];
}

internal sealed class TestEventModelWithRichText
{
    public string? Id { get; init; }

    public object? Title { get; init; }

    public object? Description { get; init; }
}

internal sealed class TestAncientEventModelWithRichText
{
    public string? Id { get; init; }

    public object? Title { get; init; }

    public object? Epithet { get; init; }
}

internal sealed class TestAncientNameBannerWithRichText
{
    private object? _titleLabel;
    private object? _epithetLabel;

    public object? TitleLabel
    {
        set => _titleLabel = value;
    }

    public object? EpithetLabel
    {
        set => _epithetLabel = value;
    }
}

internal sealed class TestNodeContainer
{
    public IReadOnlyList<object> Children { get; init; } = [];
}

internal sealed class TestAncientDialogueLineNode
{
    private object? _line;

    public object? Line
    {
        set => _line = value;
    }

    public bool Visible { get; init; } = true;

    public IReadOnlyList<object> Children { get; init; } = [];
}

internal sealed class TestAncientDialogueLineModel
{
    public object? LineText { get; init; }

    public object? NextButtonText { get; init; }

    public string? Speaker { get; init; }
}

internal sealed class TestEventOptionButtonWithRichText
{
    public bool Visible { get; init; } = true;

    public bool IsEnabled { get; init; } = true;

    public object? Option { get; init; }
}

internal sealed class TestEventOptionWithRichText
{
    public string? TextKey { get; init; }

    public object? Title { get; init; }

    public object? Description { get; init; }

    public bool IsProceed { get; init; }
}

internal sealed class TestVisibleTextLabel(string text)
{
    public string? Name { get; init; }

    public bool Visible { get; init; } = true;

    public string Text { get; } = text;
}

internal sealed class TestLocString(string formattedText, string rawText, string locTable, string locEntryKey)
{
    public string LocTable { get; } = locTable;

    public string LocEntryKey { get; } = locEntryKey;

    public string GetFormattedText() => formattedText;

    public string GetRawText() => rawText;
}

internal class TestCardOverlay
{
    public TestOverlayCard? Card { get; init; }

    public IReadOnlyList<TestOverlayCard> Cards { get; init; } = [];

    public string? PreviewText { get; init; }

    public string? SourceScreenType { get; init; }

    public TestOverlayControl? CloseButton { get; init; }

    public IReadOnlyList<TestOverlayControl> Buttons { get; init; } = [];
}

internal sealed class TestHookedCardOverlay : TestCardOverlay
{
    public void OnClose()
    {
    }
}

internal sealed class TestOverlayCard
{
    public string? Id { get; init; }

    public string? Name { get; init; }

    public string? Description { get; init; }

    public int Cost { get; init; }

    public string? CardType { get; init; }

    public string? Rarity { get; init; }

    public string? TargetType { get; init; }

    public int UpgradeLevel { get; init; }

    public string? OwnerPlayerId { get; init; }
}

internal sealed class TestOverlayControl
{
    public string? Id { get; init; }

    public string? Text { get; init; }

    public bool Visible { get; init; } = true;

    public bool Enabled { get; init; } = true;
}

internal sealed class RewardScreenDouble(ProceedButtonDouble proceedButton, IReadOnlyList<RewardButtonDouble> rewardButtons)
{
    private readonly ProceedButtonDouble _proceedButton = proceedButton;

    private readonly IReadOnlyList<RewardButtonDouble> _rewardButtons = rewardButtons;
}

internal sealed class RewardButtonDouble(object reward)
{
    public bool Visible { get; init; } = true;

    public object Reward { get; } = reward;
}

internal sealed record LinkedRewardSet(IReadOnlyList<object> Rewards);

internal sealed record ProceedButtonDouble(bool IsSkip, bool IsEnabled)
{
    public bool Visible { get; init; } = true;
}

internal sealed record PlayerDouble(ulong NetId);

internal sealed class RelicReward(PlayerDouble player, LocStringDouble description, ModelDouble relic)
{
    private readonly ModelDouble _relic = relic;

    public PlayerDouble Player { get; } = player;

    public LocStringDouble Description { get; } = description;
}

internal sealed record ModelDouble(string Entry)
{
    public ModelIdDouble Id { get; } = new(Entry);
}

internal sealed record ModelIdDouble(string Entry);

internal sealed record LocStringDouble(string LocTable, string LocEntryKey);

#if ENABLE_STS2_LIVE_HOST
// Pure-POCO merchant inventory doubles for ResolveInventoryChoices: read by reflection
// (Sts2LiveIntrospection.GetMemberValue), so they need no live game/Godot host.
file sealed class FakeMerchantInventoryDouble
{
    public List<object> CharacterCardEntries { get; } = [];

    public List<object> ColorlessCardEntries { get; } = [];

    public List<object> RelicEntries { get; } = [];

    public List<object> PotionEntries { get; } = [];

    public object? CardRemovalEntry { get; set; }
}

file sealed class FakeShopCardEntry
{
    public int Cost { get; set; }

    public object? CreationResult { get; set; }
}

file sealed class FakeShopCreationResult
{
    public object? Card { get; set; }
}

file sealed class FakeShopModelEntry
{
    public int Cost { get; set; }

    public object? Model { get; set; }
}

file sealed class FakeShopModel
{
    public string Id { get; set; } = string.Empty;
}

file sealed class FakeShopCardRemovalEntry
{
    public int Cost { get; set; }
}
#endif
