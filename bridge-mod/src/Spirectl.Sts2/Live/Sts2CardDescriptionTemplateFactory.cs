using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using Spirectl.Sts2.Core.State;

namespace Spirectl.Sts2.Live;

/// <summary>
/// Builds a card's host-rendered description template + slots by asking the GAME to render its own
/// body (<see cref="CardModel.GetDescriptionForPile"/>) with the target-varying damage/block var
/// temporarily swapped for a sentinel-valued <see cref="MarkerDynamicVar"/>. The rendered sentinel
/// digits are rewritten into <c>&lt;damage&gt;</c>/<c>&lt;block&gt;</c> tokens by the engine-free
/// <see cref="CardDescriptionTemplate"/>. When a slot's sentinel is swallowed by a number-transforming
/// formatter (word-only <c>:plural()</c> etc.) the card is not templatable and we fall back to
/// per-target fully-rendered text.
/// </summary>
///
/// <remarks>
/// P1 DECISION — in-place VALUE mutation (not a type swap, not CloneForPreview).
/// <c>GetDescriptionForPile</c> renders the card's LIVE <c>DynamicVars</c> by reference
/// (<c>DynamicVars.AddTo(description)</c>) and does not call <c>UpdateDynamicVarPreview</c> or rebuild
/// the set, so we mutate the real damage/block var's values to a sentinel, render, and restore in a
/// finally. We deliberately do NOT swap the dictionary entry for a differently-typed marker: per-card
/// <c>AddExtraArgsToDescription</c> overrides can reach the typed accessors
/// (<c>DynamicVarSet.CalculatedDamage/.Damage/.Block</c>, which cast), which would throw on a
/// substituted type. Keeping the concrete var and only changing its numbers is cast-safe. Runs on the
/// game main thread synchronously, so no other code observes the transient values. See
/// <see cref="MarkerDynamicVar"/> for why the sentinel flows through PreviewValue/EnchantedValue rather
/// than an overridden method. CloneForPreview remains the documented fallback if a future build starts
/// refreshing previews inside the render.
/// </remarks>
internal static class Sts2CardDescriptionTemplateFactory
{
    public static CardTemplateResult Build(CardModel card, IReadOnlyList<Creature> hittableEnemies)
    {
        var vars = card.DynamicVars;
        var damageVar = Sts2CombatPreviewCore.ResolveDamageVar(vars);
        var blockVar = Sts2CombatPreviewCore.ResolveBlockVar(vars);
        if (damageVar is null && blockVar is null)
        {
            // No target-varying number: nothing to template. The catalog's RenderedDescription already
            // carries the fully static body, so the state ships no per-card override.
            return CardTemplateResult.None;
        }

        var markers = new List<MarkerDynamicVar>(2);
        if (damageVar is not null)
        {
            markers.Add(new MarkerDynamicVar(
                damageVar,
                CardDescriptionTemplate.DamageSentinel,
                CardDescriptionTemplate.DamageKind,
                CardDescriptionTemplate.DamageKind,
                Baseline(damageVar),
                inverse: false));
        }

        if (blockVar is not null)
        {
            markers.Add(new MarkerDynamicVar(
                blockVar,
                CardDescriptionTemplate.BlockSentinel,
                CardDescriptionTemplate.BlockKind,
                CardDescriptionTemplate.BlockKind,
                Baseline(blockVar),
                inverse: false));
        }

        var rendered = RenderWithMarkers(card, markers);
        if (rendered is null)
        {
            // Render failed (game internals changed / threw): ship no template, let the frontend fall
            // back to the catalog's static description. The preview matrix is still computed elsewhere.
            return CardTemplateResult.None;
        }

        var result = CardDescriptionTemplate.ReplaceSentinels(
            rendered,
            markers.Select(marker => marker.ToSeed()).ToList());

        if (CardDescriptionTemplate.IsTemplatable(result))
        {
            return new CardTemplateResult(result.Template, DescriptionText: null, result.Slots, RenderedByTarget: null);
        }

        // Pathological (a slot was consumed by a number-transforming formatter): ship the verbatim
        // no-target render plus a fully game-rendered body per hittable target so the frontend can show
        // the correct string without re-implementing the loc pipeline. Corpus analysis (P2) found ZERO
        // cards that strictly hit this for the damage/block token itself, so this path is a safety net.
        var descriptionText = SafeRender(card, target: null);
        var renderedByTarget = RenderPerTarget(card, hittableEnemies);
        return new CardTemplateResult(
            DescriptionTemplate: null,
            DescriptionText: descriptionText,
            Slots: Array.Empty<StateCombatCardSlotSnapshot>(),
            RenderedByTarget: renderedByTarget);
    }

    // Enchanted, no-combat-modifier value — the game's own diff-highlight comparison basis
    // (DynamicVar.ToHighlightedString compares PreviewValue vs EnchantedValue). For calculated vars the
    // enchant is approximated by the raw base calc; that edge (enchanted scaling card) is a live-verify item.
    private static int Baseline(DynamicVar var)
        => var is CalculatedVar calc ? (int)calc.Calculate(null) : (int)var.EnchantedValue;

    private static string? RenderWithMarkers(CardModel card, IReadOnlyList<MarkerDynamicVar> markers)
    {
        try
        {
            foreach (var marker in markers)
            {
                marker.Apply();
            }

            return card.GetDescriptionForPile(PileType.Hand);
        }
        catch
        {
            return null;
        }
        finally
        {
            foreach (var marker in markers)
            {
                marker.Restore();
            }
        }
    }

    private static IReadOnlyDictionary<string, string>? RenderPerTarget(
        CardModel card,
        IReadOnlyList<Creature> hittableEnemies)
    {
        if (hittableEnemies.Count == 0)
        {
            return null;
        }

        var byTarget = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            for (var i = 0; i < hittableEnemies.Count; i++)
            {
                var enemy = hittableEnemies[i];
                // Refresh PreviewValue for this target so :diff() reflects the target's Vulnerable etc.
                card.UpdateDynamicVarPreview(CardPreviewMode.Normal, enemy, card.DynamicVars);
                byTarget[Sts2CombatIds.CreatureId(enemy, i)] = card.GetDescriptionForPile(PileType.Hand, enemy);
            }
        }
        finally
        {
            // Return the live vars to their un-previewed base so we don't leave a stale per-target preview.
            card.DynamicVars.ClearPreview();
        }

        return byTarget.Count == 0 ? null : byTarget;
    }

    private static string? SafeRender(CardModel card, Creature? target)
    {
        try
        {
            return card.GetDescriptionForPile(PileType.Hand, target);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// The template/text/slots for one hand card. Exactly one of DescriptionTemplate (templatable) or
/// DescriptionText+RenderedByTarget (pathological fallback) is populated; both are null for a card with
/// no damage/block var (<see cref="None"/>).
/// </summary>
internal sealed record CardTemplateResult(
    string? DescriptionTemplate,
    string? DescriptionText,
    IReadOnlyList<StateCombatCardSlotSnapshot> Slots,
    IReadOnlyDictionary<string, string>? RenderedByTarget)
{
    public static readonly CardTemplateResult None =
        new(null, null, Array.Empty<StateCombatCardSlotSnapshot>(), null);

    public bool HasValue => DescriptionTemplate is not null || DescriptionText is not null;
}
