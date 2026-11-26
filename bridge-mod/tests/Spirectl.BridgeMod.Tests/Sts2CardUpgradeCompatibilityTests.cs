using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

public sealed class Sts2CardUpgradeCompatibilityTests
{
    [Fact]
    public void LegacyPreviewUpgradesOnlyTheClone()
    {
        var card = new LegacyCard();
        var preview = Sts2CardStateSnapshotFactory.CreateUpgradedPreview(card, "preview");

        Assert.Equal(1, preview.UpgradeLevel);
        Assert.Equal(12, preview.DynamicVars!["damage"]);
        Assert.Equal(0, card.CurrentUpgradeLevel);
    }

    [Fact]
    public void BooleanPreviewUpgradesSilentlyWithoutNotifyingCopiedListeners()
    {
        var notifications = 0;
        var card = new BooleanCard(() => notifications++);
        var preview = Sts2CardStateSnapshotFactory.CreateUpgradedPreview(card, "preview");

        Assert.Equal(1, preview.UpgradeLevel);
        Assert.Equal(12, preview.DynamicVars!["damage"]);
        Assert.Equal(0, card.CurrentUpgradeLevel);
        Assert.Equal(0, notifications);
    }

    [Fact]
    public void BooleanUpgradePopulatesNextDynamicVarsWithoutMutatingTheOriginal()
    {
        var notifications = 0;
        var card = new BooleanCard(() => notifications++);
        var snapshot = Sts2CardStateSnapshotFactory.Create(card, "card");

        Assert.Equal(8, snapshot.DynamicVars!["damage"]);
        Assert.Equal(12, snapshot.NextDynamicVars!["damage"]);
        Assert.Equal(0, card.CurrentUpgradeLevel);
        Assert.Equal(0, notifications);
    }

    [Fact]
    public void BothSignaturesAreHookTargetsAndPreviewPrefersTheBoolean()
    {
        var targets = Sts2CardUpgradeCompatibility.FindTargets(typeof(BothSignatures));
        Assert.Equal(2, targets.Count);
        Assert.Equal(typeof(bool), Assert.Single(targets[0].GetParameters()).ParameterType);
        Assert.Empty(targets[1].GetParameters());
        var card = new BothSignatures();
        Sts2CardUpgradeCompatibility.UpgradePreview(card);
        Assert.True(card.Silent);
        Assert.False(card.LegacyCalled);
    }

    [Fact]
    public void UnknownSignatureDoesNotUpgradeOrProduceAPartialPreview()
    {
        Assert.Empty(Sts2CardUpgradeCompatibility.FindTargets(typeof(UnknownSignature)));
        Assert.Throws<MissingMethodException>(() =>
            Sts2CardUpgradeCompatibility.UpgradePreview(new UnknownSignature()));
        var card = new UnknownSignature();
        var preview = Sts2CardStateSnapshotFactory.CreateUpgradedPreview(card, "preview");
        Assert.Equal(0, preview.UpgradeLevel);
        Assert.Equal(8, preview.DynamicVars!["damage"]);
        Assert.Null(preview.NextDynamicVars);
    }

    private abstract class PreviewCard
    {
        public int CurrentUpgradeLevel { get; protected set; }
        public bool IsUpgradable => CurrentUpgradeLevel == 0;
        public PreviewScope CardScope { get; } = new();
        public Dictionary<string, DynamicValue> DynamicVars => new()
        {
            ["Damage"] = new(CurrentUpgradeLevel == 0 ? 8 : 12),
        };
        public PreviewCard Clone() => (PreviewCard)MemberwiseClone();
    }

    private sealed class PreviewScope
    {
        public PreviewCard CloneCard(PreviewCard card) => card.Clone();
    }

    private sealed record DynamicValue(int BaseValue);

    private sealed class LegacyCard : PreviewCard
    {
        public void UpgradeInternal() => CurrentUpgradeLevel++;
    }

    private sealed class BooleanCard(Action notify) : PreviewCard
    {
        public void UpgradeInternal(bool silent = false)
        {
            CurrentUpgradeLevel++;
            if (!silent)
            {
                notify();
            }
        }
    }

    private sealed class BothSignatures
    {
        public bool Silent { get; private set; }
        public bool LegacyCalled { get; private set; }
        public void UpgradeInternal() => LegacyCalled = true;
        public void UpgradeInternal(bool silent) => Silent = silent;
        public void UpgradeInternal(string unrelated) => throw new InvalidOperationException();
    }

    private sealed class UnknownSignature : PreviewCard
    {
        public void UpgradeInternal(int unknown) => throw new InvalidOperationException();
    }
}
