using System.Linq;
using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// THE MINT MARK: the pose-derived plan for telling apart slots that draw one attachment, and the arm switch that
// ties it to the headless re-mint. The write itself is Godot-typed and lives in the baker; what is provable
// offline is (a) that the plan is a function of the pose and nothing else, (b) that its ladder clears the
// tie-break's own margin, and (c) that it is unreachable unless the re-mint is armed.
//
// WHY IT EXISTS, measured. With the re-mint armed and no mark, the ironclad bakes 42 of its 44 parts headless:
// slots 25 and 26 both draw `hip armor right back`, the atlas correctly refuses that tie (on a real renderer
// too), and the colour-flip probe that breaks it there cannot work headless — a colour write changes no triangle
// indices, so it takes the attribute-region path the dummy backend discards. The mark rides on the re-mint's
// CREATE pass instead, which is the one write headless keeps.
public class Sts2SpineGeoClipHeadlessMintMarkTests
{
    private static (int Ordinal, string? AttachmentName, bool RequiresDrawing) Slot(
        int ordinal, string? attachment, bool drawing = true)
        => (ordinal, attachment, drawing);

    [Fact]
    public void UnsetFollowsTheRemint()
    {
        // One lever, not two: the mark buys nothing without the re-mint and costs nothing with it.
        Assert.True(Sts2SpineGeoClipHeadlessMintMark.Resolve(null, remintArmed: true));
        Assert.True(Sts2SpineGeoClipHeadlessMintMark.Resolve("   ", remintArmed: true));
        Assert.False(Sts2SpineGeoClipHeadlessMintMark.Resolve(null, remintArmed: false));
        Assert.False(Sts2SpineGeoClipHeadlessMintMark.Resolve(string.Empty, remintArmed: false));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("off")]
    [InlineData("false")]
    [InlineData("no")]
    [InlineData(" OFF ")]
    public void RecognisedFalsehoodsDisarmEvenUnderTheRemint(string raw)
        => Assert.False(Sts2SpineGeoClipHeadlessMintMark.Resolve(raw, remintArmed: true));

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("on")]
    [InlineData("yes")]
    public void RecognisedTruthsStillNeedTheRemint(string raw)
    {
        Assert.True(Sts2SpineGeoClipHeadlessMintMark.Resolve(raw, remintArmed: true));
        // The mark writes a slot colour that only a CREATE pass can capture. Without the re-mint there is no
        // create pass, so an armed mark would be a colour written to a live rig for no measurement at all.
        Assert.False(Sts2SpineGeoClipHeadlessMintMark.Resolve(raw, remintArmed: false));
    }

    [Fact]
    public void ArmedReadsTheDocumentedVariable()
    {
        // The name is the contract with the harness scripts and with every archived arm's `proc-environ.txt`.
        Assert.Equal("SPIRECTL_SPINE_GEOCLIP_HEADLESS_MINT_MARK", Sts2SpineGeoClipHeadlessMintMark.ArmEnv);
    }

    [Fact]
    public void NothingIsMarkedWhenNoAttachmentIsShared()
    {
        // Flail, spectral and magi are this case at `idle_loop`: no two drawable slots name one attachment, so
        // the arm is a no-op on them by construction and their measured 37/22/16 cannot move.
        var plan = Sts2SpineGeoClipHeadlessMintMark.For(
            [Slot(0, "head"), Slot(1, "cape"), Slot(2, "l shoulder")]);
        Assert.Empty(plan.Marks);
        Assert.Empty(plan.Oversized);
    }

    [Fact]
    public void SharedAttachmentGetsOneLadderStepPerMember()
    {
        // The ironclad's measured case, in miniature.
        var plan = Sts2SpineGeoClipHeadlessMintMark.For(
        [
            Slot(24, "torso"),
            Slot(25, "hip armor right back"),
            Slot(26, "hip armor right back"),
        ]);

        Assert.Equal([25, 26], plan.Marks.Select(mark => mark.Ordinal));
        Assert.Equal(0.125, plan.Marks[0].Red, 6);
        Assert.Equal(0.250, plan.Marks[1].Red, 6);
        Assert.Equal(1, plan.GroupCount);
    }

    [Fact]
    public void TheLadderClearsTheTieBreakMargin()
    {
        // The load-bearing numeric relationship between the two files: the tie-break refuses unless the runner-up
        // is at least `DefaultMargin` further than the winner, so two neighbouring ladder rungs must separate by
        // at least that. If either constant moves, this fails rather than the arm silently refusing every tie.
        var step = Sts2SpineGeoClipHeadlessMintMark.RedFor(1) - Sts2SpineGeoClipHeadlessMintMark.RedFor(0);
        Assert.True(step >= Sts2SpineGeoClipSlotColorIdentity.DefaultMargin);
        // …and comfortably outside the tolerance an 8-bit vertex colour round trip can cost.
        Assert.True(step > Sts2SpineGeoClipSlotColorIdentity.DefaultTolerance * 2);
    }

    [Fact]
    public void EveryLadderRungStaysInsideTheUnitInterval()
    {
        for (var member = 0; member < Sts2SpineGeoClipHeadlessMintMark.MaxGroupSize; member += 1)
        {
            var red = Sts2SpineGeoClipHeadlessMintMark.RedFor(member);
            Assert.True(red > 0);
            Assert.True(red <= 1);
        }
    }

    [Fact]
    public void AnOversizedGroupIsLeftEntirelyUnmarked()
    {
        // Marking part of a group would put a written colour and a pose colour on one distance scale and make the
        // rule find a "winner" that is an artefact of the marking. A group the ladder cannot separate must refuse.
        var slots = Enumerable.Range(0, Sts2SpineGeoClipHeadlessMintMark.MaxGroupSize + 1)
            .Select(ordinal => Slot(ordinal, "circ_rotator"))
            .ToArray();
        var plan = Sts2SpineGeoClipHeadlessMintMark.For(slots);

        Assert.Empty(plan.Marks);
        Assert.Equal("circ_rotator", Assert.Single(plan.Oversized).AttachmentName);
        Assert.Equal(slots.Length, plan.Oversized[0].Count);
    }

    [Fact]
    public void SlotsThatDrawNothingAreNotAShare()
    {
        // A slot with no drawable attachment is never associated, so it is not part of any tie and marking it
        // would move a colour on a rig for no reason. Two slots must BOTH require drawing to be a pair.
        var plan = Sts2SpineGeoClipHeadlessMintMark.For(
        [
            Slot(3, "hip armor right back"),
            Slot(4, "hip armor right back", drawing: false),
        ]);
        Assert.Empty(plan.Marks);
    }

    [Fact]
    public void UnnamedAttachmentsAreNeverGrouped()
    {
        // Two slots that both read back "no attachment name" are not two slots drawing one attachment; grouping
        // them on a null or empty key would invent a tie and then mark real slots for it.
        var plan = Sts2SpineGeoClipHeadlessMintMark.For([Slot(0, null), Slot(1, null), Slot(2, string.Empty)]);
        Assert.Empty(plan.Marks);
    }

    [Fact]
    public void MembersAreNumberedInOrdinalOrderWhateverTheInputOrder()
    {
        // The plan has to be a function of the POSE, not of enumeration order: two processes baking the same rig
        // must mark the same ordinals with the same colours, or their artifacts differ for no reason.
        var forward = Sts2SpineGeoClipHeadlessMintMark.For([Slot(9, "x"), Slot(4, "x"), Slot(7, "x")]);
        var reverse = Sts2SpineGeoClipHeadlessMintMark.For([Slot(7, "x"), Slot(9, "x"), Slot(4, "x")]);

        Assert.Equal([4, 7, 9], forward.Marks.Select(mark => mark.Ordinal));
        Assert.Equal(
            forward.Marks.Select(mark => (mark.Ordinal, mark.Red)),
            reverse.Marks.Select(mark => (mark.Ordinal, mark.Red)));
    }

    [Fact]
    public void TwoGroupsAreLadderedIndependently()
    {
        // Each tie is decided only against its OWN candidates, so the ladders may (and do) repeat across groups.
        var plan = Sts2SpineGeoClipHeadlessMintMark.For(
            [Slot(1, "a"), Slot(2, "a"), Slot(5, "b"), Slot(6, "b")]);

        Assert.Equal(2, plan.GroupCount);
        Assert.Equal(4, plan.Marks.Count);
        Assert.Equal(plan.Marks.Single(mark => mark.Ordinal == 1).Red, plan.Marks.Single(mark => mark.Ordinal == 5).Red, 6);
        Assert.Equal(plan.Marks.Single(mark => mark.Ordinal == 2).Red, plan.Marks.Single(mark => mark.Ordinal == 6).Red, 6);
    }

    [Fact]
    public void DuplicateOrdinalsCollapseRatherThanDoubleMarking()
    {
        // A defensive read: the pose is built per ordinal, but a caller that handed the same ordinal twice must
        // not get two marks for one slot — the second write would silently overwrite the first.
        var plan = Sts2SpineGeoClipHeadlessMintMark.For([Slot(2, "a"), Slot(2, "a"), Slot(3, "a")]);
        Assert.Equal([2, 3], plan.Marks.Select(mark => mark.Ordinal));
    }

    [Fact]
    public void TheMarkedColourSeparatesUnderTheRuleThatWillJudgeIt()
    {
        // END TO END over the pure halves: mark a pair, hand the tie-break the marks as the slot side and the
        // colours a faithful CREATE pass would have written as the mesh side, and check it pairs them the way the
        // marking says. This is the offline half of the live claim; the live half is the bake.
        var plan = Sts2SpineGeoClipHeadlessMintMark.For(
            [Slot(25, "hip armor right back"), Slot(26, "hip armor right back")]);
        var slots = plan.Marks
            .Select(mark => new Sts2SpineGeoClipSlotColorIdentity.SlotColor(mark.Ordinal, mark.Red, 1, 1, 1))
            .ToArray();
        // Deliberately CROSSED against the ordinal order, so a rule that quietly paired by list position would
        // disagree with the one that pairs by colour.
        var meshes = new[]
        {
            new Sts2SpineGeoClipSlotColorIdentity.MeshColor(0, plan.Marks[1].Red, 1, 1, 1),
            new Sts2SpineGeoClipSlotColorIdentity.MeshColor(1, plan.Marks[0].Red, 1, 1, 1),
        };

        var verdict = Sts2SpineGeoClipSlotColorIdentity.Disambiguate(slots, meshes);

        Assert.Empty(verdict.Refused);
        Assert.Equal(1, verdict.OrderByOrdinal[25]);
        Assert.Equal(0, verdict.OrderByOrdinal[26]);
    }

    [Fact]
    public void AMarkThatDidNotSurviveRefusesRatherThanGuesses()
    {
        // The renderer-applies-the-restore case, which is what a REAL renderer does: both surfaces come back at
        // the pose colour the mark was taken away from. Neither is within tolerance of either mark, so the rule
        // refuses both slots and the bake keeps exactly the association it had without the arm.
        var plan = Sts2SpineGeoClipHeadlessMintMark.For(
            [Slot(25, "hip armor right back"), Slot(26, "hip armor right back")]);
        var slots = plan.Marks
            .Select(mark => new Sts2SpineGeoClipSlotColorIdentity.SlotColor(mark.Ordinal, mark.Red, 1, 1, 1))
            .ToArray();
        var meshes = new[]
        {
            new Sts2SpineGeoClipSlotColorIdentity.MeshColor(0, 1, 1, 1, 1),
            new Sts2SpineGeoClipSlotColorIdentity.MeshColor(1, 1, 1, 1, 1),
        };

        var verdict = Sts2SpineGeoClipSlotColorIdentity.Disambiguate(slots, meshes);

        Assert.Empty(verdict.OrderByOrdinal);
        Assert.Equal(
            [Sts2SpineGeoClipSlotColorIdentity.RefusedAboveTolerance,
             Sts2SpineGeoClipSlotColorIdentity.RefusedAboveTolerance],
            verdict.Refused.Select(refusal => refusal.Reason));
    }

    [Fact]
    public void AnEightBitRoundTripStillPairs()
    {
        // The surface stores vertex colours quantised; the value read back is not the float that was written.
        // Quantising every rung to the nearest 1/255 must still land each mesh on its own slot.
        var plan = Sts2SpineGeoClipHeadlessMintMark.For(
            [Slot(1, "x"), Slot(2, "x"), Slot(3, "x"), Slot(4, "x")]);
        var slots = plan.Marks
            .Select(mark => new Sts2SpineGeoClipSlotColorIdentity.SlotColor(mark.Ordinal, mark.Red, 1, 1, 1))
            .ToArray();
        var meshes = plan.Marks
            .Select((mark, order) => new Sts2SpineGeoClipSlotColorIdentity.MeshColor(
                order, System.Math.Round(mark.Red * 255) / 255, 1, 1, 1))
            .ToArray();

        var verdict = Sts2SpineGeoClipSlotColorIdentity.Disambiguate(slots, meshes);

        Assert.Empty(verdict.Refused);
        for (var member = 0; member < plan.Marks.Count; member += 1)
        {
            Assert.Equal(member, verdict.OrderByOrdinal[plan.Marks[member].Ordinal]);
        }
    }

    [Fact]
    public void AMintMarkClaimGradesOnItsAtlasHalf()
    {
        // Same rule as the passive tie-break's claim: the mark says WHICH of our slots owns the mesh, never that
        // the mesh is ours, so an exact atlas region behind it is a proof and a bare containment is not.
        Assert.True(Sts2SpineGeoClipOwnership.IsPositiveProof(
            Sts2SpineGeoClipOwnership.AtlasMintMarkClaim(Sts2SpineGeoClipAtlas.MatchExact)));
        Assert.False(Sts2SpineGeoClipOwnership.IsPositiveProof(
            Sts2SpineGeoClipOwnership.AtlasMintMarkClaim(Sts2SpineGeoClipAtlas.MatchContainment)));
        Assert.StartsWith(
            Sts2SpineGeoClipOwnership.ClaimAtlasMintMarkPrefix,
            Sts2SpineGeoClipOwnership.AtlasMintMarkClaim(Sts2SpineGeoClipAtlas.MatchExact));
    }
}
