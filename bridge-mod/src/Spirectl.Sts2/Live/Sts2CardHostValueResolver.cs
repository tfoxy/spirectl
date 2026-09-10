using System.Collections;
using System.Collections.Concurrent;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using Spirectl.Sts2.Core.Combat;
using Spirectl.Sts2.Core.State;

namespace Spirectl.Sts2.Live;

/// <summary>
/// Resolves one hand card's host-computed values — the game-truth damage/block matrix
/// (<see cref="Sts2CombatPreviewCore"/>) plus the game-rendered description template
/// (<see cref="Sts2CardDescriptionTemplateFactory"/>) — behind a dirty-skip cache so the
/// (relatively expensive) funnel + loc render only re-runs when an input the funnel actually reads
/// changes. Runs on the game main thread (called from within the state capture).
/// </summary>
internal static class Sts2CardHostValueResolver
{
    // Keyed by the combat card id; value carries the signature the result was computed under. Combat has
    // a bounded number of live cards, so this grows slowly within a run. Concurrent for safety even though
    // state capture is serialized onto the main thread. Capped so a long host session spanning many
    // combats/runs cannot grow it unboundedly — clearing merely costs one recompute per hand card.
    private const int CacheCapacity = 512;
    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new(StringComparer.Ordinal);

    public static CardHostValues? Resolve(
        CardModel card,
        string cardId,
        int upgradeLevel,
        int energyCost,
        IReadOnlyList<Creature> hittableEnemies,
        ICollection<StateNoticeSnapshot> notices,
        string path)
    {
        try
        {
            var signature = CardDescriptionTemplate.BuildDirtySignature(BuildSignatureInputs(
                card, cardId, upgradeLevel, energyCost, hittableEnemies));

            if (Cache.TryGetValue(cardId, out var cached) && string.Equals(cached.Signature, signature, StringComparison.Ordinal))
            {
                return cached.Values;
            }

            var values = Compute(card, hittableEnemies);
            if (Cache.Count >= CacheCapacity)
            {
                Cache.Clear();
            }

            Cache[cardId] = new CacheEntry(signature, values);
            return values;
        }
        catch (Exception ex)
        {
            notices.Add(PartialNotice(path, "state-card-host-values-unavailable",
                $"Host-computed card values could not be resolved: {ex.Message}"));
            return null;
        }
    }

    private static CardHostValues? Compute(CardModel card, IReadOnlyList<Creature> hittableEnemies)
    {
        var preview = Sts2CombatPreviewCore.ComputeCardPreview(card, hittableEnemies);
        var template = Sts2CardDescriptionTemplateFactory.Build(card, hittableEnemies);
        if (preview is null && !template.HasValue)
        {
            // Pure skill with no numbered damage/block var: nothing host-specific to ship.
            return null;
        }

        var previewSnapshot = preview is null
            ? null
            : new StateCombatCardPreviewSnapshot(
                Block: preview.Block,
                SelfDamage: preview.SelfDamage,
                DamageByTarget: preview.DamageByTarget,
                Slots: template.Slots,
                RenderedByTarget: template.RenderedByTarget);

        return new CardHostValues(template.DescriptionTemplate, template.DescriptionText, previewSnapshot);
    }

    private static CardComputeSignatureInputs BuildSignatureInputs(
        CardModel card,
        string cardId,
        int upgradeLevel,
        int energyCost,
        IReadOnlyList<Creature> hittableEnemies)
    {
        var ownerCreature = card.Owner?.Creature;
        var ownerCreatureId = ownerCreature is not null ? Sts2CombatIds.CreatureId(ownerCreature, 0) : "creature:owner";
        var enemies = new List<EnemySignatureInput>(hittableEnemies.Count);
        for (var i = 0; i < hittableEnemies.Count; i++)
        {
            var enemy = hittableEnemies[i];
            enemies.Add(new EnemySignatureInput(
                Sts2CombatIds.CreatureId(enemy, i),
                enemy.Block,
                enemy.CurrentHp,
                enemy.MaxHp,
                ReadPowers(enemy)));
        }

        return new CardComputeSignatureInputs(
            CardId: cardId,
            UpgradeLevel: upgradeLevel,
            EnchantmentModelId: ReadEnchantmentId(card),
            EnergyCost: energyCost,
            OwnerCreatureId: ownerCreatureId,
            OwnerHp: ownerCreature?.CurrentHp ?? 0,
            OwnerMaxHp: ownerCreature?.MaxHp ?? 0,
            OwnerBlock: ownerCreature?.Block ?? 0,
            OwnerGold: card.Owner is { } owner
                ? Sts2StateProvider.ToInt32(Sts2LiveIntrospection.GetMemberValue(owner, "Gold"))
                : 0,
            CardBaseVars: ReadCardBaseVars(card),
            OwnerPowers: ReadPowers(ownerCreature),
            Enemies: enemies);
    }

    private static IReadOnlyList<KeyValuePair<string, int>> ReadCardBaseVars(CardModel card)
    {
        var vars = new List<KeyValuePair<string, int>>();
        foreach (var pair in card.DynamicVars)
        {
            vars.Add(new KeyValuePair<string, int>(pair.Key, (int)pair.Value.BaseValue));
        }

        return vars;
    }

    private static IReadOnlyList<KeyValuePair<string, int>> ReadPowers(object? creature)
    {
        var powers = new List<KeyValuePair<string, int>>();
        if (creature is null || Sts2LiveIntrospection.GetMemberValue(creature, "Powers") is not IEnumerable enumerable)
        {
            return powers;
        }

        foreach (var power in enumerable)
        {
            if (power is null)
            {
                continue;
            }

            var modelId = Sts2LiveIntrospection.GetMemberValue(
                Sts2LiveIntrospection.GetMemberValue(power, "ModelId"), "Entry")?.ToString()
                ?? Sts2LiveIntrospection.GetMemberValue(power, "ModelId")?.ToString()
                ?? power.GetType().Name;
            var amount = Sts2StateProvider.ToInt32(Sts2LiveIntrospection.GetMemberValue(power, "Amount"));
            powers.Add(new KeyValuePair<string, int>(modelId, amount));
        }

        return powers;
    }

    private static string? ReadEnchantmentId(CardModel card)
        => Sts2LiveIntrospection.GetMemberValue(
            Sts2LiveIntrospection.GetMemberValue(card.Enchantment, "Id"), "Entry")?.ToString();

    private static StateNoticeSnapshot PartialNotice(string path, string code, string message)
        => Sts2StateNotice.Partial(code, message, path, nameof(Sts2CardHostValueResolver));

    private readonly record struct CacheEntry(string Signature, CardHostValues? Values);
}

/// <summary>Host-computed values for one hand card, as shipped in StateCombatCardSnapshot.</summary>
internal sealed record CardHostValues(
    string? DescriptionTemplate,
    string? DescriptionText,
    StateCombatCardPreviewSnapshot? Preview);
