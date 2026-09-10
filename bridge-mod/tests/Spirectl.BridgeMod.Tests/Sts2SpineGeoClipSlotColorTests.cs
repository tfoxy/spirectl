using System.Collections.Generic;
using System.Linq;
using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// Separating meshes the ATLAS CANNOT TELL APART, by the colour each side already carries.
//
// THE DEFECT, MEASURED. A live `fight KNIGHTS_ELITE` on a real GPU (evidence archived at
// `.sts2/research/data/geoclip-knights-20260908T0500Z/`; host log `legE-envbake-combat-vfx/spine-geoclip.log`
// lines 122-138 and 184-198, reproduced identically in the quiet control leg) refused two of the four rigs at
// their attack animations, both for the same reason:
//
//   flail_knight   anim=attack_flail   associated 40/43   ambiguousAtlasRegion=3   3 slots share `circ_rotator`
//   spectral_knight anim=attack_sword  associated 19/21   ambiguousAtlasRegion=2   2 slots share `sword swish`
//
// The leftover candidates carry a BYTE-IDENTICAL uv box in each case, because the slots genuinely draw the same
// attachment. `MatchMeshToAttachmentRegion` scores them at zero against each other and answers `ambiguous`,
// which is correct — there is nothing in the atlas that separates them — and the colour NUDGE could not separate
// them either: the flail's group cost 264 colour reads and reported "43 of 43 meshes never moved", because those
// slots are exactly the ones the animation's own colour timeline rewrites every frame.
//
// WHAT SEPARATES THEM. The timeline that defeats the nudge is the discriminator. At the sampled pose the flail's
// three slots carry three different colours (archived manifest
// `envbake-artifacts/legE-combat/flail_knight--Visuals--attack_flail/manifest.json`, `frames[0].slots`), and the
// renderer writes each slot's colour into its mesh's vertices — so the pairing reads off directly, with no
// nudge and no extra device read. The spectral's two both sit at (1,1,1,0) and therefore DO NOT separate, which
// is the negative half of this file and is measured, not invented.
//
// Every colour, uv box, page size and mesh order below is that recording's. Where the recording's log prints a
// number to three decimals, the fixture carries the integer page-pixel box those three decimals round to, and
// says so at the site.
public sealed class Sts2SpineGeoClipSlotColorTests
{
    // ---- The recording ---------------------------------------------------------------------------------------

    // `page 0 … 1658x523` — SPINE_GEOCLIP flail_knight/attack_flail, page-copy line.
    private const int FlailPageWidth = 1658;

    private const int FlailPageHeight = 523;

    // `uv:[0.562,0.556]..[0.692,0.996]` on that page is the box (932, 291) 215x230 to within half a printed
    // digit (932/1658 = 0.5621, 1147/1658 = 0.6918, 291/523 = 0.5564, 521/523 = 0.9962). The fixture carries the
    // integer box so the corner arithmetic is exact rather than an artifact of the log's rounding.
    private static readonly (int X, int Y, int Width, int Height) FlailCircRotator = (932, 291, 215, 230);

    // `page 0 … 994x532` — SPINE_GEOCLIP spectral_knight/attack_sword.
    private const int SpectralPageWidth = 994;

    private const int SpectralPageHeight = 532;

    // `uv:[0.002,0.333]..[0.184,0.996]` on that page, same reading.
    private static readonly (int X, int Y, int Width, int Height) SpectralSwordSwish = (2, 177, 181, 353);

    // `frames[0].slots` of the archived attack manifest, slots 52/53/54 — the three the atlas tied. All three
    // are `part: null` there, which is the refusal this file is about.
    private static readonly Sts2SpineGeoClipSlotColorIdentity.SlotColor[] FlailRotatorSlots =
    [
        new(52, 0.7373, 0.8863, 0.9686, 0.3373),
        new(53, 0.6627, 0.8157, 0.9255, 0.4627),
        new(54, 0.7020, 0.8392, 0.8941, 0.6745),
    ];

    // …and slots 29/30 of the spectral's, which are BOTH fully transparent at the sampled pose.
    private static readonly Sts2SpineGeoClipSlotColorIdentity.SlotColor[] SpectralSwishSlots =
    [
        new(29, 1, 1, 1, 0),
        new(30, 1, 1, 1, 0),
    ];

    // ---- (1) The flail's three-way tie is real, and the atlas hands it on intact -------------------------------

    // Before anything can be broken, the tie has to be REPORTED. Three candidates over one region: the matcher
    // must refuse, and must name all three — naming only the "winner" would be the list-order guess in disguise.
    [Fact]
    public void ThreeMeshesOverOneRegionTieAndTheMatcherNamesAllOfThem()
    {
        var match = Sts2SpineGeoClipAtlas.MatchMeshToAttachmentRegion(
            "circ_rotator",
            FlailAtlas(),
            [(FlailPageWidth, FlailPageHeight)],
            [FlailBox(40), FlailBox(41), FlailBox(42)]);

        Assert.Equal(Sts2SpineGeoClipAtlas.MatchAmbiguous, match.Method);
        Assert.Equal(-1, match.Order);
        Assert.Equal([40, 41, 42], match.TiedCandidates.Select(tied => tied.Order).Order());
        // Exact, because each mesh's uv box IS the region's box — which is what makes the tie-break's claim a
        // positive ownership proof rather than a weak one. See the grading tests at the bottom.
        Assert.All(match.TiedCandidates, tied => Assert.Equal(Sts2SpineGeoClipAtlas.MatchExact, tied.Method));
    }

    // (2) THE CASE THE ROUND IS ABOUT. Three slots, three tied meshes, three distinct pose colours: every slot
    // pairs, to a DISTINCT mesh. That is what turns the recording's `associated 40/43` into 43 of 43.
    [Fact]
    public void TheFlailsThreeWayTieResolvesToThreeDistinctSlots()
    {
        var verdict = Sts2SpineGeoClipSlotColorIdentity.Disambiguate(
            FlailRotatorSlots,
            [
                // Each candidate carries the colour the renderer wrote from its slot — the pairing this arm has
                // to recover. Deliberately offered in an order that does NOT match the slot order, so a rule
                // that paired by position in the list would fail here.
                Mesh(41, FlailRotatorSlots[1]),
                Mesh(42, FlailRotatorSlots[2]),
                Mesh(40, FlailRotatorSlots[0]),
            ]);

        Assert.Empty(verdict.Refused);
        Assert.Equal(3, verdict.OrderByOrdinal.Count);
        Assert.Equal(40, verdict.OrderByOrdinal[52]);
        Assert.Equal(41, verdict.OrderByOrdinal[53]);
        Assert.Equal(42, verdict.OrderByOrdinal[54]);
        // A bijection, not three independent guesses: 40 + 3 associated = 43 of 43 only if no mesh is used twice.
        Assert.Equal(3, verdict.OrderByOrdinal.Values.Distinct().Count());
    }

    // The three separate by 0.125 at their CLOSEST pair (52 vs 53, on alpha: 0.4627 - 0.3373), which is 2.5× the
    // margin. Stated as its own assertion because the whole arm rests on that number being real, and a future
    // margin change has to be made against it deliberately.
    [Fact]
    public void TheFlailsClosestPairSeparatesByMoreThanTheMargin()
    {
        var closest = FlailRotatorSlots
            .SelectMany(a => FlailRotatorSlots, (a, b) => (a, b))
            .Where(pair => pair.a.Ordinal != pair.b.Ordinal)
            .Min(pair => Sts2SpineGeoClipSlotColorIdentity.Distance(pair.a, Mesh(0, pair.b)));

        Assert.Equal(0.1254, closest, 4);
        Assert.True(closest > Sts2SpineGeoClipSlotColorIdentity.DefaultMargin);
        Assert.True(closest > Sts2SpineGeoClipSlotColorIdentity.DefaultTolerance);
    }

    // ---- (3) The negative, also from the recording -------------------------------------------------------------

    // THE SPECTRAL KNIGHT'S TWO. Identical uv box, identical pose colour — and, from the same log's foreign
    // diagnostics, OVERLAPPING positions too: `xy:[-1378.6,-1175.6]..[-709.9,59.1]` and
    // `xy:[-1431.9,-1011.5]..[-1001.2,-35.7]` are 0.42 AABB IoU apart, with centroids 176 units apart against a
    // mean quad diagonal of 1067. So neither colour NOR position determines this pairing, and the arm must leave
    // both slots unassociated rather than hand out either of the two orderings.
    [Fact]
    public void TheSpectralsTwoWayTieDoesNotResolveAndRefuses()
    {
        var verdict = Sts2SpineGeoClipSlotColorIdentity.Disambiguate(
            SpectralSwishSlots,
            [Mesh(20, SpectralSwishSlots[0]), Mesh(21, SpectralSwishSlots[1])]);

        Assert.Empty(verdict.OrderByOrdinal);
        Assert.Equal([29, 30], verdict.Refused.Select(refusal => refusal.Ordinal));
        Assert.All(
            verdict.Refused,
            refusal => Assert.Equal(
                Sts2SpineGeoClipSlotColorIdentity.RefusedTiedCandidates, refusal.Reason));
    }

    // The same refusal has to survive the meshes being offered the OTHER way round: a rule that resolved one
    // ordering and refused the other would be reading enumeration order and reporting it as evidence.
    [Fact]
    public void TheSpectralsRefusalDoesNotDependOnCandidateOrder()
    {
        var forward = Sts2SpineGeoClipSlotColorIdentity.Disambiguate(
            SpectralSwishSlots,
            [Mesh(20, SpectralSwishSlots[0]), Mesh(21, SpectralSwishSlots[1])]);
        var reversed = Sts2SpineGeoClipSlotColorIdentity.Disambiguate(
            [SpectralSwishSlots[1], SpectralSwishSlots[0]],
            [Mesh(21, SpectralSwishSlots[1]), Mesh(20, SpectralSwishSlots[0])]);

        Assert.Empty(forward.OrderByOrdinal);
        Assert.Empty(reversed.OrderByOrdinal);
        Assert.Equal(
            forward.Refused.Select(refusal => (refusal.Ordinal, refusal.Reason)),
            reversed.Refused.Select(refusal => (refusal.Ordinal, refusal.Reason)));
    }

    // The atlas half of the spectral case, for completeness: two candidates over one region tie, and both are
    // named. This is the input the tie-break above is handed.
    [Fact]
    public void TheSpectralsTwoCandidatesTieAtTheAtlas()
    {
        var match = Sts2SpineGeoClipAtlas.MatchMeshToAttachmentRegion(
            "sword swish",
            SpectralAtlas(),
            [(SpectralPageWidth, SpectralPageHeight)],
            [SpectralBox(20), SpectralBox(21)]);

        Assert.Equal(Sts2SpineGeoClipAtlas.MatchAmbiguous, match.Method);
        Assert.Equal([20, 21], match.TiedCandidates.Select(tied => tied.Order).Order());
    }

    // ---- (4) The clean shapes are untouched ---------------------------------------------------------------------

    // At `idle_loop` all four rigs associate every drawable slot with `ambiguousAtlasRegion=0` (44/44, 37/37,
    // 22/22, 16/16), and at `attack` so do the ironclad and the magi knight. Nothing about those bakes may reach
    // the tie-break at all — which is enforced here by the property that carries them past it: an ACCEPTED
    // verdict names no tie.
    [Fact]
    public void AnAcceptedMatchCarriesNoTieSet()
    {
        var match = Sts2SpineGeoClipAtlas.MatchMeshToAttachmentRegion(
            "circ_rotator",
            FlailAtlas(),
            [(FlailPageWidth, FlailPageHeight)],
            [FlailBox(40)]);

        Assert.Equal(Sts2SpineGeoClipAtlas.MatchExact, match.Method);
        Assert.Equal(40, match.Order);
        Assert.Empty(match.TiedCandidates);
    }

    // A slot whose attachment names no region at all reaches the same place it always did, with nothing for a
    // tie-break to chew on.
    [Fact]
    public void AMatchWithNoSuchRegionCarriesNoTieSet()
    {
        var match = Sts2SpineGeoClipAtlas.MatchMeshToAttachmentRegion(
            "not_in_this_atlas",
            FlailAtlas(),
            [(FlailPageWidth, FlailPageHeight)],
            [FlailBox(40), FlailBox(41)]);

        Assert.Equal(Sts2SpineGeoClipAtlas.MatchNoSuchRegion, match.Method);
        Assert.Empty(match.TiedCandidates);
    }

    // A tie among candidates that do not even LAND inside the named region is not a tie worth handing on: it is
    // a slot whose mesh was never swept in, and offering the strays to a colour tie-break would invite it to
    // pair one of them on a coincidence.
    [Fact]
    public void ATieOutsideTheRegionIsNotHandedOn()
    {
        var far = new Sts2SpineGeoClipAtlas.MeshUvBox(70, 0.80, 0.05, 0.90, 0.15);
        var alsoFar = new Sts2SpineGeoClipAtlas.MeshUvBox(71, 0.80, 0.05, 0.90, 0.15);

        var match = Sts2SpineGeoClipAtlas.MatchMeshToAttachmentRegion(
            "circ_rotator", FlailAtlas(), [(FlailPageWidth, FlailPageHeight)], [far, alsoFar]);

        Assert.Equal(Sts2SpineGeoClipAtlas.MatchAmbiguous, match.Method);
        Assert.Equal(0, match.ContainedCount);
        Assert.Empty(match.TiedCandidates);
    }

    // ---- (5) The rule's own refusals ----------------------------------------------------------------------------

    // A candidate is never paired merely for being the NEAREST. The recording's slot 52 against a mesh the
    // renderer wrote white: nothing in that group is this slot's, and "closest available" is not evidence.
    [Fact]
    public void ANearestCandidateBeyondToleranceIsRefused()
    {
        var verdict = Sts2SpineGeoClipSlotColorIdentity.Disambiguate(
            [FlailRotatorSlots[0]],
            [new Sts2SpineGeoClipSlotColorIdentity.MeshColor(40, 1, 1, 1, 1)]);

        Assert.Empty(verdict.OrderByOrdinal);
        Assert.Equal(
            Sts2SpineGeoClipSlotColorIdentity.RefusedAboveTolerance,
            Assert.Single(verdict.Refused).Reason);
    }

    // MUTUAL best, not one-sided. Two slots whose nearest is the SAME mesh: pairing the one that happens to be
    // asked first, and leaving the other looking merely unlucky, would report a pairing nothing determined.
    [Fact]
    public void TwoSlotsContestingOneMeshBothRefuse()
    {
        var verdict = Sts2SpineGeoClipSlotColorIdentity.Disambiguate(
            [FlailRotatorSlots[0], FlailRotatorSlots[1]],
            [Mesh(40, FlailRotatorSlots[0]), Mesh(41, FlailRotatorSlots[0])]);

        Assert.Empty(verdict.OrderByOrdinal);
        Assert.Equal([52, 53], verdict.Refused.Select(refusal => refusal.Ordinal));
    }

    // NO ELIMINATION. One slot and one mesh left over do NOT pair just because nothing else is available — that
    // is the leftover-pool inference the project forbids, and it is forbidden here too. The colour still has to
    // agree.
    [Fact]
    public void ALoneSlotAndALoneMeshStillHaveToAgreeOnColour()
    {
        var agreeing = Sts2SpineGeoClipSlotColorIdentity.Disambiguate(
            [FlailRotatorSlots[2]], [Mesh(42, FlailRotatorSlots[2])]);
        var disagreeing = Sts2SpineGeoClipSlotColorIdentity.Disambiguate(
            [FlailRotatorSlots[2]], [Mesh(42, FlailRotatorSlots[0])]);

        // FlailRotatorSlots[2] is slot ordinal 54.
        Assert.Equal(42, agreeing.OrderByOrdinal[54]);
        Assert.Empty(disagreeing.OrderByOrdinal);
        Assert.Equal(
            Sts2SpineGeoClipSlotColorIdentity.RefusedAboveTolerance,
            Assert.Single(disagreeing.Refused).Reason);
    }

    // An empty candidate set is a refusal with its own name, not a silent nothing: the caller logs it, and a
    // group that lost its candidates has to be visibly different from one that had none to begin with.
    [Fact]
    public void NoCandidatesRefusesEverySlotByName()
    {
        var verdict = Sts2SpineGeoClipSlotColorIdentity.Disambiguate(FlailRotatorSlots, []);

        Assert.Empty(verdict.OrderByOrdinal);
        Assert.Equal([52, 53, 54], verdict.Refused.Select(refusal => refusal.Ordinal));
        Assert.All(
            verdict.Refused,
            refusal => Assert.Equal(
                Sts2SpineGeoClipSlotColorIdentity.RefusedNoCandidate, refusal.Reason));
    }

    // The distance is per-channel, and the reason is the recording: the flail's closest pair differs mainly on
    // ALPHA. A Euclidean or averaged distance would dilute exactly the channel carrying the signal.
    [Fact]
    public void AnAlphaOnlyDifferenceIsEnoughToSeparate()
    {
        var opaque = new Sts2SpineGeoClipSlotColorIdentity.SlotColor(1, 0.5, 0.5, 0.5, 1.0);
        var faded = new Sts2SpineGeoClipSlotColorIdentity.SlotColor(2, 0.5, 0.5, 0.5, 0.5);

        var verdict = Sts2SpineGeoClipSlotColorIdentity.Disambiguate(
            [opaque, faded], [Mesh(0, faded), Mesh(1, opaque)]);

        Assert.Equal(1, verdict.OrderByOrdinal[1]);
        Assert.Equal(0, verdict.OrderByOrdinal[2]);
        Assert.Empty(verdict.Refused);
    }

    // Eight-bit art making a float round trip does not come back bit-identical. A colour off by two LSBs
    // (2/255 = 0.0078) is still the same colour; the recording's separations are an order of magnitude above it.
    [Fact]
    public void AColourOffByAFewEightBitStepsStillMatches()
    {
        var slot = FlailRotatorSlots[0];
        var drifted = new Sts2SpineGeoClipSlotColorIdentity.MeshColor(
            40, slot.R + (2 / 255d), slot.G - (2 / 255d), slot.B, slot.A + (1 / 255d));

        var verdict = Sts2SpineGeoClipSlotColorIdentity.Disambiguate([slot], [drifted]);

        Assert.Equal(40, verdict.OrderByOrdinal[52]);
    }

    // ---- (6) The kill switch ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("0", false)]
    [InlineData("off", false)]
    [InlineData("false", false)]
    [InlineData("no", false)]
    [InlineData("OFF", false)]
    [InlineData(" 0 ", false)]
    [InlineData("1", true)]
    [InlineData("", true)]
    [InlineData(null, true)]
    public void TheKillSwitchDisarmsOnTheUsualSpellings(string? raw, bool armed)
        => Assert.Equal(armed, Sts2SpineGeoClipSlotColorIdentity.ParseArmed(raw));

    // ---- (7) The ownership grade --------------------------------------------------------------------------------

    // Graded on its ATLAS half, exactly as `atlas:` and `color-flip+atlas:` are — because the tie-break never
    // widens a pool. It chooses inside a set the atlas has already narrowed, so whether the claim could have
    // gone to a mesh that is not ours is decided by that match method and by nothing the tie-break does.
    [Theory]
    [InlineData(Sts2SpineGeoClipAtlas.MatchExact, true)]
    [InlineData(Sts2SpineGeoClipAtlas.MatchContainment, false)]
    [InlineData(Sts2SpineGeoClipAtlas.MatchAmbiguous, false)]
    [InlineData(Sts2SpineGeoClipAtlas.MatchNone, false)]
    public void AnAtlasPlusSlotColourClaimIsGradedOnItsMatchMethod(string method, bool proven)
        => Assert.Equal(
            proven,
            Sts2SpineGeoClipOwnership.IsPositiveProof(Sts2SpineGeoClipOwnership.AtlasSlotColorClaim(method)));

    // The claim is its own kind in the roll-up, so a manifest says which arm a rig's bake rests on. The flail's
    // three would read `atlas+slot-color:uv-region-exact=3` beside `atlas:uv-region-exact=40`.
    [Fact]
    public void TheClaimIsItsOwnKindInTheRollUp()
    {
        var summary = Sts2SpineGeoClipOwnership.Summarise(
            Enumerable.Repeat(
                    Sts2SpineGeoClipOwnership.AtlasClaim(Sts2SpineGeoClipAtlas.MatchExact), 40)
                .Concat(Enumerable.Repeat(
                    Sts2SpineGeoClipOwnership.AtlasSlotColorClaim(Sts2SpineGeoClipAtlas.MatchExact), 3)));

        Assert.Equal("atlas:uv-region-exact=40 atlas+slot-color:uv-region-exact=3", summary);
    }

    // ---- Fixtures -------------------------------------------------------------------------------------------------

    private static SpineAtlasDocument FlailAtlas()
        => Atlas(FlailPageWidth, FlailPageHeight, ("circ_rotator", FlailCircRotator));

    private static SpineAtlasDocument SpectralAtlas()
        => Atlas(SpectralPageWidth, SpectralPageHeight, ("sword swish", SpectralSwordSwish));

    private static SpineAtlasDocument Atlas(
        int pageWidth,
        int pageHeight,
        params (string Name, (int X, int Y, int Width, int Height) Bounds)[] regions)
    {
        var text = $"page.png\nsize: {pageWidth}, {pageHeight}\n";
        foreach (var (name, bounds) in regions)
        {
            text += $"{name}\nbounds: {bounds.X}, {bounds.Y}, {bounds.Width}, {bounds.Height}\n";
        }

        return Sts2SpineAtlasText.Parse(text);
    }

    // The uv box a mesh drawing that region addresses — identical for every one of the tied candidates, which is
    // the whole defect.
    private static Sts2SpineGeoClipAtlas.MeshUvBox FlailBox(int order)
        => Box(order, FlailCircRotator, FlailPageWidth, FlailPageHeight);

    private static Sts2SpineGeoClipAtlas.MeshUvBox SpectralBox(int order)
        => Box(order, SpectralSwordSwish, SpectralPageWidth, SpectralPageHeight);

    private static Sts2SpineGeoClipAtlas.MeshUvBox Box(
        int order,
        (int X, int Y, int Width, int Height) region,
        int pageWidth,
        int pageHeight)
        => new(
            order,
            (double)region.X / pageWidth,
            (double)region.Y / pageHeight,
            (double)(region.X + region.Width) / pageWidth,
            (double)(region.Y + region.Height) / pageHeight);

    // The colour the renderer writes into a mesh's vertices from its slot.
    private static Sts2SpineGeoClipSlotColorIdentity.MeshColor Mesh(
        int order,
        Sts2SpineGeoClipSlotColorIdentity.SlotColor from)
        => new(order, from.R, from.G, from.B, from.A);
}
