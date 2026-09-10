using System.Collections.Generic;
using System.Linq;
using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// The geoclip ADMISSION rule's third arm, after it stopped being a mesh count.
//
// WHAT THIS REPLACED, and why an offline test of it has to demonstrate BOTH directions. The arm used to read
// `foreignMeshes > 0` — some validated mesh inside the RID bracket that no slot claimed — as a refusal in its own
// right. Because the two arms above it have already fired by then, it was only ever reached by a bake that
// COMPLETED with every drawable slot associated: a correct artifact, thrown away over geometry that had nothing
// to do with any of its claims.
//
// The counter-example is measured, not argued. A live `fight KNIGHTS_ELITE` on a real GPU (evidence archived at
// `.sts2/research/data/geoclip-knights-20260908T0500Z/`, receipt
// `legF-quiet-control/receipts/8213fbb1….json`, host log `legF-quiet-control/01-ironclad-attack.spine-geoclip.log`)
// baked an Ironclad at `anim=attack` to `complete=true`, `associated 44/44`, `byAtlasRegion=37 byColorFlip=7
// byElimination=0 ambiguousAtlasRegion=0` — and refused it on `arm=foreign foreignMeshes=8`. The same rig's
// `idle_loop` bakes clean at `foreign=0` in the same session, and a completely quiet combat reproduced the 8
// exactly, so the leftovers are the rig's own geometry for attachments that are not drawable at the sampled pose.
//
// So the rule now grades the CLAIMS. The numbers below are that recording's, and the negative cases are the same
// recording with one claim's provenance weakened — because a rule that admits everything would pass the positive
// direction and be worthless.
public sealed class Sts2SpineGeoClipOwnershipTests
{
    // ---- The proof ladder -----------------------------------------------------------------------------------

    // A colour flip is a MEASUREMENT on the running skeleton: this slot's colour was nudged and exactly this
    // mesh's vertex colours moved. Geometry that is not driven by this skeleton cannot answer it, so the pairing
    // holds however contaminated the bracket is.
    [Fact]
    public void AColourFlipIsAPositiveProof()
        => Assert.True(Sts2SpineGeoClipOwnership.IsPositiveProof(Sts2SpineGeoClipOwnership.ClaimColorFlip));

    // The atlas arms are graded on their METHOD and on nothing else. `uv-region-exact` means the mesh's uv box
    // agrees with the region this slot's attachment NAMES within the containment tolerance on every corner, which
    // is an identification. `containment` means it merely lands inside that region — which is exactly what a
    // foreign mesh that happens to fall in one of our regions would also satisfy.
    [Theory]
    [InlineData(Sts2SpineGeoClipAtlas.MatchExact, true)]
    [InlineData(Sts2SpineGeoClipAtlas.MatchContainment, false)]
    [InlineData(Sts2SpineGeoClipAtlas.MatchAmbiguous, false)]
    [InlineData(Sts2SpineGeoClipAtlas.MatchNone, false)]
    [InlineData(Sts2SpineGeoClipAtlas.MatchNoSuchRegion, false)]
    public void AnAtlasClaimIsGradedOnItsMatchMethod(string method, bool proven)
    {
        Assert.Equal(proven, Sts2SpineGeoClipOwnership.IsPositiveProof(
            Sts2SpineGeoClipOwnership.AtlasClaim(method)));
        // Same ladder whichever arm invoked the atlas. The co-moving set a colour flip narrows to is provably
        // ours, but WHICH member of it the atlas hands back is only as good as the atlas match.
        Assert.Equal(proven, Sts2SpineGeoClipOwnership.IsPositiveProof(
            Sts2SpineGeoClipOwnership.ColorFlipAtlasClaim(method)));
    }

    // Elimination is a claim over the LEFTOVER pool — "one slot and one mesh remain, so they pair". Sound only
    // when the pool is trusted, and a bracket with unclaimed geometry in it is precisely the untrusted case.
    [Fact]
    public void EliminationIsNeverAProof()
        => Assert.False(Sts2SpineGeoClipOwnership.IsPositiveProof(Sts2SpineGeoClipOwnership.ClaimElimination));

    // FAILS CLOSED. A claim minted by a path that forgot to record how it was made can only make a bake
    // stricter, never laxer — the alternative is a rule that a missing field silently disarms.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("unknown")]
    [InlineData("something-new")]
    [InlineData("atlas:")]
    [InlineData("uv-region-exact")]
    public void AnUnrecognisedProvenanceIsNotAProof(string? claim)
        => Assert.False(Sts2SpineGeoClipOwnership.IsPositiveProof(claim));

    // The breakdown that goes into the manifest and the log, and the first thing that has ever been able to say
    // WHICH evidence a rig's bake rests on. Busiest kind first so two bakes of the same rig read alike.
    [Fact]
    public void SummariseCountsEachProvenanceBusiestFirst()
    {
        var claims = Enumerable
            .Repeat(Sts2SpineGeoClipOwnership.AtlasClaim(Sts2SpineGeoClipAtlas.MatchExact), 37)
            .Concat(Enumerable.Repeat(Sts2SpineGeoClipOwnership.ClaimColorFlip, 7))
            .ToArray();

        Assert.Equal("atlas:uv-region-exact=37 color-flip=7", Sts2SpineGeoClipOwnership.Summarise(claims));
        Assert.Equal("unknown=2", Sts2SpineGeoClipOwnership.Summarise([null!, "  "]));
    }

    // ---- Direction ONE: the recorded Ironclad bake is admitted ----------------------------------------------

    // The whole point. Same counters the live refusal was derived from; the arm no longer fires.
    [Fact]
    public void TheRecordedIroncladAttackBakeIsAdmitted()
    {
        var reason = Reason(IroncladAttackClaims(atlasMethod: Sts2SpineGeoClipAtlas.MatchExact));

        Assert.Null(reason);
    }

    // …and the kill switch puts the old verdict back, byte for byte, on exactly those counters. This is the
    // property that makes a regression one environment variable away from being isolated.
    [Fact]
    public void TheKillSwitchRestoresTheForeignArmOnTheSameCounters()
    {
        var reason = Sts2SpineGeoClipRequestLane.IncompletenessReason(
            IroncladComplete,
            IroncladAssociated,
            IroncladSlotsVisible,
            IroncladForeignMeshes,
            claimsProven: 44,
            claimsUnproven: 0,
            ownershipProofArmed: false);

        Assert.Equal("foreignMeshes=8", reason);
        Assert.Equal("foreign", Sts2SpineGeoClipRequestLane.ClassifyRefusal(reason));
        Assert.False(Sts2SpineGeoClipOwnership.ParseArmed("0"));
        Assert.False(Sts2SpineGeoClipOwnership.ParseArmed("off"));
        // Absent, empty and misspelt all ARM it — the same reading every other geoclip kill switch takes.
        Assert.True(Sts2SpineGeoClipOwnership.ParseArmed(null));
        Assert.True(Sts2SpineGeoClipOwnership.ParseArmed("nonsense"));
    }

    // ---- Direction TWO: a weak claim still refuses -----------------------------------------------------------

    // THE NEGATIVE THE RULE IS WORTH NOTHING WITHOUT. Identical counters to the admitted bake — complete, 44 of
    // 44 associated, 8 leftovers — with ONE claim's provenance weakened. Each of the three weak provenances is
    // exercised, because they are three different ways to reach the same unsound pairing.
    [Theory]
    [InlineData(Sts2SpineGeoClipOwnership.ClaimElimination)]
    [InlineData("atlas:" + Sts2SpineGeoClipAtlas.MatchContainment)]
    [InlineData("color-flip+atlas:" + Sts2SpineGeoClipAtlas.MatchContainment)]
    [InlineData(Sts2SpineGeoClipOwnership.ClaimUnknown)]
    public void OneWeakClaimStillRefusesTheSameBake(string weakClaim)
    {
        var claims = IroncladAttackClaims(Sts2SpineGeoClipAtlas.MatchExact);
        claims[0] = weakClaim;

        var reason = Reason(claims);

        Assert.Equal("ownership=1 of claimed=44", reason);
        Assert.Equal("ownership", Sts2SpineGeoClipRequestLane.ClassifyRefusal(reason));
    }

    // The unmeasured half of the live recording, stated as a test rather than as a hope. The host log records
    // `byAtlasRegion=37` but not whether those 37 matched EXACTLY or merely by containment — the armed
    // atlas-first arm did not report its per-match method until this change added `claimProof`. If they are
    // containment, this bake is still refused, and the ownership arm names why. The two cases are pinned side by
    // side so the next live bake's `claimProof` line settles it in one reading.
    [Fact]
    public void TheSameBakeIsRefusedIfItsAtlasClaimsAreOnlyContainment()
    {
        var reason = Reason(IroncladAttackClaims(atlasMethod: Sts2SpineGeoClipAtlas.MatchContainment));

        Assert.Equal("ownership=37 of claimed=44", reason);
        Assert.Equal("ownership", Sts2SpineGeoClipRequestLane.ClassifyRefusal(reason));
    }

    // MISSING evidence is not clean evidence. A pose snapshot from a path that carries no provenance (an older
    // durable receipt, a host seam not yet taught the counters) reaches the legacy arm unchanged rather than
    // being waved through — which is what stops this change from relaxing anything it cannot actually see.
    [Fact]
    public void ABakeWithNoRecordedProvenanceKeepsTheForeignArm()
    {
        var reason = Sts2SpineGeoClipRequestLane.IncompletenessReason(
            IroncladComplete,
            IroncladAssociated,
            IroncladSlotsVisible,
            IroncladForeignMeshes,
            claimsProven: 0,
            claimsUnproven: 0,
            ownershipProofArmed: true);

        Assert.Equal("foreignMeshes=8", reason);
        Assert.Equal("foreign", Sts2SpineGeoClipRequestLane.ClassifyRefusal(reason));
    }

    // A weak claim with NOTHING unclaimed in the bracket is not refused, and that is deliberate. With no
    // leftovers the claims exhaust the validated mesh pool, so there is no unclaimed mesh a weak claim could have
    // taken instead — an elimination over an exhausted pool is forced, not guessed. It is also the direction that
    // cannot regress a bake this rule was never asked about: every `foreign=0` artifact admitted before this
    // change is still admitted after it. The strict reading is reported as a shadow log line by the baker so a
    // later round can measure what enforcing it would cost, rather than arguing for it.
    [Fact]
    public void AWeakClaimWithNoLeftoversIsStillAdmitted()
    {
        var reason = Sts2SpineGeoClipRequestLane.IncompletenessReason(
            complete: true,
            associated: 44,
            slotsVisible: 44,
            foreignMeshes: 0,
            claimsProven: 43,
            claimsUnproven: 1,
            ownershipProofArmed: true);

        Assert.Null(reason);
    }

    // ---- The arms above it are untouched ---------------------------------------------------------------------

    // The flail knight and the spectral knight in the same recording refuse on the FIRST arm, on their own
    // counters (`legF-quiet-control/receipts/137ebe5d….json` and `e1f74ecb….json`, both `complete=false`), and
    // the ownership counters must not reorder that: an incomplete bake's association numbers are not evidence
    // about anything, so `complete=false` is still what gets reported.
    [Theory]
    [InlineData(40, 43, 3)] // flail knight, attack_flail
    [InlineData(19, 21, 3)] // spectral knight, attack_sword
    public void AnIncompleteBakeStillRefusesOnTheFirstArm(int associated, int slotsVisible, int foreign)
    {
        var reason = Sts2SpineGeoClipRequestLane.IncompletenessReason(
            complete: false,
            associated,
            slotsVisible,
            foreign,
            claimsProven: associated,
            claimsUnproven: 0,
            ownershipProofArmed: true);

        Assert.Equal("complete=false", reason);
        Assert.Equal("incomplete", Sts2SpineGeoClipRequestLane.ClassifyRefusal(reason));
    }

    // And the flail knight's own association shape — 40 of the 43 slots it shows — refuses on the SECOND arm
    // when the bake does complete, proofs or no proofs. An unassociated slot makes a leftover count unreadable,
    // so the ownership question is not even asked.
    [Fact]
    public void AnUnassociatedSlotStillRefusesOnTheSecondArm()
    {
        var reason = Sts2SpineGeoClipRequestLane.IncompletenessReason(
            complete: true,
            associated: 40,
            slotsVisible: 43,
            foreignMeshes: 3,
            claimsProven: 40,
            claimsUnproven: 0,
            ownershipProofArmed: true);

        Assert.Equal("associated=40 of slotsEverVisible=43", reason);
        Assert.Equal("unassociated", Sts2SpineGeoClipRequestLane.ClassifyRefusal(reason));
    }

    // The magi knight, the one creature in the recording that baked at 200: complete, 19 of 19, no leftovers.
    // Admitted before this change and admitted after it, under both readings of the lever.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheOneBakeThatAlreadySucceededIsUnaffected(bool armed)
        => Assert.Null(Sts2SpineGeoClipRequestLane.IncompletenessReason(
            complete: true,
            associated: 19,
            slotsVisible: 19,
            foreignMeshes: 0,
            claimsProven: 19,
            claimsUnproven: 0,
            ownershipProofArmed: armed));

    // ---- The recorded bake ------------------------------------------------------------------------------------

    private const bool IroncladComplete = true;

    private const int IroncladAssociated = 44;

    private const int IroncladSlotsVisible = 44;

    private const int IroncladForeignMeshes = 8;

    private const int IroncladByAtlasRegion = 37;

    private const int IroncladByColorFlip = 7;

    /// <summary>
    /// The 44 claims the recorded Ironclad `attack` bake made, in the mix its host log reports:
    /// <c>byAtlasRegion=37 byColorFlip=7 byElimination=0 byColorFlipPlusAtlas=0</c>. The atlas half's MATCH
    /// METHOD is the parameter, because the recording does not carry it.
    /// </summary>
    private static string[] IroncladAttackClaims(string atlasMethod)
    {
        var claims = new List<string>(IroncladAssociated);
        claims.AddRange(Enumerable.Repeat(
            Sts2SpineGeoClipOwnership.AtlasClaim(atlasMethod), IroncladByAtlasRegion));
        claims.AddRange(Enumerable.Repeat(Sts2SpineGeoClipOwnership.ClaimColorFlip, IroncladByColorFlip));
        Assert.Equal(IroncladAssociated, claims.Count);
        return [.. claims];
    }

    // The rule, driven from CLAIMS rather than from a hand-written proven/unproven pair — so the ladder above
    // and the arm below are exercised as one thing, the way the baker rolls them up.
    private static string? Reason(IReadOnlyList<string> claims)
    {
        var proven = claims.Count(Sts2SpineGeoClipOwnership.IsPositiveProof);
        return Sts2SpineGeoClipRequestLane.IncompletenessReason(
            IroncladComplete,
            IroncladAssociated,
            IroncladSlotsVisible,
            IroncladForeignMeshes,
            claimsProven: proven,
            claimsUnproven: claims.Count - proven,
            ownershipProofArmed: true);
    }
}
