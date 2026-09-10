using System.Globalization;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using Spirectl.Sts2.Core.State;

namespace Spirectl.Sts2.Live;

internal static class Sts2CombatIds
{
    public static string PlayerId(object player)
    {
        var netId = Sts2LiveIntrospection.GetMemberValue(player, "NetId")?.ToString();
        if (!string.IsNullOrWhiteSpace(netId))
        {
            return $"p:{netId}";
        }

        return $"p:{player.GetHashCode()}";
    }

    /// <summary>
    /// Combat card id = the <see cref="NetCombatCardDb"/> uint serialized as a decimal string.
    /// These ids match the game's networked <c>NetPlayCardAction.card</c> value. Canonical
    /// (immutable) cards or cards not yet registered fall back to a stable screen-instance id.
    /// </summary>
    public static string CardId(
        object card,
        string playerId,
        int index,
        ICollection<StateNoticeSnapshot>? notices = null)
        => NetCombatCardId(card, notices)
            ?? $"card:{playerId}:{index}:{card.GetType().Name.ToLowerInvariant()}";

    public static string CardIdInZone(
        object card,
        string playerId,
        string zone,
        int index,
        ICollection<StateNoticeSnapshot>? notices = null)
        => NetCombatCardId(card, notices)
            ?? $"card:{playerId}:{zone}:{index}:{card.GetType().Name.ToLowerInvariant()}";

    private static string? NetCombatCardId(object card, ICollection<StateNoticeSnapshot>? notices)
    {
        if (card is not CardModel model || !model.IsMutable)
        {
            AddCardIdFallbackNotice(notices);
            return null;
        }

        try
        {
            if (NetCombatCardDb.Instance.TryGetCardId(model, out var id))
            {
                return id.ToString(CultureInfo.InvariantCulture);
            }
        }
        catch
        {
            // TryGetCardId throws for canonical cards; treat any failure as unregistered.
        }

        AddCardIdFallbackNotice(notices);
        return null;
    }

    private static void AddCardIdFallbackNotice(ICollection<StateNoticeSnapshot>? notices)
        => Sts2StateNotice.AddPartialOnce(
            notices,
            "card-id-fallback",
            "Combat card ids fall back to screen-instance stability when the NetCombatCardDb id is unavailable.",
            "ids",
            nameof(Sts2CombatIds));

    /// <summary>
    /// Creature id (enemies, player creatures, pets, every combat target) =
    /// <c>creature:{Creature.CombatId}</c>, matching the state provider's ResolveCreatureId
    /// and the game's networked target ids (<c>NetPlayCardAction.targetId</c>).
    /// </summary>
    public static string CreatureId(
        Creature creature,
        int index,
        ICollection<StateNoticeSnapshot>? notices = null)
    {
        if (creature.CombatId is { } combatId)
        {
            return $"creature:{combatId.ToString(CultureInfo.InvariantCulture)}";
        }

        Sts2StateNotice.AddPartialOnce(
            notices,
            "creature-id-fallback",
            "Creature ids fall back to screen-instance stability when Creature.CombatId is unavailable.",
            "ids",
            nameof(Sts2CombatIds));
        return $"creature:enemy:{index}";
    }

    public static string EnemyId(
        Creature enemy,
        int index,
        ICollection<StateNoticeSnapshot>? notices = null)
        => CreatureId(enemy, index, notices);

    /// <summary>
    /// Potion id = the slot index serialized as a decimal string, matching the game's
    /// networked <c>NetUsePotionAction.potionIndex</c>.
    /// </summary>
    public static string PotionId(
        object potion,
        string playerId,
        int slotIndex)
        => slotIndex.ToString(CultureInfo.InvariantCulture);
}
