using Spirectl.Sts2.Core.Combat;
using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2.Core.State;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// Guards the shapes the preview-core refactor must preserve. The live funnel (Sts2CombatPreviewCore)
// is engine-only, but its OUTPUT contract — CardPreviewSnapshot(Block, SelfDamage, DamageByTarget) —
// is shared with the ICombatPreviewProvider oracle and now the state provider, so it must stay stable.
public sealed class CombatPreviewContractTests
{
    [Fact]
    public void CardPreviewSnapshotExposesBlockSelfDamageAndPerTargetMatrix()
    {
        var snapshot = new CardPreviewSnapshot(
            Block: 5,
            SelfDamage: 6,
            DamageByTarget: new Dictionary<string, int> { ["creature:1"] = 9 });

        Assert.Equal(5, snapshot.Block);
        Assert.Equal(6, snapshot.SelfDamage);
        Assert.Equal(9, snapshot.DamageByTarget["creature:1"]);
    }

    [Fact]
    public void StateCombatCardPreviewEmbedsTheSameNumbersPlusSlots()
    {
        var preview = new StateCombatCardPreviewSnapshot(
            Block: 5,
            SelfDamage: 6,
            DamageByTarget: new Dictionary<string, int> { ["creature:1"] = 9 },
            Slots: [new StateCombatCardSlotSnapshot("<damage>", "damage", 6)],
            RenderedByTarget: null);

        Assert.Equal(6, preview.SelfDamage);
        Assert.Equal(9, preview.DamageByTarget["creature:1"]);
        Assert.Equal("<damage>", Assert.Single(preview.Slots).Token);
    }

    [Fact]
    public void NonLiveCombatPreviewProviderReportsUnavailable()
    {
        // The interface default (used off the live host) stays a stub failure — the refactor only moved
        // the funnel, not this contract.
        ICombatPreviewProvider provider = new StubCombatPreviewProvider();

        var result = provider.GetCombatPreview(new CombatPreviewRequestSnapshot());

        Assert.Equal("combat-preview-unavailable", result.Error?.Code);
        Assert.Equal(DataSourceKind.Stub, result.Source);
    }

    private sealed class StubCombatPreviewProvider : ICombatPreviewProvider;
}
