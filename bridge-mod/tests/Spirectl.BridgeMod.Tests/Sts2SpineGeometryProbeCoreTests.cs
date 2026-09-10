using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// Pure, Godot-free core of the env-gated spine-geometry probe. The live battery (RenderingServer mesh
// readback, the spine ClassDB hops) needs a running game, but the pieces of reasoning it rests on do not:
// how a Godot RID id splits into (validator, index) and what candidate space two bracketing RIDs imply
// (offline), what window a live host can build out of the canvas-item RIDs it CAN read plus one mesh of its
// own (recipe D), how much of a slot's motion a least-squares 2D affine explains (the rigid-vs-deforming
// verdict), and how the text Spine atlas parses into a region table. Those are exercised here directly.
public sealed class Sts2SpineGeometryProbeCoreTests
{
    // ── RID arithmetic ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DecodeRid_SplitsHighValidatorFromLowIndex()
    {
        var parts = Sts2SpineGeometryMath.DecodeRid(0x0000_2A3B_0000_01F4uL);
        Assert.Equal(0x2A3Bu, parts.Validator);
        Assert.Equal(0x1F4u, parts.Index);
    }

    [Fact]
    public void ComposeRid_RoundTripsDecode()
    {
        const uint validator = 987_654u;
        const uint index = 4_321u;
        var id = Sts2SpineGeometryMath.ComposeRid(validator, index);
        var parts = Sts2SpineGeometryMath.DecodeRid(id);
        Assert.Equal(validator, parts.Validator);
        Assert.Equal(index, parts.Index);
    }

    // The bracket is inclusive on both validator ends, and the index range is widened symmetrically because a
    // per-owner slot index is only ROUGHLY monotonic (freed slots get reused).
    [Fact]
    public void PlanRidCandidates_SpansBothBracketsAndWidensTheIndexRange()
    {
        var low = Sts2SpineGeometryMath.ComposeRid(100, 500);
        var high = Sts2SpineGeometryMath.ComposeRid(104, 510);

        var plan = Sts2SpineGeometryMath.PlanRidCandidates(low, high, indexSlack: 2, cap: 1_000_000);

        Assert.Equal(100u, plan.ValidatorLow);
        Assert.Equal(104u, plan.ValidatorHigh);
        Assert.Equal(498u, plan.IndexLow);
        Assert.Equal(512u, plan.IndexHigh);
        Assert.Equal(5L * 15L, plan.TotalCandidates);
        Assert.False(plan.Truncated);
        Assert.Equal(75L, plan.EmittedCount);
    }

    // The brackets may arrive in either order (and an index below the slack must clamp at zero rather than
    // wrapping around the unsigned range).
    [Fact]
    public void PlanRidCandidates_IsOrderAgnosticAndClampsIndexAtZero()
    {
        var low = Sts2SpineGeometryMath.ComposeRid(7, 3);
        var high = Sts2SpineGeometryMath.ComposeRid(5, 1);

        var plan = Sts2SpineGeometryMath.PlanRidCandidates(high, low, indexSlack: 64, cap: 1_000_000);

        Assert.Equal(5u, plan.ValidatorLow);
        Assert.Equal(7u, plan.ValidatorHigh);
        Assert.Equal(0u, plan.IndexLow);
        Assert.Equal(67u, plan.IndexHigh);
    }

    [Fact]
    public void PlanRidCandidates_MarksTruncationAndStopsEnumerationAtTheCap()
    {
        var low = Sts2SpineGeometryMath.ComposeRid(1, 0);
        var high = Sts2SpineGeometryMath.ComposeRid(1_000, 0);

        var plan = Sts2SpineGeometryMath.PlanRidCandidates(low, high, indexSlack: 64, cap: 50);

        Assert.True(plan.Truncated);
        Assert.Equal(50L, plan.EmittedCount);
        Assert.Equal(50, Sts2SpineGeometryMath.EnumerateRidCandidates(plan).Count());
    }

    // Validator-major: every index of a validator is offered before moving to the next validator, so a sweep
    // that bails early still covers whole validators.
    [Fact]
    public void EnumerateRidCandidates_WalksValidatorMajor()
    {
        var low = Sts2SpineGeometryMath.ComposeRid(10, 20);
        var high = Sts2SpineGeometryMath.ComposeRid(11, 20);
        var plan = Sts2SpineGeometryMath.PlanRidCandidates(low, high, indexSlack: 1, cap: 1_000);

        var ids = Sts2SpineGeometryMath.EnumerateRidCandidates(plan).ToArray();

        Assert.Equal(6, ids.Length);
        Assert.Equal(
            [
                Sts2SpineGeometryMath.ComposeRid(10, 19),
                Sts2SpineGeometryMath.ComposeRid(10, 20),
                Sts2SpineGeometryMath.ComposeRid(10, 21),
                Sts2SpineGeometryMath.ComposeRid(11, 19),
                Sts2SpineGeometryMath.ComposeRid(11, 20),
                Sts2SpineGeometryMath.ComposeRid(11, 21),
            ],
            ids);
    }

    // ── Recipe D: the live candidate window ──────────────────────────────────────────────────────────

    // Live there is no lower bracket to stand on, so the window opens one validator past the HIGHEST
    // canvas-item validator the sprite's own mesh children carry, and closes on a mesh the probe mints now.
    // The index axis is bounded by that probe mesh's per-owner index (plus slack, because a freed slot gets
    // reused and a skeleton mesh can therefore sit above it) and starts at zero.
    [Fact]
    public void PlanCanvasItemBracket_OpensJustPastTheHighestCanvasItemValidator()
    {
        ulong[] canvasItems =
        [
            Sts2SpineGeometryMath.ComposeRid(100, 9),
            Sts2SpineGeometryMath.ComposeRid(103, 4),
            Sts2SpineGeometryMath.ComposeRid(101, 7),
        ];
        ulong[] probes = [Sts2SpineGeometryMath.ComposeRid(140, 40)];

        var plan = Sts2SpineProbeMath.PlanCanvasItemBracket(canvasItems, probes, indexSlack: 10, cap: 1_000_000);

        Assert.True(plan.Viable);
        Assert.Equal(100u, plan.CanvasValidatorLow);
        Assert.Equal(103u, plan.CanvasValidatorHigh);
        Assert.Equal(140u, plan.ProbeValidator);
        Assert.Equal(40u, plan.ProbeIndex);
        Assert.Equal(104u, plan.Candidates.ValidatorLow);
        Assert.Equal(140u, plan.Candidates.ValidatorHigh);
        Assert.Equal(0u, plan.Candidates.IndexLow);
        Assert.Equal(50u, plan.Candidates.IndexHigh);
        Assert.Equal(51L, plan.IndexSpan);
        Assert.Equal(37L * 51L, plan.Candidates.TotalCandidates);
        Assert.False(plan.Candidates.Truncated);
        Assert.Equal(37L, plan.ValidatorsCovered);
    }

    // The measured mesh offset is a few dozen validators past the canvas items, and the enumeration is
    // validator-major ascending — so the meshes sit at the FRONT of the sweep and a cap only discards the far
    // end. That ordering is the whole reason a truncated live sweep is still evidence.
    [Fact]
    public void PlanCanvasItemBracket_SweepsTheMeshNeighbourhoodBeforeTheCapBites()
    {
        ulong[] canvasItems = [Sts2SpineGeometryMath.ComposeRid(1_000, 3)];
        ulong[] probes = [Sts2SpineGeometryMath.ComposeRid(9_000_000, 9)];

        var plan = Sts2SpineProbeMath.PlanCanvasItemBracket(canvasItems, probes, indexSlack: 0, cap: 500);

        Assert.True(plan.Viable);
        Assert.True(plan.Candidates.Truncated);
        Assert.Equal(500L, plan.Candidates.EmittedCount);
        Assert.Equal(10L, plan.IndexSpan);
        Assert.Equal(50L, plan.ValidatorsCovered);

        var ids = Sts2SpineGeometryMath.EnumerateRidCandidates(plan.Candidates).ToArray();
        Assert.Equal(Sts2SpineGeometryMath.ComposeRid(1_001, 0), ids[0]);
        Assert.Equal(Sts2SpineGeometryMath.ComposeRid(1_050, 9), ids[^1]);
        // A mesh minted at the measured offset past the canvas items is inside the truncated sweep.
        Assert.Contains(
            Sts2SpineGeometryMath.ComposeRid(1_000 + Sts2SpineProbeMath.MeasuredMeshValidatorOffset, 4),
            ids);
    }

    // A cap too small to clear the measured offset produces an honest-looking zero, so the plan carries what a
    // sufficient cap would have been.
    [Fact]
    public void PlanCanvasItemBracket_RecommendsACapThatClearsTheMeasuredOffset()
    {
        ulong[] canvasItems = [Sts2SpineGeometryMath.ComposeRid(10, 0)];
        ulong[] probes = [Sts2SpineGeometryMath.ComposeRid(500_000, 999)];

        var plan = Sts2SpineProbeMath.PlanCanvasItemBracket(canvasItems, probes, indexSlack: 0, cap: 1_000);

        Assert.Equal(1_000L, plan.IndexSpan);
        Assert.Equal(1L, plan.ValidatorsCovered);
        Assert.Equal(1_000L * Sts2SpineProbeMath.RecommendedValidatorDepth, plan.RecommendedCap);
        Assert.True(
            plan.RecommendedCap > plan.IndexSpan * Sts2SpineProbeMath.MeasuredMeshValidatorOffset,
            "the recommendation must clear the measured mesh offset with headroom");
    }

    [Fact]
    public void PlanCanvasItemBracket_RefusesWithoutBothEdges()
    {
        ulong[] one = [Sts2SpineGeometryMath.ComposeRid(5, 1)];

        var noCanvas = Sts2SpineProbeMath.PlanCanvasItemBracket([], one, indexSlack: 8, cap: 1_000);
        Assert.False(noCanvas.Viable);
        Assert.Equal(0L, noCanvas.Candidates.EmittedCount);
        Assert.False(noCanvas.Candidates.Truncated);
        Assert.Contains("lower edge", noCanvas.Reason, StringComparison.Ordinal);

        var noProbe = Sts2SpineProbeMath.PlanCanvasItemBracket(one, [], indexSlack: 8, cap: 1_000);
        Assert.False(noProbe.Viable);
        Assert.Contains("upper edge", noProbe.Reason, StringComparison.Ordinal);
    }

    // The probe mesh is created after the fact, so its validator MUST outrank the canvas items. If it does
    // not, the validator counter is not what the whole recipe assumes and the plan refuses rather than sweeping
    // an inverted window.
    [Fact]
    public void PlanCanvasItemBracket_RefusesAnInvertedWindow()
    {
        ulong[] canvasItems = [Sts2SpineGeometryMath.ComposeRid(900, 1)];
        ulong[] probes = [Sts2SpineGeometryMath.ComposeRid(900, 2)];

        var plan = Sts2SpineProbeMath.PlanCanvasItemBracket(canvasItems, probes, indexSlack: 8, cap: 1_000);

        Assert.False(plan.Viable);
        Assert.Equal(0L, plan.Candidates.EmittedCount);
        Assert.Equal(900u, plan.CanvasValidatorHigh);
        Assert.Equal(900u, plan.ProbeValidator);
    }

    // The offline dry run: recipe A's validated RIDs measured against the window recipe D would have built
    // from the same skeleton's canvas items. This is what lets an offline report say whether the live recipe
    // is sound before a game session is spent on it.
    [Fact]
    public void CheckCanvasBracketCoverage_CountsKnownMeshRidsInsideTheWindow()
    {
        ulong[] canvasItems = [Sts2SpineGeometryMath.ComposeRid(200, 3)];
        ulong[] probes = [Sts2SpineGeometryMath.ComposeRid(400, 60)];
        var plan = Sts2SpineProbeMath.PlanCanvasItemBracket(canvasItems, probes, indexSlack: 4, cap: 1_000_000);

        ulong[] meshes =
        [
            Sts2SpineGeometryMath.ComposeRid(228, 40),
            Sts2SpineGeometryMath.ComposeRid(229, 41),
            Sts2SpineGeometryMath.ComposeRid(150, 42), // minted before the canvas items: outside
        ];

        var (contained, total, offset) = Sts2SpineProbeMath.CheckCanvasBracketCoverage(plan, meshes);

        Assert.Equal(2, contained);
        Assert.Equal(3, total);
        Assert.Equal(0u, offset); // the lowest mesh validator is BELOW the canvas edge, so there is no offset
    }

    [Fact]
    public void CheckCanvasBracketCoverage_ReportsTheOffsetFromTheCanvasEdge()
    {
        ulong[] canvasItems = [Sts2SpineGeometryMath.ComposeRid(200, 3)];
        ulong[] probes = [Sts2SpineGeometryMath.ComposeRid(400, 60)];
        var plan = Sts2SpineProbeMath.PlanCanvasItemBracket(canvasItems, probes, indexSlack: 4, cap: 1_000_000);

        ulong[] meshes =
        [
            Sts2SpineGeometryMath.ComposeRid(228, 40),
            Sts2SpineGeometryMath.ComposeRid(240, 41),
        ];

        var (contained, total, offset) = Sts2SpineProbeMath.CheckCanvasBracketCoverage(plan, meshes);

        Assert.Equal(2, contained);
        Assert.Equal(2, total);
        Assert.Equal(28u, offset);
    }

    // ── Affine fit ───────────────────────────────────────────────────────────────────────────────────

    // A slot that merely moved is explained exactly by an affine map, so the residual is zero: this is the
    // "rigid, replay with a transform" case.
    [Fact]
    public void FitAffine2D_RecoversAPureTranslationWithNoResidual()
    {
        double[] x = [0, 10, 10, 0];
        double[] y = [0, 0, 20, 20];
        var targetX = x.Select(value => value + 7.5).ToArray();
        var targetY = y.Select(value => value - 3.25).ToArray();

        var fit = Sts2SpineGeometryMath.FitAffine2D(x, y, targetX, targetY);

        Assert.False(fit.Degenerate);
        Assert.Equal(1.0, fit.A, 6);
        Assert.Equal(0.0, fit.B, 6);
        Assert.Equal(7.5, fit.C, 6);
        Assert.Equal(0.0, fit.D, 6);
        Assert.Equal(1.0, fit.E, 6);
        Assert.Equal(-3.25, fit.F, 6);
        Assert.True(fit.MaxResidual < 1e-6);
    }

    [Fact]
    public void FitAffine2D_RecoversRotationAndScaleWithNoResidual()
    {
        double[] x = [0, 100, 100, 0, 50];
        double[] y = [0, 0, 60, 60, 30];
        // 90 degrees plus a 2x scale, plus a translation.
        var targetX = x.Zip(y, (px, py) => (-2 * py) + 11).ToArray();
        var targetY = x.Zip(y, (px, py) => (2 * px) - 4).ToArray();

        var fit = Sts2SpineGeometryMath.FitAffine2D(x, y, targetX, targetY);

        Assert.False(fit.Degenerate);
        Assert.True(fit.MaxResidual < 1e-6, $"expected an exact affine fit, got {fit.MaxResidual}");
        Assert.True(fit.RmsResidual < 1e-6);
    }

    // A genuine deformation (one vertex pulled away from the rigid map) cannot be absorbed by any affine, so
    // the residual survives — this is the "must stream vertices" case.
    [Fact]
    public void FitAffine2D_ReportsResidualWhenOneVertexBreaksTheAffineMap()
    {
        double[] x = [0, 100, 100, 0];
        double[] y = [0, 0, 100, 100];
        double[] targetX = [0, 100, 100, 0];
        double[] targetY = [0, 0, 100, 140];

        var fit = Sts2SpineGeometryMath.FitAffine2D(x, y, targetX, targetY);

        Assert.True(fit.MaxResidual > 1.0, $"expected a visible residual, got {fit.MaxResidual}");
    }

    // Fewer than three points (or collinear ones) cannot pin a 6-parameter affine; the fit says so instead of
    // returning a confident-looking garbage matrix.
    [Fact]
    public void FitAffine2D_FallsBackToTranslationWhenTheSystemIsSingular()
    {
        double[] x = [0, 10];
        double[] y = [0, 0];
        double[] targetX = [5, 15];
        double[] targetY = [5, 5];

        var fit = Sts2SpineGeometryMath.FitAffine2D(x, y, targetX, targetY);

        Assert.True(fit.Degenerate);
        Assert.Equal(5.0, fit.C, 6);
        Assert.Equal(5.0, fit.F, 6);
        Assert.True(fit.MaxResidual < 1e-6);
    }

    [Fact]
    public void FitAffine2D_HandlesAnEmptyPointSet()
    {
        var fit = Sts2SpineGeometryMath.FitAffine2D([], [], [], []);

        Assert.True(fit.Degenerate);
        Assert.Equal(0.0, fit.MaxResidual);
    }

    [Fact]
    public void BoundingBoxDiagonal_MeasuresTheSlotsOwnScale()
    {
        double[] x = [0, 3, 3, 0];
        double[] y = [0, 0, 4, 4];

        Assert.Equal(5.0, Sts2SpineGeometryMath.BoundingBoxDiagonal(x, y), 6);
        Assert.Equal(0.0, Sts2SpineGeometryMath.BoundingBoxDiagonal([], []), 6);
    }

    // Normalizing by the slot's own diagonal is what makes one threshold mean the same thing for a small hand
    // and a large torso.
    [Fact]
    public void ClassifyRigidity_NormalizesTheResidualAgainstTheSlotSize()
    {
        var small = Sts2SpineGeometryMath.ClassifyRigidity(0.5, diagonal: 100, threshold: 0.01);
        Assert.Equal(0.005, small.NormalizedResidual, 9);
        Assert.True(small.IsRigid);

        var large = Sts2SpineGeometryMath.ClassifyRigidity(5, diagonal: 100, threshold: 0.01);
        Assert.Equal(0.05, large.NormalizedResidual, 9);
        Assert.False(large.IsRigid);
    }

    // A collapsed slot has no scale to normalize against; only a zero residual may still be called rigid.
    [Fact]
    public void ClassifyRigidity_TreatsAZeroSizedSlotConservatively()
    {
        Assert.True(Sts2SpineGeometryMath.ClassifyRigidity(0, diagonal: 0, threshold: 0.01).IsRigid);
        Assert.False(Sts2SpineGeometryMath.ClassifyRigidity(1, diagonal: 0, threshold: 0.01).IsRigid);
    }

    // ── Atlas parser ─────────────────────────────────────────────────────────────────────────────────

    // The legacy libgdx spelling: xy / size / orig / offset, boolean rotate.
    [Fact]
    public void ParseAtlas_ReadsTheLegacyRegionSpelling()
    {
        const string atlas = """

            character.png
            size: 2048,1024
            format: RGBA8888
            filter: Linear,Linear
            repeat: none
            head
              rotate: false
              xy: 2, 2
              size: 100, 200
              orig: 110, 210
              offset: 5, 6
              index: -1
            sword
              rotate: true
              xy: 104, 2
              size: 40, 30
              orig: 40, 30
              offset: 0, 0
              index: 3
            """;

        var document = Sts2SpineAtlasText.Parse(atlas);

        var page = Assert.Single(document.Pages);
        Assert.Equal("character.png", page.Name);
        Assert.Equal(2048, page.Width);
        Assert.Equal(1024, page.Height);
        Assert.Equal("RGBA8888", page.Format);
        Assert.Equal("Linear,Linear", page.Filter);
        Assert.Equal("none", page.Repeat);
        Assert.Equal(1d, page.Scale);
        Assert.False(page.PremultipliedAlpha);

        Assert.Equal(2, document.Regions.Count);

        var head = document.Regions[0];
        Assert.Equal("head", head.Name);
        Assert.Equal("character.png", head.Page);
        Assert.Equal(0, head.PageIndex);
        Assert.Equal(2, head.X);
        Assert.Equal(2, head.Y);
        Assert.Equal(100, head.Width);
        Assert.Equal(200, head.Height);
        Assert.Equal(110, head.OriginalWidth);
        Assert.Equal(210, head.OriginalHeight);
        Assert.Equal(5, head.OffsetX);
        Assert.Equal(6, head.OffsetY);
        Assert.Equal(0, head.Rotate);
        Assert.Equal(-1, head.Index);

        var sword = document.Regions[1];
        Assert.Equal(90, sword.Rotate);
        Assert.Equal(3, sword.Index);
    }

    // The 4.x spelling: one bounds tuple, one offsets tuple, numeric rotate, pma on the page.
    [Fact]
    public void ParseAtlas_ReadsTheModernRegionSpelling()
    {
        const string atlas = """
            monster.png
            size:1024,1024
            filter:Linear,Linear
            pma:true
            scale:0.5
            body
            bounds:10,20,64,128
            offsets:3,4,70,140
            rotate:90
            index:2
            """;

        var document = Sts2SpineAtlasText.Parse(atlas);

        var page = Assert.Single(document.Pages);
        Assert.True(page.PremultipliedAlpha);
        Assert.Equal(0.5d, page.Scale);

        var body = Assert.Single(document.Regions);
        Assert.Equal("body", body.Name);
        Assert.Equal(10, body.X);
        Assert.Equal(20, body.Y);
        Assert.Equal(64, body.Width);
        Assert.Equal(128, body.Height);
        Assert.Equal(3, body.OffsetX);
        Assert.Equal(4, body.OffsetY);
        Assert.Equal(70, body.OriginalWidth);
        Assert.Equal(140, body.OriginalHeight);
        Assert.Equal(90, body.Rotate);
        Assert.Equal(2, body.Index);
    }

    // A blank line closes a page: the next bare line names a new page, not another region of the old one.
    [Fact]
    public void ParseAtlas_StartsANewPageAfterABlankLine()
    {
        const string atlas = """
            page_one.png
            size:64,64
            alpha
            bounds:0,0,8,8

            page_two.png
            size:128,128
            beta
            bounds:1,1,16,16
            gamma
            bounds:20,1,16,16
            """;

        var document = Sts2SpineAtlasText.Parse(atlas);

        Assert.Equal(2, document.Pages.Count);
        Assert.Equal("page_one.png", document.Pages[0].Name);
        Assert.Equal("page_two.png", document.Pages[1].Name);

        Assert.Equal(3, document.Regions.Count);
        Assert.Equal("page_one.png", document.Regions[0].Page);
        Assert.Equal(0, document.Regions[0].PageIndex);
        Assert.Equal("page_two.png", document.Regions[1].Page);
        Assert.Equal(1, document.Regions[1].PageIndex);
        Assert.Equal("gamma", document.Regions[2].Name);
        Assert.Equal(1, document.Regions[2].PageIndex);
    }

    // Unmodelled keys are carried through verbatim rather than dropped, so the probe's dump never loses a
    // field the parser did not anticipate.
    [Fact]
    public void ParseAtlas_KeepsUnknownFieldsVerbatim()
    {
        const string atlas = """
            page.png
            size:32,32
            thing
            bounds:0,0,4,4
            split:1,2,3,4
            """;

        var region = Assert.Single(Sts2SpineAtlasText.Parse(atlas).Regions);
        Assert.Equal("1,2,3,4", region.Fields["split"]);
    }

    [Fact]
    public void ParseAtlas_ToleratesEmptyAndNullInput()
    {
        Assert.Empty(Sts2SpineAtlasText.Parse(null).Regions);
        Assert.Empty(Sts2SpineAtlasText.Parse(string.Empty).Pages);
        Assert.Empty(Sts2SpineAtlasText.Parse("   \n\n  ").Pages);
    }

    // ── Recipe D live acquisition: anchor scan, stride walk, trim ────────────────────────────────────
    //
    // Everything below is exercised against Phase1LiveRun (bottom of this file): the REAL numbers the Phase-1
    // live battery recorded off the main menu — 33 meshes on an exact +5/+1 progression, the four contaminants
    // the full sweep also dragged in, the target's skeleton bounds, and the window the canvas bracket built.
    // Using the measurement rather than a tidy synthetic is the point: the 256x256 quad that no plausibility
    // test can reject, and the build-order/bounds disagreement, are both properties of that data.

    // ── The anchor step ──────────────────────────────────────────────────────────────────────────────

    // The whole scan rests on this: sample the validator axis with a step COPRIME to the run's stride and any
    // `stride` consecutive samples cover every residue class, so one of them must land on a member.
    [Theory]
    [InlineData(26, 5u, 24u)]
    [InlineData(26, 1u, 25u)]
    [InlineData(26, 7u, 25u)]
    [InlineData(34, 5u, 33u)]
    [InlineData(10, 5u, 9u)]
    [InlineData(2, 5u, 1u)]
    [InlineData(1, 5u, 1u)]
    public void ChooseAnchorValidatorStep_IsTheLargestStepCoprimeToTheStride(
        int expectedRunLength,
        uint stride,
        uint expectedStep)
    {
        var step = Sts2SpineProbeMath.ChooseAnchorValidatorStep(expectedRunLength, stride);

        Assert.Equal(expectedStep, step);
        Assert.True(step <= Math.Max(1, expectedRunLength - 1), "the step must fit inside the run's own span");
        Assert.Equal(1u, Gcd(step, Math.Max(1u, stride)));
    }

    // The failure this rule exists to prevent, stated as a theorem rather than a war story: when the step is a
    // MULTIPLE of the stride, every sample sits in the same residue class as the first one, so a run whose
    // members sit in any other class is invisible no matter how long the scan runs.
    [Theory]
    [InlineData(5u, 65u)]
    [InlineData(5u, 130u)]
    [InlineData(5u, 5u)]
    public void AnAnchorStepThatIsAMultipleOfTheStrideCanMissEveryMember(uint stride, uint step)
    {
        const uint scanLow = 4_484;
        // The Phase-1 run's own offset from the window's low edge: 11541 - 4484 = 7057, which is 2 (mod 5).
        const uint runLow = 11_541;
        var hit = false;
        for (var validator = scanLow; validator <= 18_691; validator += step)
        {
            for (var member = 0; member < 33; member += 1)
            {
                hit |= validator == runLow + ((uint)member * stride);
            }
        }

        Assert.False(hit, "a step that is a multiple of the stride never samples this run");
        Assert.NotEqual(0u, Sts2SpineProbeMath.ChooseAnchorValidatorStep(26, stride) % stride);
    }

    // ── The anchor scan plan ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PlanAnchorScan_GuaranteesAHitOnThePhase1LiveRun()
    {
        var plan = Phase1LiveRun.AnchorScan();

        Assert.True(plan.Viable);
        Assert.Equal(4_484u, plan.ValidatorLow);
        Assert.Equal(18_692u, plan.ValidatorHigh);
        Assert.Equal(24u, plan.ValidatorStep);
        Assert.Equal(0u, plan.IndexLow);
        Assert.Equal(346u, plan.IndexHigh);
        Assert.Equal(593L, plan.SampledValidators);
        Assert.Equal(205_771L, plan.TotalCandidates);
        Assert.Equal(4_930_523L, plan.FullWindowCandidates);
        Assert.False(plan.Truncated);

        var runIds = Phase1LiveRun.Run.Select(mesh => mesh.Id).ToHashSet();
        var hits = Sts2SpineProbeMath.EnumerateAnchorScanCandidates(plan)
            .Where(runIds.Contains)
            .ToArray();

        // Exactly one member of the real run sits on a sampled validator, and the arithmetic says which:
        // 11541 + 5*19 = 11636, index 46 + 19 = 65. One hit is all the scan needs.
        var anchor = Assert.Single(hits);
        Assert.Equal(Sts2SpineGeometryMath.ComposeRid(11_636, 65), anchor);
    }

    // The guarantee is not a property of where THIS run happened to start. Slide a 26-member run of the same
    // stride to every offset in the window and the scan still lands on it.
    [Fact]
    public void PlanAnchorScan_HitsARunOfTheExpectedLengthAtEveryOffset()
    {
        var plan = Phase1LiveRun.AnchorScan();
        var sampled = new HashSet<uint>();
        for (var validator = plan.ValidatorLow; validator <= plan.ValidatorHigh; validator += plan.ValidatorStep)
        {
            sampled.Add(validator);
        }

        for (uint shift = 0; shift < 240; shift += 1)
        {
            var runLow = plan.ValidatorLow + shift;
            var hit = false;
            for (uint member = 0; member < 26; member += 1)
            {
                hit |= sampled.Contains(runLow + (member * Sts2SpineGeometryMath.DefaultLiveValidatorStride));
            }

            Assert.True(hit, $"a 26-member +5 run starting at validator {runLow} was never sampled");
        }
    }

    [Fact]
    public void PlanAnchorScan_CostsFarLessThanAQuarterOfTheFullWindow()
    {
        var plan = Phase1LiveRun.AnchorScan();

        Assert.True(
            plan.TotalCandidates * 4 < plan.FullWindowCandidates,
            $"expected the coarse scan to cost under a quarter of the full window, got {plan.TotalCandidates} "
            + $"vs {plan.FullWindowCandidates}");
    }

    // The index axis is NOT sampled: a run's index moves by one per member, so striding it as well would slice
    // the lattice into lines that miss the run entirely.
    [Fact]
    public void EnumerateAnchorScanCandidates_TestsEveryIndexAtEachSampledValidator()
    {
        var bracket = Sts2SpineProbeMath.PlanCanvasItemBracket(
            [Sts2SpineGeometryMath.ComposeRid(100, 0)],
            [Sts2SpineGeometryMath.ComposeRid(110, 2)],
            indexSlack: 0,
            cap: 1_000_000);
        var plan = Sts2SpineProbeMath.PlanAnchorScan(bracket, 5, stride: 5, stepOverride: 0, cap: 1_000_000);

        Assert.Equal(4u, plan.ValidatorStep);
        var ids = Sts2SpineProbeMath.EnumerateAnchorScanCandidates(plan).ToArray();

        // Validators 101, 105, 109 (from 101 up to 110 in steps of 4) x indices 0..2.
        Assert.Equal(9, ids.Length);
        Assert.Equal(Sts2SpineGeometryMath.ComposeRid(101, 0), ids[0]);
        Assert.Equal(Sts2SpineGeometryMath.ComposeRid(101, 2), ids[2]);
        Assert.Equal(Sts2SpineGeometryMath.ComposeRid(105, 0), ids[3]);
        Assert.Equal(Sts2SpineGeometryMath.ComposeRid(109, 2), ids[^1]);
    }

    [Fact]
    public void PlanAnchorScan_RefusesAnUnviableBracket()
    {
        var bracket = Sts2SpineProbeMath.PlanCanvasItemBracket([], [], indexSlack: 0, cap: 1_000);

        var plan = Sts2SpineProbeMath.PlanAnchorScan(bracket, 26, 5, 0, 1_000);

        Assert.False(plan.Viable);
        Assert.Empty(Sts2SpineProbeMath.EnumerateAnchorScanCandidates(plan));
    }

    // ── The stride ladder ────────────────────────────────────────────────────────────────────────────

    // Measured off the real run: from any member, only delta 5 produces BOTH (v+5, i+1) and (v+10, i+2) — the
    // three-point progression. Any two points are collinear, which is why one confirmation is not enough.
    [Fact]
    public void ChooseValidatorStride_MeasuresFiveOnThePhase1Ladder()
    {
        var runIds = Phase1LiveRun.Run.Select(mesh => mesh.Id).ToHashSet();
        var anchor = Phase1LiveRun.Run[10];
        var singles = new List<uint>();
        var doubles = new List<uint>();
        for (uint delta = 1; delta <= 24; delta += 1)
        {
            if (runIds.Contains(Sts2SpineGeometryMath.ComposeRid(anchor.Validator + delta, anchor.Index + 1)))
            {
                singles.Add(delta);
            }

            if (runIds.Contains(Sts2SpineGeometryMath.ComposeRid(anchor.Validator + (2 * delta), anchor.Index + 2)))
            {
                doubles.Add(delta);
            }
        }

        var choice = Sts2SpineGeometryMath.ChooseValidatorStride(singles, doubles, fallback: 9_999);

        Assert.Equal([5u], singles);
        Assert.Equal([5u], doubles);
        Assert.Equal(5u, choice.ValidatorStride);
        Assert.Equal(1u, choice.IndexStride);
        Assert.Equal("measured", choice.Source);
    }

    [Fact]
    public void ChooseValidatorStride_PrefersTheSmallestDoubleConfirmedDelta()
    {
        // 3 validated once (a coincidence) but never confirmed; 5 and 11 both did — the smaller wins.
        var choice = Sts2SpineGeometryMath.ChooseValidatorStride([3u, 5u, 11u], [5u, 11u], fallback: 5);

        Assert.Equal(5u, choice.ValidatorStride);
        Assert.Equal("measured", choice.Source);
        Assert.Equal([3u, 5u, 11u], choice.ConfirmedDeltas);
    }

    [Fact]
    public void ChooseValidatorStride_FallsBackWhenNothingIsDoubleConfirmed()
    {
        var nothing = Sts2SpineGeometryMath.ChooseValidatorStride([], [], fallback: 5);
        Assert.Equal(5u, nothing.ValidatorStride);
        Assert.Equal("default", nothing.Source);

        // A single neighbour is not a progression, so it does not get to set the stride either.
        var lonely = Sts2SpineGeometryMath.ChooseValidatorStride([7u], [], fallback: 5);
        Assert.Equal(5u, lonely.ValidatorStride);
        Assert.Equal("default", lonely.Source);
    }

    [Theory]
    [InlineData("5,1", true, 5u, 1u)]
    [InlineData("7", true, 7u, 1u)]
    [InlineData(" 5 , 2 ", true, 5u, 2u)]
    [InlineData("", false, 5u, 1u)]
    [InlineData(null, false, 5u, 1u)]
    [InlineData("0,1", false, 5u, 1u)]
    [InlineData("nonsense", false, 5u, 1u)]
    public void TryParseStrideOverride_ReadsTheEnvSpelling(
        string? text,
        bool expected,
        uint expectedValidator,
        uint expectedIndex)
    {
        var parsed = Sts2SpineProbeMath.TryParseStrideOverride(text, out var validator, out var index);

        Assert.Equal(expected, parsed);
        Assert.Equal(expectedValidator, validator);
        Assert.Equal(expectedIndex, index);
    }

    // ── The walk ─────────────────────────────────────────────────────────────────────────────────────

    // The point of anchoring: ANY member recovers the whole run, so the scan only has to find one.
    [Fact]
    public void WalkStride_RecoversThePhase1RunFromEveryAnchorPosition()
    {
        var runIds = Phase1LiveRun.Run.Select(mesh => mesh.Id).ToHashSet();
        var expected = Phase1LiveRun.Run.Select(mesh => mesh.Id).ToArray();

        foreach (var mesh in Phase1LiveRun.Run)
        {
            var walk = Sts2SpineGeometryMath.WalkStride(
                new Sts2SpineGeometryMath.RidPoint(mesh.Validator, mesh.Index),
                5,
                1,
                runIds.Contains,
                Phase1LiveRun.Limits());

            Assert.Equal(33, walk.Found.Count);
            Assert.Equal(expected, walk.Found);
            Assert.Empty(walk.GapSizes);
            Assert.Equal("consecutive-misses", walk.StopReasonUp);
            Assert.Equal("consecutive-misses", walk.StopReasonDown);
            // Two dead probes per end (the miss budget), and nothing else beyond the run itself.
            Assert.Equal(32 + 4, walk.Probed);
        }
    }

    // A hole in the progression (a slot whose mesh was freed and re-minted elsewhere, say) is bridged while it
    // fits in the miss budget, and the bridged width is REPORTED so nobody reads a gapped run as contiguous.
    [Fact]
    public void WalkStride_BridgesAGapInsideTheMissBudgetAndReportsItsWidth()
    {
        var withHole = Phase1LiveRun.Run
            .Where(mesh => mesh.Validator != 11_611)
            .Select(mesh => mesh.Id)
            .ToHashSet();

        var bridged = Sts2SpineGeometryMath.WalkStride(
            new Sts2SpineGeometryMath.RidPoint(11_541, 46),
            5,
            1,
            withHole.Contains,
            Phase1LiveRun.Limits());

        Assert.Equal(32, bridged.Found.Count);
        Assert.Equal([1], bridged.GapSizes);
        Assert.Equal("consecutive-misses", bridged.StopReasonUp);

        // With a budget of one, the same hole ends the walk where it starts.
        var strict = Sts2SpineGeometryMath.WalkStride(
            new Sts2SpineGeometryMath.RidPoint(11_541, 46),
            5,
            1,
            withHole.Contains,
            Phase1LiveRun.Limits() with { MaxConsecutiveMisses = 1 });

        Assert.Equal(14, strict.Found.Count);
        Assert.Empty(strict.GapSizes);
    }

    [Fact]
    public void WalkStride_StopsAtTheValidatorEdgesOfTheWindow()
    {
        // Everything validates, so only the limits can stop it.
        var limits = new Sts2SpineGeometryMath.StrideWalkLimits(
            ValidatorFloor: 1_000,
            ValidatorCeiling: 1_100,
            IndexLow: 0,
            IndexHigh: uint.MaxValue - 1,
            MaxRunLength: 10_000,
            MaxConsecutiveMisses: 2);

        var walk = Sts2SpineGeometryMath.WalkStride(
            new Sts2SpineGeometryMath.RidPoint(1_050, 500), 5, 1, _ => true, limits);

        Assert.Equal("validator-ceiling", walk.StopReasonUp);
        Assert.Equal("validator-floor", walk.StopReasonDown);
        Assert.Equal(21, walk.Found.Count); // 1000..1100 inclusive, stepping 5.
    }

    [Fact]
    public void WalkStride_StopsAtTheIndexEdgesOfTheWindow()
    {
        var limits = new Sts2SpineGeometryMath.StrideWalkLimits(
            ValidatorFloor: 0,
            ValidatorCeiling: uint.MaxValue - 1,
            IndexLow: 40,
            IndexHigh: 44,
            MaxRunLength: 10_000,
            MaxConsecutiveMisses: 2);

        var walk = Sts2SpineGeometryMath.WalkStride(
            new Sts2SpineGeometryMath.RidPoint(9_000, 42), 5, 1, _ => true, limits);

        Assert.Equal("index-high", walk.StopReasonUp);
        Assert.Equal("index-low", walk.StopReasonDown);
        Assert.Equal(5, walk.Found.Count);
    }

    [Fact]
    public void WalkStride_StopsAtTheRunLengthBudget()
    {
        var limits = Phase1LiveRun.Limits() with { MaxRunLength = 12 };

        var walk = Sts2SpineGeometryMath.WalkStride(
            new Sts2SpineGeometryMath.RidPoint(11_541, 46), 5, 1, _ => true, limits);

        Assert.Equal(12, walk.Found.Count);
        Assert.Equal("max-run-length", walk.StopReasonUp);
    }

    // THE CONTAMINANT THAT MATTERS. The 256x256 quad at (4489,33) passes every plausibility clause there is —
    // it was attempt 1's only find — so the walk, not the predicate, is what refuses it: nothing sits a stride
    // away in either direction, so the run is one mesh long, well under the minimum, and the scan carries on.
    [Fact]
    public void WalkStride_RefusesThe256QuadBecauseNoRunCanBeWalkedOffIt()
    {
        var quad = Phase1LiveRun.Contaminants[0];
        var everything = Phase1LiveRun.Run
            .Concat(Phase1LiveRun.Contaminants)
            .Select(mesh => mesh.Id)
            .ToHashSet();

        var walk = Sts2SpineGeometryMath.WalkStride(
            new Sts2SpineGeometryMath.RidPoint(quad.Validator, quad.Index),
            5,
            1,
            everything.Contains,
            Phase1LiveRun.Limits());

        Assert.Single(walk.Found);
        Assert.True(
            walk.Found.Count < Sts2SpineProbeMath.MinimumAnchorRunLength(26),
            "a one-mesh run must fall under the minimum, which is what sends the scan back to looking");
    }

    [Theory]
    [InlineData(26, 6)]
    [InlineData(34, 8)]
    [InlineData(10, 3)]
    [InlineData(4, 3)]
    [InlineData(1, 3)]
    public void MinimumAnchorRunLength_IsAQuarterOfTheRigWithAFloor(int expectedRunLength, int expected)
        => Assert.Equal(expected, Sts2SpineProbeMath.MinimumAnchorRunLength(expectedRunLength));

    // ── The trim: build order ────────────────────────────────────────────────────────────────────────

    // Phase 1's three main-menu rigs, in canvas-validator (= build) order: Bg 7, Fg 26 (the target), Logo 2.
    // A 33-member run is explained by exactly one contiguous group — Bg + Fg — so the target's block starts 7
    // meshes in. One feasible offset; that is what makes this signal decisive here.
    [Fact]
    public void FeasibleTargetOffsets_SinglesOutOneOffsetOnThePhase1Rigs()
    {
        var offsets = Sts2SpineProbeMath.FeasibleTargetOffsets(
            33, Phase1LiveRun.ChildCounts, Phase1LiveRun.TargetOrdinal);

        Assert.Equal([7], offsets);
    }

    [Fact]
    public void FeasibleTargetOffsets_ReportsAmbiguityRatherThanPickingOne()
    {
        // Three equal rigs: a run of 8 could be rigs 0+1 (target starts at 4) or rigs 1+2 (target starts at 0).
        var offsets = Sts2SpineProbeMath.FeasibleTargetOffsets(8, [4, 4, 4], targetOrdinal: 1);

        Assert.Equal([0, 4], offsets);
    }

    [Fact]
    public void FeasibleTargetOffsets_ReturnsNothingWhenNoGroupOfRigsSumsToTheRun()
    {
        Assert.Empty(Sts2SpineProbeMath.FeasibleTargetOffsets(5, Phase1LiveRun.ChildCounts, 1));
        Assert.Empty(Sts2SpineProbeMath.FeasibleTargetOffsets(33, Phase1LiveRun.ChildCounts, 9));
        Assert.Empty(Sts2SpineProbeMath.FeasibleTargetOffsets(0, Phase1LiveRun.ChildCounts, 1));
    }

    // ── The trim: bounds ─────────────────────────────────────────────────────────────────────────────

    // The Phase-1 score table, reproduced from the recorded bboxes. Three offsets reproduce the skeleton's
    // reported bounds EXACTLY (L1 0.0) and the build-order answer scores 507.8 — this table is the whole
    // reason the trim is not allowed to trust one signal.
    [Fact]
    public void ChooseOffsetByBounds_ReproducesThePhase1ScoreTable()
    {
        var choice = Sts2SpineProbeMath.ChooseOffsetByBounds(
            Phase1LiveRun.RunBboxes(), 26, Phase1LiveRun.SkeletonBounds);

        Assert.Equal(8, choice.Scores.Count);
        Assert.Equal(112.1407, choice.Scores[0].L1, 3);
        Assert.Equal(112.1407, choice.Scores[1].L1, 3);
        Assert.Equal(227.0, choice.Scores[2].L1, 3);
        Assert.Equal(227.0, choice.Scores[3].L1, 3);
        Assert.Equal(0.0, choice.Scores[4].L1, 6);
        Assert.Equal(0.0, choice.Scores[5].L1, 6);
        Assert.Equal(0.0, choice.Scores[6].L1, 6);
        Assert.Equal(507.8047, choice.Scores[7].L1, 3);

        // One mesh of the first two windows pokes strictly outside the reported bounds; none of the rest do.
        Assert.Equal(1, choice.Scores[0].StrictlyOutside);
        Assert.Equal(0, choice.Scores[7].StrictlyOutside);

        Assert.Equal([4, 5, 6], choice.BestOffsets);
        Assert.True(choice.Tied);
        Assert.Null(choice.Offset);
    }

    [Fact]
    public void ChooseOffsetByBounds_SinglesOutAWindowWhenOneReallyIsBest()
    {
        IReadOnlyList<double>[] boxes =
        [
            [-5_000, -5_000, -4_000, -4_000],
            [0, 0, 10, 10],
            [5, 5, 20, 20],
        ];

        var choice = Sts2SpineProbeMath.ChooseOffsetByBounds(boxes, 2, [0, 0, 20, 20]);

        Assert.Equal(1, choice.Offset);
        Assert.False(choice.Tied);
        Assert.Equal([1], choice.BestOffsets);
    }

    // ── The trim verdict ─────────────────────────────────────────────────────────────────────────────

    // The finding this round is allowed to end on: on the real data the two signals point at different
    // windows, and the lane says so instead of choosing. `Trimmed` stays empty and gate (a) fails honestly,
    // with both answers and the untrimmed run in the report so the question can be settled offline.
    [Fact]
    public void TrimRunToTarget_ReportsTheKnownPhase1DisagreementRatherThanGuessing()
    {
        var trim = Sts2SpineProbeMath.TrimRunToTarget(
            [.. Phase1LiveRun.Run.Select(mesh => mesh.Id)],
            Phase1LiveRun.RunBboxes(),
            Phase1LiveRun.ChildCounts,
            Phase1LiveRun.TargetOrdinal,
            Phase1LiveRun.SkeletonBounds);

        Assert.Equal("disagreement", trim.Method);
        Assert.False(trim.Agreed);
        Assert.Null(trim.Offset);
        Assert.Equal(26, trim.WindowLength);
        Assert.Equal([7], trim.BuildOrderOffsets);
        Assert.Equal([4, 5, 6], trim.Bounds.BestOffsets);
        Assert.Empty(trim.Trimmed);
        Assert.Contains("build order", trim.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void TrimRunToTarget_CutsTheWindowWhenBothSignalsPointAtTheSameOffset()
    {
        // Two rigs: a 2-mesh one built first, then the 3-mesh target. Build order says offset 2; the bounds
        // agree because the first two meshes sit nowhere near the target's reported box.
        ulong[] run =
        [
            Sts2SpineGeometryMath.ComposeRid(10, 0),
            Sts2SpineGeometryMath.ComposeRid(15, 1),
            Sts2SpineGeometryMath.ComposeRid(20, 2),
            Sts2SpineGeometryMath.ComposeRid(25, 3),
            Sts2SpineGeometryMath.ComposeRid(30, 4),
        ];
        IReadOnlyList<double>[] boxes =
        [
            [-900, -900, -800, -800],
            [-880, -880, -790, -790],
            [0, 0, 50, 50],
            [10, 10, 90, 90],
            [20, 20, 100, 100],
        ];

        var trim = Sts2SpineProbeMath.TrimRunToTarget(run, boxes, [2, 3], 1, [0, 0, 100, 100]);

        Assert.True(trim.Agreed);
        Assert.Equal("build-order", trim.Method);
        Assert.Equal(2, trim.Offset);
        Assert.Equal([run[2], run[3], run[4]], trim.Trimmed);
    }

    [Fact]
    public void TrimRunToTarget_RefusesARunShorterThanTheTargetsOwnMeshCount()
    {
        var trim = Sts2SpineProbeMath.TrimRunToTarget(
            [1uL, 2uL],
            [[0d, 0d, 1d, 1d], [0d, 0d, 1d, 1d]],
            [2, 3],
            1,
            [0, 0, 1, 1]);

        Assert.Equal("ambiguous", trim.Method);
        Assert.False(trim.Agreed);
        Assert.Empty(trim.Trimmed);
        Assert.Contains("shorter than", trim.Detail, StringComparison.Ordinal);
    }

    // ── Spine-likeness ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void IsSpineLikeSurface_AcceptsEveryOneOfThePhase1RunsThirtyThreeMeshes()
    {
        foreach (var mesh in Phase1LiveRun.Run)
        {
            var verdict = Sts2SpineProbeMath.IsSpineLikeSurface(mesh.Facts, Phase1LiveRun.SkeletonBounds);
            Assert.True(
                verdict.Accepted,
                $"mesh ({mesh.Validator},{mesh.Index}) was rejected as '{verdict.Reason}'");
        }
    }

    // The three 230-vertex PackedVector3Array meshes some other subsystem owns. They are rejected on vertex
    // type, and INDEPENDENTLY on their UV range — which is the check that matters, because their UVs spanning
    // [-0.48, 1.50] is exactly what failed gate (b) on the Phase-1 run.
    [Fact]
    public void IsSpineLikeSurface_RejectsTheVector3Contaminants()
    {
        foreach (var mesh in Phase1LiveRun.Contaminants.Where(mesh => mesh.VertexVariantType == Vector3))
        {
            var verdict = Sts2SpineProbeMath.IsSpineLikeSurface(mesh.Facts, Phase1LiveRun.SkeletonBounds);
            Assert.False(verdict.Accepted);
            Assert.Equal("vertex-type", verdict.Reason);

            // Even if the engine had handed these back as 2D, the UV range still refuses them.
            var asVector2 = Sts2SpineProbeMath.IsSpineLikeSurface(
                mesh.Facts with { VertexVariantType = Vector2 }, Phase1LiveRun.SkeletonBounds);
            Assert.False(asVector2.Accepted);
            Assert.Equal("uv-out-of-unit-range", asVector2.Reason);
        }
    }

    // THE DOCUMENTED BLIND SPOT, with its real numbers. Nothing about this surface distinguishes it from a
    // slot mesh, so the predicate accepts it and the WALK is what throws it out. Do not "fix" this test by
    // tightening the predicate: a four-vertex region attachment looks exactly like this too.
    [Fact]
    public void IsSpineLikeSurface_CannotRejectThe256QuadThatSitsInsideTheBounds()
    {
        var quad = Phase1LiveRun.Contaminants[0];

        var verdict = Sts2SpineProbeMath.IsSpineLikeSurface(quad.Facts, Phase1LiveRun.SkeletonBounds);

        Assert.Equal(4489u, quad.Validator);
        Assert.Equal(4, quad.VertexCount);
        Assert.Equal([-128d, -128d, 128d, 128d], quad.Bbox);
        Assert.True(verdict.Accepted);
        Assert.Equal("ok", verdict.Reason);
    }

    [Fact]
    public void IsSpineLikeSurface_NamesEachRejectionWithAStableReason()
    {
        var good = Phase1LiveRun.Run[0].Facts;

        Assert.Equal("surface-count", Reject(good with { SurfaceCount = 2 }));
        Assert.Equal("no-vertices", Reject(good with { VertexCount = 0 }));
        Assert.Equal("uv-missing", Reject(good with { UvCount = 0 }));
        Assert.Equal("uv-count-mismatch", Reject(good with { UvCount = 3 }));
        Assert.Equal("uv-out-of-unit-range", Reject(good with { UvMax = [1.4, 1.0] }));
        Assert.Equal("indices-not-triangles", Reject(good with { IndexCount = 7 }));
        Assert.Equal("bbox-outside-skeleton-bounds", Reject(good with { Bbox = [-90_000, 0, 1, 1] }));

        // No bounds to compare against ⇒ the bbox clause simply does not run, rather than rejecting.
        Assert.True(Sts2SpineProbeMath
            .IsSpineLikeSurface(good with { Bbox = [-90_000, 0, 1, 1] }, null).Accepted);

        static string Reject(Sts2SpineProbeMath.SurfaceFacts facts)
        {
            var verdict = Sts2SpineProbeMath.IsSpineLikeSurface(facts, Phase1LiveRun.SkeletonBounds);
            Assert.False(verdict.Accepted);
            return verdict.Reason;
        }
    }

    // The 2% inflation is load-bearing on the real data: one Fg mesh reaches 68 px left of the bounds the
    // skeleton reports, and a strict containment test would throw a genuine slot mesh away.
    [Fact]
    public void IsSpineLikeSurface_TolerancePassesTheMeshThatPokesPastTheReportedBounds()
    {
        var pokesOut = Phase1LiveRun.Run[1];
        Assert.True(pokesOut.Bbox[0] < Phase1LiveRun.SkeletonBounds[0], "this fixture mesh must exceed the bounds");

        Assert.True(Sts2SpineProbeMath.IsSpineLikeSurface(pokesOut.Facts, Phase1LiveRun.SkeletonBounds).Accepted);
        Assert.Equal(
            "bbox-outside-skeleton-bounds",
            Sts2SpineProbeMath
                .IsSpineLikeSurface(pokesOut.Facts, Phase1LiveRun.SkeletonBounds, boundsTolerance: 0)
                .Reason);
    }

    private static uint Gcd(uint a, uint b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a;
    }

    private const string Vector2 = "PackedVector2Array";

    private const string Vector3 = "PackedVector3Array";

    /// <summary>
    /// The Phase-1 live battery's own measurement, transcribed from
    /// <c>data/spine-geoclip-phase1/probe-live/live.json</c>: the 37 meshes the 4.93M-candidate full sweep
    /// validated on the main menu, split into the 33-member +5/+1 run and the four contaminants, plus the
    /// window the canvas bracket built around them and the three rigs' mesh-child counts in build order.
    /// </summary>
    private static class Phase1LiveRun
    {
        internal const uint CanvasValidatorHigh = 4_483;

        internal const uint ProbeValidator = 18_692;

        internal const uint IndexHigh = 346;

        // Bg 7, Fg 26 (the target), Logo 2 — ordered by ascending canvas-item validator, which is build order.
        internal static readonly int[] ChildCounts = [7, 26, 2];

        internal const int TargetOrdinal = 1;

        internal static readonly double[] SkeletonBounds = [-640.4252, -227, 4480.0005, 2287.3984];

        internal static readonly Phase1Mesh[] Run =
        [
            new(11541, 46, 4, Vector2, 6, [0.000752, 0.408498], [0.847194, 0.998882], [-640.0001, -120, 4480, 2280]),
            new(11546, 47, 61, Vector2, 294, [0.000752, 0.083116], [0.847194, 0.406262], [-708.1105, -228.972, 4522.4839, 1413.2412]),
            new(11551, 48, 4, Vector2, 6, [0.848697, 0.294819], [0.95516, 0.638837], [2971.4624, 566, 4370.4624, 1210]),
            new(11556, 49, 4, Vector2, 6, [0.848697, 0.058517], [0.948647, 0.292583], [1896.3103, 633, 2848.3103, 1238]),
            new(11561, 50, 4, Vector2, 6, [0.848697, 0.641073], [0.999249, 0.998882], [-543.8051, 410, 911.1949, 1320]),
            new(11566, 51, 4, Vector2, 6, [0.000752, 0.001118], [0.776303, 0.08088], [-423.5, 764, 4267.5, 1088]),
            new(11571, 52, 4, Vector2, 6, [0.000558, 0.810934], [0.628975, 0.997722], [-639.9999, 1839.1294, 4480.0005, 2212.0342]),
            new(11576, 53, 4, Vector2, 6, [0.630091, 0.126803], [0.999442, 0.997722], [-640.4252, 546.6394, 2369.2861, 2284.6016]),
            new(11581, 54, 15, Vector2, 48, [0.000681, 0.255695], [0.253178, 0.384464], [1849.196, 1923.175, 3972.1958, 2287.3984]),
            new(11586, 55, 4, Vector2, 6, [0.000558, 0.68489], [0.464013, 0.806378], [0.0001, 1885, 3775, 2127]),
            new(11591, 56, 4, Vector2, 6, [0.000558, 0.034928], [0.230798, 0.250569], [2914.448, 0, 3344.3923, 1875]),
            new(11596, 57, 4, Vector2, 6, [0.254417, 0.34776], [0.454901, 0.466211], [2998.3923, 111, 3234.3923, 1744]),
            new(11601, 58, 4, Vector2, 6, [0.231914, 0.113136], [0.431839, 0.230068], [2999.9084, 112, 3233.3923, 1741.4841]),
            new(11606, 59, 4, Vector2, 6, [0.583411, 0.002278], [0.613725, 0.806378], [3003.3923, 145, 3250.3923, 1750]),
            new(11611, 60, 4, Vector2, 6, [0.000558, 0.389522], [0.253115, 0.680334], [1838.7166, 1416.2634, 3895.7166, 1996.2634]),
            new(11616, 61, 4, Vector2, 6, [0.254231, 0.470767], [0.45583, 0.680334], [2051.5569, 1503.9011, 3693.5569, 1921.9011]),
            new(11621, 62, 4, Vector2, 6, [0.254417, 0.234624], [0.458062, 0.343204], [2044.2266, 1937.9011, 3703.4624, 2154.9011]),
            new(11626, 63, 4, Vector2, 6, [0.456946, 0.629461], [0.462898, 0.680334], [200.0001, 1914, 301.4572, 1963]),
            new(11631, 64, 4, Vector2, 6, [0.248466, 0.091875], [0.258322, 0.10858], [830.0001, 1941, 911.0001, 1975]),
            new(11636, 65, 4, Vector2, 6, [0.000558, 0.003797], [0.01432, 0.030372], [1113, 1920, 1225, 1973]),
            new(11641, 66, 4, Vector2, 6, [0.231914, 0.236143], [0.238609, 0.250569], [1832, 1956, 1887, 1985]),
            new(11646, 67, 4, Vector2, 6, [0.231914, 0.066819], [0.24735, 0.10858], [188.0001, 1838.2261, 314.0001, 1922.2261]),
            new(11651, 68, 4, Vector2, 6, [0.231914, 0.042521], [0.241399, 0.062263], [831, 1906.0894, 908, 1946.0894]),
            new(11656, 69, 4, Vector2, 6, [0.432955, 0.186788], [0.447833, 0.230068], [1103.0001, 1841.9871, 1224.0001, 1927.9871]),
            new(11661, 70, 4, Vector2, 6, [0.448949, 0.208808], [0.455272, 0.230068], [1835.0001, 1918.4038, 1887.0001, 1961.4038]),
            new(11666, 71, 4, Vector2, 6, [0.614841, 0.787396], [0.626372, 0.806378], [219.0001, 1901.2261, 313.0001, 1939.2261]),
            new(11671, 72, 4, Vector2, 6, [0.015436, 0.015945], [0.0199, 0.030372], [853, 1930.0894, 890, 1959.0894]),
            new(11676, 73, 4, Vector2, 6, [0.432955, 0.161731], [0.440952, 0.182232], [1128.0001, 1907.9871, 1193.0001, 1948.9871]),
            new(11681, 74, 4, Vector2, 6, [0.456946, 0.606682], [0.458248, 0.624905], [1868.0001, 1944.4038, 1878.0001, 1980.4038]),
            new(11686, 75, 4, Vector2, 6, [0.465129, 0.031891], [0.582295, 0.806378], [2397.6699, -227, 3942.9919, 726.9744]),
            new(11691, 76, 4, Vector2, 6, [0.465129, 0.031891], [0.582295, 0.806378], [2399.5962, -227, 3944.918, 726.9744]),
            new(11696, 77, 4, Vector2, 6, [0.002523, 0.005693], [0.876367, 0.994307], [607.784, 411.7594, 2181.7842, 1201.7594]),
            new(11701, 78, 4, Vector2, 6, [0.94365, 0.267552], [0.997477, 0.434535], [1272.3475, 615.9639, 1369.3093, 749.3563]),
        ];

        // [0] is the 256x256 quad the plausibility test cannot reject; [1..3] are the 3D meshes that broke
        // gate (b)'s UV range.
        internal static readonly Phase1Mesh[] Contaminants =
        [
            new(4489, 33, 4, Vector2, 6, [0, 0], [1, 1], [-128, -128, 128, 128]),
            new(13967, 80, 230, Vector3, 1056, [-0.484549, 0.005904], [1.49625, 0.460101], [-1, 0.0103, 1, 0.3493]),
            new(14260, 85, 230, Vector3, 1056, [-0.484549, 0.005904], [1.49625, 0.460101], [-1, 0.0103, 1, 0.3493]),
            new(14282, 86, 230, Vector3, 1056, [-0.484549, 0.005904], [1.49625, 0.460101], [-1, 0.0103, 1, 0.3493]),
        ];

        // The bracket the live lane actually built: 26 canvas items ending at validator 4483, a probe mesh at
        // 18692 with per-owner index 90, and the live index-slack floor of 256 ⇒ indices [0, 346].
        internal static Sts2SpineProbeMath.CanvasBracketPlan Bracket()
            => Sts2SpineProbeMath.PlanCanvasItemBracket(
                [Sts2SpineGeometryMath.ComposeRid(CanvasValidatorHigh, 1_266)],
                [Sts2SpineGeometryMath.ComposeRid(ProbeValidator, 90)],
                indexSlack: 256,
                cap: 5_000_000);

        internal static Sts2SpineProbeMath.AnchorScanPlan AnchorScan()
            => Sts2SpineProbeMath.PlanAnchorScan(
                Bracket(),
                expectedRunLength: 26,
                stride: Sts2SpineGeometryMath.DefaultLiveValidatorStride,
                stepOverride: 0,
                cap: 500_000);

        internal static Sts2SpineGeometryMath.StrideWalkLimits Limits()
            => new(
                ValidatorFloor: CanvasValidatorHigh + 1,
                ValidatorCeiling: ProbeValidator - 1,
                IndexLow: 0,
                IndexHigh: IndexHigh,
                MaxRunLength: 51,
                MaxConsecutiveMisses: 2);

        internal static IReadOnlyList<double>[] RunBboxes()
            => [.. Run.Select(mesh => (IReadOnlyList<double>)mesh.Bbox)];
    }

    private sealed record Phase1Mesh(
        uint Validator,
        uint Index,
        int VertexCount,
        string VertexVariantType,
        int IndexCount,
        double[] UvMin,
        double[] UvMax,
        double[] Bbox)
    {
        internal ulong Id => Sts2SpineGeometryMath.ComposeRid(Validator, Index);

        // Every mesh in the recorded run has exactly one UV per vertex, which is what a slot mesh looks like.
        internal Sts2SpineProbeMath.SurfaceFacts Facts
            => new(1, VertexVariantType, VertexCount, VertexCount, UvMin, UvMax, IndexCount, Bbox);
    }
}
