namespace Spirectl.Sts2.Core.State;

internal static class PresentationScaffoldState
{
    public static PresentationScaffoldSnapshot Create(string playerId)
        => CreateMultiplayer().PlayersById.TryGetValue(playerId, out var player)
            ? player
            : CreatePlayer(playerId, isLocal: true, isHost: true);

    public static MultiplayerPresentationScaffoldSnapshot CreateMultiplayer()
    {
        var p1 = CreatePlayer("p1", isLocal: true, isHost: true);
        var p2 = CreatePlayer("p2", isLocal: false, isHost: false, isRemote: true);
        return new MultiplayerPresentationScaffoldSnapshot(
            [p1, p2],
            new Dictionary<string, PresentationScaffoldSnapshot>(StringComparer.Ordinal)
            {
                ["p1"] = p1,
                ["p2"] = p2,
            },
            CreateEnemy(["p1", "p2"]));
    }

    private static PresentationScaffoldSnapshot CreatePlayer(
        string playerId,
        bool isLocal,
        bool isHost,
        bool isRemote = false)
    {
        var suffix = playerId == "p1" ? string.Empty : $"-{playerId}";
        var cardPrefix = playerId == "p1" ? "c" : $"c_{playerId}";
        var potionA = playerId == "p1" ? "fire-potion" : "swift-potion";
        var potionB = playerId == "p1" ? "block-potion" : "dexterity-potion";
        var relicModelId = playerId == "p1" ? "burning-blood" : "bag-of-preparation";
        var statusModelId = playerId == "p1" ? "vulnerable" : "strength";
        var strike = new CardStateSnapshot(
            $"{cardPrefix}_1",
            playerId == "p1" ? "Jab" : "Twin Jab",
            1,
            playerId,
            true,
            null,
            ["e_1"],
            false,
            ModelId: $"strike_r{suffix}",
            Description: playerId == "p1" ? "Land 6 harm." : "Land 5 harm twice.",
            Type: "attack",
            Rarity: "basic",
            TargetType: "enemy",
            CostLabel: "1",
            AssetRefs: [Asset("image", $"model://cards/strike_r{suffix}/image", $"{playerId} strike art")]);
        var defend = new CardStateSnapshot(
            $"{cardPrefix}_2",
            playerId == "p1" ? "Defend" : "Guard",
            1,
            playerId,
            true,
            null,
            [],
            false,
            ModelId: $"defend_r{suffix}",
            Description: playerId == "p1" ? "Gain 5 Guard." : "Gain 7 Guard.",
            Type: "skill",
            Rarity: "basic",
            TargetType: "self",
            CostLabel: "1",
            AssetRefs: [Asset("image", $"model://cards/defend_r{suffix}/image", $"{playerId} defend art")]);
        var firePotion = new PotionStateSnapshot(
            $"potion:{playerId}:0:{potionA}",
            playerId == "p1" ? "Fire Potion" : "Swift Potion",
            playerId,
            0,
            true,
            null,
            ["e_1"],
            ModelId: potionA,
            Description: playerId == "p1" ? "Land 20 harm." : "Take 3 cards.",
            TargetType: "enemy",
            RequiresTarget: true,
            AssetRefs: [Asset("icon", $"model://potions/{potionA}/icon", $"{playerId} potion icon")]);
        var blockPotion = new PotionStateSnapshot(
            $"potion:{playerId}:1:{potionB}",
            playerId == "p1" ? "Guard Potion" : "Finesse Potion",
            playerId,
            1,
            true,
            null,
            [],
            ModelId: potionB,
            Description: playerId == "p1" ? "Gain 12 Guard." : "Gain 2 Finesse.",
            TargetType: "self",
            AssetRefs: [Asset("icon", $"model://potions/{potionB}/icon", $"{playerId} potion icon")]);
        var relic = new RelicStateSnapshot(
            $"relic:{playerId}:{relicModelId}",
            relicModelId,
            playerId == "p1" ? "Ember Heart" : "Satchel of Readiness",
            playerId == "p1" ? "When a battle ends, restore 6 HP." : "When a battle starts, take 2 extra cards.",
            playerId,
            0,
            true,
            2,
            "combats",
            [Asset("icon", $"model://relics/{relicModelId}/icon", $"{playerId} relic icon")]);
        var status = new StatusEffectStateSnapshot(
            $"status:{playerId}:{statusModelId}",
            statusModelId,
            playerId == "p1" ? "Exposed" : "Resolve",
            playerId == "p1" ? "Takes 50% more attack harm." : "Deals more attack harm.",
            playerId,
            2,
            "2",
            2,
            playerId == "p1" ? "turns" : "stacks",
            playerId == "p1" ? "debuff" : "buff",
            [Asset("icon", playerId == "p1" ? "res://resources/textures/icons/status_vulnerable.png" : "res://resources/textures/icons/status_strength.png", $"{playerId} status icon")]);
        var drawPile = new CardPileStateSnapshot($"draw:{playerId}", "Draw pile", playerId, playerId == "p1" ? 1 : 3, [defend], true, false);
        var discardPile = new CardPileStateSnapshot($"discard:{playerId}", "Discard pile", playerId, playerId == "p1" ? 1 : 2, [], false, false);
        var exhaustPile = new CardPileStateSnapshot($"exhaust:{playerId}", "Exhaust pile", playerId, 0, [], true, true);

        var runPlayer = new PlayerStateSnapshot(
            playerId,
            playerId == "p1" ? "ironclad" : "silent",
            playerId == "p1" ? 67 : 58,
            80,
            IsLocal: isLocal,
            IsHost: isHost,
            IsRemote: isRemote,
            Gold: playerId == "p1" ? 99 : 137,
            Relics: [relic],
            Potions: [firePotion, blockPotion],
            MasterDeck: new CardPileStateSnapshot($"master-deck:{playerId}", "Master deck", playerId, 2, [strike, defend], true, false),
            StatusEffects: [status]);
        var combatPlayer = new CombatPlayerStateSnapshot(
            playerId,
            playerId == "p1" ? "ironclad" : "silent",
            playerId == "p1" ? 67 : 58,
            80,
            playerId == "p1" ? 0 : 4,
            playerId == "p1" ? 3 : 2,
            3,
            [strike, defend],
            Potions: [firePotion, blockPotion],
            IsLocal: isLocal,
            IsHost: isHost,
            IsRemote: isRemote,
            DrawPileCardIds: [$"draw:{playerId}:defend"],
            DiscardPileCardIds: [$"discard:{playerId}:bash"],
            ExhaustPileCardIds: [],
            DrawPile: drawPile,
            DiscardPile: discardPile,
            ExhaustPile: exhaustPile,
            Gold: playerId == "p1" ? 99 : 137,
            Relics: [relic],
            StatusEffects: [status]);
        return new PresentationScaffoldSnapshot(
            runPlayer,
            combatPlayer,
            [strike, defend],
            [firePotion, blockPotion],
            drawPile,
            discardPile,
            exhaustPile);
    }

    private static EnemyStateSnapshot CreateEnemy(IReadOnlyList<string> targetPlayerIds)
    {
        var enemy = new EnemyStateSnapshot(
            "e_1",
            "Gnash Grub",
            38,
            "attack+block",
            42,
            0,
            true,
            [new EnemyIntentSnapshot("attack", 11, 1, 11, Label: "Gnash", Description: "Attacks for 11.", TargetIds: targetPlayerIds, AssetRefs: [Asset("icon", "res://resources/textures/icons/intent_attack.png", "Attack intent icon")])],
            RuntimeEntityId: "entity:jaw-worm:1",
            ModelId: "jaw-worm",
            StatusEffects:
            [
                new StatusEffectStateSnapshot(
                    "status:enemy:e_1:weak",
                    "weak",
                    "Weak",
                    "Deals less attack harm.",
                    null,
                    1,
                    "1",
                    1,
                    "turn",
                    "debuff",
                    [Asset("icon", "res://resources/textures/icons/status_weak.png", "Weak icon")]),
                new StatusEffectStateSnapshot(
                    "status:enemy:e_1:slimed",
                    "slimed",
                    "Slimed",
                    "Slime clings to this enemy.",
                    null,
                    1,
                    "1",
                    0,
                    null,
                    "debuff",
                    [Asset("icon", "res://images/atlases/card_atlas.sprites/status/slimed.tres", "Slimed icon")]),
            ],
            AssetRefs:
            [
                Asset("image", "res://scenes/vfx/block_spark_vfx.tscn", "Block spark VFX reference"),
            ],
            Visual: new EnemyVisualMetadataSnapshot(
                null,
                "slot:front",
                "normal",
                "right",
                "encounter:jaw-worm:visuals",
                []));
        return enemy;
    }

    public static EncounterVisualsStateSnapshot CreateEncounterVisuals()
        => new(
            "kaiser_crab_boss",
            "kaiser_crab_boss",
            Provisional: true,
            VisualParts:
            [
                new EncounterVisualPartStateSnapshot("rocket", "kaiser-crab", "idle", "right", "center"),
            ],
            RecentEvents: [],
            Notices:
            [
                new StateNoticeSnapshot(
                    "encounter-visual-live-only",
                    "Scaffold encounter visual parts are representative metadata; live extraction remains environment-gated.",
                    true,
                    Path: "combat.encounterVisuals",
                    Severity: "info",
                    Source: nameof(PresentationScaffoldState)),
            ]);

    private static AssetReferenceSnapshot Asset(string kind, string key, string label)
        => new(kind, key, label, Provisional: false);
}

internal sealed record PresentationScaffoldSnapshot(
    PlayerStateSnapshot RunPlayer,
    CombatPlayerStateSnapshot CombatPlayer,
    IReadOnlyList<CardStateSnapshot> Hand,
    IReadOnlyList<PotionStateSnapshot> Potions,
    CardPileStateSnapshot DrawPile,
    CardPileStateSnapshot DiscardPile,
    CardPileStateSnapshot ExhaustPile);

internal sealed record MultiplayerPresentationScaffoldSnapshot(
    IReadOnlyList<PresentationScaffoldSnapshot> Players,
    IReadOnlyDictionary<string, PresentationScaffoldSnapshot> PlayersById,
    EnemyStateSnapshot Enemy);
