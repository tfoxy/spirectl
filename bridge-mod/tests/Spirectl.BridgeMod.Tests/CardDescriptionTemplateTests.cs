using Spirectl.Sts2.Core.State;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// Engine-free coverage of the host-computed card-value pure logic: sentinel<->token replacement,
// pathological classification, and the dirty-skip signature. The GetDescriptionForPile integration
// (marker swap + live render) is deferred to live validation.
public sealed class CardDescriptionTemplateTests
{
    private static TemplateSlotSeed DamageSeed(int baseline = 6, bool inverse = false)
        => new(CardDescriptionTemplate.DamageSentinel, CardDescriptionTemplate.DamageKind, CardDescriptionTemplate.DamageKind, baseline, inverse);

    private static TemplateSlotSeed BlockSeed(int baseline = 5)
        => new(CardDescriptionTemplate.BlockSentinel, CardDescriptionTemplate.BlockKind, CardDescriptionTemplate.BlockKind, baseline, Inverse: false);

    [Fact]
    public void ReplaceSentinelsRewritesDamageDigitsIntoToken()
    {
        var rendered = $"Deal {CardDescriptionTemplate.DamageSentinel} damage.";

        var result = CardDescriptionTemplate.ReplaceSentinels(rendered, [DamageSeed(baseline: 6)]);

        Assert.Equal("Deal <damage> damage.", result.Template);
        Assert.True(CardDescriptionTemplate.IsTemplatable(result));
        var slot = Assert.Single(result.Slots);
        Assert.Equal("<damage>", slot.Token);
        Assert.Equal("damage", slot.Kind);
        Assert.Equal(6, slot.Baseline);
        Assert.False(slot.Inverse);
        Assert.Empty(result.MissingSlotNames);
    }

    [Fact]
    public void ReplaceSentinelsHandlesDamageAndBlockAndRepeatedOccurrence()
    {
        var rendered =
            $"Gain {CardDescriptionTemplate.BlockSentinel} guard. Land {CardDescriptionTemplate.DamageSentinel} harm twice ({CardDescriptionTemplate.DamageSentinel}).";

        var result = CardDescriptionTemplate.ReplaceSentinels(rendered, [DamageSeed(), BlockSeed()]);

        Assert.Equal("Gain <block> guard. Land <damage> harm twice (<damage>).", result.Template);
        Assert.True(CardDescriptionTemplate.IsTemplatable(result));
        Assert.Equal(2, result.Slots.Count);
    }

    [Fact]
    public void ReplaceSentinelsReportsMissingSlotWhenSentinelConsumed()
    {
        // A word-only :plural() would swallow the number entirely: the sentinel never appears.
        var rendered = "Draw cards.";

        var result = CardDescriptionTemplate.ReplaceSentinels(rendered, [DamageSeed()]);

        Assert.Equal("Draw cards.", result.Template);
        Assert.False(CardDescriptionTemplate.IsTemplatable(result));
        Assert.Equal("damage", Assert.Single(result.MissingSlotNames));
        Assert.Empty(result.Slots);
    }

    [Fact]
    public void IsTemplatableFalseWhenNoSlots()
        => Assert.False(CardDescriptionTemplate.IsTemplatable(
            CardDescriptionTemplate.ReplaceSentinels("no numbers here", [])));

    [Fact]
    public void InverseFlagFlowsThroughToSlot()
    {
        var rendered = $"Cost {CardDescriptionTemplate.DamageSentinel}.";

        var result = CardDescriptionTemplate.ReplaceSentinels(rendered, [DamageSeed(inverse: true)]);

        Assert.True(Assert.Single(result.Slots).Inverse);
    }

    [Fact]
    public void DirtySignatureStableRegardlessOfPowerOrder()
    {
        var a = Signature(ownerPowers: [Pair("StrengthPower", 2), Pair("WeakPower", 1)]);
        var b = Signature(ownerPowers: [Pair("WeakPower", 1), Pair("StrengthPower", 2)]);

        Assert.Equal(a, b);
    }

    [Fact]
    public void DirtySignatureChangesWhenEnemyVulnerableChanges()
    {
        var before = Signature(enemies:
            [new EnemySignatureInput("creature:1", 0, 40, 40, [Pair("VulnerablePower", 0)])]);
        var after = Signature(enemies:
            [new EnemySignatureInput("creature:1", 0, 40, 40, [Pair("VulnerablePower", 2)])]);

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void DirtySignatureChangesWithOwnerVitals()
    {
        // BodySlam-style CalculatedVars scale off the owner's block/hp/gold — the cache must miss
        // when only those change, or the browser shows a stale number.
        var baseSig = Signature();
        Assert.NotEqual(baseSig, Signature(ownerBlock: 12));
        Assert.NotEqual(baseSig, Signature(ownerHp: 30));
        Assert.NotEqual(baseSig, Signature(ownerGold: 250));
    }

    [Fact]
    public void DirtySignatureChangesWhenEnemyHpChanges()
    {
        var before = Signature(enemies:
            [new EnemySignatureInput("creature:1", 0, 40, 40, [Pair("VulnerablePower", 0)])]);
        var after = Signature(enemies:
            [new EnemySignatureInput("creature:1", 0, 28, 40, [Pair("VulnerablePower", 0)])]);

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void DirtySignatureChangesWhenUpgradeOrEnchantChanges()
    {
        var baseSig = Signature();
        Assert.NotEqual(baseSig, Signature(upgradeLevel: 1));
        Assert.NotEqual(baseSig, Signature(enchantmentModelId: "TEZCATARAS_EMBER"));
        Assert.NotEqual(baseSig, Signature(energyCost: 0));
    }

    [Fact]
    public void DirtySignatureStableForIdenticalInputs()
        => Assert.Equal(Signature(), Signature());

    private static KeyValuePair<string, int> Pair(string key, int value) => new(key, value);

    private static string Signature(
        int upgradeLevel = 0,
        string? enchantmentModelId = null,
        int energyCost = 1,
        int ownerHp = 68,
        int ownerBlock = 0,
        int ownerGold = 99,
        IReadOnlyList<KeyValuePair<string, int>>? ownerPowers = null,
        IReadOnlyList<EnemySignatureInput>? enemies = null)
        => CardDescriptionTemplate.BuildDirtySignature(new CardComputeSignatureInputs(
            CardId: "42",
            UpgradeLevel: upgradeLevel,
            EnchantmentModelId: enchantmentModelId,
            EnergyCost: energyCost,
            OwnerCreatureId: "creature:owner",
            OwnerHp: ownerHp,
            OwnerMaxHp: 80,
            OwnerBlock: ownerBlock,
            OwnerGold: ownerGold,
            CardBaseVars: [Pair("Damage", 6)],
            OwnerPowers: ownerPowers ?? [Pair("StrengthPower", 0)],
            Enemies: enemies ?? [new EnemySignatureInput("creature:1", 0, 40, 40, [Pair("VulnerablePower", 0)])]));
}
