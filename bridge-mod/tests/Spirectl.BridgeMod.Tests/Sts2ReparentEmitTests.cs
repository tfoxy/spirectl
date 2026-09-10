using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// P2 discard-select flash — the pure reparent-emit transform-omission predicate (Sts2ReparentEmit.OmitTransformOnReparent).
// A reparent force-emit that fires WHILE a streaming-suppression window covers the node must ship its re-attach
// upsert WITHOUT a transform so the client holds the node's last-known placed local (renders at centre) instead of
// pinning the transition-START value (the bottom-centre flash). Truth-table over
// {enabled}×{parentChanged}×{suppressed}×{justAdded}, plus a narrative axis showing the decision is independent of
// whether OTHER volatile props changed (the emit still ships; only the transform is dropped).
public sealed class Sts2ReparentEmitTests
{
    // The exact contract: omit ⇔ enabled AND a reparent AND inside a suppression window AND not a fresh add.
    private static bool Expected(bool enabled, bool parentChanged, bool suppressed, bool justAdded)
        => enabled && parentChanged && suppressed && !justAdded;

    [Fact]
    public void TruthTable_OmitOnlyWhenEnabledReparentSuppressedNotJustAdded()
    {
        var bools = new[] { false, true };
        foreach (var enabled in bools)
        foreach (var parentChanged in bools)
        foreach (var suppressed in bools)
        foreach (var justAdded in bools)
        {
            var actual = Sts2ReparentEmit.OmitTransformOnReparent(enabled, parentChanged, suppressed, justAdded);
            var expected = Expected(enabled, parentChanged, suppressed, justAdded);
            Assert.True(
                actual == expected,
                $"enabled={enabled} parentChanged={parentChanged} suppressed={suppressed} justAdded={justAdded}: expected {expected}, got {actual}");
        }
    }

    [Fact]
    public void Select_ReparentUnderOpenWindow_OmitsTransform()
    {
        // The discard SELECT: a hand card reparents into the fresh centre holder while the container lifts on a tween
        // (window open); the card is not a fresh add → OMIT (client holds last-known local (0,0) = centre).
        Assert.True(Sts2ReparentEmit.OmitTransformOnReparent(enabled: true, parentChanged: true, suppressTransform: true, justAdded: false));
    }

    [Fact]
    public void Deselect_ReparentNoWindow_ShipsTransform()
    {
        // The DESELECT: reparent back to a fresh hand holder, NO tween ⇒ no suppression window → keep the transform
        // (the client re-parents AND re-places in one upsert; byte-identical to round-3).
        Assert.False(Sts2ReparentEmit.OmitTransformOnReparent(enabled: true, parentChanged: true, suppressTransform: false, justAdded: false));
    }

    [Fact]
    public void FreshAdd_UnderWindow_ShipsTransform()
    {
        // A brand-new node (JustAdded) has no last-known transform to hold — it MUST ship its initial placement even
        // inside a window, else it strands at the origin. Never omit for a fresh add.
        Assert.False(Sts2ReparentEmit.OmitTransformOnReparent(enabled: true, parentChanged: true, suppressTransform: true, justAdded: true));
    }

    [Fact]
    public void NonReparentEmit_UnderWindow_ShipsTransform()
    {
        // A normal emit that is NOT a reparent (parentChanged=false) never omits, even under a window — only the
        // reparent force-emit is the leak this fix plugs. (A suppressed node's transform is dropped by the change
        // test elsewhere; this predicate is scoped to the reparent path.)
        Assert.False(Sts2ReparentEmit.OmitTransformOnReparent(enabled: true, parentChanged: false, suppressTransform: true, justAdded: false));
    }

    [Fact]
    public void Disabled_KillSwitch_NeverOmits()
    {
        // SPIRECTL_SCENE_WATCH_REPARENT_HOLD_TWEENED=0 → the round-3 ship-the-transform behaviour for every combo.
        var bools = new[] { false, true };
        foreach (var parentChanged in bools)
        foreach (var suppressed in bools)
        foreach (var justAdded in bools)
        {
            Assert.False(Sts2ReparentEmit.OmitTransformOnReparent(enabled: false, parentChanged, suppressed, justAdded));
        }
    }

    [Fact]
    public void OmitDecision_IsIndependentOfOtherVolatileChanges()
    {
        // The "volatile-changed" axis: whether OTHER props (texture/opacity/…) changed does NOT enter the predicate.
        // The reparent still EMITS (parentChanged forces it); the transform is dropped either way, so a texture swap
        // that coincides with the suppressed reparent ships its texture but not its transform.
        var omitA = Sts2ReparentEmit.OmitTransformOnReparent(enabled: true, parentChanged: true, suppressTransform: true, justAdded: false);
        var omitB = Sts2ReparentEmit.OmitTransformOnReparent(enabled: true, parentChanged: true, suppressTransform: true, justAdded: false);
        Assert.True(omitA);
        Assert.Equal(omitA, omitB);
    }
}
