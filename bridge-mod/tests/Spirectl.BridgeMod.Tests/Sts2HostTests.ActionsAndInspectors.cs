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
    [Fact]
    public void ActionCatalogOnlyAdvertisesExecutableCombatEndTurn()
    {
        var unavailable = Sts2ActionCatalog.CombatActions(activePlayerId: "p1", isPlayerTurn: false);
        var available = Sts2ActionCatalog.CombatActions(activePlayerId: "p1", isPlayerTurn: true);

        Assert.Empty(unavailable);
        Assert.Single(available);
        Assert.Equal("p1", available[0].Arguments?.PlayerId);
        Assert.Equal(SemanticActionKind.EndTurn, available[0].Kind);
    }

    [Fact]
    public void ActionCatalogAdvertisesExecutableCombatCardPlays()
    {
        var combat = new CombatStateSnapshot(
            Turn: 3,
            ActivePlayerId: "p1",
            IsPlayerTurn: true,
            Hand:
            [
                new CardStateSnapshot("c_1", "Jab", 1, "p1", true, null, ["e_1"], false),
                new CardStateSnapshot("c_2", "Defend", 1, "p1", true, null, [], false),
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
                        new CardStateSnapshot("c_2", "Defend", 1, "p1", true, null, [], false),
                    ]),
            ],
            Enemies:
            [
                new EnemyStateSnapshot("e_1", "Gnash Grub", 38, "attack", 42, 0, true, []),
            ],
            PlayersById: new Dictionary<string, CombatPlayerStateSnapshot>(StringComparer.Ordinal)
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
                        new CardStateSnapshot("c_2", "Defend", 1, "p1", true, null, [], false),
                    ]),
            });

        var actions = Sts2ActionCatalog.CombatActions(combat);

        Assert.Equal(3, actions.Count);
        Assert.Equal(SemanticActionKind.PlayCard, actions[0].Kind);
        Assert.Equal("c_1", actions[0].Arguments?.CardId);
        Assert.Equal("e_1", actions[0].Arguments?.TargetId);
        Assert.Equal(combat.Hand[0].Id, actions[0].Arguments?.CardId);
        Assert.Equal(combat.Enemies[0].Id, actions[0].Arguments?.TargetId);
        Assert.Equal(SemanticActionKind.PlayCard, actions[1].Kind);
        Assert.Equal("c_2", actions[1].Arguments?.CardId);
        Assert.Equal(combat.Hand[1].Id, actions[1].Arguments?.CardId);
        Assert.Null(actions[1].Arguments?.TargetId);
        Assert.Equal(SemanticActionKind.EndTurn, actions[2].Kind);
    }

    [Fact]
    public void ActionCatalogAdvertisesExecutableCombatPotionUses()
    {
        var firePotion = new PotionStateSnapshot(
            "potion:p1:0:fire-potion",
            "Fire Potion",
            "p1",
            0,
            true,
            null,
            ["e_1"]);
        var blockPotion = new PotionStateSnapshot(
            "potion:p1:1:block-potion",
            "Block Potion",
            "p1",
            1,
            true,
            null,
            []);
        var spentPotion = new PotionStateSnapshot(
            "potion:p1:2:spent-potion",
            "Spent Potion",
            "p1",
            2,
            false,
            "Potion is already queued for use.",
            []);
        var combat = new CombatStateSnapshot(
            Turn: 3,
            ActivePlayerId: "p1",
            IsPlayerTurn: true,
            Hand: [],
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
                    [],
                    [firePotion, blockPotion, spentPotion]),
            ],
            Enemies:
            [
                new EnemyStateSnapshot("e_1", "Gnash Grub", 38, "attack", 42, 0, true, []),
            ],
            PlayersById: new Dictionary<string, CombatPlayerStateSnapshot>(StringComparer.Ordinal)
            {
                ["p1"] = new CombatPlayerStateSnapshot(
                    "p1",
                    "ironclad",
                    70,
                    80,
                    0,
                    3,
                    3,
                    [],
                    [firePotion, blockPotion, spentPotion]),
            },
            Potions: [firePotion, blockPotion, spentPotion]);

        var actions = Sts2ActionCatalog.CombatActions(combat);

        Assert.Equal(5, actions.Count);
        Assert.Equal(SemanticActionKind.UsePotion, actions[0].Kind);
        Assert.Equal("potion:p1:0:fire-potion", actions[0].Arguments?.PotionId);
        Assert.Equal("e_1", actions[0].Arguments?.TargetId);
        Assert.Equal(firePotion.Id, actions[0].Arguments?.PotionId);
        Assert.Equal(combat.Enemies[0].Id, actions[0].Arguments?.TargetId);
        Assert.Equal(SemanticActionKind.UsePotion, actions[1].Kind);
        Assert.Equal("potion:p1:1:block-potion", actions[1].Arguments?.PotionId);
        Assert.Equal(blockPotion.Id, actions[1].Arguments?.PotionId);
        Assert.Null(actions[1].Arguments?.TargetId);
        Assert.Equal(SemanticActionKind.OpenPotionPopup, actions[2].Kind);
        Assert.Equal(firePotion.Id, actions[2].Arguments?.PotionId);
        Assert.Equal("open-potion-popup", actions[2].Arguments?.IntentKind);
        Assert.Equal(SemanticActionKind.OpenPotionPopup, actions[3].Kind);
        Assert.Equal(blockPotion.Id, actions[3].Arguments?.PotionId);
        Assert.Equal("open-potion-popup", actions[3].Arguments?.IntentKind);
        Assert.Equal(SemanticActionKind.EndTurn, actions[4].Kind);
    }

    [Fact]
    public void ActionCatalogAdvertisesHostLocalCombatRefsForActiveOwner()
    {
        var firePotion = new PotionStateSnapshot(
            "potion:p:2:0:fire-potion",
            "Fire Potion",
            "p:2",
            0,
            true,
            null,
            ["e:nibbit"]);
        var blockPotion = new PotionStateSnapshot(
            "potion:p:2:1:block-potion",
            "Block Potion",
            "p:2",
            1,
            true,
            null,
            []);
        var activePlayer = new CombatPlayerStateSnapshot(
            "p:2",
            "ironclad",
            70,
            80,
            0,
            3,
            3,
            [
                new CardStateSnapshot("card:p:2:0:strike", "Jab", 1, "p:2", true, null, ["e:nibbit"], false),
                new CardStateSnapshot("card:p:2:1:defend", "Defend", 1, "p:2", true, null, [], false),
            ],
            [firePotion, blockPotion],
            IsHostLocalSeat: true);
        var remotePlayer = new CombatPlayerStateSnapshot(
            "p:3",
            "ironclad",
            70,
            80,
            0,
            3,
            3,
            [],
            IsRemote: true);
        var combat = new CombatStateSnapshot(
            Turn: 1,
            ActivePlayerId: "p:2",
            IsPlayerTurn: true,
            Hand: activePlayer.Hand,
            Players: [activePlayer, remotePlayer],
            Enemies: [new EnemyStateSnapshot("e:nibbit", "Nibbit", 12, "attack", 12, 0, true, [])],
            PlayersById: new Dictionary<string, CombatPlayerStateSnapshot>(StringComparer.Ordinal)
            {
                ["p:2"] = activePlayer,
                ["p:3"] = remotePlayer,
            },
            Potions: [firePotion, blockPotion]);

        var actions = Sts2ActionCatalog.CombatActions(combat);

        Assert.All(actions, action =>
        {
            Assert.Equal("p:2", action.OwnerPlayerId);
            Assert.Equal("p:2", action.Arguments?.PlayerId);
            Assert.Equal(RemoteClientOrchestrationStateSnapshot.HostLocalSeat, action.RemoteOrchestration?.State);
        });
        Assert.Contains(actions, action =>
            action.Id == "action:p:2:play-card:card:p:2:0:strike:e:nibbit"
            && action.Kind == SemanticActionKind.PlayCard
            && action.Arguments?.CardId == "card:p:2:0:strike"
            && action.Arguments?.TargetId == "e:nibbit");
        Assert.Contains(actions, action =>
            action.Id == "action:p:2:play-card:card:p:2:1:defend"
            && action.Kind == SemanticActionKind.PlayCard
            && action.Arguments?.CardId == "card:p:2:1:defend"
            && action.Arguments?.TargetId is null);
        Assert.Contains(actions, action =>
            action.Id == "action:p:2:use-potion:potion:p:2:0:fire-potion:e:nibbit"
            && action.Kind == SemanticActionKind.UsePotion
            && action.Arguments?.PotionId == "potion:p:2:0:fire-potion"
            && action.Arguments?.TargetId == "e:nibbit");
        Assert.Contains(actions, action =>
            action.Id == "action:p:2:use-potion:potion:p:2:1:block-potion"
            && action.Kind == SemanticActionKind.UsePotion
            && action.Arguments?.PotionId == "potion:p:2:1:block-potion"
            && action.Arguments?.TargetId is null);
        Assert.Contains(actions, action =>
            action.Id == "action:p:2:end-turn"
            && action.Kind == SemanticActionKind.EndTurn);
        Assert.DoesNotContain(actions, action => string.Equals(action.OwnerPlayerId, "p:3", StringComparison.Ordinal));
    }

    [Fact]
    public void ActionIdsFormatCombatIdsWithAndWithoutTargets()
    {
        Assert.Equal("action:p1:play-card:c_1", Sts2ActionIds.PlayCard("p1", "c_1"));
        Assert.Equal("action:p1:play-card:c_1:e_1", Sts2ActionIds.PlayCard("p1", "c_1", "e_1"));
        Assert.Equal("action:p1:use-potion:potion:p1:0", Sts2ActionIds.UsePotion("p1", "potion:p1:0"));
        Assert.Equal("action:p1:use-potion:potion:p1:0:e_1", Sts2ActionIds.UsePotion("p1", "potion:p1:0", "e_1"));
        Assert.Equal("action:p1:end-turn", Sts2ActionIds.EndTurn("p1"));
    }

    [Fact]
    public void PreferredActionRefsMatchAvailableActionsForRepresentativeScreens()
    {
        var rewardClaim = ChoiceWithRef(
            "reward:p1:0",
            "Claim 30 Gold",
            "reward",
            "p1",
            "claim-reward",
            SemanticActionKind.ClaimReward,
            Sts2ActionIds.Intent("rewards", "claim-reward", "reward:p1:0"));
        var rewardSkip = ChoiceWithRef(
            Sts2RewardIds.FlowChoiceId(isSkip: true),
            "Skip Rewards",
            "reward-flow",
            "p1",
            "skip-rewards",
            SemanticActionKind.SkipRewards,
            Sts2ActionIds.Intent("rewards", "skip-rewards", Sts2RewardIds.FlowChoiceId(isSkip: true)));
        AssertPreferredRefsMatchAvailable(
            [rewardClaim, rewardSkip],
            Sts2ActionCatalog.RewardActions(
                [rewardClaim, rewardSkip],
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    [rewardClaim.Id] = "p1",
                    [rewardSkip.Id] = "p1",
                }));

        var shopBuy = ChoiceWithRef("shop:p1:card:strike:0", "Buy Strike", "shop-card", "p1", "buy-card", SemanticActionKind.BuyCard, Sts2ActionIds.Intent("shop", "buy-card", "shop:p1:card:strike:0"));
        var shopLeave = ChoiceWithRef(Sts2ShopIds.LeaveChoiceId(), "Leave Shop", "shop-flow", "p1", "leave-shop", SemanticActionKind.LeaveShop, Sts2ActionIds.Intent("shop", "leave-shop", Sts2ShopIds.LeaveChoiceId()));
        AssertPreferredRefsMatchAvailable(
            [shopBuy, shopLeave],
            Sts2ActionCatalog.ShopActions(
                [shopBuy, shopLeave],
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    [shopBuy.Id] = "p1",
                    [shopLeave.Id] = "p1",
                }));

        var cardPick = ChoiceWithRef("card-selection:card:bash:0", "Take Bash", "card-selection-card", "p1", "select-card", SemanticActionKind.SelectCard, Sts2ActionIds.Intent("card-selection", "select-card", "card-selection:card:bash:0"));
        var cardSkip = ChoiceWithRef(Sts2CardSelectionIds.SkipChoiceId(), "Skip", "card-selection-skip", "p1", "skip-card-selection", SemanticActionKind.SkipCardSelection, Sts2ActionIds.Intent("card-selection", "skip-card-selection", Sts2CardSelectionIds.SkipChoiceId()));
        AssertPreferredRefsMatchAvailable([cardPick, cardSkip], Sts2ActionCatalog.CardSelectionActions("p1", [cardPick, cardSkip]));

        var mapNode = new ChoiceSnapshot(
            "map-node:3:1",
            "Monster (3,1)",
            "map-node",
            Provisional: false,
            OwnerPlayerId: "p1",
            PreferredAction: "select-map-node",
            PreferredActionRef: new VisibleActionReferenceSnapshot(Sts2ActionIds.Intent("map", "select-map-node", "map-node:3:1"), "Select Monster (3,1).", true, "p1", SemanticActionKind.SelectMapNode, "select-map-node"));
        var mapBack = ChoiceWithRef(Sts2MapIds.BackChoiceId(), "Back", "map-flow", "p1", "back-from-map", SemanticActionKind.BackFromMap, Sts2ActionIds.Intent("map", "back-from-map", Sts2MapIds.BackChoiceId()));
        AssertPreferredRefsMatchAvailable(
            [mapNode, mapBack],
            Sts2ActionCatalog.MapActions("p1", [new Sts2MapNodeSnapshot("map-node:3:1", "Monster (3,1)", "map-node", Travelable: true)], flowChoices: [mapBack]));

        var rest = ChoiceWithRef("rest-site:p1:heal:0", "Heal", "rest-site-option", "p1", "rest", SemanticActionKind.Rest, Sts2ActionIds.Intent("rest-site", "rest", "rest-site:p1:heal:0"));
        var restProceed = ChoiceWithRef(Sts2RestSiteIds.ProceedChoiceId(), "Proceed", "rest-site-flow", "p1", "proceed-rest-site", SemanticActionKind.ProceedRestSite, Sts2ActionIds.Intent("rest-site", "proceed-rest-site", Sts2RestSiteIds.ProceedChoiceId()));
        AssertPreferredRefsMatchAvailable(
            [rest, restProceed],
            Sts2ActionCatalog.RestSiteActions(
                [rest, restProceed],
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    [rest.Id] = "p1",
                    [restProceed.Id] = "p1",
                }));

        var openChest = ChoiceWithRef(Sts2TreasureRoomIds.OpenChestChoiceId(), "Open Chest", "treasure-room-flow", "p1", "open-chest", SemanticActionKind.OpenChest, Sts2ActionIds.Intent("treasure-room", "open-chest", Sts2TreasureRoomIds.OpenChestChoiceId()));
        var takeRelic = ChoiceWithRef("treasure-room:relic:anchor:0", "Take Anchor", "treasure-room-relic", "p1", "take-relic", SemanticActionKind.TakeRelic, Sts2ActionIds.Intent("treasure-room", "take-relic", "treasure-room:relic:anchor:0"));
        AssertPreferredRefsMatchAvailable([openChest, takeRelic], Sts2ActionCatalog.TreasureRoomActions("p1", [openChest, takeRelic]));

        var eventFallback = ChoiceWithRef("event-room:gain-gold:0", "Take 75 Gold", "event-option", "p1", "choose", SemanticActionKind.Choose, Sts2ActionIds.Choice("event-room", "event-room:gain-gold:0"));
        AssertPreferredRefsMatchAvailable([eventFallback], Sts2ActionCatalog.EventRoomActions("p1", [eventFallback]));

        var crystalFallback = ChoiceWithRef(Sts2CrystalSphereIds.BigToolChoiceId(), "Big Divination", "crystal-sphere-tool", "p1", "choose", SemanticActionKind.Choose, Sts2ActionIds.Choice("crystal-sphere", Sts2CrystalSphereIds.BigToolChoiceId()));
        AssertPreferredRefsMatchAvailable([crystalFallback], Sts2ActionCatalog.CrystalSphereActions("p1", [crystalFallback]));

        var lobby = new LobbyStateSnapshot(
            "start-run",
            "selecting",
            [new LobbyPlayerSnapshot("p1", "not-ready", null, "ironclad", false, 0, true, true, false)],
            [new LobbyCharacterSnapshot("silent", "Silent", true)],
            "p1",
            "p1",
            "host",
            new Dictionary<string, LobbyPlayerSnapshot>(StringComparer.Ordinal)
            {
                ["p1"] = new LobbyPlayerSnapshot("p1", "not-ready", null, "ironclad", false, 0, true, true, false),
            },
            new Dictionary<string, LobbyCharacterSnapshot>(StringComparer.Ordinal)
            {
                ["silent"] = new LobbyCharacterSnapshot("silent", "Silent", true),
            });
        AssertPreferredRefsMatchAvailable(Sts2ActionCatalog.LobbyChoices(lobby, includeCharacterChoices: true), Sts2ActionCatalog.LobbyActions(lobby));
    }

    [Fact]
    public void ActionCatalogOnlyAdvertisesExecutableLobbyActions()
    {
        var localPlayer = new LobbyPlayerSnapshot("p:100", "not-ready", null, "ironclad", false, 0, true, true, false);
        var lobby = new LobbyStateSnapshot(
            "start-run",
            "selecting",
            [localPlayer],
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
            },
            new Dictionary<string, LobbyCharacterSnapshot>(StringComparer.Ordinal)
            {
                ["ironclad"] = new LobbyCharacterSnapshot("ironclad", "Ironclad", true),
                ["silent"] = new LobbyCharacterSnapshot("silent", "Silent", true),
            });

        var actions = Sts2ActionCatalog.LobbyActions(lobby);

        Assert.Equal(2, actions.Count);
        Assert.Equal(SemanticActionKind.Ready, actions[0].Kind);
        Assert.Equal(SemanticActionKind.SelectCharacter, actions[1].Kind);
        Assert.Equal("silent", actions[1].Arguments?.CharacterId);
    }

    [Fact]
    public void ActionCatalogAdvertisesLoadRunCharacterSelectionWhenCharactersAreVisible()
    {
        var localPlayer = new LobbyPlayerSnapshot("p:100", "not-ready", null, "ironclad", false, 0, true, true, false);
        var lobby = new LobbyStateSnapshot(
            "load-run",
            "waiting",
            [localPlayer],
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
            },
            new Dictionary<string, LobbyCharacterSnapshot>(StringComparer.Ordinal)
            {
                ["ironclad"] = new LobbyCharacterSnapshot("ironclad", "Ironclad", true),
                ["silent"] = new LobbyCharacterSnapshot("silent", "Silent", true),
            });

        var actions = Sts2ActionCatalog.LobbyActions(lobby);

        Assert.Equal(2, actions.Count);
        Assert.Equal(SemanticActionKind.Ready, actions[0].Kind);
        Assert.Equal(SemanticActionKind.SelectCharacter, actions[1].Kind);
        Assert.Equal("silent", actions[1].Arguments?.CharacterId);
    }

    [Fact]
    public void RuntimeObservationRandomLobbyCharacterUsesLiveRandomPortraitAsset()
    {
        var providerType = typeof(Sts2ActionCatalog).Assembly.GetType("Spirectl.Sts2.Live.Sts2RuntimeObservationProvider");
        if (providerType is null)
        {
            return;
        }

        var method = providerType.GetMethod(
            "RandomLobbyCharacter",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var character = Assert.IsType<LobbyCharacterSnapshot>(method!.Invoke(null, [true]));

        Assert.Equal("RANDOM_CHARACTER", character.Id);
        Assert.True(character.IsUnlocked);
        Assert.Equal("res://images/packed/character_select/char_select_random.png", character.PortraitAssetKey);
        Assert.Null(character.IconAssetKey);
        Assert.Equal("res://scenes/screens/char_select/char_select_bg_random_character.tscn", character.SelectBackgroundAssetKey);
    }

    [Fact]
    public void RuntimeObservationOmitsRandomLobbyCharacterWhenAnyVisibleCharacterIsLocked()
    {
        var providerType = typeof(Sts2ActionCatalog).Assembly.GetType("Spirectl.Sts2.Live.Sts2RuntimeObservationProvider");
        if (providerType is null)
        {
            return;
        }

        var method = providerType.GetMethod(
            "RemoveRandomCharacterWhenAnyVisibleCharacterIsLocked",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var ironclad = new LobbyCharacterSnapshot("ironclad", "Ironclad", IsUnlocked: true);
        var necrobinder = new LobbyCharacterSnapshot("necrobinder", "Necrobinder", IsUnlocked: false);
        var random = new LobbyCharacterSnapshot("RANDOM_CHARACTER", "Random Character", IsUnlocked: true);

        var characters = Assert.IsAssignableFrom<IReadOnlyList<LobbyCharacterSnapshot>>(
            method!.Invoke(null, [new[] { ironclad, necrobinder, random }]));

        Assert.Contains(characters, character => character.Id == "ironclad");
        Assert.Contains(characters, character => character.Id == "necrobinder" && !character.IsUnlocked);
        Assert.DoesNotContain(characters, character => character.Id == "RANDOM_CHARACTER");
    }

    [Fact]
    public void ActionCatalogAdvertisesExecutableEventRoomChoices()
    {
        var actions = Sts2ActionCatalog.EventRoomActions(
            "p1",
            [
                new ChoiceSnapshot("event-room:gain-gold:0", "Take 75 Gold", "event-option", Provisional: false),
                new ChoiceSnapshot("event-room:leave:1", "Leave", "event-option", Provisional: false),
            ]);

        Assert.Equal(2, actions.Count);
        Assert.Equal(SemanticActionKind.Choose, actions[0].Kind);
        Assert.Equal("event-room:gain-gold:0", actions[0].Arguments?.ChoiceId);
        Assert.Equal("p1", actions[0].Arguments?.PlayerId);
        Assert.Equal("event-room:leave:1", actions[1].Arguments?.ChoiceId);
        Assert.Equal("p1", actions[1].Arguments?.PlayerId);
    }

    [Fact]
    public void CustomEventIdsExposeStableChoiceIds()
    {
        Assert.Equal("event-room:fake-merchant:open-shop", Sts2EventRoomIds.FakeMerchantOpenShopChoiceId());
        Assert.Equal("event-room:neow-pages-initial-options-massive_scroll:0", Sts2EventRoomIds.ChoiceId(
            Sts2EventRoomIds.ResolveOptionId(
                "NEOW.pages.INITIAL.options.MASSIVE_SCROLL",
                isProceed: false,
                labelFallback: null,
                optionIndex: 0,
                out var fallbackKind),
            0));
        Assert.Equal(Sts2EventRoomIds.OptionIdFallbackKind.None, fallbackKind);
        Assert.Equal("crystal-sphere:tool:big", Sts2CrystalSphereIds.BigToolChoiceId());
        Assert.Equal("crystal-sphere:tool:small", Sts2CrystalSphereIds.SmallToolChoiceId());
        Assert.Equal("crystal-sphere:cell:2:3", Sts2CrystalSphereIds.CellChoiceId(2, 3));
        Assert.Equal("crystal-sphere:proceed", Sts2CrystalSphereIds.ProceedChoiceId());

        Assert.True(Sts2CrystalSphereIds.TryParseCellChoiceId("crystal-sphere:cell:2:3", out var x, out var y));
        Assert.Equal(2, x);
        Assert.Equal(3, y);
        Assert.False(Sts2CrystalSphereIds.TryParseCellChoiceId("crystal-sphere:cell:2", out _, out _));
        Assert.False(Sts2CrystalSphereIds.TryParseCellChoiceId("crystal-sphere:cell:x:3", out _, out _));
        Assert.False(Sts2CrystalSphereIds.TryParseCellChoiceId("crystal-sphere:tool:big", out _, out _));
    }

    [Fact]
    public void ActionCatalogAdvertisesExecutableCrystalSphereChoices()
    {
        var actions = Sts2ActionCatalog.CrystalSphereActions(
            "p1",
            [
                new ChoiceSnapshot("crystal-sphere:tool:big", "Big Divination", "crystal-sphere-tool", Provisional: false),
                new ChoiceSnapshot("crystal-sphere:cell:2:3", "Cell 2,3", "crystal-sphere-cell", Provisional: false),
                new ChoiceSnapshot("crystal-sphere:proceed", "Proceed", "crystal-sphere-flow", Provisional: false),
            ]);

        Assert.Equal(3, actions.Count);
        Assert.All(actions, action => Assert.Equal(SemanticActionKind.Choose, action.Kind));
        Assert.Equal("action:crystal-sphere:choose:crystal-sphere:tool:big", actions[0].Id);
        Assert.Equal("sts2 act choose --choice crystal-sphere:tool:big", actions[0].CliCommandHint);
        Assert.Equal("crystal-sphere:tool:big", actions[0].Arguments?.ChoiceId);
        Assert.Equal("p1", actions[0].Arguments?.PlayerId);
        Assert.Equal("action:crystal-sphere:choose:crystal-sphere:cell:2:3", actions[1].Id);
        Assert.Equal("sts2 act choose --choice crystal-sphere:cell:2:3", actions[1].CliCommandHint);
        Assert.Equal("crystal-sphere:proceed", actions[2].Arguments?.ChoiceId);
    }

#if ENABLE_STS2_LIVE_HOST
    [Fact]
    public void EventRoomInspectorResolvesLocalizedRichPageTextFromEventModel()
    {
        var notices = new List<StateNoticeSnapshot>();
        var eventRoom = new TestRichTextEventRoom
        {
            Event = new TestEventModelWithRichText
            {
                Id = "whispering-grove",
                Title = new TestLocString(
                    "Hondonada Susurrante",
                    "Whispering Grove",
                    "events",
                    "whispering-grove.title"),
                Description = new TestLocString(
                    "Es un [red]árbol[/red] [wave]espeluznante[/wave].",
                    "It is a {0} tree.",
                    "events",
                    "whispering-grove.description"),
            },
            Layout = new TestEventLayoutWithRichText
            {
                SharedEventLabel = new TestVisibleTextLabel("-- [cyan]Intercambio[/cyan] --"),
            },
        };

        var page = InvokeResolveEventRoomPage(eventRoom, notices);

        Assert.NotNull(page);
        Assert.Equal("whispering-grove", page!.EventId);
        Assert.Equal(typeof(TestEventModelWithRichText).FullName, page.EventType);
        Assert.Equal("Hondonada Susurrante", page.Title?.Text);
        Assert.Equal("Es un [red]árbol[/red] [wave]espeluznante[/wave].", page.Description?.Text);
        Assert.Equal("It is a {0} tree.", page.Description?.RawText);
        Assert.Equal("events", page.Description?.LocTable);
        Assert.Equal("whispering-grove.description", page.Description?.LocKey);
        Assert.Equal("loc-string", page.Description?.Source);
        Assert.Equal("-- [cyan]Intercambio[/cyan] --", page.SharedLabel?.Text);
        Assert.Equal("event-model", page.TextSource);
        Assert.False(page.Provisional);
        Assert.Empty(notices);
    }

    [Fact]
    public void EventRoomInspectorFallsBackToVisibleLayoutLabelsWithNotice()
    {
        var notices = new List<StateNoticeSnapshot>();
        var eventRoom = new TestRichTextEventRoom
        {
            Layout = new TestEventLayoutWithRichText
            {
                TitleLabel = new TestVisibleTextLabel("Título visible"),
                DescriptionLabel = new TestVisibleTextLabel("Texto [yellow]visible[/yellow] [wave]animado[/wave]."),
                SharedEventLabel = new TestVisibleTextLabel("-- [cyan]Compartido[/cyan] --"),
            },
        };

        var page = InvokeResolveEventRoomPage(eventRoom, notices);

        Assert.NotNull(page);
        Assert.Equal("Título visible", page!.Title?.Text);
        Assert.Equal("Texto [yellow]visible[/yellow] [wave]animado[/wave].", page.Description?.Text);
        Assert.Equal("-- [cyan]Compartido[/cyan] --", page.SharedLabel?.Text);
        Assert.Equal("layout-label", page.TextSource);
        Assert.True(page.Provisional);
        Assert.Contains(notices, notice => notice.Code == "event-room-text-partial" && notice.Path == "eventRoom.page");
    }

    [Fact]
    public void EventRoomInspectorUsesLocalizedRichOptionTitleAndDescription()
    {
        var eventRoom = new TestRichTextEventRoom
        {
            Layout = new TestEventLayoutWithRichText
            {
                OptionButtons =
                [
                    new TestEventOptionButtonWithRichText
                    {
                        Option = new TestEventOptionWithRichText
                        {
                            TextKey = "trade_gold",
                            Title = new TestLocString(
                                "Intercambiar oro",
                                "Trade Gold",
                                "events",
                                "whispering-grove.options.trade.title"),
                            Description = new TestLocString(
                                "Pierdes [red]35[/red] de oro. Obtienes [yellow]2[/yellow] pociones.",
                                "Lose {0} Gold. Gain {1} Potions.",
                                "events",
                                "whispering-grove.options.trade.description"),
                        },
                    },
                ],
            },
        };

        var choice = Assert.Single(InvokeResolveEventRoomChoicesForRichText(eventRoom, "p1", notices: null));
        var description = Assert.IsType<RichLocalizedTextSnapshot>(ReadReflectedProperty(choice, "DescriptionText"));

        Assert.Equal("event-room:trade_gold:0", ReadChoiceSnapshotText(choice, "Id"));
        Assert.Equal("Intercambiar oro", ReadChoiceSnapshotText(choice, "Label"));
        Assert.Equal("Pierdes [red]35[/red] de oro. Obtienes [yellow]2[/yellow] pociones.", description.Text);
        Assert.Equal("Lose {0} Gold. Gain {1} Potions.", description.RawText);
        Assert.Equal("events", description.LocTable);
        Assert.Equal("whispering-grove.options.trade.description", description.LocKey);
        Assert.Equal("loc-string", description.Source);
    }

    [Fact]
    public void EventRoomInspectorKeepsProceedExecutableWhenButtonIsStillDisabled()
    {
        var eventRoom = new TestRichTextEventRoom
        {
            Layout = new TestEventLayoutWithRichText
            {
                OptionButtons =
                [
                    new TestEventOptionButtonWithRichText
                    {
                        IsEnabled = false,
                        Option = new TestEventOptionWithRichText
                        {
                            TextKey = "proceed",
                            Title = new TestLocString("Continuar", "Continue", "events", "PROCEED"),
                            Description = new TestLocString("", "", "events", "PROCEED.description"),
                            IsProceed = true,
                        },
                    },
                ],
            },
        };

        var choice = Assert.Single(InvokeResolveEventRoomChoicesForRichText(eventRoom, "p1", notices: null));

        Assert.Equal("event-room:proceed:0", ReadChoiceSnapshotText(choice, "Id"));
        Assert.Equal("Continuar", ReadChoiceSnapshotText(choice, "Label"));
        Assert.Equal("proceed-event", ReadChoiceSnapshotText(choice, "PreferredAction"));
        Assert.True(Assert.IsType<bool>(ReadReflectedProperty(choice, "IsExecutable")));
    }

    [Fact]
    public void EventRoomInspectorResolvesAncientPageText()
    {
        var notices = new List<StateNoticeSnapshot>();
        var eventRoom = new TestRichTextEventRoom
        {
            Layout = new TestAncientEventLayoutWithRichText
            {
                AncientEvent = new TestAncientEventModelWithRichText
                {
                    Id = "neow",
                    Title = new TestLocString("NEOW", "NEOW", "ancients", "neow.title"),
                    Epithet = new TestLocString("Madre de la resurrección (desterrada)", "Mother of resurrection", "ancients", "neow.epithet"),
                },
                NameBanner = new TestAncientNameBannerWithRichText
                {
                    TitleLabel = new TestVisibleTextLabel("[ancient_banner]NEOW[/ancient_banner]") { Name = "Title" },
                    EpithetLabel = new TestVisibleTextLabel("Madre de la resurrección (desterrada)") { Name = "Epithet" },
                },
                CurrentDialogueLine = 0,
                DialogueContainer = new TestNodeContainer
                {
                    Children =
                    [
                        new TestAncientDialogueLineNode
                        {
                            Line = new TestAncientDialogueLineModel
                            {
                                LineText = new TestLocString("[sine]... Hola ... de nuevo ... [/sine]", "... Hello ... again ...", "ancients", "neow.dialogue.0"),
                                NextButtonText = new TestLocString("Continuar", "Continue", "ui", "next"),
                                Speaker = "Neow",
                            },
                            Children = [new TestVisibleTextLabel("[sine]... Hola ... de nuevo ... [/sine]") { Name = "Text" }],
                        },
                    ],
                },
                FakeNextButtonLabel = new TestVisibleTextLabel("Continuar"),
                OptionButtons =
                [
                    new TestEventOptionButtonWithRichText
                    {
                        Option = new TestEventOptionWithRichText
                        {
                            TextKey = "lead-paperweight",
                            Title = new TestLocString("Pisapapeles de plomo", "Lead Paperweight", "ancients", "neow.option.paperweight.title"),
                            Description = new TestLocString("Eliges [yellow]1[/yellow] de 2 cartas incoloras para agregar al mazo.", "Choose 1 of 2 colorless cards.", "ancients", "neow.option.paperweight.description"),
                        },
                    },
                ],
            },
        };

        var page = InvokeResolveEventRoomPage(eventRoom, notices);
        var choice = Assert.Single(InvokeResolveEventRoomChoicesForRichText(eventRoom, "p1", notices));
        var description = Assert.IsType<RichLocalizedTextSnapshot>(ReadReflectedProperty(choice, "DescriptionText"));

        Assert.NotNull(page);
        Assert.NotNull(page!.Ancient);
        Assert.Equal("[ancient_banner]NEOW[/ancient_banner]", page.Title?.Text);
        Assert.Equal("[sine]... Hola ... de nuevo ... [/sine]", page.Description?.Text);
        Assert.Equal("neow", page.EventId);
        Assert.Equal(typeof(TestAncientEventModelWithRichText).FullName, page.EventType);
        Assert.Equal("NEOW", page.Ancient!.Title?.Text);
        Assert.Equal("[ancient_banner]NEOW[/ancient_banner]", page.Ancient.BannerTitle?.Text);
        Assert.Equal("Madre de la resurrección (desterrada)", page.Ancient.Epithet?.Text);
        Assert.Equal("[sine]... Hola ... de nuevo ... [/sine]", page.Ancient.CurrentDialogue?.Text);
        Assert.Equal(0, page.Ancient.CurrentDialogueIndex);
        Assert.Equal("Neow", page.Ancient.CurrentSpeaker);
        Assert.Equal("Continuar", page.Ancient.NextButtonText?.Text);
        Assert.Equal("ancient-layout-label", page.TextSource);
        Assert.False(page.Provisional);
        Assert.Equal("Pisapapeles de plomo", ReadChoiceSnapshotText(choice, "Label"));
        Assert.Equal("Eliges [yellow]1[/yellow] de 2 cartas incoloras para agregar al mazo.", description.Text);
        Assert.Empty(notices);
    }

    private static EventRoomPageStateSnapshot? InvokeResolveEventRoomPage(
        object eventRoom,
        ICollection<StateNoticeSnapshot>? notices)
    {
        var inspectorType = typeof(Sts2ActionCatalog).Assembly
            .GetType("Spirectl.Sts2.Live.Sts2EventRoomScreenInspector");
        Assert.NotNull(inspectorType);

        var method = inspectorType!.GetMethod(
            "ResolvePage",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        return Assert.IsType<EventRoomPageStateSnapshot>(method!.Invoke(null, [eventRoom, notices]));
    }

    private static IReadOnlyList<object> InvokeResolveEventRoomChoicesForRichText(
        object eventRoom,
        string? defaultPlayerId,
        ICollection<StateNoticeSnapshot>? notices)
    {
        var inspectorType = typeof(Sts2ActionCatalog).Assembly
            .GetType("Spirectl.Sts2.Live.Sts2EventRoomScreenInspector");
        Assert.NotNull(inspectorType);

        var method = inspectorType!.GetMethod(
            "ResolveChoices",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, [eventRoom, defaultPlayerId, notices]);
        return Assert.IsAssignableFrom<IReadOnlyList<object>>(result);
    }

    private static string? ReadChoiceSnapshotText(object choice, string propertyName)
    {
        var snapshot = ReadReflectedProperty(choice, "Snapshot");
        return snapshot is null ? null : ReadReflectedProperty(snapshot, propertyName) as string;
    }

    private static object? ReadReflectedProperty(object target, string propertyName)
        => target.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(target);
#endif

    private static ChoiceSnapshot ChoiceWithRef(
        string id,
        string label,
        string kind,
        string ownerPlayerId,
        string preferredAction,
        SemanticActionKind actionKind,
        string actionId)
        => new(
            id,
            label,
            kind,
            Provisional: false,
            OwnerPlayerId: ownerPlayerId,
            ChoiceKind: kind,
            IntentKind: preferredAction,
            Perspective: $"player:{ownerPlayerId}",
            PreferredAction: preferredAction,
            Arguments: new ActionArgumentsSnapshot(ownerPlayerId, null, null, preferredAction == "choose" ? id : null, null, null, IntentKind: preferredAction),
            Enabled: true,
            PreferredActionRef: new VisibleActionReferenceSnapshot(
                actionId,
                label,
                Enabled: true,
                OwnerPlayerId: ownerPlayerId,
                ActionKind: actionKind,
                IntentKind: preferredAction,
                Arguments: new ActionArgumentsSnapshot(ownerPlayerId, null, null, preferredAction == "choose" ? id : null, null, null, IntentKind: preferredAction),
                LegalityStatus: ActionLegalityKind.Legal,
                Perspective: $"player:{ownerPlayerId}"),
            LegalityStatus: ActionLegalityKind.Legal);

    private static void AssertPreferredRefsMatchAvailable(
        IReadOnlyList<ChoiceSnapshot> choices,
        IReadOnlyList<AvailableActionSnapshot> actions)
    {
        foreach (var choice in choices.Where(choice => choice.PreferredActionRef is not null))
        {
            Assert.Contains(actions, action => string.Equals(action.Id, choice.PreferredActionRef!.Id, StringComparison.Ordinal));
        }
    }

#if ENABLE_STS2_LIVE_HOST && ENABLE_STALE_STS2_SCREEN_FIXTURE_TESTS
    [Fact]
    public void EventRoomInspectorResolvesFakeMerchantEntryChoice()
    {
        var eventRoom = new TestEventRoomWithLayout
        {
            Layout = new TestEventLayout(),
        };
        eventRoom.AddChild(new Sts2Live::MegaCrit.Sts2.Core.Nodes.Events.Custom.NFakeMerchant
        {
            MerchantButton = new TestMerchantButton(),
        });

        var choices = InvokeResolveEventRoomChoices(eventRoom, "p1", notices: null);

        var choice = Assert.Single(choices);
        Assert.Equal("event-room:fake-merchant:open-shop", ReadChoiceSnapshotString(choice, "Id"));
        Assert.Equal("Open Shop", ReadChoiceSnapshotString(choice, "Label"));
        Assert.Equal("event-shop-entry", ReadChoiceSnapshotString(choice, "Kind"));
        Assert.Equal("p1", ReadChoiceSnapshotString(choice, "OwnerPlayerId"));
        Assert.True(ReadChoiceBool(choice, "IsExecutable"));
    }

    [Fact]
    public void EventRoomInspectorResolvesFakeMerchantEntryChoiceFromProceedButtonChild()
    {
        var eventRoom = new TestEventRoomWithLayout
        {
            Layout = new TestEventLayout(),
        };
        var fakeMerchant = new Sts2Live::MegaCrit.Sts2.Core.Nodes.Events.Custom.NFakeMerchant();
        fakeMerchant.AddChild(new TestMerchantButton { Name = "ProceedButton" });
        eventRoom.AddChild(fakeMerchant);

        var choices = InvokeResolveEventRoomChoices(eventRoom, "p1", notices: null);

        var choice = Assert.Single(choices);
        Assert.Equal("event-room:fake-merchant:open-shop", ReadChoiceSnapshotString(choice, "Id"));
        Assert.Equal("event-shop-entry", ReadChoiceSnapshotString(choice, "Kind"));
        Assert.True(ReadChoiceBool(choice, "IsExecutable"));
    }

    [Fact]
    public void EventRoomInspectorKeepsNormalEventOptionsAuthoritative()
    {
        var eventRoom = new TestEventRoomWithLayout
        {
            Layout = new TestEventLayout
            {
                OptionButtons =
                [
                    new TestEventOptionButton
                    {
                        Option = new TestEventOption { TextKey = "take_gold", Title = "Take Gold" },
                    },
                ],
            },
        };
        eventRoom.AddChild(new Sts2Live::MegaCrit.Sts2.Core.Nodes.Events.Custom.NFakeMerchant
        {
            MerchantButton = new TestMerchantButton(),
        });

        var choice = Assert.Single(InvokeResolveEventRoomChoices(eventRoom, "p1", notices: null));

        Assert.Equal("event-room:take_gold:0", ReadChoiceSnapshotString(choice, "Id"));
        Assert.Equal("event-option", ReadChoiceSnapshotString(choice, "Kind"));
    }

    [Fact]
    public void EventRoomInspectorMarksHiddenOrDisabledFakeMerchantEntryNonExecutable()
    {
        var disabledEventRoom = new TestEventRoomWithLayout
        {
            Layout = new TestEventLayout(),
        };
        disabledEventRoom.AddChild(new Sts2Live::MegaCrit.Sts2.Core.Nodes.Events.Custom.NFakeMerchant
        {
            MerchantButton = new TestMerchantButton { Disabled = true },
        });

        var disabledChoice = Assert.Single(InvokeResolveEventRoomChoices(disabledEventRoom, "p1", notices: null));

        Assert.False(ReadChoiceBool(disabledChoice, "IsExecutable"));

        var hiddenEventRoom = new TestEventRoomWithLayout
        {
            Layout = new TestEventLayout(),
        };
        hiddenEventRoom.AddChild(new Sts2Live::MegaCrit.Sts2.Core.Nodes.Events.Custom.NFakeMerchant
        {
            MerchantButton = new TestMerchantButton { Visible = false },
        });

        Assert.Empty(InvokeResolveEventRoomChoices(hiddenEventRoom, "p1", notices: null));
    }

    [Fact]
    public void CrystalSphereInspectorResolvesActiveToolAndHiddenCellChoices()
    {
        var screen = new Sts2Live::MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere.NCrystalSphereScreen
        {
            _entity = new TestCrystalSphereMinigame
            {
                DivinationCount = 2,
                IsFinished = false,
                cells =
                [
                    new TestCrystalSphereCell(2, 3, true),
                    new TestCrystalSphereCell(4, 5, false),
                    new TestCrystalSphereCell(7, 8, true),
                ],
            },
            _bigDivinationButton = new TestHitbox(),
            _smallDivinationButton = new TestHitbox(),
            _cellContainer = new TestUiContainer(),
        };
        var hiddenCellControl = new TestCrystalSphereCellControl(screen._entity.cells[0]);
        var revealedCellControl = new TestCrystalSphereCellControl(screen._entity.cells[1]);
        ((TestUiContainer)screen._cellContainer).AddChild(revealedCellControl);
        ((TestUiContainer)screen._cellContainer).AddChild(hiddenCellControl);

        var notices = new List<StateNoticeSnapshot>();
        var choices = InvokeResolveCrystalSphereChoices(screen, "p1", notices);

        Assert.Collection(
            choices,
            choice =>
            {
                Assert.Equal("crystal-sphere:tool:big", ReadChoiceSnapshotString(choice, "Id"));
                Assert.Equal("Big Divination", ReadChoiceSnapshotString(choice, "Label"));
                Assert.Equal("crystal-sphere-tool", ReadChoiceSnapshotString(choice, "Kind"));
                Assert.True(ReadChoiceBool(choice, "IsExecutable"));
            },
            choice =>
            {
                Assert.Equal("crystal-sphere:tool:small", ReadChoiceSnapshotString(choice, "Id"));
                Assert.Equal("Small Divination", ReadChoiceSnapshotString(choice, "Label"));
                Assert.Equal("crystal-sphere-tool", ReadChoiceSnapshotString(choice, "Kind"));
                Assert.True(ReadChoiceBool(choice, "IsExecutable"));
            },
            choice =>
            {
                Assert.Equal("crystal-sphere:cell:2:3", ReadChoiceSnapshotString(choice, "Id"));
                Assert.Equal("Cell 2,3", ReadChoiceSnapshotString(choice, "Label"));
                Assert.Equal("crystal-sphere-cell", ReadChoiceSnapshotString(choice, "Kind"));
                Assert.Equal("p1", ReadChoiceSnapshotString(choice, "OwnerPlayerId"));
                Assert.True(ReadChoiceBool(choice, "IsExecutable"));
            });
        Assert.DoesNotContain(choices, choice => ReadChoiceSnapshotString(choice, "Id") == "crystal-sphere:cell:4:5");
        Assert.DoesNotContain(choices, choice => ReadChoiceSnapshotString(choice, "Id") == "crystal-sphere:cell:7:8");
        Assert.Empty(notices);
    }

    [Fact]
    public void CrystalSphereInspectorKeepsHiddenCellsVisibleButUnavailableWhenDivinationsAreSpent()
    {
        var screen = new Sts2Live::MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere.NCrystalSphereScreen
        {
            _entity = new TestCrystalSphereMinigame
            {
                DivinationCount = 0,
                IsFinished = false,
                cells = [new TestCrystalSphereCell(2, 3, true)],
            },
            _bigDivinationButton = new TestHitbox(),
            _smallDivinationButton = new TestHitbox(),
            _cellContainer = new TestUiContainer(),
        };
        ((TestUiContainer)screen._cellContainer).AddChild(new TestCrystalSphereCellControl(screen._entity.cells[0]));
        var notices = new List<StateNoticeSnapshot>();

        var choices = InvokeResolveCrystalSphereChoices(screen, "p1", notices);

        var cellChoice = Assert.Single(choices, choice => ReadChoiceSnapshotString(choice, "Id") == "crystal-sphere:cell:2:3");
        Assert.False(ReadChoiceBool(cellChoice, "IsExecutable"));
        Assert.Contains(notices, notice => notice.Code == "crystal-sphere-visible-choices-partial");
    }

    [Fact]
    public void CrystalSphereInspectorResolvesProceedOnlyWhenVisibleAndFinished()
    {
        var unfinished = new Sts2Live::MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere.NCrystalSphereScreen
        {
            _entity = new TestCrystalSphereMinigame { DivinationCount = 0, IsFinished = false },
            _proceedButton = new TestHitbox(),
        };
        Assert.DoesNotContain(
            InvokeResolveCrystalSphereChoices(unfinished, "p1", notices: null),
            choice => ReadChoiceSnapshotString(choice, "Id") == "crystal-sphere:proceed");

        var finished = new Sts2Live::MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere.NCrystalSphereScreen
        {
            _entity = new TestCrystalSphereMinigame { DivinationCount = 0, IsFinished = true },
            _proceedButton = new TestHitbox(),
        };

        var proceedChoice = Assert.Single(
            InvokeResolveCrystalSphereChoices(finished, "p1", notices: null),
            choice => ReadChoiceSnapshotString(choice, "Id") == "crystal-sphere:proceed");

        Assert.Equal("Proceed", ReadChoiceSnapshotString(proceedChoice, "Label"));
        Assert.Equal("crystal-sphere-flow", ReadChoiceSnapshotString(proceedChoice, "Kind"));
        Assert.True(ReadChoiceBool(proceedChoice, "IsExecutable"));
    }

    [Fact]
    public void CrystalSphereExecutionInvokesToolCellAndProceedHooks()
    {
        var screen = new TestCrystalSphereExecutionScreen
        {
            _entity = new TestCrystalSphereMinigame
            {
                DivinationCount = 1,
                IsFinished = false,
                cells = [new TestCrystalSphereCell(2, 3, true)],
            },
            _bigDivinationButton = new TestHitbox(),
            _smallDivinationButton = new TestHitbox(),
            _cellContainer = new TestUiContainer(),
        };
        var cellControl = new TestCrystalSphereCellControl(screen._entity.cells[0]);
        ((TestUiContainer)screen._cellContainer).AddChild(cellControl);
        var choices = InvokeResolveCrystalSphereChoices(screen, "p1", notices: null);

        Assert.True(InvokeExecuteCrystalSphereChoice(screen, Assert.Single(choices, choice => ReadChoiceSnapshotString(choice, "Id") == "crystal-sphere:tool:big")));
        Assert.Same(screen._bigDivinationButton, screen.BigDivinationButton);
        Assert.True(InvokeExecuteCrystalSphereChoice(screen, Assert.Single(choices, choice => ReadChoiceSnapshotString(choice, "Id") == "crystal-sphere:tool:small")));
        Assert.Same(screen._smallDivinationButton, screen.SmallDivinationButton);
        Assert.True(InvokeExecuteCrystalSphereChoice(screen, Assert.Single(choices, choice => ReadChoiceSnapshotString(choice, "Id") == "crystal-sphere:cell:2:3")));
        Assert.Same(cellControl, screen.ClickedCell);

        screen._entity = new TestCrystalSphereMinigame { DivinationCount = 0, IsFinished = true };
        screen._proceedButton = new TestHitbox();
        var proceedChoice = Assert.Single(
            InvokeResolveCrystalSphereChoices(screen, "p1", notices: null),
            choice => ReadChoiceSnapshotString(choice, "Id") == "crystal-sphere:proceed");

        Assert.True(InvokeExecuteCrystalSphereChoice(screen, proceedChoice));
        Assert.Same(screen._proceedButton, screen.ProceedButton);
    }

    [Fact]
    public void CrystalSphereOverlayExtractorProjectsObservableGridState()
    {
        var screen = new Sts2Live::MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere.NCrystalSphereScreen
        {
            _entity = new TestCrystalSphereMinigame
            {
                DivinationCount = 6,
                IsFinished = false,
                CrystalSphereTool = "Big",
                cells =
                [
                    new TestCrystalSphereCell(1, 2, true)
                    {
                        IsHighlighted = true,
                        Item = new { ModelId = "hidden-relic" },
                    },
                    new TestCrystalSphereCell(3, 4, false),
                    new TestCrystalSphereCell(8, 8, true),
                ],
                Items =
                [
                    new TestCrystalSphereItem(
                        3,
                        4,
                        2,
                        1,
                        "res://images/events/crystal_sphere/crystal_sphere_relic.png"),
                    new TestCrystalSphereItem(
                        8,
                        8,
                        1,
                        1,
                        "res://images/events/crystal_sphere/hidden.png"),
                ],
            },
        };

        var overlay = InvokeBuildCrystalSphereOverlay(screen, "p1");

        Assert.Equal("overlay:p1:crystal-sphere:visible", overlay.Id);
        Assert.Equal("screens/crystal_sphere_screen", overlay.Scene);
        Assert.Equal(Sts2SupportedScreenIds.CrystalSphereScreenId, overlay.ScreenId);
        Assert.NotNull(overlay.CrystalSphere);
        Assert.Equal(6, overlay.CrystalSphere!.DivinationsRemaining);
        Assert.Equal("big", overlay.CrystalSphere.SelectedTool);
        Assert.False(overlay.CrystalSphere.IsFinished);

        Assert.Collection(
            overlay.CrystalSphere.Cells,
            cell =>
            {
                Assert.Equal("crystal-sphere:cell:1:2", cell.Id);
                Assert.Equal(1, cell.X);
                Assert.Equal(2, cell.Y);
                Assert.True(cell.IsHidden);
                Assert.True(cell.IsHighlighted);
                Assert.True(cell.Enabled);
            },
            cell =>
            {
                Assert.Equal("crystal-sphere:cell:3:4", cell.Id);
                Assert.False(cell.IsHidden);
                Assert.False(cell.Enabled);
            },
            cell =>
            {
                Assert.Equal("crystal-sphere:cell:8:8", cell.Id);
                Assert.True(cell.IsHidden);
                Assert.True(cell.Enabled);
            });

        var item = Assert.Single(overlay.CrystalSphere.RevealedItems!);
        Assert.Equal("crystal-sphere:item:3:4:2:1", item.Id);
        Assert.Equal(3, item.X);
        Assert.Equal(4, item.Y);
        Assert.Equal(2, item.WidthCells);
        Assert.Equal(1, item.HeightCells);
        Assert.False(item.ShowsCard);
        Assert.Equal(
            "res://images/events/crystal_sphere/crystal_sphere_relic.png",
            item.IconAssetKey);

        Assert.DoesNotContain(
            overlay.CrystalSphere.Cells[0].GetType().GetProperties(),
            property => string.Equals(property.Name, "Item", StringComparison.Ordinal));
    }

    [Fact]
    public void CrystalSphereOverlayExtractorFallsBackToRevealedCellItems()
    {
        var revealedItem = new TestCrystalSphereItem(
            4,
            5,
            2,
            1,
            "res://images/events/crystal_sphere/crystal_sphere_gold.png");
        var screen = new Sts2Live::MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere.NCrystalSphereScreen
        {
            _entity = new TestCrystalSphereMinigame
            {
                DivinationCount = 1,
                IsFinished = false,
                CrystalSphereTool = "Big",
                cells =
                [
                    new TestCrystalSphereCell(4, 5, false) { Item = revealedItem },
                    new TestCrystalSphereCell(5, 5, false) { Item = revealedItem },
                ],
                Items = [],
            },
        };

        var overlay = InvokeBuildCrystalSphereOverlay(screen, "p1");

        Assert.NotNull(overlay.CrystalSphere);
        Assert.Equal(0, overlay.CrystalSphere!.Cells.Count(cell => cell.IsHidden));
        var item = Assert.Single(overlay.CrystalSphere.RevealedItems!);
        Assert.Equal("crystal-sphere:item:4:5:2:1", item.Id);
        Assert.Equal(4, item.X);
        Assert.Equal(5, item.Y);
        Assert.Equal(2, item.WidthCells);
        Assert.Equal(1, item.HeightCells);
        Assert.Equal(
            "res://images/events/crystal_sphere/crystal_sphere_gold.png",
            item.IconAssetKey);
    }

    [Fact]
    public void CrystalSphereOverlayExtractorOmitsFullyHiddenCellItemFallback()
    {
        var hiddenItem = new TestCrystalSphereItem(
            8,
            8,
            1,
            1,
            "res://images/events/crystal_sphere/hidden.png");
        var screen = new Sts2Live::MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere.NCrystalSphereScreen
        {
            _entity = new TestCrystalSphereMinigame
            {
                DivinationCount = 1,
                IsFinished = false,
                CrystalSphereTool = "Big",
                cells =
                [
                    new TestCrystalSphereCell(8, 8, true) { Item = hiddenItem },
                ],
                Items = [],
            },
        };

        var overlay = InvokeBuildCrystalSphereOverlay(screen, "p1");

        Assert.NotNull(overlay.CrystalSphere);
        Assert.Empty(overlay.CrystalSphere!.RevealedItems!);
    }

    [Fact]
    public void MapScreenInspectorResolvesVisibleBackChoice()
    {
        var mapScreen = new TestMapScreenWithBack
        {
            _backButton = new TestMapBackButton(),
        };

        var choices = InvokeResolveMapFlowChoices(mapScreen, "p1");

        var choice = Assert.Single(choices);
        Assert.Equal("map:back", ReadChoiceSnapshotString(choice, "Id"));
        Assert.Equal("Back", ReadChoiceSnapshotString(choice, "Label"));
        Assert.Equal("map-flow", ReadChoiceSnapshotString(choice, "Kind"));
        Assert.Equal("p1", ReadChoiceSnapshotString(choice, "OwnerPlayerId"));
        Assert.True(ReadChoiceBool(choice, "IsExecutable"));
    }

    [Fact]
    public void MapScreenInspectorOmitsDisabledBackChoice()
    {
        var mapScreen = new TestMapScreenWithBack
        {
            _backButton = new TestMapBackButton { Disabled = true },
        };

        Assert.Empty(InvokeResolveMapFlowChoices(mapScreen, "p1"));
    }

    [Fact]
    public void MapScreenInspectorOmitsMissingBackChoice()
    {
        Assert.Empty(InvokeResolveMapFlowChoices(new TestMapScreenWithBack(), "p1"));
    }

    [Fact]
    public void ActionHandlerMapChoiceInvokesBackButtonRelease()
    {
        var button = new TestMapBackButton();
        var choice = CreateResolvedMapChoice(Sts2MapIds.BackChoiceId(), button, isExecutable: true);

        Assert.True(InvokeTryExecuteMapChoice(choice));
        Assert.True(button.Released);
    }

    [Fact]
    public void ActionHandlerMapChoiceRejectsMissingHook()
    {
        var choice = CreateResolvedMapChoice(Sts2MapIds.BackChoiceId(), new object(), isExecutable: true);

        Assert.False(InvokeTryExecuteMapChoice(choice));
    }
#endif

    [Fact]
    public void TreasureRoomIdsExposeStableChoiceIds()
    {
        Assert.Equal("treasure-room:open-chest", Sts2TreasureRoomIds.OpenChestChoiceId());
        Assert.Equal("treasure-room:relic:anchor:0", Sts2TreasureRoomIds.RelicChoiceId("anchor", 0));
        Assert.Equal("treasure-room:proceed", Sts2TreasureRoomIds.ProceedChoiceId());
        Assert.True(Sts2TreasureRoomIds.IsOpenChestChoiceId("treasure-room:open-chest"));
        Assert.True(Sts2TreasureRoomIds.TryParseRelicChoiceId("treasure-room:relic:anchor:0", out var relicId, out var relicIndex));
        Assert.Equal("anchor", relicId);
        Assert.Equal(0, relicIndex);
        Assert.True(Sts2TreasureRoomIds.IsProceedChoiceId("treasure-room:proceed"));
    }

    [Fact]
    public void ActionCatalogAdvertisesExecutableTreasureRoomChoices()
    {
        var actions = Sts2ActionCatalog.TreasureRoomActions(
            "p1",
            [
                new ChoiceSnapshot("treasure-room:open-chest", "Open Chest", "treasure-room-flow", Provisional: false, PreferredAction: "open-chest"),
                new ChoiceSnapshot("treasure-room:relic:anchor:0", "Take Anchor", "treasure-room-relic", Provisional: false, PreferredAction: "take-relic"),
                new ChoiceSnapshot("treasure-room:proceed", "Proceed", "treasure-room-flow", Provisional: false, PreferredAction: "proceed-treasure-room"),
            ]);

        Assert.Equal(3, actions.Count);
        Assert.Equal(SemanticActionKind.OpenChest, actions[0].Kind);
        Assert.Equal(SemanticActionKind.TakeRelic, actions[1].Kind);
        Assert.Equal(SemanticActionKind.ProceedTreasureRoom, actions[2].Kind);
        Assert.Null(actions[0].Arguments?.ChoiceId);
        Assert.Equal("p1", actions[0].Arguments?.PlayerId);
        Assert.Equal("anchor", actions[1].Arguments?.Values?["relicId"]);
        Assert.Null(actions[2].Arguments?.ChoiceId);
    }

    [Fact]
    public void TreasureRoomScreenInspectorTreatsRelicSelectionAsTreasureRoomFamily()
    {
        var helperType = GetLoadableTypes(typeof(Sts2ActionCatalog).Assembly)
            .SingleOrDefault(type => string.Equals(type.Name, "Sts2SupportedScreenIds", StringComparison.Ordinal));
        Assert.NotNull(helperType);

        var method = helperType!.GetMethod(
            "IsTreasureRoomFamily",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(method);

        Assert.True(Assert.IsType<bool>(method!.Invoke(null, ["Rooms.NTreasureRoom"])));
        Assert.True(Assert.IsType<bool>(method.Invoke(null, ["Screens.NChooseARelicSelection"])));
        Assert.False(Assert.IsType<bool>(method.Invoke(null, ["Screens.CardSelection.NCardRewardSelectionScreen"])));
    }

}
