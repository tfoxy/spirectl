using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// WS-A phantom top-left potion — the pure unmapped-SubViewport prune predicate
// (Sts2ViewportContentPrune.ShouldPrune). A SubViewport the producer has NO viewport→screen prefix for streams its
// CanvasItem content at VIEWPORT-LOCAL coordinates, i.e. pinned at ~the design origin; NPotion.DoFlash() duplicates
// the whole potion subtree into exactly such a viewport (vfx_potion_flash.tscn, sampled only as a CPUParticles2D
// texture), which under the headless permanent particle freeze never finishes and never QueueFrees. Skipping the
// subtree keeps it off the wire. Truth-table over {enabled}x{prefixComputable}x{parentIsSubViewportContainer} plus
// the three named real-scene cases.
public sealed class Sts2ViewportContentPruneTests
{
    // The exact contract: prune ⇔ enabled AND no prefix AND the parent is not a SubViewportContainer.
    private static bool Expected(bool enabled, bool prefixComputable, bool parentIsSubViewportContainer)
        => enabled && !prefixComputable && !parentIsSubViewportContainer;

    [Fact]
    public void TruthTable_PruneOnlyWhenEnabledAndUnmappedAndNotContainerParented()
    {
        var bools = new[] { false, true };
        foreach (var enabled in bools)
        {
            foreach (var prefixComputable in bools)
            {
                foreach (var parentIsContainer in bools)
                {
                    var actual = Sts2ViewportContentPrune.ShouldPrune(enabled, prefixComputable, parentIsContainer);
                    var expected = Expected(enabled, prefixComputable, parentIsContainer);
                    Assert.True(
                        actual == expected,
                        $"enabled={enabled} prefixComputable={prefixComputable} parentIsSubViewportContainer={parentIsContainer}: "
                        + $"expected {expected}, got {actual}");
                }
            }
        }
    }

    [Fact]
    public void PotionFlashVfx_ParticleOnlyViewport_IsPruned()
    {
        // res://scenes/vfx/vfx_potion_flash.tscn: a 60x60 SubViewport whose ONLY reader is the "Flash"
        // CPUParticles2D (its texture is a ViewportTexture of it) — no Control/Sprite2D consumer resolves, and the
        // parent is the VFX root, not a SubViewportContainer. This is the live-confirmed phantom
        // (…/PotionHolder/Potion/VfxPotionFlash/Potion/Container/Image at ~(3,3)-(57,57)) → PRUNE.
        Assert.True(Sts2ViewportContentPrune.ShouldPrune(
            enabled: true, prefixComputable: false, parentIsSubViewportContainer: false));
    }

    [Fact]
    public void DoomAndCardEnchantVfx_UnmappedViewports_ArePruned_AcceptedBehaviourFlip()
    {
        // The same shape as the potion flash: vfx_doom.tscn (687x687 "Viewport") and vfx_card_enchant.tscn
        // (144x108 "EnchantmentViewport") also resolve no consumer. Their content flips from MISPLACED-AT-THE-ORIGIN
        // to ABSENT — an accepted improvement (it was never correctly placed), named in the landing commit.
        Assert.True(Sts2ViewportContentPrune.ShouldPrune(
            enabled: true, prefixComputable: false, parentIsSubViewportContainer: false));
    }

    [Fact]
    public void SubViewportContainerContent_IsNeverPruned_EvenWithoutAPrefix()
    {
        // The timeline epoch screens (scenes/timeline_screen/epoch.tscn, epoch_slot.tscn) put their SubViewport
        // under a SubViewportContainer, which DRAWS it — that content is genuinely on screen. Belt-and-braces: the
        // container prefix branch normally makes prefixComputable TRUE for these, but even if that math
        // conservatively bails, the exclusion keeps today's streaming behaviour instead of hiding the screen.
        Assert.False(Sts2ViewportContentPrune.ShouldPrune(
            enabled: true, prefixComputable: false, parentIsSubViewportContainer: true));
        Assert.False(Sts2ViewportContentPrune.ShouldPrune(
            enabled: true, prefixComputable: true, parentIsSubViewportContainer: true));
    }

    [Fact]
    public void MappedViewports_KeepStreaming()
    {
        // The prefix path — single-player map drawing, the multiplayer card intent, monster-death render textures —
        // is untouched by construction: a computable prefix never prunes.
        Assert.False(Sts2ViewportContentPrune.ShouldPrune(
            enabled: true, prefixComputable: true, parentIsSubViewportContainer: false));
    }

    [Fact]
    public void Disabled_KillSwitch_NeverPrunes()
    {
        // SPIRECTL_SCENE_WATCH_PRUNE_UNMAPPED_VIEWPORTS=0 → the pre-fix stream, byte-identical, for every combo.
        var bools = new[] { false, true };
        foreach (var prefixComputable in bools)
        {
            foreach (var parentIsContainer in bools)
            {
                Assert.False(Sts2ViewportContentPrune.ShouldPrune(enabled: false, prefixComputable, parentIsContainer));
            }
        }
    }
}
