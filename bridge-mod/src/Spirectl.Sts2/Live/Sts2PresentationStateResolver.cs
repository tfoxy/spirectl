using System.Collections;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using Spirectl.Sts2.Core.State;

namespace Spirectl.Sts2.Live;

internal static class Sts2PresentationStateResolver
{
    public static CardStateSnapshot WithCardPresentation(CardStateSnapshot card, object cardObject)
    {
        var modelId = ResolveModelId(cardObject) ?? card.ModelId ?? Slug(card.Name);
        var affliction = Sts2LiveIntrospection.GetMemberValue(cardObject, "Affliction");
        var afflictionModelId = ResolveModelId(affliction);
        return card with
        {
            ModelId = modelId,
            Description = FirstString(cardObject, "Description", "RulesText", "Text", "TooltipText"),
            Type = Sts2LiveIntrospection.GetMemberValue(cardObject, "CardType")?.ToString()?.ToLowerInvariant()
                ?? Sts2LiveIntrospection.GetMemberValue(cardObject, "Type")?.ToString()?.ToLowerInvariant(),
            Rarity = Sts2LiveIntrospection.GetMemberValue(cardObject, "Rarity")?.ToString()?.ToLowerInvariant(),
            TargetType = Sts2LiveIntrospection.GetMemberValue(cardObject, "TargetType")?.ToString()?.ToLowerInvariant(),
            UpgradeLevel = ReadInt(cardObject, "CurrentUpgradeLevel", "UpgradeLevel"),
            CostLabel = ResolveCostLabel(cardObject, card.Cost),
            AfflictionModelId = afflictionModelId,
            AfflictionAmount = afflictionModelId is null ? 0 : ReadInt(affliction, "Amount"),
            AssetRefs = [Asset("image", $"model://cards/{modelId}/image", $"{card.Name} image")],
        };
    }

    public static PotionStateSnapshot WithPotionPresentation(PotionStateSnapshot potion, object potionObject)
    {
        var modelId = ResolveModelId(potionObject) ?? Slug(potion.Name);
        var targetType = Sts2LiveIntrospection.GetMemberValue(potionObject, "TargetType")?.ToString()?.ToLowerInvariant();
        return potion with
        {
            ModelId = modelId,
            Description = FirstString(potionObject, "Description", "RulesText", "Text", "TooltipText"),
            TargetType = targetType,
            RequiresTarget = !string.IsNullOrWhiteSpace(targetType) && targetType != "none",
            AssetRefs = [Asset("icon", $"model://potions/{modelId}/icon", $"{potion.Name} icon")],
        };
    }

    public static CardPileStateSnapshot ResolvePile(
        object? playerCombatState,
        string pileMemberName,
        string pileId,
        string label,
        string playerId,
        IReadOnlyList<string> cardIds,
        ICollection<StateNoticeSnapshot> notices)
    {
        var cards = ResolveCards(playerCombatState, pileMemberName, playerId, notices);
        var cardsObservable = cards.Count > 0 || cardIds.Count == 0;
        if (cardIds.Count > 0 && cards.Count == 0)
        {
            AddPartialNotice(
                notices,
                $"combat.{pileMemberName}.cards",
                "pile-cards-partial",
                $"{label} card details were not observable; only stable card ids/count are available.");
        }

        return new CardPileStateSnapshot(
            pileId,
            label,
            playerId,
            Math.Max(cardIds.Count, cards.Count),
            cards,
            cardsObservable,
            OrderObservable: false);
    }

    public static PlayerStateSnapshot WithPlayerPresentation(PlayerStateSnapshot player, object playerObject)
        => player with
        {
            Gold = ReadInt(playerObject, "Gold", "CurrentGold"),
            Relics = ResolveRelics(playerObject, player.Id),
            Potions = ResolvePotions(playerObject, player.Id),
            MasterDeck = ResolveMasterDeck(playerObject, player.Id),
            StatusEffects = ResolveStatusEffects(Sts2LiveIntrospection.GetMemberValue(playerObject, "Creature"), player.Id),
        };

    public static CombatPlayerStateSnapshot WithCombatPlayerPresentation(CombatPlayerStateSnapshot player, Creature creature, object playerObject)
        => player with
        {
            Gold = ReadInt(playerObject, "Gold", "CurrentGold"),
            Relics = ResolveRelics(playerObject, player.Id),
            StatusEffects = ResolveStatusEffects(creature, player.Id),
        };

    public static EnemyStateSnapshot WithEnemyPresentation(EnemyStateSnapshot enemy, Creature creature, int index)
    {
        var modelId = NormalizeAssetKeyId(ResolveModelId(creature.Monster) ?? Slug(enemy.Name));
        return enemy with
        {
            RuntimeEntityId = $"entity:{enemy.Id}",
            ModelId = modelId,
            StatusEffects = ResolveStatusEffects(creature, null),
            Visual = new EnemyVisualMetadataSnapshot(
                null,
                $"slot:{index}",
                null,
                "right",
                null,
                []),
        };
    }

    public static EnemyIntentSnapshot WithIntentPresentation(EnemyIntentSnapshot intent, IReadOnlyList<string> targetIds)
    {
        var label = string.IsNullOrWhiteSpace(intent.Label) ? Humanize(intent.Type) : intent.Label;
        var intentId = NormalizeAssetKeyId(intent.Type);
        IReadOnlyList<AssetReferenceSnapshot> assetRefs = string.Equals(intentId, "attack", StringComparison.Ordinal)
            ? [Asset("icon", "res://resources/textures/icons/intent_attack.png", $"{label} intent icon")]
            : [];
        return intent with
        {
            Label = label,
            Description = intent.TotalDamage > 0
                ? $"Attack for {intent.TotalDamage}."
                : label,
            TargetIds = targetIds,
            AssetRefs = assetRefs,
        };
    }

    public static (string? Id, string? Label) ResolveEncounter(RunState runState)
    {
        try
        {
            if (runState.CurrentRoom is CombatRoom combatRoom)
            {
                var id = combatRoom.Encounter.Id.Entry;
                return (id, FirstString(combatRoom.Encounter, "Name", "Title", "DisplayName") ?? Humanize(id));
            }
        }
        catch
        {
            // Best effort only.
        }

        return (null, null);
    }

    public static string? ResolveRoomLabel(object? room)
        => room is null ? null : Humanize(room.GetType().Name);

    public static StateNoticeSnapshot PartialNotice(string path, string code, string message)
        => new(code, message, true, Path: path, Severity: "partial", Source: nameof(Sts2PresentationStateResolver));

    private static IReadOnlyList<CardStateSnapshot> ResolveCards(
        object? playerCombatState,
        string pileMemberName,
        string playerId,
        ICollection<StateNoticeSnapshot> notices)
    {
        var pile = Sts2LiveIntrospection.GetMemberValue(playerCombatState, pileMemberName);
        var cardsObject = Sts2LiveIntrospection.GetMemberValue(pile, "Cards") ?? pile;
        if (cardsObject is not IEnumerable cards || cardsObject is string)
        {
            return [];
        }

        var zone = pileMemberName.Replace("Pile", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        var result = new List<CardStateSnapshot>();
        var index = 0;
        foreach (var card in cards)
        {
            if (card is null)
            {
                continue;
            }

            var id = Sts2CombatIds.CardIdInZone(card, playerId, zone, index, notices);
            result.Add(WithCardPresentation(new CardStateSnapshot(id, card.GetType().Name, 0, playerId, false, null, [], false), card));
            index++;
        }

        return result;
    }

    private static IReadOnlyList<RelicStateSnapshot> ResolveRelics(object playerObject, string playerId)
        => EnumerateAny(playerObject, "Relics", "RelicCollection", "OwnedRelics")
            .Select((relic, index) =>
            {
                var modelId = ResolveModelId(relic) ?? Slug(relic.GetType().Name);
                return new RelicStateSnapshot(
                    $"relic:{playerId}:{modelId}:{index}",
                    modelId,
                    FirstString(relic, "Name", "Title", "DisplayName") ?? relic.GetType().Name,
                    FirstString(relic, "Description", "RulesText", "TooltipText"),
                    playerId,
                    index,
                    HasCounter(relic),
                    ReadInt(relic, "Counter", "Count", "Amount"),
                    FirstString(relic, "CounterLabel"),
                    [Asset("icon", $"model://relics/{modelId}/icon", $"{modelId} icon")]);
            })
            .ToArray();

    private static IReadOnlyList<PotionStateSnapshot> ResolvePotions(object playerObject, string playerId)
        => EnumerateAny(playerObject, "PotionSlots", "Potions")
            .Where(potion => potion is not null)
            .Select((potion, index) => WithPotionPresentation(
                new PotionStateSnapshot($"potion:{playerId}:{index}:{ResolveModelId(potion) ?? Slug(potion.GetType().Name)}", ResolvePotionName(potion), playerId, index, true, null, []),
                potion))
            .ToArray();

    private static CardPileStateSnapshot? ResolveMasterDeck(object playerObject, string playerId)
    {
        var cards = EnumerateAny(playerObject, "MasterDeck", "Deck", "Cards")
            .Select((card, index) => WithCardPresentation(new CardStateSnapshot($"master-deck:{playerId}:{index}", card.GetType().Name, 0, playerId, false, null, [], false), card))
            .ToArray();
        return cards.Length == 0
            ? null
            : new CardPileStateSnapshot($"master-deck:{playerId}", "Master deck", playerId, cards.Length, cards, true, false);
    }

    private static IReadOnlyList<StatusEffectStateSnapshot> ResolveStatusEffects(object? creature, string? ownerPlayerId)
        => EnumerateAny(creature, "Powers", "StatusEffects", "Buffs", "Debuffs")
            .Select((status, index) =>
            {
                var modelId = ResolveModelId(status) ?? Slug(status.GetType().Name);
                IReadOnlyList<AssetReferenceSnapshot> assetRefs = StatusIconAssetPath(modelId) is { } iconPath
                    ? [Asset("icon", iconPath, $"{modelId} icon")]
                    : [];
                return new StatusEffectStateSnapshot(
                    $"status:{ownerPlayerId ?? "enemy"}:{modelId}:{index}",
                    modelId,
                    FirstString(status, "Name", "Title", "DisplayName") ?? status.GetType().Name,
                    FirstString(status, "Description", "RulesText", "TooltipText"),
                    ownerPlayerId,
                    ReadInt(status, "StackCount", "Stacks", "Amount"),
                    FirstString(status, "StackLabel"),
                    ReadInt(status, "Duration", "Turns"),
                    FirstString(status, "DurationLabel"),
                    status.GetType().Name,
                    assetRefs);
            })
            .ToArray();

    private static string? StatusIconAssetPath(string modelId)
        => modelId switch
        {
            "vulnerable" => "res://resources/textures/icons/status_vulnerable.png",
            "weak" => "res://resources/textures/icons/status_weak.png",
            "strength" => "res://resources/textures/icons/status_strength.png",
            "slimed" => "res://images/atlases/card_atlas.sprites/status/slimed.tres",
            "dazed" => "res://images/atlases/card_atlas.sprites/status/dazed.tres",
            "wound" => "res://images/atlases/card_atlas.sprites/status/wound.tres",
            _ => null,
        };

    private static IEnumerable<object> EnumerateAny(object? target, params string[] memberNames)
    {
        foreach (var memberName in memberNames)
        {
            var value = Sts2LiveIntrospection.GetMemberValue(target, memberName);
            var enumerable = Sts2LiveIntrospection.GetMemberValue(value, "Cards") ?? value;
            if (enumerable is IEnumerable items && enumerable is not string)
            {
                foreach (var item in items)
                {
                    if (item is not null)
                    {
                        yield return item;
                    }
                }

                yield break;
            }
        }
    }

    private static void AddPartialNotice(ICollection<StateNoticeSnapshot> notices, string path, string code, string message)
        => notices.Add(PartialNotice(path, code, message));

    private static AssetReferenceSnapshot Asset(string kind, string key, string label)
        => new(kind, key, label, Provisional: false);

    private static string? ResolveModelId(object? value)
    {
        var id = Sts2LiveIntrospection.GetMemberValue(value, "Id");
        return Sts2LiveIntrospection.GetMemberValue(id, "Entry")?.ToString()
            ?? id?.ToString()
            ?? Sts2LiveIntrospection.GetMemberValue(value, "ModelId")?.ToString();
    }

    private static string? FirstString(object? value, params string[] memberNames)
        => memberNames
            .Select(memberName => Sts2LiveIntrospection.GetMemberValue(value, memberName)?.ToString())
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));

    private static int ReadInt(object? value, params string[] memberNames)
    {
        foreach (var memberName in memberNames)
        {
            var memberValue = Sts2LiveIntrospection.GetMemberValue(value, memberName);
            if (memberValue is null)
            {
                continue;
            }

            if (int.TryParse(memberValue.ToString(), out var parsed))
            {
                return parsed;
            }
        }

        return 0;
    }

    private static bool HasCounter(object value)
        => ReadInt(value, "Counter", "Count", "Amount") != 0
            || Sts2LiveIntrospection.GetMemberValue(value, "Counter") is not null;

    private static string ResolvePotionName(object potion)
        => FirstString(potion, "Title", "DisplayName", "Name") ?? ResolveModelId(potion) ?? potion.GetType().Name;

    private static string ResolveCostLabel(object card, int fallback)
        => FirstString(card, "CostLabel")
            ?? Sts2LiveIntrospection.GetMemberValue(Sts2LiveIntrospection.GetMemberValue(card, "EnergyCost"), "Canonical")?.ToString()
            ?? fallback.ToString();

    private static string Slug(string value)
        => string.Join("-", value.Trim().Split([' ', '_', ':'], StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    private static string NormalizeAssetKeyId(string value)
        => Sts2ModelResolver.NormalizeFixtureId(value);

    private static string Humanize(string value)
        => string.Join(" ", value.Replace("_", " ", StringComparison.Ordinal).Replace("-", " ", StringComparison.Ordinal).Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
