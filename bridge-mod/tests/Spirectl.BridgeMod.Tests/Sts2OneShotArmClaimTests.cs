using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// The process-global single-arm claim that keeps ONE env var from arming TWO copies of this runtime. The hazard it
// exists for needs a game and a two-mod loadout to reproduce, but the rule itself is pure: given the subsystem's
// arming spec, whoever is parked in the claim slot, and this copy's identity, should this copy arm? That truth
// table is exercised here directly. The impure half (AppDomain Get/SetData) is exercised through TryClaim, which
// is safe in-process because each test uses its own subsystem name — the slot is keyed by it.
public sealed class Sts2OneShotArmClaimTests
{
    // ── The arming truth table ───────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ShouldArm_WithoutASpec_IsNotRequested(string? spec)
    {
        var verdict = Sts2OneShotArmClaim.ShouldArm("spine-geoclip-bake", spec, existingOwner: null, "Spirectl.Sts2");

        Assert.Equal(OneShotArmDecision.NotRequested, verdict.Decision);
        Assert.Null(verdict.Owner);
    }

    [Fact]
    public void ShouldArm_WithASpecAndAFreeSlot_Arms()
    {
        var verdict = Sts2OneShotArmClaim.ShouldArm(
            "spine-geoclip-bake",
            "res://a.tscn?anim=idle_loop",
            existingOwner: null,
            "Spirectl.Sts2");

        Assert.Equal(OneShotArmDecision.Arm, verdict.Decision);
    }

    // The whole point: the OTHER copy got there first, and the verdict names it so the caller can log who won.
    [Fact]
    public void ShouldArm_WhenAnotherAssemblyIdentityOwnsTheSlot_StandsDownAndNamesTheOwner()
    {
        var verdict = Sts2OneShotArmClaim.ShouldArm(
            "spine-geoclip-bake",
            "res://a.tscn?anim=idle_loop",
            existingOwner: "CouchCoop.Spirectl",
            "Spirectl.Sts2");

        Assert.Equal(OneShotArmDecision.StandDown, verdict.Decision);
        Assert.Equal("CouchCoop.Spirectl", verdict.Owner);
    }

    // Re-entry by the copy that already owns the claim is NOT a stand-down — it is what the subsystem's existing
    // per-assembly `_installed` latch already means, and preserving that keeps the claim a purely additional outer
    // guard rather than a behaviour change for a host that only ever loads one copy.
    [Fact]
    public void ShouldArm_WhenThisAssemblyIdentityAlreadyOwnsTheSlot_IsNotRequestedAndStillNamesItself()
    {
        var verdict = Sts2OneShotArmClaim.ShouldArm(
            "spine-geoclip-bake",
            "res://a.tscn?anim=idle_loop",
            existingOwner: "Spirectl.Sts2",
            "Spirectl.Sts2");

        Assert.Equal(OneShotArmDecision.NotRequested, verdict.Decision);
        Assert.Equal("Spirectl.Sts2", verdict.Owner);
    }

    // Ordinal, not case-insensitive: an assembly name that differs only by case is a different assembly.
    [Fact]
    public void ShouldArm_ComparesOwnersOrdinally()
    {
        var verdict = Sts2OneShotArmClaim.ShouldArm(
            "spine-geoclip-bake",
            "spec",
            existingOwner: "spirectl.sts2",
            "Spirectl.Sts2");

        Assert.Equal(OneShotArmDecision.StandDown, verdict.Decision);
    }

    // ── The claim slot ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TryClaim_GivesTheSlotToTheFirstCallerAndStandsTheSecondOneDown()
    {
        var subsystem = NewSubsystem();

        Assert.True(Sts2OneShotArmClaim.TryClaim(subsystem, "Spirectl.Sts2", out var firstOwner));
        Assert.Null(firstOwner);

        Assert.False(Sts2OneShotArmClaim.TryClaim(subsystem, "CouchCoop.Spirectl", out var secondOwner));
        Assert.Equal("Spirectl.Sts2", secondOwner);
    }

    // The slot is keyed per subsystem, so arming the baker must not disarm the probe: they are separate one-shots
    // that an operator can legitimately run in the same launch.
    [Fact]
    public void TryClaim_KeepsSubsystemsIndependent()
    {
        var baker = NewSubsystem();
        var probe = NewSubsystem();

        Assert.True(Sts2OneShotArmClaim.TryClaim(baker, "Spirectl.Sts2", out _));
        Assert.True(Sts2OneShotArmClaim.TryClaim(probe, "Spirectl.Sts2", out _));
    }

    [Fact]
    public void TryClaim_LetsTheOwnerSeeItselfRatherThanReportingNobody()
    {
        var subsystem = NewSubsystem();

        Assert.True(Sts2OneShotArmClaim.TryClaim(subsystem, "Spirectl.Sts2", out _));

        Assert.False(Sts2OneShotArmClaim.TryClaim(subsystem, "Spirectl.Sts2", out var owner));
        Assert.Equal("Spirectl.Sts2", owner);
    }

    // ── Why the identity is a STRING and not a Type ───────────────────────────────────────────────────

    // This is the reason the whole file exists. The two copies of this runtime are two ASSEMBLIES: an embedder
    // compiles the same source under `AssemblyName=CouchCoop.Spirectl`. Nothing type-shaped survives that split —
    // `typeof(X)` in one copy is a different Type object, with a different assembly-qualified name, from
    // `typeof(X)` in the other, so a Type (or a static field, or anything keyed on either) can never be the shared
    // identity. A `const string` literal compiles to the same characters in both, which is why the claim key is
    // one, and why it must never be derived from an assembly-qualified name.
    [Fact]
    public void TheClaimKeyIsAPlainStringBecauseATypeWouldNotSurviveTheAssemblyIdentitySplit()
    {
        var typeName = typeof(Sts2OneShotArmClaim).AssemblyQualifiedName!;

        // The key carries no assembly identity of its own...
        Assert.Equal("spirectl.one-shot-arm/", Sts2OneShotArmClaim.ClaimKeyPrefix);
        Assert.DoesNotContain("Spirectl.Sts2", Sts2OneShotArmClaim.ClaimKeyPrefix, StringComparison.Ordinal);

        // ...whereas anything reached through the Type does, which is exactly what differs between the two copies.
        Assert.Contains("Spirectl.Sts2", typeName, StringComparison.Ordinal);
    }

    // The owner, by contrast, IS read off the assembly — deliberately, because the assembly name is the identity
    // that split the statics. In this test run that is the bridge's own name.
    [Fact]
    public void SelfOwnerIsTheAssemblySimpleName()
        => Assert.Equal("Spirectl.Sts2", Sts2OneShotArmClaim.SelfOwner);

    // A fresh slot per test: the claim is process-global by design and xunit runs a class's tests in one process,
    // so tests that must start unclaimed cannot share a subsystem name.
    private static string NewSubsystem() => $"test-{Guid.NewGuid():N}";
}
