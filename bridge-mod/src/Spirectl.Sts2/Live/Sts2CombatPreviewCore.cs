using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using Spirectl.Sts2.Core.Combat;
using Spirectl.Sts2.Live.GameApi;

namespace Spirectl.Sts2.Live;

/// <summary>
/// The shared game-truth damage/block funnel, lifted out of <see cref="Sts2CombatPreviewProvider"/> so
/// both the validation oracle (the ICombatPreviewProvider endpoint) and the live state provider compute
/// identical post-modifier numbers. For one hand card it runs the value through the game's own
/// <see cref="Hook.ModifyDamage"/> / <see cref="Hook.ModifyBlock"/> funnel, so every power, relic and
/// enchantment is reflected exactly as the in-game preview shows it. Taking the number from the funnel's
/// RETURN VALUE sidesteps a headless dead-end: the card's own cached preview field is filled in by the
/// UI, which never runs on a headless host.
///
/// Must run on the game main thread (both callers already marshal there).
/// </summary>
internal static class Sts2CombatPreviewCore
{
    /// <summary>
    /// The live hittable-enemy list (combat targets), or empty when combat is not in progress. Same
    /// source both callers key their per-target matrices on, so creature ids line up.
    /// </summary>
    public static IReadOnlyList<Creature> HittableEnemies()
    {
        var combatManager = CombatManager.Instance;
        if (combatManager is null || !combatManager.IsInProgress)
        {
            return Array.Empty<Creature>();
        }

        var combatState = combatManager.DebugOnlyGetState();
        return (combatState?.HittableEnemies ?? Array.Empty<Creature>()).ToList();
    }

    /// <summary>
    /// Game-truth numbers for one card, or null when the card has neither a damage nor a block dynamic
    /// var (a pure skill the formula doesn't number). Basic cards carry a plain DamageVar/BlockVar;
    /// scaling/extra-damage cards carry a CalculatedDamageVar/CalculatedBlockVar. Both run the same funnel.
    /// </summary>
    public static CardPreviewSnapshot? ComputeCardPreview(CardModel card, IReadOnlyList<Creature> hittableEnemies)
    {
        var vars = card.DynamicVars;
        var damageVar = ResolveDamageVar(vars);
        var blockVar = ResolveBlockVar(vars);
        if (damageVar is null && blockVar is null)
        {
            return null;
        }

        // ModifyBlock requires a non-null CombatState (it iterates its hook listeners).
        var block = blockVar is not null && card.CombatState is not null
            ? FunnelBlock(card, blockVar)
            : 0;

        var selfDamage = 0;
        var damageByTarget = new Dictionary<string, int>(StringComparer.Ordinal);
        if (damageVar is not null)
        {
            // In-hand number: no creature target → dealer-side modifiers only (Strength/Weak),
            // no per-target Vulnerable.
            selfDamage = FunnelDamage(card, damageVar, target: null);
            for (var i = 0; i < hittableEnemies.Count; i++)
            {
                var enemy = hittableEnemies[i];
                damageByTarget[Sts2CombatIds.CreatureId(enemy, i)] = FunnelDamage(card, damageVar, enemy);
            }
        }

        return new CardPreviewSnapshot(block, selfDamage, damageByTarget);
    }

    // Prefers the calculated (scaling) var; falls back to the plain base-value var.
    public static DynamicVar? ResolveDamageVar(DynamicVarSet vars)
        => vars.ContainsKey("CalculatedDamage") ? vars["CalculatedDamage"]
            : vars.ContainsKey("Damage") ? vars["Damage"]
            : null;

    public static DynamicVar? ResolveBlockVar(DynamicVarSet vars)
        => vars.ContainsKey("CalculatedBlock") ? vars["CalculatedBlock"]
            : vars.ContainsKey("Block") ? vars["Block"]
            : null;

    // A read-only recomputation of the number the in-game preview shows: the displayed value IS the
    // funnel's return (the funnel applies enchantment itself), so nothing else has to be re-applied.
    private static int FunnelDamage(CardModel card, DynamicVar damageVar, Creature? target)
        => damageVar switch
        {
            CalculatedDamageVar calc => (int)GameApiHooks.ModifyDamage(
                card.Owner.RunState,
                card.CombatState ?? card.Owner.Creature.CombatState,
                target,
                calc.IsFromOsty ? card.Owner.Osty : card.Owner.Creature,
                calc.Calculate(target),
                calc.Props,
                card,
                ModifyDamageHookType.All,
                CardPreviewMode.Normal),
            DamageVar dmg => (int)GameApiHooks.ModifyDamage(
                card.Owner.RunState,
                card.CombatState,
                target,
                card.Owner.Creature,
                dmg.BaseValue,
                dmg.Props,
                card,
                ModifyDamageHookType.All,
                CardPreviewMode.Normal),
            _ => 0,
        };

    // The same read-only recomputation for the block channel.
    private static int FunnelBlock(CardModel card, DynamicVar blockVar)
        => blockVar switch
        {
            CalculatedBlockVar calc => (int)Hook.ModifyBlock(
                card.CombatState!,
                card.Owner.Creature,
                calc.Calculate(null),
                calc.Props,
                card,
                null,
                out _),
            BlockVar blk => (int)Hook.ModifyBlock(
                card.CombatState!,
                card.Owner.Creature,
                blk.BaseValue,
                blk.Props,
                card,
                null,
                out _),
            _ => 0,
        };
}
