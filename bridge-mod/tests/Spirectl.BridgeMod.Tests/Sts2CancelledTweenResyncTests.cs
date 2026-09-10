using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// WS-REST rest-site "description stuck invisible" — the pure cancelled-tween opacity-resync predicate
// (Sts2CancelledTweenResync.ShouldForceOpacityResync). A killed fade-IN never opened an opacity-suppression window,
// so CancelTweenSuppression's collapse-open-window path is a no-op; this predicate decides when the watcher should
// instead FORCE one settled opacity re-emit of the node's live alpha (window-less) so a hint-less re-show un-sticks.
// Truth-table over {enabled}×{hasOpacityStep}×{endAlphaVisible}, plus the narrative fade-IN vs fade-OUT axis.
public sealed class Sts2CancelledTweenResyncTests
{
    // The exact contract: force ⇔ enabled AND a killed opacity step AND its end alpha is visible (a fade-IN).
    private static bool Expected(bool enabled, bool hasOpacityStep, bool endAlphaVisible)
        => enabled && hasOpacityStep && endAlphaVisible;

    [Fact]
    public void TruthTable_ForceOnlyWhenEnabledOpacityStepVisibleEnd()
    {
        var bools = new[] { false, true };
        foreach (var enabled in bools)
        foreach (var hasOpacityStep in bools)
        foreach (var endAlphaVisible in bools)
        {
            var actual = Sts2CancelledTweenResync.ShouldForceOpacityResync(enabled, hasOpacityStep, endAlphaVisible);
            var expected = Expected(enabled, hasOpacityStep, endAlphaVisible);
            Assert.True(
                actual == expected,
                $"enabled={enabled} hasOpacityStep={hasOpacityStep} endAlphaVisible={endAlphaVisible}: expected {expected}, got {actual}");
        }
    }

    [Fact]
    public void KilledFadeIn_ForcesResync()
    {
        // The rest-site defect: a re-focused option's description fade-IN (end alpha ≈ 1) is killed by the unfocus
        // before Finalize → force a settled opacity re-emit so the client gets a fresh, un-suppressed alpha write.
        Assert.True(Sts2CancelledTweenResync.ShouldForceOpacityResync(enabled: true, hasOpacityStep: true, endAlphaVisible: true));
    }

    [Fact]
    public void KilledFadeOut_DoesNotForceResync()
    {
        // A killed fade-OUT (end alpha ≈ 0) is going hidden — there is no restore to un-stick, and forcing a re-emit
        // would just re-ship the ≈0 the hide already covers. Never force it.
        Assert.False(Sts2CancelledTweenResync.ShouldForceOpacityResync(enabled: true, hasOpacityStep: true, endAlphaVisible: false));
    }

    [Fact]
    public void KilledTransformOnlyTween_DoesNotForceResync()
    {
        // A killed move/scale tween with NO opacity step never needs an opacity resync (the existing transform-window
        // collapse handles it). Only the opacity channel is at issue here.
        Assert.False(Sts2CancelledTweenResync.ShouldForceOpacityResync(enabled: true, hasOpacityStep: false, endAlphaVisible: true));
        Assert.False(Sts2CancelledTweenResync.ShouldForceOpacityResync(enabled: true, hasOpacityStep: false, endAlphaVisible: false));
    }

    [Fact]
    public void Disabled_KillSwitch_NeverForces()
    {
        // SPIRECTL_SCENE_WATCH_CANCELLED_RISE_RESYNC=0 → byte-identical to today (a killed tween only collapses an
        // already-open window, never forces a from-cold re-emit) for every combo.
        var bools = new[] { false, true };
        foreach (var hasOpacityStep in bools)
        foreach (var endAlphaVisible in bools)
        {
            Assert.False(Sts2CancelledTweenResync.ShouldForceOpacityResync(enabled: false, hasOpacityStep, endAlphaVisible));
        }
    }
}
