using Spirectl.Sts2;
using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Perspective;
using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2.Core.State;
using Spirectl.Sts2.Core.Transport;
using Spirectl.Sts2;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

public sealed class RuntimePresentationStateTests
{
    private readonly RuntimeStateMapper _mapper = new();

    [Fact]
    public void SnapshotModelCanRepresentPresentationGradeCombatState()
    {
        var snapshot = PresentationCombatObservation();

        Assert.Equal("combat", snapshot.ScreenType);
        Assert.Equal("screen:combat:NCombatScreen", snapshot.ScreenSource);
        Assert.Equal("NCombatScreen", snapshot.ScreenRawType);
        Assert.Equal("STS2.Screens.NCombatScreen", snapshot.ScreenClassName);
        Assert.Equal("Act 1", snapshot.Run!.ActLabel);
        Assert.Equal("Floor 6", snapshot.Run.FloorLabel);
        Assert.Equal("encounter:jaw-worm", snapshot.Run.EncounterId);
        Assert.Equal("Slime Boss", snapshot.Run.BossLabel);

        var player = snapshot.Run.PlayersById["p1"];
        Assert.Equal(99, player.Gold);
        var relic = Assert.Single(player.Relics!);
        Assert.Equal("Ember Heart", relic.Name);
        Assert.True(relic.HasCounter);
        Assert.Equal("Heals 6 after combat", relic.Description);
        Assert.Equal("charges", relic.CounterLabel);
        Assert.Equal("model://relics/burning-blood/icon", Assert.Single(relic.AssetRefs!).Key);

        var potion = Assert.Single(player.Potions!);
        Assert.Equal("Fire Potion", potion.Name);
        Assert.Equal("enemy", potion.TargetType);
        Assert.True(potion.RequiresTarget);
        Assert.Equal("model://potions/fire-potion/icon", Assert.Single(potion.AssetRefs!).Key);

        var masterDeck = player.MasterDeck!;
        Assert.Equal("master-deck:p1", masterDeck.Id);
        Assert.True(masterDeck.CardsObservable);
        Assert.Equal("Jab", Assert.Single(masterDeck.Cards).Name);

        var status = Assert.Single(player.StatusEffects!);
        Assert.Equal("Exposed", status.Name);
        Assert.Equal(2, status.StackCount);
        Assert.Equal("turns", status.DurationLabel);

        var combat = snapshot.Combat!;
        Assert.Equal("encounter:jaw-worm", combat.EncounterId);
        Assert.Equal("Gnash Grub", combat.EncounterLabel);
        Assert.Equal(3, combat.DrawPile!.Count);
        Assert.True(combat.DrawPile.CardsObservable);
        Assert.False(combat.DrawPile.OrderObservable);
        Assert.Equal("discard:p1", combat.DiscardPile!.Id);
        Assert.Equal("exhaust:p1", combat.ExhaustPile!.Id);

        var card = combat.Hand[0];
        Assert.Equal("strike_r", card.ModelId);
        Assert.Equal("Land 6 harm.", card.Description);
        Assert.Equal("attack", card.Type);
        Assert.Equal("common", card.Rarity);
        Assert.Equal("enemy", card.TargetType);
        Assert.Equal(1, card.UpgradeLevel);
        Assert.Equal("1", card.CostLabel);
        Assert.Equal("model://cards/strike_r/image", Assert.Single(card.AssetRefs!).Key);

        var enemy = Assert.Single(combat.Enemies);
        Assert.Equal("entity:jaw-worm:1", enemy.RuntimeEntityId);
        Assert.Equal("jaw-worm", enemy.ModelId);
        Assert.Null(enemy.Visual!.AssetKey);
        Assert.Equal("slot:front", enemy.Visual.EncounterSlotId);
        Assert.Equal("right", enemy.Visual.ScreenSide);
        Assert.Contains(enemy.AssetRefs!, asset => asset.Key == "res://scenes/vfx/block_spark_vfx.tscn");
        Assert.Empty(enemy.Visual.AssetRefs!);
        Assert.Contains(enemy.StatusEffects!, status => status.Name == "Weak");
        Assert.Contains(enemy.StatusEffects!, status => status.ModelId == "slimed" && status.AssetRefs!.Any(asset => asset.Key == "res://images/atlases/card_atlas.sprites/status/slimed.tres"));

        var encounterVisuals = combat.EncounterVisuals!;
        Assert.Equal("kaiser_crab_boss", encounterVisuals.PackageId);
        Assert.Contains(encounterVisuals.VisualParts, part => part.PartId == "rocket" && part.ActiveStateId == "idle");

        var intent = Assert.Single(enemy.Intents);
        Assert.Equal("Gnash", intent.Label);
        Assert.Equal("Attacks for 11.", intent.Description);
        Assert.Equal(["p1"], intent.TargetIds);
        Assert.Equal("res://resources/textures/icons/intent_attack.png", Assert.Single(intent.AssetRefs!).Key);

        var notice = Assert.Single(snapshot.Notices);
        Assert.Equal("combat.drawPile.cards", notice.Path);
        Assert.Equal("partial", notice.Severity);
        Assert.Equal("Sts2PresentationStateResolver", notice.Source);
    }

    [Fact]
    public void SnapshotModelCanRepresentPresentationGradeRunStateOutsideCombat()
    {
        var snapshot = PresentationMapObservation();

        Assert.Equal("map", snapshot.ScreenType);
        Assert.Equal("map-screen", snapshot.ScreenSource);
        Assert.Equal("NMapScreen", snapshot.ScreenRawType);
        Assert.Equal("MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen", snapshot.ScreenClassName);
        Assert.Null(snapshot.Combat);

        var run = snapshot.Run!;
        Assert.Equal("Act 2", run.ActLabel);
        Assert.Equal("Floor 18", run.FloorLabel);
        Assert.Equal("room:shop", run.RoomId);
        Assert.Equal("Shop", run.RoomLabel);
        Assert.Equal("boss:collector", run.BossId);
        Assert.Equal("The Collector", run.BossLabel);

        var player = run.PlayersById["p1"];
        Assert.Equal(175, player.Gold);
        Assert.Equal("Anchor", Assert.Single(player.Relics!).Name);
        Assert.Equal("Fire Potion", Assert.Single(player.Potions!).Name);
        Assert.Equal("Defend", Assert.Single(player.MasterDeck!.Cards).Name);
        Assert.Equal("Resolve", Assert.Single(player.StatusEffects!).Name);
    }

    [Fact]
    public void OutOfCombatPresentationObservationKeepsScreenMetadataAndStructuredNotices()
    {
        var snapshot = PresentationMapObservation() with
        {
            Provisional = true,
            Notices =
            [
                new(
                    "map-visible-choices-partial",
                    "Visible map nodes include entries that are not currently executable.",
                    true,
                    Path: "availableActions",
                    Severity: "partial",
                    Source: "Sts2PartialChoiceNotice"),
            ],
        };

        Assert.Equal("map-screen", snapshot.ScreenSource);
        Assert.Equal("NMapScreen", snapshot.ScreenRawType);
        Assert.Equal("MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen", snapshot.ScreenClassName);
        Assert.Null(snapshot.Combat);
        Assert.NotNull(snapshot.Run);
        Assert.NotNull(snapshot.Map);
        Assert.NotNull(snapshot.EventRoom);
        Assert.NotNull(snapshot.TreasureRoom);
        Assert.NotNull(snapshot.RelicSelection);
        Assert.NotNull(snapshot.RestSite);
        Assert.NotNull(snapshot.Map!.Nodes);
        Assert.NotNull(snapshot.RestSite!.Controls);
        Assert.Equal("Act 2", snapshot.Run!.ActLabel);
        Assert.Equal("Floor 18", snapshot.Run.FloorLabel);
        Assert.NotEmpty(snapshot.Run.PlayersById["p1"].Relics!);
        Assert.NotEmpty(snapshot.Run.PlayersById["p1"].Potions!);
        Assert.NotNull(snapshot.Run.PlayersById["p1"].MasterDeck);

        var notice = Assert.Single(snapshot.Notices);
        Assert.Equal("availableActions", notice.Path);
        Assert.Equal("partial", notice.Severity);
        Assert.Equal("Sts2PartialChoiceNotice", notice.Source);
        Assert.Null(snapshot.Debug);
    }

    [Fact]
    public void LocalPerspectiveFiltersRemotePresentationDetails()
    {
        var state = _mapper.Map(
            PresentationCombatObservation(),
            new PlayerPerspective(PlayerScope.Local, "p1", UsesDefault: false));

        Assert.NotNull(state.Combat);
        Assert.Single(state.Combat.Players);
        Assert.Equal("p1", state.Combat.Players[0].Id);
        Assert.Equal("strike-local", Assert.Single(state.Combat.Hand).Id);
        Assert.Equal("relic-local", Assert.Single(state.Combat.Players[0].Relics!).Id);
        Assert.Equal("status-local", Assert.Single(state.Combat.Players[0].StatusEffects!).Id);
        Assert.Equal("draw:p1", state.Combat.DrawPile!.Id);
        Assert.DoesNotContain(state.Combat.PlayersById.Keys, id => id == "p2");
        Assert.Contains(state.Notices, notice => notice.Code == "player-presentation-filtered");

        var remoteRunPlayer = state.Run!.PlayersById["p2"];
        Assert.Empty(remoteRunPlayer.Relics!);
        Assert.Empty(remoteRunPlayer.Potions!);
        Assert.Empty(remoteRunPlayer.StatusEffects!);
        Assert.Empty(remoteRunPlayer.MasterDeck!.Cards);
    }

    [Fact]
    public void OmniscientPerspectiveRetainsAllPresentationDetails()
    {
        var state = _mapper.Map(
            PresentationCombatObservation(),
            new PlayerPerspective(PlayerScope.Omniscient, "p1", UsesDefault: false));

        Assert.NotNull(state.Combat);
        Assert.Equal(2, state.Combat.Players.Count);
        Assert.True(state.Combat.PlayersById.ContainsKey("p1"));
        Assert.True(state.Combat.PlayersById.ContainsKey("p2"));
        Assert.Equal("relic-remote", Assert.Single(state.Combat.PlayersById["p2"].Relics!).Id);
        Assert.Equal("status-remote", Assert.Single(state.Run!.PlayersById["p2"].StatusEffects!).Id);
        Assert.DoesNotContain(state.Notices, notice => notice.Code == "player-presentation-filtered");
    }

    [Fact]
    public void LocalPerspectiveDoesNotEmitFilterNoticeWhenNoRemotePresentationWasRemoved()
    {
        var observation = PresentationCombatObservation();
        var remoteCombatPlayer = observation.Combat!.PlayersById["p2"] with
        {
            Hand = [],
            Potions = [],
            DrawPileCardIds = [],
            DiscardPileCardIds = [],
            ExhaustPileCardIds = [],
            DrawPile = null,
            DiscardPile = null,
            ExhaustPile = null,
            Relics = [],
            StatusEffects = [],
        };
        var remoteRunPlayer = observation.Run!.PlayersById["p2"] with
        {
            Relics = [],
            Potions = [],
            MasterDeck = null,
            StatusEffects = [],
        };
        observation = observation with
        {
            Combat = observation.Combat with
            {
                Players = [observation.Combat.PlayersById["p1"], remoteCombatPlayer],
                PlayersById = new Dictionary<string, CombatPlayerStateSnapshot>
                {
                    ["p1"] = observation.Combat.PlayersById["p1"],
                    ["p2"] = remoteCombatPlayer,
                },
            },
            Run = observation.Run with
            {
                Players = [observation.Run.PlayersById["p1"], remoteRunPlayer],
                PlayersById = new Dictionary<string, PlayerStateSnapshot>
                {
                    ["p1"] = observation.Run.PlayersById["p1"],
                    ["p2"] = remoteRunPlayer,
                },
            },
        };

        var state = _mapper.Map(
            observation,
            new PlayerPerspective(PlayerScope.Local, "p1", UsesDefault: false));

        Assert.DoesNotContain(state.Notices, notice => notice.Code == "player-presentation-filtered");
    }

    [Fact]
    public void LocalPerspectiveFiltersRemoteOwnedTypedNonCombatEntriesWithStructuredNotices()
    {
        var observation = PresentationMapObservation() with
        {
            DefaultPlayerId = "p1",
            Map = new MapStateSnapshot(
            [
                new VisibleItemStateSnapshot("node:local", "Local Node", "local", "p1", false, false),
                new VisibleItemStateSnapshot("node:remote", "Remote Node", "remote", "p2", false, false),
            ]),
            EventRoom = new EventRoomStateSnapshot(
            [
                new VisibleItemStateSnapshot("event:local", "Local Option", "local", "p1", false, false),
                new VisibleItemStateSnapshot("event:remote", "Remote Option", "remote", "p2", false, false),
            ]),
            Shop = new ShopStateSnapshot(
            [
                new VisibleItemStateSnapshot("shop:local", "Local Item", "local", "p1", false, false),
                new VisibleItemStateSnapshot("shop:remote", "Remote Item", "remote", "p2", false, false),
            ]),
        };

        var state = _mapper.Map(
            observation,
            new PlayerPerspective(PlayerScope.Local, "p1", UsesDefault: false));

        Assert.Single(state.Map!.Nodes);
        Assert.Equal("node:local", state.Map.Nodes[0].Id);
        Assert.Single(state.EventRoom!.Options);
        Assert.Equal("event:local", state.EventRoom.Options[0].Id);
        Assert.Single(state.Shop!.PurchasableItems);
        Assert.Equal("shop:local", state.Shop.PurchasableItems[0].Id);
        Assert.Contains(state.Notices, n => n.Code == "player-presentation-filtered" && n.Path == "map.nodes" && n.Stability == "stable" && n.Perspective == "local:p1");
        Assert.Contains(state.Notices, n => n.Code == "player-presentation-filtered" && n.Path == "eventRoom.options" && n.Stability == "stable" && n.Perspective == "local:p1");
        Assert.Contains(state.Notices, n => n.Code == "player-presentation-filtered" && n.Path == "shop.purchasableItems" && n.Stability == "stable" && n.Perspective == "local:p1");
        Assert.Equal("p1", state.Run!.PlayersById["p1"].Id);
    }

    [Fact]
    public void NonCombatPresentationState_TypedSectionsExposeStableMetadata()
    {
        var snapshot = PresentationMapObservation();

        Assert.Equal("node:1:2", Assert.Single(snapshot.Map!.Nodes).Id);
        Assert.Equal("event:choice:test", Assert.Single(snapshot.EventRoom!.Options).Id);
        Assert.Equal("treasure:relic:test", Assert.Single(snapshot.TreasureRoom!.Relics).Id);
        Assert.Equal("treasure:relic:test", Assert.Single(snapshot.RelicSelection!.Relics).Id);
        Assert.Equal("rest:smith", Assert.Single(snapshot.RestSite!.Controls).Id);
        Assert.Equal("p1", snapshot.DefaultPlayerId);
    }

    [Fact]
    public void NonCombatPresentationState_CompatibilityActionsExposeIntentMetadata()
    {
        var rewardChoice = new ChoiceSnapshot(
            "reward:p1:0",
            "Claim 30 Gold",
            "reward",
            Provisional: false,
            OwnerPlayerId: "p1",
            ChoiceKind: "gold",
            Perspective: "local",
            PreferredAction: "choose");

        var action = Assert.Single(Sts2ActionCatalog.RewardActions(
            [rewardChoice],
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [rewardChoice.Id] = "p1",
            }));

        Assert.Equal("claim-reward", action.IntentKind);
        Assert.Equal("p1", action.OwnerPlayerId);
        Assert.Equal("local", action.Perspective);
        Assert.Equal("choose", action.PreferredAction);
    }

    [Fact]
    public void NonCombatPresentationState_DefaultStateOmitsRawSceneTreeData()
    {
        var snapshot = PresentationMapObservation();

        Assert.Null(snapshot.Debug);
        Assert.DoesNotContain(snapshot.Notices, notice => string.Equals(notice.Source, "dev scene tree", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(snapshot.Notices, notice => string.Equals(notice.Path, "debug.sceneTree", StringComparison.OrdinalIgnoreCase));
    }

    private static BridgeRuntimeObservation PresentationCombatObservation()
    {
        var strike = new CardStateSnapshot(
            "strike-local",
            "Jab",
            1,
            "p1",
            true,
            null,
            ["enemy-1"],
            true,
            ModelId: "strike_r",
            Description: "Land 6 harm.",
            Type: "attack",
            Rarity: "common",
            TargetType: "enemy",
            UpgradeLevel: 1,
            CostLabel: "1",
            AssetRefs: [new AssetReferenceSnapshot("image", "model://cards/strike_r/image", "Strike art", false)]);
        var defend = new CardStateSnapshot(
            "defend-remote",
            "Defend",
            1,
            "p2",
            true,
            null,
            [],
            false,
            ModelId: "defend_r",
            Description: "Gain 5 Guard.",
            Type: "skill",
            Rarity: "common",
            TargetType: "self",
            UpgradeLevel: 0,
            CostLabel: "1",
            AssetRefs: [new AssetReferenceSnapshot("image", "model://cards/defend_r/image", "Defend art", false)]);

        var localRelic = new RelicStateSnapshot(
            "relic-local",
            "burning-blood",
            "Ember Heart",
            "Heals 6 after combat",
            "p1",
            0,
            true,
            2,
            "charges",
            [new AssetReferenceSnapshot("icon", "model://relics/burning-blood/icon", "Ember Heart icon", false)]);
        var remoteRelic = localRelic with { Id = "relic-remote", OwnerPlayerId = "p2", SlotIndex = 1 };
        var localStatus = new StatusEffectStateSnapshot(
            "status-local",
            "vulnerable",
            "Exposed",
            "Takes 50% more attack harm.",
            "p1",
            2,
            "2",
            2,
            "turns",
            "debuff",
            [new AssetReferenceSnapshot("icon", "res://resources/textures/icons/status_vulnerable.png", "Vulnerable icon", false)]);
        var remoteStatus = localStatus with { Id = "status-remote", OwnerPlayerId = "p2" };
        var firePotion = new PotionStateSnapshot(
            "potion-fire",
            "Fire Potion",
            "p1",
            0,
            true,
            null,
            ["enemy-1"],
            ModelId: "fire-potion",
            Description: "Land 20 harm.",
            TargetType: "enemy",
            RequiresTarget: true,
            AssetRefs: [new AssetReferenceSnapshot("icon", "model://potions/fire-potion/icon", "Fire Potion icon", false)]);
        var remotePotion = firePotion with { Id = "potion-block", OwnerPlayerId = "p2", SlotIndex = 0 };
        var localPile = new CardPileStateSnapshot("draw:p1", "Draw pile", "p1", 3, [strike], true, false);
        var remotePile = new CardPileStateSnapshot("draw:p2", "Draw pile", "p2", 4, [defend], true, false);

        var localRunPlayer = new PlayerStateSnapshot(
            "p1",
            "ironclad",
            70,
            80,
            IsLocal: true,
            IsHost: true,
            Gold: 99,
            Relics: [localRelic],
            Potions: [firePotion],
            MasterDeck: new CardPileStateSnapshot("master-deck:p1", "Master deck", "p1", 1, [strike], true, true),
            StatusEffects: [localStatus]);
        var remoteRunPlayer = new PlayerStateSnapshot(
            "p2",
            "silent",
            64,
            70,
            IsRemote: true,
            Gold: 42,
            Relics: [remoteRelic],
            Potions: [remotePotion],
            MasterDeck: new CardPileStateSnapshot("master-deck:p2", "Master deck", "p2", 1, [defend], true, true),
            StatusEffects: [remoteStatus]);

        var localCombatPlayer = new CombatPlayerStateSnapshot(
            "p1",
            "ironclad",
            70,
            80,
            4,
            3,
            3,
            [strike],
            Potions: [firePotion],
            IsLocal: true,
            IsHost: true,
            DrawPileCardIds: ["strike-local"],
            Relics: [localRelic],
            StatusEffects: [localStatus],
            DrawPile: localPile,
            DiscardPile: new CardPileStateSnapshot("discard:p1", "Discard pile", "p1", 0, [], true, true),
            ExhaustPile: new CardPileStateSnapshot("exhaust:p1", "Exhaust pile", "p1", 0, [], true, true),
            Gold: 99);
        var remoteCombatPlayer = localCombatPlayer with
        {
            Id = "p2",
            Character = "silent",
            Hp = 64,
            MaxHp = 70,
            Hand = [defend],
            Potions = [remotePotion],
            IsLocal = false,
            IsHost = false,
            IsRemote = true,
            Relics = [remoteRelic],
            StatusEffects = [remoteStatus],
            DrawPile = remotePile,
            Gold = 42,
        };

        var enemyStatus = localStatus with { Id = "enemy-status", OwnerPlayerId = null, Name = "Weak" };
        var enemySlimedStatus = new StatusEffectStateSnapshot(
            "enemy-status-slimed",
            "slimed",
            "Slimed",
            "Slime clings to this enemy.",
            null,
            1,
            "1",
            0,
            null,
            "debuff",
            [new AssetReferenceSnapshot("icon", "res://images/atlases/card_atlas.sprites/status/slimed.tres", "Slimed icon", false)]);
        var enemy = new EnemyStateSnapshot(
            "enemy-1",
            "Gnash Grub",
            38,
            "attack",
            42,
            0,
            true,
            [new EnemyIntentSnapshot(
                "attack",
                11,
                1,
                11,
                Label: "Gnash",
                Description: "Attacks for 11.",
                TargetIds: ["p1"],
                AssetRefs: [new AssetReferenceSnapshot("icon", "res://resources/textures/icons/intent_attack.png", "Attack icon", false)])],
            RuntimeEntityId: "entity:jaw-worm:1",
            ModelId: "jaw-worm",
            StatusEffects: [enemyStatus, enemySlimedStatus],
            AssetRefs:
            [
                new AssetReferenceSnapshot("image", "res://scenes/vfx/block_spark_vfx.tscn", "Block spark VFX reference", false),
            ],
            Visual: new EnemyVisualMetadataSnapshot(
                null,
                "slot:front",
                "normal",
                "right",
                "encounter:jaw-worm:visuals",
                []));

        return new BridgeRuntimeObservation(
            SchemaVersion: "spirectl/v0",
            GameVersion: "sts2-live",
            BridgeVersion: "test",
            Source: DataSourceKind.Live,
            Provisional: false,
            ScreenType: "combat",
            ScreenTitle: "Combat",
            ScreenInstanceId: "screen:combat:1",
            DefaultPlayerId: "p1",
            Menu: null,
            Lobby: null,
            Run: new RunStateSnapshot(
                "SEED",
                6,
                1,
                [localRunPlayer, remoteRunPlayer],
                new Dictionary<string, PlayerStateSnapshot>(StringComparer.Ordinal)
                {
                    ["p1"] = localRunPlayer,
                    ["p2"] = remoteRunPlayer,
                },
                ActLabel: "Act 1",
                FloorLabel: "Floor 6",
                EncounterId: "encounter:jaw-worm",
                EncounterLabel: "Gnash Grub",
                RoomId: "room:monster",
                RoomLabel: "Monster Room",
                BossId: "boss:slime-boss",
                BossLabel: "Slime Boss"),
            Combat: new CombatStateSnapshot(
                Turn: 1,
                ActivePlayerId: "p1",
                IsPlayerTurn: true,
                Hand: [strike],
                Players: [localCombatPlayer, remoteCombatPlayer],
                Enemies: [enemy],
                PlayersById: new Dictionary<string, CombatPlayerStateSnapshot>(StringComparer.Ordinal)
                {
                    ["p1"] = localCombatPlayer,
                    ["p2"] = remoteCombatPlayer,
                },
                Potions: [firePotion],
                DrawPileCardIds: ["strike-local"],
                EncounterId: "encounter:jaw-worm",
                EncounterLabel: "Gnash Grub",
                DrawPile: localPile,
                DiscardPile: new CardPileStateSnapshot("discard:p1", "Discard pile", "p1", 0, [], true, true),
                ExhaustPile: new CardPileStateSnapshot("exhaust:p1", "Exhaust pile", "p1", 0, [], true, true),
                EncounterVisuals: new EncounterVisualsStateSnapshot(
                    "kaiser_crab_boss",
                    "kaiser_crab_boss",
                    Provisional: true,
                    VisualParts:
                    [
                        new EncounterVisualPartStateSnapshot("rocket", "kaiser-crab", "idle", "right", "center"),
                    ],
                    RecentEvents: [])),
            Choices: [],
            AvailableActions: [],
            Notices:
            [
                new StateNoticeSnapshot(
                    "pile-cards-partial",
                    "Draw pile order is not observable.",
                    true,
                    Path: "combat.drawPile.cards",
                    Severity: "partial",
                    Source: "Sts2PresentationStateResolver"),
            ],
            Debug: null,
            ScreenSource: "screen:combat:NCombatScreen",
            ScreenRawType: "NCombatScreen",
            ScreenClassName: "STS2.Screens.NCombatScreen");
    }

    private static BridgeRuntimeObservation PresentationMapObservation()
    {
        var defend = new CardStateSnapshot(
            "deck-defend",
            "Defend",
            1,
            "p1",
            false,
            null,
            [],
            false,
            ModelId: "defend_r",
            Description: "Gain 5 Guard.",
            Type: "skill",
            Rarity: "common",
            TargetType: "self",
            UpgradeLevel: 0,
            CostLabel: "1",
            AssetRefs: [new AssetReferenceSnapshot("image", "model://cards/defend_r/image", "Defend art", false)]);
        var player = new PlayerStateSnapshot(
            "p1",
            "ironclad",
            66,
            80,
            IsLocal: true,
            IsHost: true,
            Gold: 175,
            Relics:
            [
                new RelicStateSnapshot(
                    "relic-anchor",
                    "anchor",
                    "Anchor",
                    "Start combat with 10 Block.",
                    "p1",
                    0,
                    false,
                    0,
                    null,
                    [new AssetReferenceSnapshot("icon", "model://relics/anchor/icon", "Anchor icon", false)]),
            ],
            Potions:
            [
                new PotionStateSnapshot(
                    "potion-fire",
                    "Fire Potion",
                    "p1",
                    0,
                    true,
                    null,
                    [],
                    ModelId: "fire-potion",
                    Description: "Land 20 harm.",
                    TargetType: "enemy",
                    RequiresTarget: true,
                    AssetRefs: [new AssetReferenceSnapshot("icon", "model://potions/fire-potion/icon", "Fire Potion icon", false)]),
            ],
            MasterDeck: new CardPileStateSnapshot("master-deck:p1", "Master deck", "p1", 1, [defend], true, true),
            StatusEffects:
            [
                new StatusEffectStateSnapshot(
                    "status-strength",
                    "strength",
                    "Resolve",
                    "Increases attack harm.",
                    "p1",
                    1,
                    "1",
                    0,
                    null,
                    "buff",
                    [new AssetReferenceSnapshot("icon", "res://resources/textures/icons/status_strength.png", "Strength icon", false)]),
            ]);

        return new BridgeRuntimeObservation(
            SchemaVersion: "spirectl/v0",
            GameVersion: "sts2-live",
            BridgeVersion: "test",
            Source: DataSourceKind.Live,
            Provisional: false,
            ScreenType: "map",
            ScreenTitle: "Map",
            ScreenInstanceId: "screen:map:live",
            DefaultPlayerId: "p1",
            Menu: null,
            Lobby: null,
            Run: new RunStateSnapshot(
                "SEED",
                18,
                2,
                [player],
                new Dictionary<string, PlayerStateSnapshot>(StringComparer.Ordinal)
                {
                    ["p1"] = player,
                },
                ActLabel: "Act 2",
                FloorLabel: "Floor 18",
                RoomId: "room:shop",
                RoomLabel: "Shop",
                BossId: "boss:collector",
                BossLabel: "The Collector"),
            Combat: null,
            Map: new MapStateSnapshot(
            [
                new VisibleItemStateSnapshot("node:1:2", "Unknown (1,2)", "row=1;col=2;travelable=true;order=0;preferredAction=select-map-node", "p1", false, false),
            ]),
            EventRoom: new EventRoomStateSnapshot(
            [
                new VisibleItemStateSnapshot("event:choice:test", "Inspect Idol", "index=0;enabled=true;preferredAction=choose", "p1", false, false),
            ]),
            TreasureRoom: new TreasureRoomStateSnapshot(
            [
                new VisibleItemStateSnapshot("treasure:relic:test", "Take Anchor", "enabled=true;order=0;preferredAction=choose", "p1", false, false),
            ]),
            RelicSelection: new RelicSelectionStateSnapshot(
            [
                new VisibleItemStateSnapshot("treasure:relic:test", "Take Anchor", "enabled=true;order=0;preferredAction=choose", "p1", false, false),
            ]),
            RestSite: new RestSiteStateSnapshot(
            [
                new VisibleControlStateSnapshot("rest:smith", "Smith", true, "p1"),
            ]),
            Shop: null,
            Rewards: null,
            CardSelection: null,
            SimpleCardSelection: null,
            DeckCardSelection: null,
            BundleSelection: null,
            MultiplayerLobby: null,
            Choices: [],
            AvailableActions: [],
            Notices: [],
            Debug: null,
            ScreenSource: "map-screen",
            ScreenRawType: "NMapScreen",
            ScreenClassName: "MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen");
    }

    private sealed class FixedSnapshotExtractor(GameStateSnapshot snapshot) : IGameStateExtractor
    {
        public GameStateSnapshot Extract(GameStateQuery query, PlayerPerspective perspective) => snapshot;
    }

    private sealed class FixedBridgeHost(BridgeHostStatus status) : IBridgeHost
    {
        public BridgeHostStatus DescribeStatus() => status;
    }
}
