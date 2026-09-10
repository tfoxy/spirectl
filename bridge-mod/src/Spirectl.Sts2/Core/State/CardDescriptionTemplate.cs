using System.Globalization;
using System.Text;

namespace Spirectl.Sts2.Core.State;

/// <summary>
/// Engine-free helpers for the host-computed card-value pipeline: sentinel <-> token translation,
/// pathological-card classification, and the dirty-skip signature. Kept free of any live STS2 /
/// Godot type so it compiles (and unit-tests) without the game assemblies. The live template
/// factory (<c>Sts2CardDescriptionTemplateFactory</c>) does the game-side work (swapping the
/// damage/block <c>DynamicVar</c> for a sentinel-valued marker and rendering
/// <c>CardModel.GetDescriptionForPile</c>) and then delegates the pure string work to this class.
/// </summary>
///
/// <remarks>
/// SENTINEL MECHANISM (why numbers, not overridden methods):
/// The canonical <c>:diff()</c> formatter calls <c>DynamicVar.ToHighlightedString(bool)</c>, which is
/// NOT virtual in the game assembly, so a subclass cannot override the emitted token. Instead the
/// marker sets its BaseValue/EnchantedValue/PreviewValue to a
/// distinctive sentinel integer; EVERY render path — <c>:diff()</c> (=(int)PreviewValue, uncolored
/// because Preview==Enchanted), plain <c>{Damage}</c> (=ToString()=IntValue), and the IConvertible
/// path (:plural/:energyIcons) — then emits that same integer as digits. We scan the rendered text
/// for those digits and replace them with a slot token like <c>&lt;damage&gt;</c>. Godot card text uses
/// [bbcode] not &lt;angle&gt; markup, so the token can never collide with real body text, and the
/// sentinels live far above any realistic card number (and below DynamicVar's 999,999,999 cap).
/// </remarks>
public static class CardDescriptionTemplate
{
    // Distinctive, reversible sentinel values (below DynamicVar's 999,999,999 BaseValue cap, far above
    // any real card number). One per var-kind: a card renders at most one damage var and one block var,
    // and if the same var appears twice both occurrences map to the same token (both are the same slot).
    public const int DamageSentinel = 999_000_001;
    public const int BlockSentinel = 999_000_002;

    public const string DamageKind = "damage";
    public const string BlockKind = "block";
    public const string SelfDamageKind = "selfDamage";

    /// <summary>The token literal substituted into the template, e.g. <c>&lt;damage&gt;</c>.</summary>
    public static string Token(string slotName) => $"<{slotName}>";

    /// <summary>
    /// Replace each seed's sentinel-integer digits in <paramref name="rendered"/> with its
    /// <c>&lt;token&gt;</c>. A seed whose sentinel is absent from the output was consumed by a
    /// number-transforming formatter (a word-only <c>:plural()</c>, etc.) and cannot be templated;
    /// it is reported in <see cref="TemplateReplacementResult.MissingSlotNames"/> so the caller can
    /// fall back to per-target fully-rendered text. Order-independent and idempotent.
    /// </summary>
    public static TemplateReplacementResult ReplaceSentinels(
        string rendered,
        IReadOnlyList<TemplateSlotSeed> seeds)
    {
        ArgumentNullException.ThrowIfNull(rendered);
        ArgumentNullException.ThrowIfNull(seeds);

        var text = rendered;
        var slots = new List<StateCombatCardSlotSnapshot>(seeds.Count);
        var missing = new List<string>();
        foreach (var seed in seeds)
        {
            var digits = seed.Sentinel.ToString(CultureInfo.InvariantCulture);
            if (!text.Contains(digits, StringComparison.Ordinal))
            {
                missing.Add(seed.SlotName);
                continue;
            }

            var token = Token(seed.SlotName);
            text = text.Replace(digits, token, StringComparison.Ordinal);
            slots.Add(new StateCombatCardSlotSnapshot(token, seed.Kind, seed.Baseline, seed.Inverse));
        }

        return new TemplateReplacementResult(text, slots, missing);
    }

    /// <summary>
    /// The card is templatable iff every requested slot was found and at least one slot exists. When
    /// false the caller ships fully-rendered per-target text (RenderedByTarget) instead of a template.
    /// </summary>
    public static bool IsTemplatable(TemplateReplacementResult result)
        => result.MissingSlotNames.Count == 0 && result.Slots.Count > 0;

    /// <summary>
    /// A cheap, deterministic signature over exactly the inputs the damage/block funnel + description
    /// render read: card identity/upgrade/enchant/energy, the card's base dynamic-var amounts, the
    /// owner's vitals (hp/maxHp/block/gold — CalculatedVars can scale off these, e.g. BodySlam) and
    /// power stacks (Strength/Weak/…), and every hittable enemy's id + vitals + power stacks
    /// (Vulnerable/…). Recompute only when this string changes. Powers are sorted so the signature is
    /// stable regardless of enumeration order.
    /// </summary>
    public static string BuildDirtySignature(CardComputeSignatureInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var builder = new StringBuilder(128);
        builder.Append(inputs.CardId).Append('|')
            .Append(inputs.UpgradeLevel).Append('|')
            .Append(inputs.EnchantmentModelId ?? string.Empty).Append('|')
            .Append(inputs.EnergyCost).Append('|')
            .Append(inputs.OwnerCreatureId).Append('|')
            .Append(inputs.OwnerHp).Append('/').Append(inputs.OwnerMaxHp).Append('/')
            .Append(inputs.OwnerBlock).Append('/').Append(inputs.OwnerGold).Append('|');
        AppendPairs(builder, inputs.CardBaseVars);
        builder.Append("|owner:");
        AppendPairs(builder, inputs.OwnerPowers);
        foreach (var enemy in inputs.Enemies.OrderBy(e => e.CreatureId, StringComparer.Ordinal))
        {
            builder.Append("|e:").Append(enemy.CreatureId).Append(':').Append(enemy.Block)
                .Append(':').Append(enemy.Hp).Append('/').Append(enemy.MaxHp).Append(':');
            AppendPairs(builder, enemy.Powers);
        }

        return builder.ToString();
    }

    private static void AppendPairs(StringBuilder builder, IReadOnlyList<KeyValuePair<string, int>> pairs)
    {
        var first = true;
        foreach (var pair in pairs.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!first)
            {
                builder.Append(',');
            }

            builder.Append(pair.Key).Append('=').Append(pair.Value);
            first = false;
        }
    }
}

/// <summary>A slot to substitute: its sentinel value plus the wire metadata to emit when found.</summary>
public sealed record TemplateSlotSeed(
    int Sentinel,
    string SlotName,
    string Kind,
    int Baseline,
    bool Inverse);

/// <summary>Outcome of <see cref="CardDescriptionTemplate.ReplaceSentinels"/>.</summary>
public sealed record TemplateReplacementResult(
    string Template,
    IReadOnlyList<StateCombatCardSlotSnapshot> Slots,
    IReadOnlyList<string> MissingSlotNames);

/// <summary>One creature's identity + combat state that the funnel reads (for the dirty signature).</summary>
public sealed record EnemySignatureInput(
    string CreatureId,
    int Block,
    int Hp,
    int MaxHp,
    IReadOnlyList<KeyValuePair<string, int>> Powers);

/// <summary>
/// All primitives the dirty-skip signature is built from (extracted live, hashed here). Owner
/// hp/block/gold are included because CalculatedDamage/Block vars can scale off them (e.g. BodySlam
/// multiplies by <c>Owner.Creature.Block</c>) — omitting them would serve a stale cached preview when
/// only the external value changes.
/// </summary>
public sealed record CardComputeSignatureInputs(
    string CardId,
    int UpgradeLevel,
    string? EnchantmentModelId,
    int EnergyCost,
    string OwnerCreatureId,
    int OwnerHp,
    int OwnerMaxHp,
    int OwnerBlock,
    int OwnerGold,
    IReadOnlyList<KeyValuePair<string, int>> CardBaseVars,
    IReadOnlyList<KeyValuePair<string, int>> OwnerPowers,
    IReadOnlyList<EnemySignatureInput> Enemies);
