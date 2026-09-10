using System.Collections.Generic;
using System.Linq;
using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// NOT EXPECTING A SLOT THAT DRAWS NOTHING — and the single-pose boundary that is the whole safety argument.
//
// THE DEFECT, MEASURED. A live `fight KNIGHTS_ELITE` on a real GPU refused the spectral knight at
// `anim=attack_sword` on the FIRST admission arm: `complete=false`, `associated 19/21`, `ambiguousAtlasRegion=2`
// (evidence `.sts2/research/data/geoclip-knights-stage2a-20260908T150649Z/legs/leg2-attack/`
// `03-spectral_knight-attack_sword.spine-geoclip.log`, reproduced in an independent process in
// `legs/legR-repeat-instB/` and in the Stage-0 control `geoclip-knights-20260908T0500Z/legF-quiet-control/`).
// The two unassociated slots are ordinals 29 and 30, both showing the `sword swish` attachment.
//
// WHAT THE TWO SLOTS ACTUALLY CARRY, read rather than inferred. The archived manifest for that bake —
// `geoclip-knights-20260908T0500Z/envbake-artifacts/legE-combat/spectral_knight--Visuals--attack_sword/`
// `manifest.json`, `frames[0].slots` — records `"29": {"part": null, "color": [1,1,1,0]}` and the same for
// `"30"`. That `[1,1,1,0]` is a READ colour and cannot be a capture failure: an unreadable slot colour is
// recorded as WHITE, `[1,1,1,1]`, so an alpha of 0 could only have come off the skeleton. (The same two colours
// were already pinned independently as a fixture in Sts2SpineGeoClipSlotColorTests, from the same recording.)
//
// SO THE REFUSAL IS OVER GEOMETRY THAT WOULD HAVE DRAWN NOTHING. Alpha 0 multiplies the attachment away; the
// baker already emits both slots as hidden ("slot 29 shows attachment 'sword swish' but has no associated mesh,
// so it is baked as hidden for every frame"), and a hidden slot and a slot drawn at alpha 0 are the same
// picture. The artifact being thrown away is pixel-identical to the one that would have been kept.
//
// AND THE BOUNDARY THAT KEEPS THIS LEGAL. The 2026-09-06 correction contract
// (`.sts2/research/data/knights-sequential-20260906T065302Z/report.md`) says: "Do not infer non-drawing status
// from a failed search, missing atlas name, zero alpha or failed association." Every clause of that still holds
// here except the alpha one, and only for a bake that sampled exactly ONE pose — because "transparent here,
// opaque at the next frame" is a real thing for a clip and is not a thing that exists for a single frozen pose.
// The multi-frame case is therefore the test in this file that matters most: same shape, more than one pose,
// still refused.
public sealed class Sts2SpineGeoClipPoseTransparencyTests
{
    // ---- (1) The recorded spectral shape, at ONE pose ---------------------------------------------------------

    // 21 slots show a drawable attachment; ordinals 29 and 30 are read at (1,1,1,0). The bake is graded on 19.
    [Fact]
    public void ASlotReadFullyTransparentAtTheOneSampledPoseIsNotExpectedToDraw()
    {
        var expectation = Sts2SpineGeoClipPoseTransparency.Expect(SpectralAttackSword(poses: 1), armed: true);

        Assert.Equal(19, expectation.Ordinals.Count);
        Assert.Equal<int>([29, 30], expectation.TransparentOrdinals);
        Assert.DoesNotContain(29, expectation.Ordinals);
        Assert.DoesNotContain(30, expectation.Ordinals);
        Assert.True(expectation.SinglePose);
    }

    // …and that is what turns the live refusal into a complete bake. `complete` is `associated == expected`, and
    // the 19 the recording associated are exactly the 19 that are expected.
    [Fact]
    public void TheRecordedSpectralBakeCompletesOnceTheTransparentSlotsAreNotExpected()
    {
        var expectation = Sts2SpineGeoClipPoseTransparency.Expect(SpectralAttackSword(poses: 1), armed: true);
        var associated = expectation.Ordinals.Count(SpectralAssociatedOrdinals.Contains);

        Assert.Equal(SpectralAssociated, associated);
        Assert.True(associated >= expectation.Ordinals.Count, "the bake is complete");
        Assert.Equal(0, expectation.Ordinals.Count - associated);
    }

    // ---- (2) THE SCOPING TEST. The same shape, more than one pose, unchanged ----------------------------------

    // THE ONE THAT MATTERS MOST. Identical slots, identical colours — read at alpha 0 at EVERY sampled pose, so
    // not even a "but it draws later" escape is being relied on — and the expectation is still all 21. A clip
    // can show a slot at another frame, and no sample of it licenses a statement about the rest; the contract's
    // rule stands untouched for every bake with more than one pose in it.
    [Theory]
    [InlineData(2)]
    [InlineData(18)]
    public void TheSameShapeAsAMultiFrameBakeStillExpectsEveryDrawableSlot(int poses)
    {
        var expectation = Sts2SpineGeoClipPoseTransparency.Expect(SpectralAttackSword(poses), armed: true);

        Assert.Equal(21, expectation.Ordinals.Count);
        Assert.Empty(expectation.TransparentOrdinals);
        Assert.Contains(29, expectation.Ordinals);
        Assert.Contains(30, expectation.Ordinals);
        Assert.False(expectation.SinglePose);
    }

    // …and it therefore still REFUSES, on the same first arm and with the same counters the live bake reported.
    // This is the assertion a later agent's "simplification" has to break in order to land.
    [Fact]
    public void TheSameShapeAsAMultiFrameBakeStillRefusesOnTheFirstArm()
    {
        var expectation = Sts2SpineGeoClipPoseTransparency.Expect(SpectralAttackSword(poses: 2), armed: true);
        var associated = expectation.Ordinals.Count(SpectralAssociatedOrdinals.Contains);
        var complete = associated >= expectation.Ordinals.Count;

        Assert.False(complete);
        var reason = Sts2SpineGeoClipRequestLane.IncompletenessReason(
            complete,
            associated,
            expectation.Ordinals.Count,
            SpectralForeignMeshes,
            claimsProven: SpectralClaimsProven,
            claimsUnproven: SpectralClaimsUnproven,
            ownershipProofArmed: true);

        Assert.Equal("complete=false", reason);
        Assert.Equal("incomplete", Sts2SpineGeoClipRequestLane.ClassifyRefusal(reason));
    }

    // ---- (3) An UNREAD colour keeps the slot expected ---------------------------------------------------------

    // The reason PoseSlot.ColorReadSucceeded exists at all. A capture that could not read a colour records WHITE
    // — which is a real colour some slot genuinely has — so a rule that trusted the value would turn a capture
    // failure into a dropped expectation. Here the fallback is spelt as alpha 0 rather than white, so the test
    // fails if the implementation ever grades on the number instead of on whether it was measured.
    [Fact]
    public void ASlotWhoseColourWasNotREADStaysExpectedEvenAtAlphaZero()
    {
        var pose = SpectralSlots()
            .Select(slot => slot.Ordinal is 29
                ? slot with { ColorReadSucceeded = false }
                : slot)
            .ToArray();

        var expectation = Sts2SpineGeoClipPoseTransparency.Expect([pose], armed: true);

        Assert.Contains(29, expectation.Ordinals);
        Assert.Equal<int>([30], expectation.TransparentOrdinals);
        Assert.Equal(20, expectation.Ordinals.Count);
    }

    // ---- (4) The kill switch --------------------------------------------------------------------------------

    // Disarmed, the expectation is the drawable-attachment set again, byte for byte — which is the property that
    // puts a regression one environment variable away from being isolated.
    [Fact]
    public void TheKillSwitchExpectsEveryDrawableSlotAgain()
    {
        var expectation = Sts2SpineGeoClipPoseTransparency.Expect(SpectralAttackSword(poses: 1), armed: false);

        Assert.Equal(21, expectation.Ordinals.Count);
        Assert.Empty(expectation.TransparentOrdinals);
        Assert.False(expectation.Armed);
        Assert.False(Sts2SpineGeoClipPoseTransparency.ParseArmed("0"));
        Assert.False(Sts2SpineGeoClipPoseTransparency.ParseArmed("off"));
        Assert.False(Sts2SpineGeoClipPoseTransparency.ParseArmed("false"));
        Assert.False(Sts2SpineGeoClipPoseTransparency.ParseArmed("no"));
        // Absent, empty and misspelt all ARM it — the same reading every other geoclip kill switch takes.
        Assert.True(Sts2SpineGeoClipPoseTransparency.ParseArmed(null));
        Assert.True(Sts2SpineGeoClipPoseTransparency.ParseArmed(""));
        Assert.True(Sts2SpineGeoClipPoseTransparency.ParseArmed("nonsense"));
        Assert.Equal("SPIRECTL_SPINE_GEOCLIP_ALPHA0_SINGLE_POSE", Sts2SpineGeoClipPoseTransparency.ArmEnv);
    }

    // ---- (5) ALPHA IS THE ONLY SIGNAL, and it is read without tolerance ---------------------------------------

    // No epsilon. A slot at 1/255 draws — faintly, but it draws — and an epsilon here would start deciding that
    // faint things do not count, which is a judgement about appearance this rule has no business making. Only a
    // value at or below zero is "puts no pixel on screen".
    [Theory]
    [InlineData(0d, false)]
    // Not physical, but the comparison is `<= 0` rather than `== 0` precisely so a clamp that ever hands back a
    // negative cannot read as opaque.
    [InlineData(-0.5d, false)]
    [InlineData(1d / 255d, true)]
    [InlineData(0.0039d, true)]
    [InlineData(0.5d, true)]
    [InlineData(1d, true)]
    public void OnlyAnAlphaAtOrBelowZeroDropsTheExpectation(double alpha, bool expected)
    {
        var expectation = Sts2SpineGeoClipPoseTransparency.Expect(
            [[new Sts2SpineGeoClipPoseTransparency.SlotPose(4, RequiresDrawing: true, ColorReadSucceeded: true, alpha)]],
            armed: true);

        Assert.Equal(expected, expectation.Ordinals.Contains(4));
    }

    // A slot that never had a drawable attachment was never in the set to begin with — this arm neither adds it
    // nor reports it as transparent, whatever colour it carries. The two rules stay separate: this one is about
    // a DRAWABLE slot that draws nothing here, not about what counts as drawable.
    [Fact]
    public void ANonDrawingSlotIsNeitherExpectedNorReportedAsTransparent()
    {
        var expectation = Sts2SpineGeoClipPoseTransparency.Expect(
            [
                [
                    new Sts2SpineGeoClipPoseTransparency.SlotPose(26, RequiresDrawing: false, ColorReadSucceeded: true, 0d),
                    new Sts2SpineGeoClipPoseTransparency.SlotPose(27, RequiresDrawing: false, ColorReadSucceeded: true, 1d),
                ],
            ],
            armed: true);

        Assert.Empty(expectation.Ordinals);
        Assert.Empty(expectation.TransparentOrdinals);
    }

    // ---- (6) THE OTHER THREE RIGS ARE UNTOUCHED ---------------------------------------------------------------

    // Every other bake in the recording is opaque at its sampled pose, so this arm subtracts nothing from any of
    // them and their counters are the ones the live legs reported. The shapes: ironclad/idle_loop 44/44,
    // flail/idle_loop 37/37, magi/idle_loop 16/16, flail/attack_flail 43/43 — the last one being the rig whose
    // OWN three-way tie was resolved by the slot-colour arm rather than by anything here.
    [Theory]
    [InlineData(44)]
    [InlineData(37)]
    [InlineData(16)]
    [InlineData(43)]
    public void ARigWithNoTransparentSlotIsGradedOnExactlyItsDrawableSlots(int drawable)
    {
        foreach (var poses in new[] { 1, 15 })
        {
            var expectation = Sts2SpineGeoClipPoseTransparency.Expect(
                OpaqueRig(drawable, poses),
                armed: true);

            Assert.Equal(drawable, expectation.Ordinals.Count);
            Assert.Empty(expectation.TransparentOrdinals);
            Assert.Null(Sts2SpineGeoClipRequestLane.IncompletenessReason(
                complete: true,
                associated: expectation.Ordinals.Count,
                slotsVisible: expectation.Ordinals.Count,
                foreignMeshes: 0,
                claimsProven: expectation.Ordinals.Count,
                claimsUnproven: 0,
                ownershipProofArmed: true));
        }
    }

    // ---- (7) WHAT THE RECORDED SPECTRAL BAKE THEN DOES AT THE ADMISSION RULE ----------------------------------

    // MEASURED, NOT HOPED FOR, and the part of this round that does NOT finish the job. With the first arm
    // satisfied the recording's own remaining counters carry it to the THIRD arm and it is refused there:
    // `foreignMeshes=3` unclaimed meshes in the bracket alongside `claimsUnproven=3` — the three claims the host
    // log records as `atlas:containment=3`, which is an atlas match that merely lands INSIDE the region the
    // slot's attachment names rather than identifying it. That is a pre-existing, independent weakness in the
    // spectral's atlas matching; this change removes one of the two blockers on that address, not both.
    //
    // Pinned as a test so the next round starts from the measured arm rather than from an assumption about it.
    [Fact]
    public void TheRecordedSpectralBakeThenReachesTheOwnershipArm()
    {
        var expectation = Sts2SpineGeoClipPoseTransparency.Expect(SpectralAttackSword(poses: 1), armed: true);
        var associated = expectation.Ordinals.Count(SpectralAssociatedOrdinals.Contains);

        var reason = Sts2SpineGeoClipRequestLane.IncompletenessReason(
            complete: associated >= expectation.Ordinals.Count,
            associated,
            expectation.Ordinals.Count,
            SpectralForeignMeshes,
            claimsProven: SpectralClaimsProven,
            claimsUnproven: SpectralClaimsUnproven,
            ownershipProofArmed: true);

        Assert.Equal("ownership=3 of claimed=19", reason);
        Assert.Equal("ownership", Sts2SpineGeoClipRequestLane.ClassifyRefusal(reason));
    }

    // …and the counterfactual that says the first arm really was cleared: the SAME bake with its three atlas
    // claims matching exactly rather than by containment is admitted outright. Nothing else about the recording
    // changes — same 19 associations, same 3 leftovers.
    [Fact]
    public void TheSameBakeIsAdmittedOnceItsAtlasClaimsAreExact()
    {
        var expectation = Sts2SpineGeoClipPoseTransparency.Expect(SpectralAttackSword(poses: 1), armed: true);
        var associated = expectation.Ordinals.Count(SpectralAssociatedOrdinals.Contains);

        Assert.Null(Sts2SpineGeoClipRequestLane.IncompletenessReason(
            complete: associated >= expectation.Ordinals.Count,
            associated,
            expectation.Ordinals.Count,
            SpectralForeignMeshes,
            claimsProven: SpectralAssociated,
            claimsUnproven: 0,
            ownershipProofArmed: true));
    }

    // …while the UNCHANGED bake — the one this round did not touch — is still refused on the first arm, so the
    // difference between the two verdicts is attributable to this change and to nothing else.
    [Fact]
    public void TheUnchangedSpectralBakeIsStillRefusedOnTheFirstArm()
    {
        var expectation = Sts2SpineGeoClipPoseTransparency.Expect(SpectralAttackSword(poses: 1), armed: false);
        var associated = expectation.Ordinals.Count(SpectralAssociatedOrdinals.Contains);

        Assert.Equal(21, expectation.Ordinals.Count);
        Assert.Equal(19, associated);
        var reason = Sts2SpineGeoClipRequestLane.IncompletenessReason(
            complete: associated >= expectation.Ordinals.Count,
            associated,
            expectation.Ordinals.Count,
            SpectralForeignMeshes,
            claimsProven: SpectralClaimsProven,
            claimsUnproven: SpectralClaimsUnproven,
            ownershipProofArmed: true);

        Assert.Equal("complete=false", reason);
        Assert.Equal("incomplete", Sts2SpineGeoClipRequestLane.ClassifyRefusal(reason));
    }

    // ---- The recording ---------------------------------------------------------------------------------------

    // `associated 19/21 … foreign=3` and `claimsProven=16 claimsUnproven=3
    // [atlas:uv-region-exact=15 atlas:containment=3 color-flip=1]` — SPINE_GEOCLIP spectral_knight/attack_sword,
    // completeness and ownership lines.
    private const int SpectralAssociated = 19;

    private const int SpectralForeignMeshes = 3;

    private const int SpectralClaimsProven = 16;

    private const int SpectralClaimsUnproven = 3;

    // The 21 ordinals that show a drawable attachment at the sampled pose. The recording's slot table has 31
    // slots of which 10 show nothing there ("slot N never shows a drawable attachment in this animation" for
    // 1, 12, 17, 22-28) — so the drawable set is everything else, and 29/30 are the two the atlas tied.
    private static readonly int[] SpectralNonDrawing = [1, 12, 17, 22, 23, 24, 25, 26, 27, 28];

    private static readonly int[] SpectralTransparent = [29, 30];

    private static readonly HashSet<int> SpectralAssociatedOrdinals =
        [.. Enumerable.Range(0, 31).Except(SpectralNonDrawing).Except(SpectralTransparent)];

    /// <summary>
    /// The spectral knight's `attack_sword` slot table as the pose capture read it, repeated over
    /// <paramref name="poses"/> sampled frames. Every pose carries the SAME readings — including alpha 0 on
    /// 29/30 — so a multi-frame case here is refused by the pose count alone and not by some frame happening to
    /// disagree.
    /// </summary>
    private static IReadOnlyList<IReadOnlyList<Sts2SpineGeoClipPoseTransparency.SlotPose>> SpectralAttackSword(
        int poses)
    {
        var view = new List<IReadOnlyList<Sts2SpineGeoClipPoseTransparency.SlotPose>>(poses);
        for (var pose = 0; pose < poses; pose += 1)
        {
            view.Add(SpectralSlots());
        }

        return view;
    }

    private static Sts2SpineGeoClipPoseTransparency.SlotPose[] SpectralSlots()
    {
        var slots = Enumerable
            .Range(0, 31)
            .Select(ordinal => new Sts2SpineGeoClipPoseTransparency.SlotPose(
                ordinal,
                RequiresDrawing: !SpectralNonDrawing.Contains(ordinal),
                ColorReadSucceeded: true,
                // `frames[0].slots["29"] = {"part": null, "color": [1,1,1,0]}`, same for "30"; every other slot
                // in that frame carries alpha 1.
                Alpha: SpectralTransparent.Contains(ordinal) ? 0d : 1d))
            .ToArray();

        Assert.Equal(21, slots.Count(slot => slot.RequiresDrawing));
        return slots;
    }

    /// <summary><paramref name="drawable"/> opaque drawable slots, over <paramref name="poses"/> frames.</summary>
    private static IReadOnlyList<IReadOnlyList<Sts2SpineGeoClipPoseTransparency.SlotPose>> OpaqueRig(
        int drawable,
        int poses)
    {
        var slots = new List<Sts2SpineGeoClipPoseTransparency.SlotPose>(drawable);
        for (var ordinal = 0; ordinal < drawable; ordinal += 1)
        {
            slots.Add(new Sts2SpineGeoClipPoseTransparency.SlotPose(
                ordinal,
                RequiresDrawing: true,
                ColorReadSucceeded: true,
                Alpha: 1d));
        }

        var view = new List<IReadOnlyList<Sts2SpineGeoClipPoseTransparency.SlotPose>>(poses);
        for (var pose = 0; pose < poses; pose += 1)
        {
            view.Add(slots);
        }

        return view;
    }
}
