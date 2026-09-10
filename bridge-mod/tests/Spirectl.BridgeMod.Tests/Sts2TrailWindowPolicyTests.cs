using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// R14 — whether a card flight opens the SELF-ONLY suppression window on its trail VFX root, which is what stops that
// root re-sending its own transform for the flight's lifetime.
//
// The rule has three inputs and therefore exactly eight cases, all enumerated below, because the interesting half of
// it is a DEFAULT: the mirror runs in LOCAL emit mode, so before R14 the R13 feature silently never engaged in
// production. The truth table is the contract that the local-mode opt-in changes that and nothing else — in
// particular that an embedder raising the local-mode lever alone (feature switch off) still opens nothing, and that
// GLOBAL mode ignores the new lever in both positions.
public sealed class Sts2TrailWindowPolicyTests
{
    [Theory]
    // trailSelfSuppress OFF — the feature's own kill switch wins over everything, in either emit mode and
    // regardless of the local-mode opt-in.
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, true, false)]
    // GLOBAL emit mode, feature ON — opens, and the local-mode lever is not consulted (both positions agree).
    [InlineData(true, false, false, true)]
    [InlineData(true, false, true, true)]
    // LOCAL emit mode, feature ON — the opt-in decides. Default (off) keeps the root streaming, which is the
    // behaviour every pre-R14 consumer depends on.
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, true)]
    public void ShouldOpenTrailSelfWindow_TruthTable(
        bool trailSelfSuppress, bool emitLocalTransforms, bool localModeLever, bool expected)
    {
        Assert.Equal(
            expected,
            Sts2TrailWindowPolicy.ShouldOpenTrailSelfWindow(trailSelfSuppress, emitLocalTransforms, localModeLever));
    }

    [Fact]
    public void LocalModeLeverAloneNeverOpensTheWindow()
    {
        // Guards the ordering of the two levers: the local-mode opt-in EXTENDS the feature, it does not re-enable a
        // feature its own kill switch has turned off. An embedder that ships the capability vote must not be able to
        // resurrect the window on a process where the operator disabled trail self-suppression outright.
        Assert.False(Sts2TrailWindowPolicy.ShouldOpenTrailSelfWindow(
            trailSelfSuppress: false, emitLocalTransforms: true, localModeLever: true));
    }

    [Fact]
    public void GlobalModeIsUnchangedByTheNewLever()
    {
        // R13's shipped behaviour in global mode must be byte-identical after R14, so the new input cannot be
        // observable there.
        Assert.Equal(
            Sts2TrailWindowPolicy.ShouldOpenTrailSelfWindow(true, emitLocalTransforms: false, localModeLever: false),
            Sts2TrailWindowPolicy.ShouldOpenTrailSelfWindow(true, emitLocalTransforms: false, localModeLever: true));
    }

    [Fact]
    public void DefaultSettingsLeaveTheLocalModeExtensionOff()
    {
        // The shipped default is the conservative one: a process nobody configured behaves exactly as R13 did.
        Assert.False(Sts2SceneWatchRuntimeSettings.TrailSelfSuppressLocalMode);
    }
}
