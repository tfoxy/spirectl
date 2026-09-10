using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;

namespace Spirectl.Sts2.Live;

/// <summary>
/// Shared, game-native combat fact extraction reused by both the compact runtime
/// observation provider and the state provider so their derived values agree.
/// </summary>
internal static class Sts2CombatFacts
{
    /// <summary>
    /// A single resolved intent: the <see cref="AbstractIntent.IntentType"/> name, and for
    /// attack intents the local-perspective single-hit <see cref="Damage"/> and
    /// <see cref="Repeats"/> (game <see cref="AttackIntent.Repeats"/>: SingleAttackIntent => 1,
    /// MultiAttackIntent => N; total damage = Damage * Repeats). <see cref="Hits"/> is the
    /// legacy off-by-one encoding (Repeats + 1) kept for back-compat. <see cref="Intent"/> is
    /// the source intent (the <see cref="AttackIntent"/> when <see cref="IsAttack"/> is true).
    /// </summary>
    internal readonly record struct IntentFact(AbstractIntent Intent, string Type, bool IsAttack, int Damage, int Hits, int Repeats, int? CardCount = null);

    /// <summary>
    /// Extracts the monster's next move id and per-intent facts. Returns null when the
    /// creature has no monster / next move. <paramref name="allTargets"/> should be the
    /// combat creatures (allies + enemies) used to evaluate attack damage from the owner.
    /// </summary>
    internal static (string Id, IReadOnlyList<IntentFact> Intents)? ResolveNextMove(
        Creature owner,
        IReadOnlyList<Creature> allTargets)
    {
        var move = owner.Monster?.NextMove;
        if (move is null)
        {
            return null;
        }

        var intents = new List<IntentFact>();
        foreach (var intent in move.Intents ?? [])
        {
            if (intent is null)
            {
                continue;
            }

            if (intent is AttackIntent attackIntent)
            {
                intents.Add(new IntentFact(
                    Intent: intent,
                    Type: intent.IntentType.ToString(),
                    IsAttack: true,
                    Damage: attackIntent.GetSingleDamage(allTargets, owner),
                    Hits: attackIntent.Repeats + 1,
                    Repeats: attackIntent.Repeats));
            }
            else
            {
                intents.Add(new IntentFact(
                    intent,
                    intent.IntentType.ToString(),
                    IsAttack: false,
                    Damage: 0,
                    Hits: 0,
                    Repeats: 0,
                    CardCount: intent is StatusIntent statusIntent ? statusIntent.CardCount : null));
            }
        }

        return (move.Id, intents);
    }

    /// <summary>
    /// Resolves the set of <see cref="UnplayableReason"/> flag names for a card.
    /// Returns an empty list when the card is currently playable (reason == None).
    /// </summary>
    internal static IReadOnlyList<string> ResolveUnplayableReasons(CardModel card)
    {
        if (card.CanPlay(out var reason, out _) || reason == UnplayableReason.None)
        {
            return [];
        }

        return UnplayableReasonNames(reason);
    }

    internal static (bool Gold, bool Red) ResolveCardGlow(CardModel card)
    {
        if (!IsPlayPhase(card.Owner))
        {
            return (Gold: false, Red: false);
        }

        return (
            Gold: card.CanPlay() && card.ShouldGlowGold,
            Red: card.ShouldGlowRed);
    }

    internal static bool IsPlayPhase(Player? player)
    {
        var combatManager = CombatManager.Instance;
        if (player is null
            || combatManager is null
            || !combatManager.IsInProgress
            || player.PlayerCombatState?.Phase != PlayerTurnPhase.Play)
        {
            return false;
        }

        return combatManager.IsPartOfPlayerTurn(player);
    }

    internal static bool IsAnyPlayerInPlayPhase(IEnumerable<Player>? players = null)
    {
        if (CombatManager.Instance?.IsInProgress != true)
        {
            return false;
        }

        players ??= CombatManager.Instance.DebugOnlyGetState()?.Players ?? [];
        return players.Any(IsPlayPhase);
    }

    /// <summary>
    /// Splits a resolved <see cref="UnplayableReason"/> into its set flag names (excluding None).
    /// </summary>
    internal static IReadOnlyList<string> UnplayableReasonNames(UnplayableReason reason)
    {
        var names = new List<string>();
        foreach (var value in Enum.GetValues<UnplayableReason>())
        {
            if (value == UnplayableReason.None)
            {
                continue;
            }

            if (reason.HasFlag(value))
            {
                names.Add(value.ToString());
            }
        }

        return names;
    }
}
