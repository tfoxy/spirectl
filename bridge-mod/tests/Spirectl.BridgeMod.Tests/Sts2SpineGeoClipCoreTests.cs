using System.Text.Json;
using Spirectl.Sts2.Core.Artifacts;
using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// Pure, Godot-free core of the env-gated spine GEOCLIP BAKER. The baker itself needs a running game (a
// bracketed mesh-RID sweep, a RenderingServer readback, a slot-colour association probe), but everything it
// does with what it read does not: the arming spec, the frame clock, which atlas page a part's uvs address,
// the src-rect crop and the uv renormalization against it, the rigid-vs-deforming track selection, and the
// assembly of the geoclip/0 manifest a client plays back. Those are exercised here directly, because the bake
// runs against a game that cannot be run in CI and the contract has to be provable without it.
public sealed class Sts2SpineGeoClipCoreTests
{
    [Fact]
    public void AttachmentClassification_OnlyPositivelyIdentifiedPathControlsAreNonDrawing()
    {
        var drawable = Sts2SpineGeoClipAttachmentClassification.Classify(
            attachmentReadSucceeded: true,
            hasAttachment: true,
            attachmentWrapperClass: "SpineAttachment",
            isPathConstraintTarget: false);
        var pathTarget = Sts2SpineGeoClipAttachmentClassification.Classify(
            attachmentReadSucceeded: true,
            hasAttachment: true,
            attachmentWrapperClass: "SpineAttachment",
            isPathConstraintTarget: true);
        var nullAttachment = Sts2SpineGeoClipAttachmentClassification.Classify(
            attachmentReadSucceeded: true,
            hasAttachment: false,
            attachmentWrapperClass: null,
            isPathConstraintTarget: true);
        var unknown = Sts2SpineGeoClipAttachmentClassification.Classify(
            attachmentReadSucceeded: true,
            hasAttachment: true,
            attachmentWrapperClass: null,
            isPathConstraintTarget: false);
        var directNativePath = Sts2SpineGeoClipAttachmentClassification.Classify(
            attachmentReadSucceeded: true,
            hasAttachment: true,
            attachmentWrapperClass: "SpinePathAttachment",
            isPathConstraintTarget: false);

        Assert.True(drawable.RequiresDrawing);
        Assert.Equal(Sts2SpineGeoClipAttachmentClassification.DrawingDefault, drawable.Source);
        Assert.False(pathTarget.RequiresDrawing);
        Assert.Equal(Sts2SpineGeoClipAttachmentClassification.PathConstraintTarget, pathTarget.Source);
        Assert.False(nullAttachment.RequiresDrawing);
        Assert.Equal(Sts2SpineGeoClipAttachmentClassification.NoAttachment, nullAttachment.Source);
        Assert.True(unknown.RequiresDrawing);
        Assert.Equal(Sts2SpineGeoClipAttachmentClassification.DrawingDefault, unknown.Source);
        Assert.False(directNativePath.RequiresDrawing);
        Assert.Equal(Sts2SpineGeoClipAttachmentClassification.NativePathAttachment, directNativePath.Source);
    }

    [Fact]
    public void AttachmentClassification_LeavesMissingDrawableAssociationOnTheStrictRefusalPath()
    {
        var classifications = new[]
        {
            Sts2SpineGeoClipAttachmentClassification.Classify(true, true, "SpineAttachment", false),
            Sts2SpineGeoClipAttachmentClassification.Classify(true, true, "SpineAttachment", true),
            Sts2SpineGeoClipAttachmentClassification.Classify(true, false, null, false),
        };
        var drawableSlots = classifications.Count(classification => classification.RequiresDrawing);

        // The path target and null attachment do not inflate the drawable denominator, but the remaining
        // conservatively drawable attachment still refuses admission when it has no association.
        Assert.Equal(1, drawableSlots);
        Assert.Equal(
            "associated=0 of slotsEverVisible=1",
            Sts2SpineGeoClipRequestLane.IncompletenessReason(
                complete: true,
                associated: 0,
                slotsVisible: drawableSlots,
                foreignMeshes: 0));
        Assert.Null(Sts2SpineGeoClipRequestLane.IncompletenessReason(true, 1, drawableSlots, 0));
    }

    [Fact]
    public void AttachmentClassification_TreatsAnUnreadableAttachmentAsDrawable()
    {
        var unreadable = Sts2SpineGeoClipAttachmentClassification.Classify(
            attachmentReadSucceeded: false,
            hasAttachment: false,
            attachmentWrapperClass: null,
            isPathConstraintTarget: false);

        Assert.True(unreadable.RequiresDrawing);
        Assert.Equal(Sts2SpineGeoClipAttachmentClassification.AttachmentReadUnavailable, unreadable.Source);
    }

    [Fact]
    public void OutputBudget_IsCumulativeAcrossBatchAndUnlimitedByDefault()
    {
        var bounded = new GeoClipOutputBudget(10);
        Assert.True(bounded.TryConsume(6));
        Assert.False(bounded.TryConsume(5));
        Assert.Equal(6, bounded.UsedBytes);
        Assert.True(bounded.TryConsume(4));

        var zero = new GeoClipOutputBudget(0);
        Assert.True(zero.TryConsume(0));
        Assert.False(zero.TryConsume(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GeoClipOutputBudget(-1));

        var unlimited = new GeoClipOutputBudget(null);
        Assert.True(unlimited.TryConsume(long.MaxValue));
    }

    [Fact]
    public void RequestBudget_IsForwardedIntoTheSharedBakeConfig()
    {
        var config = Sts2SpineGeoClipRequestLane.PlanConfig(
            new SpineGeoClipBakeRequestSnapshot(
                "res://scene.tscn", null, "idle", null, "/tmp/geoclips", MaxOutputBytes: 1234),
            "test",
            out var rejected);

        Assert.Null(rejected);
        Assert.NotNull(config);
        Assert.Equal(1234, config.OutputBudget.MaximumBytes);
    }
    // ── The association probe's colour checksum, in the two shapes the two read paths hold colours in ─
    //
    // The association identifies which mesh draws which slot by nudging a slot's colour and seeing whose vertex
    // colours move. It used to get that number off the FULL mesh read — the one PASS 3 needs, which marshals
    // vertices, UVs and indices the probe throws away and allocates a float[4] per vertex. The colour-only read
    // walks the rendering server's colour array directly. Both must produce the SAME double, bit for bit: a
    // checksum that differs in the last place is a slot associated to a different mesh, which is a different
    // artifact. This is the offline half of that guarantee — the Godot-typed half cannot be compiled here.

    [Fact]
    public void ColorChecksum_IsBitIdenticalWhicheverShapeTheColoursArrivedIn()
    {
        var random = new Random(20260902);
        for (var trial = 0; trial < 200; trial += 1)
        {
            var vertices = trial % 37;
            var jagged = new float[vertices][];
            var flat = new float[vertices * 4];
            for (var i = 0; i < vertices; i += 1)
            {
                jagged[i] =
                [
                    (float)((random.NextDouble() * 4) - 2),
                    (float)((random.NextDouble() * 4) - 2),
                    (float)((random.NextDouble() * 4) - 2),
                    (float)random.NextDouble(),
                ];
                jagged[i].CopyTo(flat, i * 4);
            }

            // BitConverter, not Assert.Equal(double, double): the point is that the two agree in the last bit,
            // and xunit's double comparison would let a difference smaller than its tolerance through.
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(Sts2SpineGeoClipColorChecksum.FromJagged(jagged)),
                BitConverter.DoubleToInt64Bits(Sts2SpineGeoClipColorChecksum.FromRgba(flat)));
        }
    }

    // The checksum is POSITION-WEIGHTED so a permutation of the same colours is not read as "nothing moved" —
    // which is the whole reason it is not a plain sum, and the property a rewrite could silently drop.
    [Fact]
    public void ColorChecksum_IsPositionWeightedAndChannelWeighted()
    {
        float[] a = [0.25f, 0.5f, 0.75f, 1f];
        float[] b = [1f, 0.75f, 0.5f, 0.25f];

        Assert.NotEqual(
            Sts2SpineGeoClipColorChecksum.FromJagged([a, b]),
            Sts2SpineGeoClipColorChecksum.FromJagged([b, a]));

        // Swapping two CHANNELS of one vertex must move it too, or a red/green flip would read as no change.
        Assert.NotEqual(
            Sts2SpineGeoClipColorChecksum.FromRgba([0.25f, 0.5f, 0f, 1f]),
            Sts2SpineGeoClipColorChecksum.FromRgba([0.5f, 0.25f, 0f, 1f]));

        Assert.Equal(0d, Sts2SpineGeoClipColorChecksum.FromJagged([]));
        Assert.Equal(0d, Sts2SpineGeoClipColorChecksum.FromJagged(null));
        Assert.Equal(0d, Sts2SpineGeoClipColorChecksum.FromRgba([]));
    }

    // The weights themselves, against a transcription of the expression the single-shape loop used before this
    // was split in two. Without this the pair above could agree with each other and with nothing else.
    [Fact]
    public void ColorChecksum_KeepsTheWeightsTheOneShapeLoopUsed()
    {
        float[][] colors = [[0.1f, 0.2f, 0.3f, 0.4f], [0.5f, 0.6f, 0.7f, 0.8f], [1f, 0f, 0.25f, 0.125f]];

        double expected = 0;
        for (var i = 0; i < colors.Length; i += 1)
        {
            for (var c = 0; c < colors[i].Length; c += 1)
            {
                expected += (i + 1) * (c + 3) * colors[i][c];
            }
        }

        Assert.Equal(
            BitConverter.DoubleToInt64Bits(expected),
            BitConverter.DoubleToInt64Bits(Sts2SpineGeoClipColorChecksum.FromJagged(colors)));
        Assert.Equal(
            BitConverter.DoubleToInt64Bits(expected),
            BitConverter.DoubleToInt64Bits(
                Sts2SpineGeoClipColorChecksum.FromRgba([.. colors.SelectMany(color => color)])));
    }

    // ── The same read, kept as every sum a probe can read a code out of ──────────────────────────────
    //
    // A readback stalls the device for ~0.26 ms whatever it returns, so summing each colour COMPONENT
    // separately alongside the combined checksum is free — and it is what lets one awaited frame carry a probe
    // bit per component instead of one bit in total. Two things have to hold: the combined sum must not move
    // (it is the alpha arm's predicate, and every recorded association pair was produced under it), and the
    // component sums must be independent of each other (a nudge on R that showed up in the G sum would decode
    // to a different slot).

    [Fact]
    public void ColorSums_CarryTheCombinedChecksumUnchangedBitForBit()
    {
        var random = new Random(20260902);
        for (var trial = 0; trial < 200; trial += 1)
        {
            var flat = new float[(trial % 37) * 4];
            for (var i = 0; i < flat.Length; i += 1)
            {
                flat[i] = (float)random.NextDouble();
            }

            Assert.Equal(
                BitConverter.DoubleToInt64Bits(Sts2SpineGeoClipColorChecksum.FromRgba(flat)),
                BitConverter.DoubleToInt64Bits(Sts2SpineGeoClipColorChecksum.SumsFromRgba(flat).Combined));
        }
    }

    // Each component sum sees its own component and nothing else. Mutating ONE component of ONE vertex must
    // move exactly one component sum — and the combined checksum, which is why the alpha arm still works.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ColorSums_AreIndependentPerComponent(int component)
    {
        float[] before = [0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.7f, 0.8f];
        var after = (float[])before.Clone();
        after[4 + component] = Sts2SpineGeoClipProbe.NudgeValue(after[4 + component]);

        var a = Sts2SpineGeoClipColorChecksum.SumsFromRgba(before);
        var b = Sts2SpineGeoClipColorChecksum.SumsFromRgba(after);

        Assert.NotEqual(a.Combined, b.Combined);
        for (var sum = 1; sum <= 4; sum += 1)
        {
            if (sum == component + 1)
            {
                Assert.NotEqual(a[sum], b[sum]);
            }
            else
            {
                Assert.Equal(a[sum], b[sum]);
            }
        }
    }

    // Position-weighted, like the combined checksum: two vertices swapping colours is a change, not a
    // coincidence. Without the weight a slot whose mesh only reordered would read as never having moved.
    [Fact]
    public void ColorSums_ArePositionWeightedSoAPermutationIsNotMissed()
    {
        var straight = Sts2SpineGeoClipColorChecksum.SumsFromRgba([1f, 0f, 0f, 1f, 0f, 1f, 0f, 1f]);
        var swapped = Sts2SpineGeoClipColorChecksum.SumsFromRgba([0f, 1f, 0f, 1f, 1f, 0f, 0f, 1f]);

        Assert.NotEqual(straight.R, swapped.R);
        Assert.NotEqual(straight.G, swapped.G);
    }

    // The weights themselves. Without this the component sums could agree with each other and with nothing.
    [Fact]
    public void ColorSums_WeightEachVertexByItsPositionAndNothingElse()
    {
        var sums = Sts2SpineGeoClipColorChecksum.SumsFromRgba([0.5f, 0.25f, 0.125f, 1f, 0.5f, 0.25f, 0.125f, 1f]);

        // (0 + 1) * v + (1 + 1) * v = 3v for each component, both vertices carrying the same colour.
        Assert.Equal(1.5d, sums.R, 12);
        Assert.Equal(0.75d, sums.G, 12);
        Assert.Equal(0.375d, sums.B, 12);
        Assert.Equal(3d, sums.A, 12);
    }

    // An unreadable surface is NaN in every sum, because the probe's predicate treats a NaN/not-NaN transition
    // as movement and that has to keep working on a per-component read.
    [Fact]
    public void ColorSums_IndexTheSumsAndAnswerNaNOutsideThem()
    {
        var sums = new GeoClipColorSums(1, 2, 3, 4, 5);

        Assert.Equal(1d, sums[GeoClipColorSums.CombinedIndex]);
        Assert.Equal([2d, 3d, 4d, 5d], [sums[1], sums[2], sums[3], sums[4]]);
        Assert.All([-1, GeoClipColorSums.Count, 99], sum => Assert.True(double.IsNaN(sums[sum])));

        Assert.All(
            Enumerable.Range(0, GeoClipColorSums.Count),
            sum => Assert.True(double.IsNaN(GeoClipColorSums.NotRead[sum])));
        Assert.All(Enumerable.Range(0, GeoClipColorSums.Count), sum => Assert.Equal(0d, GeoClipColorSums.Zero[sum]));
    }

    // ── The arming spec ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseTargets_ReadsSceneAndAnimation()
    {
        var targets = Sts2SpineGeoClipSpec.ParseTargets("res://scenes/merchant.tscn?anim=idle_loop");

        var target = Assert.Single(targets);
        Assert.Equal("res://scenes/merchant.tscn", target.ScenePath);
        Assert.Equal("idle_loop", target.Animation);
        Assert.Null(target.NodePath);
    }

    [Fact]
    public void ParseTargets_ReadsSeveralTargetsAndAnOptionalNode()
    {
        var targets = Sts2SpineGeoClipSpec.ParseTargets(
            "res://a.tscn?anim=idle_loop&node=Visuals/Spine ; res://b.tscn?anim=attack");

        Assert.Equal(2, targets.Count);
        Assert.Equal("res://a.tscn", targets[0].ScenePath);
        Assert.Equal("idle_loop", targets[0].Animation);
        Assert.Equal("Visuals/Spine", targets[0].NodePath);
        Assert.Equal("res://b.tscn", targets[1].ScenePath);
        Assert.Equal("attack", targets[1].Animation);
    }

    // A target with no `anim=` still parses: the baker has to be able to name the target it is refusing.
    [Fact]
    public void ParseTargets_KeepsATargetThatNamedNoAnimation()
    {
        var target = Assert.Single(Sts2SpineGeoClipSpec.ParseTargets("res://a.tscn"));

        Assert.Equal("res://a.tscn", target.ScenePath);
        Assert.Null(target.Animation);
    }

    [Fact]
    public void ParseTargets_IgnoresEmptyChunks()
        => Assert.Empty(Sts2SpineGeoClipSpec.ParseTargets(" ;; "));

    [Theory]
    [InlineData("60", 60)]
    [InlineData("0", 1)]
    [InlineData("9999", 120)]
    [InlineData("", 30)]
    [InlineData("not-a-number", 30)]
    public void ParseFps_ClampsAndFallsBack(string raw, int expected)
        => Assert.Equal(expected, Sts2SpineGeoClipSpec.ParseFps(raw));

    [Fact]
    public void DirectoryName_IsOneDirectoryPerSceneNodeAnim()
    {
        Assert.Equal(
            "ironclad_merchant--Visuals-Spine--idle_loop",
            Sts2SpineGeoClipSpec.DirectoryName("res://scenes/merchant/ironclad_merchant.tscn", "Visuals/Spine", "idle_loop"));
        Assert.Equal(
            "byrdonis--root--idle_loop",
            Sts2SpineGeoClipSpec.DirectoryName("res://scenes/creature_visuals/byrdonis.tscn", ".", "idle_loop"));
    }

    // ── The frame clock ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FrameTimes_LandOnExactFrameIntervalsAndIncludeBothEnds()
    {
        var times = Sts2SpineGeoClipMath.FrameTimes(1.0, 30);

        Assert.Equal(31, times.Length);
        Assert.Equal(0d, times[0]);
        Assert.Equal(1d, times[^1], 9);
        Assert.Equal(1d / 30d, times[1], 9);
    }

    // A duration that is not a whole number of frames gets a final SHORT step so the animation's end is
    // sampled; `t` is what a player should trust, `fps` is only the nominal rate.
    [Fact]
    public void FrameTimes_AppendAShortFinalStepWhenTheDurationIsNotAWholeNumberOfFrames()
    {
        var times = Sts2SpineGeoClipMath.FrameTimes(1.06, 20);

        Assert.Equal(23, times.Length);
        Assert.Equal(1.05d, times[^2], 9);
        Assert.Equal(1.06d, times[^1], 9);
    }

    // …and no phantom extra frame when the duration IS a whole number of frames.
    [Fact]
    public void FrameTimes_DoNotDuplicateTheEndWhenItAlreadyLandsOnAFrame()
    {
        var times = Sts2SpineGeoClipMath.FrameTimes(1.05, 20);

        Assert.Equal(22, times.Length);
        Assert.Equal(1.05d, times[^1], 9);
        Assert.Equal(1.0d, times[^2], 9);
    }

    [Fact]
    public void FrameTimes_CollapseToASinglePoseWhenTheAnimationHasNoLength()
        => Assert.Equal([0d], Sts2SpineGeoClipMath.FrameTimes(0d, 30));

    [Fact]
    public void FrameTimes_TruncateAtTheFrameCapAndSaySo()
    {
        Assert.Equal(10, Sts2SpineGeoClipMath.FrameTimes(100d, 30, maxFrames: 10).Length);
        Assert.True(Sts2SpineGeoClipMath.FrameTimesWereTruncated(100d, 30, maxFrames: 10));
        Assert.False(Sts2SpineGeoClipMath.FrameTimesWereTruncated(1d, 30, maxFrames: 900));
    }

    // ── src rect + uv renormalization ────────────────────────────────────────────────────────────────

    [Fact]
    public void DeriveSrcRect_RoundsOutwardAndPads()
    {
        var rect = Sts2SpineGeoClipMath.DeriveSrcRect([0.105, 0.305], [0.105, 0.305], 100, 100, padPixels: 1);

        // 10.5..30.5 px, rounded outward to 10..31, then padded by one on each side.
        Assert.Equal(9, rect.X);
        Assert.Equal(9, rect.Y);
        Assert.Equal(23, rect.Width);
        Assert.Equal(23, rect.Height);
    }

    [Fact]
    public void DeriveSrcRect_ClampsToThePage()
    {
        var rect = Sts2SpineGeoClipMath.DeriveSrcRect([0d, 1d], [0d, 1d], 64, 64, padPixels: 4);

        Assert.Equal(0, rect.X);
        Assert.Equal(0, rect.Y);
        Assert.Equal(64, rect.Width);
        Assert.Equal(64, rect.Height);
    }

    [Fact]
    public void DeriveSrcRect_IsEmptyWhenThePageHasNoSize()
        => Assert.True(Sts2SpineGeoClipMath.DeriveSrcRect([0.1], [0.1], 0, 0).IsEmpty);

    [Fact]
    public void RenormalizeUvs_MapsThePartsOwnCropToZeroOne()
    {
        var u = new[] { 0.1d, 0.3d };
        var v = new[] { 0.1d, 0.3d };
        var rect = Sts2SpineGeoClipMath.DeriveSrcRect(u, v, 100, 100, padPixels: 0);
        var (outU, outV) = Sts2SpineGeoClipMath.RenormalizeUvs(u, v, rect, 100, 100);

        Assert.Equal(10, rect.X);
        Assert.Equal(20, rect.Width);
        Assert.Equal(0d, outU[0], 9);
        Assert.Equal(1d, outU[1], 9);
        Assert.Equal(0d, outV[0], 9);
        Assert.Equal(1d, outV[1], 9);
    }

    // ── Page association ─────────────────────────────────────────────────────────────────────────────

    private const string TwoPageAtlas = """
page-0.png
size: 100, 100
format: RGBA8888
head
bounds: 10, 10, 20, 20

page-1.png
size: 200, 200
format: RGBA8888
torso
bounds: 50, 50, 40, 60
""";

    private static readonly (int Width, int Height)[] TwoPageSizes = [(100, 100), (200, 200)];

    [Fact]
    public void ResolvePage_ShortCircuitsOnASinglePageAtlas()
    {
        var match = Sts2SpineGeoClipAtlas.ResolvePage(
            "anything", 0.1, 0.1, 0.2, 0.2, new SpineAtlasDocument([], []), [(512, 512)]);

        Assert.Equal(0, match.PageId);
        Assert.Equal("single-page", match.Method);
    }

    [Fact]
    public void ResolvePage_AcceptsANameMatchOnlyWhenTheUvsAlsoLandInsideThatRegion()
    {
        var atlas = Sts2SpineAtlasText.Parse(TwoPageAtlas);

        // 50..90 x, 50..110 y on the 200px page — exactly the `torso` region.
        var match = Sts2SpineGeoClipAtlas.ResolvePage("torso", 0.25, 0.25, 0.45, 0.55, atlas, TwoPageSizes);

        Assert.Equal(1, match.PageId);
        Assert.Equal("name+containment", match.Method);
        Assert.Equal("torso", match.RegionName);
    }

    // An attachment whose `path` renames it onto another region cannot be matched by name, which is exactly
    // why containment is the real discriminator.
    [Fact]
    public void ResolvePage_FallsBackToContainmentWhenTheAttachmentWasRenamed()
    {
        var atlas = Sts2SpineAtlasText.Parse(TwoPageAtlas);

        var match = Sts2SpineGeoClipAtlas.ResolvePage("renamed_by_path", 0.25, 0.25, 0.45, 0.55, atlas, TwoPageSizes);

        Assert.Equal(1, match.PageId);
        Assert.Equal("containment", match.Method);
        Assert.Equal("torso", match.RegionName);
    }

    [Fact]
    public void ResolvePage_ReportsUnresolvedRatherThanGuessing()
    {
        var atlas = Sts2SpineAtlasText.Parse(TwoPageAtlas);

        var match = Sts2SpineGeoClipAtlas.ResolvePage("ghost", 0.9, 0.9, 0.95, 0.95, atlas, TwoPageSizes);

        Assert.Equal(-1, match.PageId);
        Assert.Equal("unresolved", match.Method);
    }

    // A rotated region occupies the SWAPPED box on the page, and containment has to know that or every rotated
    // part lands on the wrong page.
    [Fact]
    public void PageBox_SwapsWidthAndHeightForARotatedRegion()
    {
        var atlas = Sts2SpineAtlasText.Parse("""
page-0.png
size: 256, 256
tall
bounds: 4, 4, 20, 100
rotate: 90
""");

        var box = Sts2SpineGeoClipAtlas.PageBox(atlas.Regions[0]);

        Assert.Equal(4, box.X);
        Assert.Equal(100, box.Width);
        Assert.Equal(20, box.Height);
    }

    [Fact]
    public void ExtractAtlasTextFromEnvelope_ReadsTheImportedSpatlasWrapper()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("{\"atlas_data\":\"page-0.png\\nsize: 4, 4\\n\"}");

        Assert.Equal("page-0.png\nsize: 4, 4\n", Sts2SpineGeoClipAtlas.ExtractAtlasTextFromEnvelope(bytes));
    }

    [Fact]
    public void ExtractAtlasTextFromEnvelope_IsEmptyForAnythingElse()
    {
        Assert.Equal(string.Empty, Sts2SpineGeoClipAtlas.ExtractAtlasTextFromEnvelope([]));
        Assert.Equal(string.Empty, Sts2SpineGeoClipAtlas.ExtractAtlasTextFromEnvelope("not json"u8.ToArray()));
        Assert.Equal(string.Empty, Sts2SpineGeoClipAtlas.ExtractAtlasTextFromEnvelope("{\"other\":1}"u8.ToArray()));
    }

    [Fact]
    public void ExtractImportedResourcePath_PullsTheRemapOutOfTheImportSidecar()
    {
        const string Sidecar = """
        [remap]
        importer="spine.atlas"
        path="res://.godot/imported/ironclad.atlas-2f1a9c.spatlas"
        """;

        Assert.Equal(
            "res://.godot/imported/ironclad.atlas-2f1a9c.spatlas",
            Sts2SpineGeoClipAtlas.ExtractImportedResourcePath(Sidecar, ".spatlas"));
        Assert.Equal(string.Empty, Sts2SpineGeoClipAtlas.ExtractImportedResourcePath(Sidecar, ".spskel"));
        Assert.Equal(string.Empty, Sts2SpineGeoClipAtlas.ExtractImportedResourcePath(string.Empty, ".spatlas"));
    }

    // ── Rigid vs deforming ───────────────────────────────────────────────────────────────────────────

    // The manifest's `[a,b,c,d,tx,ty]` means x' = a*x + b*y + tx, y' = c*x + d*y + ty. A player that reads the
    // six numbers in a different order draws a mirrored skeleton, so the order is asserted against a KNOWN map.
    [Fact]
    public void FitXform_LaysTheAffineOutAsTheManifestPromises()
    {
        double[] refX = [0, 10, 10, 0];
        double[] refY = [0, 0, 10, 10];
        const double A = 1.5, B = -0.25, C = 0.75, D = 2.0, Tx = 7, Ty = -3;
        var targetX = refX.Select((x, i) => (A * x) + (B * refY[i]) + Tx).ToArray();
        var targetY = refX.Select((x, i) => (C * x) + (D * refY[i]) + Ty).ToArray();

        var xform = Sts2SpineGeoClipMath.FitXform(refX, refY, targetX, targetY);

        Assert.Equal(A, xform[0], 6);
        Assert.Equal(B, xform[1], 6);
        Assert.Equal(C, xform[2], 6);
        Assert.Equal(D, xform[3], 6);
        Assert.Equal(Tx, xform[4], 6);
        Assert.Equal(Ty, xform[5], 6);
    }

    [Fact]
    public void ClassifyPart_CallsAWholeRotationRigid()
    {
        double[] refX = [0, 10, 10, 0];
        double[] refY = [0, 0, 10, 10];
        var angle = Math.PI / 5;
        var rotatedX = refX.Select((x, i) => (x * Math.Cos(angle)) - (refY[i] * Math.Sin(angle)) + 40).ToArray();
        var rotatedY = refX.Select((x, i) => (x * Math.Sin(angle)) + (refY[i] * Math.Cos(angle)) - 12).ToArray();

        var track = Sts2SpineGeoClipMath.ClassifyPart(
            refX, refY, [(rotatedX, rotatedY)]);

        Assert.True(track.IsRigid);
        Assert.Equal(1, track.ComparedFrames);
        Assert.False(track.VertexCountMismatch);
    }

    [Fact]
    public void ClassifyPart_CallsASingleDraggedVertexDeforming()
    {
        double[] refX = [0, 10, 10, 0];
        double[] refY = [0, 0, 10, 10];
        double[] frameX = [0, 10, 10, 0];
        double[] frameY = [0, 0, 10, 25];

        var track = Sts2SpineGeoClipMath.ClassifyPart(refX, refY, [(frameX, frameY)]);

        Assert.False(track.IsRigid);
        Assert.True(track.MaxNormalizedResidual > Sts2SpineGeometryMath.DefaultRigidThreshold);
    }

    // A vertex-count change under one attachment name has no affine map at all; it must never be reported as
    // rigid, because a client would then replay it with a transform against the wrong reference pose.
    [Fact]
    public void ClassifyPart_NeverCallsAVertexCountChangeRigid()
    {
        double[] refX = [0, 10, 10, 0];
        double[] refY = [0, 0, 10, 10];

        var track = Sts2SpineGeoClipMath.ClassifyPart(refX, refY, [(new[] { 0d, 10d, 10d }, new[] { 0d, 0d, 10d })]);

        Assert.False(track.IsRigid);
        Assert.True(track.VertexCountMismatch);
        Assert.Equal(0, track.ComparedFrames);
    }

    // ── Manifest assembly ────────────────────────────────────────────────────────────────────────────

    // Carries a `sha256` on purpose: it is emitted only when the bake had the bytes in hand, so a fixture
    // without one would leave every key-shape assertion below blind to the newest field on a page.
    private static readonly GeoClipPage[] OnePage =
        [new GeoClipPage(0, "page-0.png", 100, 100, "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")];

    private static GeoClipSlotSample Quad(
        int slotIndex,
        string attachment,
        double dx = 0,
        double dy = 0,
        double stretchY = 1,
        int blendMode = 0)
        => new(
            slotIndex,
            attachment,
            [1, 1, 1, 1],
            blendMode,
            HasGeometry: true,
            [dx, dx + 10, dx + 10, dx],
            [dy, dy, dy + (10 * stretchY), dy + (10 * stretchY)],
            [0.1, 0.3, 0.3, 0.1],
            [0.1, 0.1, 0.3, 0.3],
            [0, 1, 2, 0, 2, 3]);

    private static GeoClipBuildRequest Request(params GeoClipFrameSample[] frames)
        => new(
            "res://scenes/x.tscn",
            "Spine",
            "idle_loop",
            30,
            (frames.Length - 1) / 30d,
            frames,
            OnePage,
            new SpineAtlasDocument([], []));

    [Fact]
    public void Build_EmitsOnePartPerSlotAndAttachmentWithACroppedUvSet()
    {
        var result = Sts2SpineGeoClipBuilder.Build(Request(
            new GeoClipFrameSample(0d, [0], [0, 0, 10, 10], [Quad(0, "head")])));

        var part = Assert.Single(result.Document.Parts);
        Assert.Equal(0, part.Id);
        Assert.Equal(0, part.SlotIndex);
        Assert.Equal("head", part.AttachmentName);
        Assert.Equal(0, part.PageId);
        Assert.Equal(0, part.RefFrame);
        Assert.Equal([0, 1, 2, 0, 2, 3], part.Indices);
        // 10..30 px on a 100px page, padded by one: 9,9,22,22.
        Assert.Equal([9, 9, 22, 22], part.SrcRect);
        // The first uv (0.1, 0.1) is 10px, one pixel into a crop that starts at 9 and is 22 wide.
        Assert.Equal(1d / 22d, part.Uvs[0], 4);
        Assert.Equal([0, 0, 10, 0, 10, 10, 0, 10], part.RefVerts);
    }

    [Fact]
    public void Build_StreamsARigidPartAsAPerFrameTransformAndNoVertices()
    {
        var result = Sts2SpineGeoClipBuilder.Build(Request(
            new GeoClipFrameSample(0d, [0], [0, 0, 10, 10], [Quad(0, "head")]),
            new GeoClipFrameSample(1d / 30d, [0], [5, 0, 10, 10], [Quad(0, "head", dx: 5)])));

        Assert.True(Assert.Single(result.Document.Parts).Rigid);
        var moved = result.Document.Frames[1].Slots["0"];
        Assert.Equal(0, moved.Part);
        Assert.Null(moved.Verts);
        Assert.NotNull(moved.Xform);
        Assert.Equal(1d, moved.Xform![0], 5);
        Assert.Equal(5d, moved.Xform[4], 3);
        Assert.Equal(0d, moved.Xform[5], 3);
    }

    [Fact]
    public void Build_StreamsADeformingPartAsRawVerticesAndNoTransform()
    {
        var result = Sts2SpineGeoClipBuilder.Build(Request(
            new GeoClipFrameSample(0d, [0], [0, 0, 10, 10], [Quad(0, "cloak")]),
            new GeoClipFrameSample(
                1d / 30d,
                [0],
                [0, 0, 10, 30],
                [
                    new GeoClipSlotSample(
                        0,
                        "cloak",
                        [1, 1, 1, 1],
                        0,
                        HasGeometry: true,
                        [0, 10, 10, 0],
                        [0, 0, 10, 30],
                        [0.1, 0.3, 0.3, 0.1],
                        [0.1, 0.1, 0.3, 0.3],
                        [0, 1, 2, 0, 2, 3]),
                ])));

        Assert.False(Assert.Single(result.Document.Parts).Rigid);
        foreach (var frame in result.Document.Frames)
        {
            Assert.Null(frame.Slots["0"].Xform);
            Assert.NotNull(frame.Slots["0"].Verts);
        }

        Assert.Equal([0, 0, 10, 0, 10, 10, 0, 30], result.Document.Frames[1].Slots["0"].Verts);
    }

    [Fact]
    public void Build_GivesAnAttachmentSwapItsOwnPartWithItsOwnReferenceFrame()
    {
        var result = Sts2SpineGeoClipBuilder.Build(Request(
            new GeoClipFrameSample(0d, [0], [0, 0, 10, 10], [Quad(0, "head")]),
            new GeoClipFrameSample(1d / 30d, [0], [0, 0, 10, 10], [Quad(0, "head_hurt")])));

        Assert.Equal(2, result.Document.Parts.Count);
        Assert.Equal(0, result.Document.Parts[0].RefFrame);
        Assert.Equal(1, result.Document.Parts[1].RefFrame);
        Assert.Equal("head_hurt", result.Document.Parts[1].AttachmentName);
        Assert.Equal(0, result.Document.Frames[0].Slots["0"].Part);
        Assert.Equal(1, result.Document.Frames[1].Slots["0"].Part);
    }

    [Fact]
    public void Build_EmitsAHiddenSlotWithANullPartAndStillCarriesItsColour()
    {
        var result = Sts2SpineGeoClipBuilder.Build(Request(
            new GeoClipFrameSample(0d, [0], [0, 0, 10, 10], [GeoClipSlotSample.Hidden(0, [1, 0.5, 0.25, 0], 0)])));

        var slot = result.Document.Frames[0].Slots["0"];
        Assert.Null(slot.Part);
        Assert.Null(slot.Xform);
        Assert.Null(slot.Verts);
        Assert.Equal([1, 0.5, 0.25, 0], slot.Color);
        Assert.Empty(result.Document.Parts);
        Assert.DoesNotContain(result.Diagnostics, note => note.Contains("stable attachment name", StringComparison.Ordinal));
    }

    // A slot whose attachment IS set but whose mesh read back empty is a bake fault, not a hidden slot: it
    // still has to render as hidden, but the manifest must say why so a first real run is debuggable.
    [Fact]
    public void Build_ReportsAnAttachmentWhoseMeshReadBackEmpty()
    {
        var result = Sts2SpineGeoClipBuilder.Build(Request(
            new GeoClipFrameSample(0d, [0], [0, 0, 0, 0], [GeoClipSlotSample.Hidden(0, [1, 1, 1, 1], 0, "head")])));

        Assert.Null(result.Document.Frames[0].Slots["0"].Part);
        Assert.Contains(result.Diagnostics, note => note.Contains("read back empty", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, note => note.Contains("No drawable part was produced", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_EmitsOnlyDrawablePartsAndDoesNotDiagnosePathControlsAsEmptyMeshes()
    {
        var pathControl = GeoClipSlotSample.Hidden(1, [1, 1, 1, 1], 0, "motion-path", requiresDrawing: false);
        var result = Sts2SpineGeoClipBuilder.Build(Request(
            new GeoClipFrameSample(0d, [0, 1], [0, 0, 10, 10], [Quad(0, "head"), pathControl])));

        var part = Assert.Single(result.Document.Parts);
        Assert.Equal("head", part.AttachmentName);
        Assert.Null(result.Document.Frames[0].Slots["1"].Part);
        Assert.DoesNotContain(result.Diagnostics, note => note.Contains("motion-path", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Diagnostics, note => note.Contains("No drawable part", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_DistinguishesMissingDrawableMeshesFromNonDrawingAttachments()
    {
        var missingDrawable = Sts2SpineGeoClipBuilder.Build(Request(
            new GeoClipFrameSample(0d, [0], [0, 0, 0, 0], [GeoClipSlotSample.Hidden(0, [1, 1, 1, 1], 0, "head")])));
        var onlyPathControl = Sts2SpineGeoClipBuilder.Build(Request(
            new GeoClipFrameSample(
                0d,
                [0],
                [0, 0, 0, 0],
                [GeoClipSlotSample.Hidden(0, [1, 1, 1, 1], 0, "motion-path", requiresDrawing: false)])));

        Assert.Contains(missingDrawable.Diagnostics, note => note.Contains("read back empty", StringComparison.Ordinal));
        Assert.Contains(onlyPathControl.Diagnostics, note => note.Contains("No drawable part", StringComparison.Ordinal));
        Assert.DoesNotContain(onlyPathControl.Diagnostics, note => note.Contains("read back empty", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_CarriesTheMetaTheContractPromises()
    {
        var result = Sts2SpineGeoClipBuilder.Build(Request(
            new GeoClipFrameSample(0d, [1, 0], [-4, -8, 20, 30], [Quad(0, "head"), Quad(1, "torso", blendMode: 1)])));

        Assert.Equal("geoclip/0", result.Document.Meta.Schema);
        Assert.Equal("res://scenes/x.tscn", result.Document.Meta.Scene);
        Assert.Equal("Spine", result.Document.Meta.Node);
        Assert.Equal("idle_loop", result.Document.Meta.Anim);
        Assert.Equal(30, result.Document.Meta.Fps);
        Assert.Equal(1, result.Document.Meta.FrameCount);
        Assert.Equal(0d, result.Document.Meta.DurationMs);
        Assert.Equal([-4, -8, 20, 30], Assert.Single(result.Document.Meta.BoundsPerFrame));
        Assert.Equal([1, 0], result.Document.Frames[0].DrawOrder);
        Assert.Equal(1, result.Document.Parts[1].BlendMode);
    }

    // The frozen wire shape, asserted through the serializer a player will actually read.
    [Fact]
    public void Serialize_ProducesTheFrozenGeoclipV1Shape()
    {
        var result = Sts2SpineGeoClipBuilder.Build(Request(
            new GeoClipFrameSample(0d, [0, 1], [0, 0, 10, 10], [Quad(0, "head"), GeoClipSlotSample.Hidden(1, [1, 1, 1, 1], 0)]),
            new GeoClipFrameSample(1d / 30d, [0, 1], [5, 0, 10, 10], [Quad(0, "head", dx: 5), GeoClipSlotSample.Hidden(1, [1, 1, 1, 1], 0)])));

        using var document = JsonDocument.Parse(Sts2SpineGeoClipBuilder.Serialize(result.Document));
        var root = document.RootElement;

        var meta = root.GetProperty("meta");
        foreach (var key in new[] { "schema", "scene", "node", "anim", "fps", "frameCount", "durationMs", "boundsPerFrame" })
        {
            Assert.True(meta.TryGetProperty(key, out _), $"meta.{key} is missing");
        }

        var page = root.GetProperty("pages")[0];
        Assert.Equal(0, page.GetProperty("id").GetInt32());
        Assert.Equal("page-0.png", page.GetProperty("file").GetString());
        Assert.Equal(100, page.GetProperty("width").GetInt32());
        Assert.Equal(100, page.GetProperty("height").GetInt32());

        var part = root.GetProperty("parts")[0];
        foreach (var key in new[]
        {
            "id", "slotIndex", "attachmentName", "pageId", "srcRect", "indices", "uvs", "refVerts", "refFrame",
            "rigid", "blendMode",
        })
        {
            Assert.True(part.TryGetProperty(key, out _), $"parts[0].{key} is missing");
        }

        var frame = root.GetProperty("frames")[1];
        Assert.True(frame.TryGetProperty("t", out _));
        Assert.True(frame.TryGetProperty("drawOrder", out _));

        var drawn = frame.GetProperty("slots").GetProperty("0");
        Assert.Equal(0, drawn.GetProperty("part").GetInt32());
        Assert.Equal(6, drawn.GetProperty("xform").GetArrayLength());
        Assert.False(drawn.TryGetProperty("verts", out _));

        // A hidden slot still carries `part: null` explicitly — the player distinguishes "hidden" from
        // "this slot was never baked", and an omitted key would collapse the two.
        var hidden = frame.GetProperty("slots").GetProperty("1");
        Assert.Equal(JsonValueKind.Null, hidden.GetProperty("part").ValueKind);
        Assert.Equal(4, hidden.GetProperty("color").GetArrayLength());
        Assert.False(hidden.TryGetProperty("xform", out _));
    }

    // ── The Phase-1 fixture ──────────────────────────────────────────────────────────────────────────
    //
    // Every number below was MEASURED against the real game in Phase 1 and is recorded in
    // `.sts2/research/data/spine-geoclip-phase1/probe-offline/offline-*.json`. They are the reason the split
    // bracket exists, so they are the fixture the split bracket is tested against: a plan that stops reproducing
    // window A's shape is a regression against a run nobody can repeat in CI.
    //
    //   * merchant bracket: low = (validator 16204, index 87), mid = (16448, 131)
    //   * merchant recipe-A window: validators 16204..16448 × indices 23..195 = 42 385 candidates → 30 meshes
    //   * those 30 meshes: indices 88..115 plus 129 and 130, on validators 16212..16435
    //   * 44 merchant slots show an attachment, so 14 meshes were missed; the wide-slack rerun proved the index
    //     axis innocent, which leaves them ABOVE the mid bracket — predicted indices 132..145
    //   * byrdonis bracket: low = (18727, 88), mid = (18941, 117); 26 meshes at indices 89..116 for 28 visible
    //     slots, so the 2 missing ones are predicted at indices 118..119

    private static ulong Rid(uint validator, uint index) => ((ulong)validator << 32) | index;

    private const uint MerchantBracketLowValidator = 16204;
    private const uint MerchantBracketMidValidator = 16448;
    private const uint ByrdonisBracketMidValidator = 18941;

    private static readonly ulong MerchantBracketLow = Rid(MerchantBracketLowValidator, 87);
    private static readonly ulong MerchantBracketMid = Rid(MerchantBracketMidValidator, 131);
    private static readonly ulong ByrdonisBracketLow = Rid(18727, 88);
    private static readonly ulong ByrdonisBracketMid = Rid(ByrdonisBracketMidValidator, 117);

    // A probe mesh taken after the acquisition-frame seek. Not a Phase-1 measurement (Phase 1 never took one);
    // the validator distance is modelled on the recipe-D probe mesh the same run recorded at validator 17456.
    private static readonly ulong MerchantBracketPost = Rid(16948, 160);
    private static readonly ulong ByrdonisBracketPost = Rid(19430, 150);

    private static readonly (uint Validator, uint Index)[] MerchantValidatedMeshes =
    [
        (16212, 88), (16224, 89), (16235, 90), (16240, 91), (16245, 92), (16250, 93), (16255, 94), (16260, 95),
        (16265, 96), (16270, 97), (16275, 98), (16280, 99), (16285, 100), (16290, 101), (16295, 102), (16300, 103),
        (16305, 104), (16310, 105), (16315, 106), (16320, 107), (16325, 108), (16330, 109), (16335, 110),
        (16340, 111), (16345, 112), (16350, 113), (16355, 114), (16360, 115), (16430, 129), (16435, 130),
    ];

    private static bool Covers(Sts2SpineGeometryMath.RidCandidatePlan plan, uint validator, uint index)
        => validator >= plan.ValidatorLow
            && validator <= plan.ValidatorHigh
            && index >= plan.IndexLow
            && index <= plan.IndexHigh;

    // ── Window A: the regression anchor ──────────────────────────────────────────────────────────────

    // Window A is Phase 1's window, byte for byte. If this test ever needs updating, the bake's answer has
    // silently changed shape and the recorded "30 meshes / 42 385 candidates" stops being a comparable number.
    [Fact]
    public void PlanWindowA_ReproducesThePhase1MerchantSweepExactly()
    {
        var window = Sts2SpineGeoClipSweep.PlanWindowA(MerchantBracketLow, MerchantBracketMid, indexSlack: 64, cap: 50_000);

        Assert.Equal("A", window.Name);
        Assert.Equal(16204u, window.Plan.ValidatorLow);
        Assert.Equal(16448u, window.Plan.ValidatorHigh);
        Assert.Equal(23u, window.Plan.IndexLow);
        Assert.Equal(195u, window.Plan.IndexHigh);
        Assert.Equal(42_385, window.Plan.TotalCandidates);
        Assert.Equal(50_000, window.Plan.Cap);
        Assert.False(window.Plan.Truncated);
        Assert.Equal(42_385, window.Plan.EmittedCount);
        Assert.Equal("config", window.CapSource);
    }

    [Fact]
    public void PlanWindowA_ReproducesThePhase1ByrdonisSweepExactly()
    {
        var window = Sts2SpineGeoClipSweep.PlanWindowA(ByrdonisBracketLow, ByrdonisBracketMid, indexSlack: 64, cap: 50_000);

        Assert.Equal(18727u, window.Plan.ValidatorLow);
        Assert.Equal(18941u, window.Plan.ValidatorHigh);
        Assert.Equal(24u, window.Plan.IndexLow);
        Assert.Equal(181u, window.Plan.IndexHigh);
        Assert.Equal(33_970, window.Plan.TotalCandidates);
        Assert.False(window.Plan.Truncated);
    }

    [Fact]
    public void PlanWindowA_StillReachesEveryMeshPhase1Validated()
    {
        var window = Sts2SpineGeoClipSweep.PlanWindowA(MerchantBracketLow, MerchantBracketMid, indexSlack: 64, cap: 50_000);

        Assert.Equal(30, MerchantValidatedMeshes.Length);
        foreach (var (validator, index) in MerchantValidatedMeshes)
        {
            Assert.True(Covers(window.Plan, validator, index), $"window A no longer reaches ({validator}, {index})");
        }
    }

    // The other half of the diagnosis: window A cannot reach the meshes the animation mints, no matter how wide
    // the index axis is opened. (Phase 1 proved this against the game with an 8× index slack; here it is
    // arithmetic.)
    [Fact]
    public void PlanWindowA_CannotReachTheMeshesMintedAfterTheMidBracketAtAnyIndexSlack()
    {
        foreach (var slack in new[] { 64, 512, 65_536 })
        {
            var window = Sts2SpineGeoClipSweep.PlanWindowA(MerchantBracketLow, MerchantBracketMid, slack, cap: 5_000_000);
            for (uint index = 132; index <= 145; index += 1)
            {
                Assert.False(Covers(window.Plan, MerchantBracketMidValidator + 1, index));
            }
        }
    }

    // ── Window B: the fix ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PlanWindowB_OpensAtTheMidBracketAndClosesAtThePostBracket()
    {
        var window = Sts2SpineGeoClipSweep.PlanWindowB(
            MerchantBracketMid, MerchantBracketPost, indexSlack: 64, configuredCap: 50_000, envCap: null);

        Assert.Equal("B", window.Name);
        Assert.Equal(MerchantBracketMidValidator, window.Plan.ValidatorLow);
        Assert.Equal(16948u, window.Plan.ValidatorHigh);
        Assert.Equal(224u, window.Plan.IndexHigh);
    }

    // The load-bearing difference from a plain bracket: window B's meshes come out of a DIFFERENT owner's index
    // pool than the probe meshes that bound it, so inheriting the bracket's index range would floor the axis at
    // 67 and miss every one of them. Forced to zero.
    [Fact]
    public void PlanWindowB_FloorsTheIndexAxisAtZeroWhereARawBracketWouldNot()
    {
        var raw = Sts2SpineGeometryMath.PlanRidCandidates(MerchantBracketMid, MerchantBracketPost, 64, 50_000);
        var window = Sts2SpineGeoClipSweep.PlanWindowB(
            MerchantBracketMid, MerchantBracketPost, indexSlack: 64, configuredCap: 50_000, envCap: null);

        Assert.Equal(67u, raw.IndexLow);
        Assert.Equal(0u, window.Plan.IndexLow);
    }

    [Fact]
    public void PlanWindowB_CoversTheFourteenMerchantMeshesPhase1Missed()
    {
        var window = Sts2SpineGeoClipSweep.PlanWindowB(
            MerchantBracketMid, MerchantBracketPost, indexSlack: 64, configuredCap: 50_000, envCap: null);

        for (uint index = 132; index <= 145; index += 1)
        {
            Assert.True(Covers(window.Plan, MerchantBracketMidValidator + 1, index));
            Assert.True(Covers(window.Plan, MerchantBracketMidValidator + 200, index));
        }

        Assert.False(window.Plan.Truncated);
    }

    [Fact]
    public void PlanWindowB_CoversTheTwoByrdonisMeshesPhase1Missed()
    {
        var window = Sts2SpineGeoClipSweep.PlanWindowB(
            ByrdonisBracketMid, ByrdonisBracketPost, indexSlack: 64, configuredCap: 50_000, envCap: null);

        Assert.True(Covers(window.Plan, ByrdonisBracketMidValidator + 1, 118));
        Assert.True(Covers(window.Plan, ByrdonisBracketMidValidator + 1, 119));
    }

    // Window A's cap was chosen for a 173-wide index axis. Window B's axis is the full pool, so the same cap
    // would stop a couple of validator rows past the mid edge — before the meshes it exists to find.
    [Fact]
    public void ChooseWindowBCap_AutoRaisesToCoverTheValidatorRowsTheMeshesLiveIn()
    {
        var (cap, source, from, to) = Sts2SpineGeoClipSweep.ChooseWindowBCap(
            indexSpan: 225, validatorSpan: 501, configuredCap: 50_000, envCap: null);

        Assert.Equal(225 * 501, cap);
        Assert.Equal("auto-raised", source);
        Assert.Equal(50_000, from);
        Assert.Equal(225 * 501, to);
    }

    [Fact]
    public void ChooseWindowBCap_NeverLowersAConfiguredCap()
    {
        var (cap, source, _, _) = Sts2SpineGeoClipSweep.ChooseWindowBCap(
            indexSpan: 10, validatorSpan: 4, configuredCap: 50_000, envCap: null);

        Assert.Equal(50_000, cap);
        Assert.Equal("config", source);
    }

    [Fact]
    public void ChooseWindowBCap_LetsAnExplicitEnvironmentCapWinOutright()
    {
        var (cap, source, from, to) = Sts2SpineGeoClipSweep.ChooseWindowBCap(
            indexSpan: 225, validatorSpan: 501, configuredCap: 50_000, envCap: 7_777);

        Assert.Equal(7_777, cap);
        Assert.Equal("env", source);
        Assert.Equal(50_000, from);
        Assert.Equal(7_777, to);
    }

    // A bracket that spans a whole scene load must not plan a multi-minute sweep: the auto-raise stops at the
    // ceiling, covers the rows nearest the mid edge, and the window reports itself truncated.
    [Fact]
    public void ChooseWindowBCap_StopsAtTheCeiling()
    {
        var (cap, source, _, _) = Sts2SpineGeoClipSweep.ChooseWindowBCap(
            indexSpan: 4_000, validatorSpan: 14_000, configuredCap: 50_000, envCap: null);

        Assert.Equal(Sts2SpineGeoClipSweep.WindowBCapCeiling, cap);
        Assert.Equal("auto-raised", source);
    }

    // Truncation is only tolerable because enumeration runs ASCENDING from the mid edge — the edge the lazily
    // minted meshes sit next to. A cap therefore discards the far end, which is where they are known not to be.
    [Fact]
    public void PlanWindowB_EnumeratesFromTheMidEdgeFirstSoACapOnlyDiscardsTheFarEnd()
    {
        var window = Sts2SpineGeoClipSweep.PlanWindowB(
            MerchantBracketMid, MerchantBracketPost, indexSlack: 64, configuredCap: 50_000, envCap: 1_000);
        var ids = Sts2SpineGeometryMath.EnumerateRidCandidates(window.Plan).ToList();

        Assert.True(window.Plan.Truncated);
        Assert.Equal(1_000, ids.Count);
        Assert.Equal(MerchantBracketMidValidator, Sts2SpineGeoClipSweep.DecodeRidId(ids[0]).Validator);
        Assert.Equal(0u, Sts2SpineGeoClipSweep.DecodeRidId(ids[0]).Index);
        Assert.True(Sts2SpineGeoClipSweep.DecodeRidId(ids[^1]).Validator < window.Plan.ValidatorHigh);
    }

    [Fact]
    public void ComposeAndDecodeRidId_RoundTripThroughTheValidatorIndexSplit()
    {
        var id = Sts2SpineGeoClipSweep.ComposeRidId(16448, 131);

        Assert.Equal(MerchantBracketMid, id);
        Assert.Equal((16448u, 131u), Sts2SpineGeoClipSweep.DecodeRidId(id));
    }

    [Fact]
    public void MergeWindows_DedupesAndAppliesTheRetentionCapToTheUnion()
    {
        var (merged, dropped) = Sts2SpineGeoClipSweep.MergeWindows([1, 2, 3], [3, 4], maxRetained: 10);

        Assert.Equal([1ul, 2ul, 3ul, 4ul], merged);
        Assert.Equal(0, dropped);

        // The retention cap exists to stop a runaway sweep from making every later pass quadratic; two windows
        // that each came in just under it would still do exactly that, so it applies to the union.
        var (capped, over) = Sts2SpineGeoClipSweep.MergeWindows([1, 2, 3], [4, 5], maxRetained: 3);
        Assert.Equal([1ul, 2ul, 3ul], capped);
        Assert.Equal(2, over);
    }

    // ── The tightened mesh filter ────────────────────────────────────────────────────────────────────

    // Phase 1's merchant: skeleton bounds position (-529.3121, -1209.0583) size 875.2423 × 1278.9694, which the
    // probe's JSON records as the min/max corners (-529.3121, -1209.0583) .. (345.9302, 69.9111).
    private static readonly Sts2SpineGeoClipMath.SkeletonBox MerchantBounds =
        Sts2SpineGeoClipMath.SkeletonBox.FromPositionSize(-529.3121, -1209.0583, 875.2423, 1278.9694);

    // The live main-menu rig the geometry probe swept, whose contaminants are the reason this filter exists.
    private static readonly Sts2SpineGeoClipMath.SkeletonBox LiveMenuBounds =
        new(-640.4252, -227, 4480.0005, 2287.3984);

    private static Sts2SpineGeoClipMath.SlotMeshFacts Facts(
        int surfaceCount = 1,
        bool verticesAreVector2 = true,
        int vertexCount = 4,
        bool hasUvChannel = true,
        int uvCount = 4,
        double uvMin = 0.091062,
        double uvMax = 0.998789,
        int indexCount = 6,
        double minX = -400,
        double minY = -900,
        double maxX = 100,
        double maxY = 0)
        => new(
            surfaceCount,
            verticesAreVector2,
            vertexCount,
            hasUvChannel,
            uvCount,
            uvMin,
            uvMax,
            indexCount,
            minX,
            minY,
            maxX,
            maxY);

    [Fact]
    public void IsPlausibleSlotMesh_AcceptsARealMerchantSlotMesh()
        => Assert.Null(Sts2SpineGeoClipMath.IsPlausibleSlotMesh(Facts(), MerchantBounds, strict: true));

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void IsPlausibleSlotMesh_RejectsAnythingThatIsNotExactlyOneSurface(int surfaceCount)
        => Assert.Equal(
            "surface-count",
            Sts2SpineGeoClipMath.IsPlausibleSlotMesh(Facts(surfaceCount: surfaceCount), MerchantBounds, strict: true));

    // The live sweep's three contaminants: 230-vertex PackedVector3Array meshes with bbox [-1, 0] .. [1, 0.3].
    // They are some other subsystem's 3D geometry, and they are what broke the probe's uv-range gate.
    [Fact]
    public void IsPlausibleSlotMesh_RejectsTheThreeDimensionalContaminantsTheLiveSweepFound()
        => Assert.Equal(
            "vertex-type",
            Sts2SpineGeoClipMath.IsPlausibleSlotMesh(
                Facts(
                    verticesAreVector2: false,
                    vertexCount: 230,
                    uvCount: 230,
                    indexCount: 690,
                    minX: -1,
                    minY: 0,
                    maxX: 1,
                    maxY: 0.3),
                LiveMenuBounds,
                strict: true));

    [Fact]
    public void IsPlausibleSlotMesh_RejectsAnEmptySurface()
        => Assert.Equal(
            "no-vertices",
            Sts2SpineGeoClipMath.IsPlausibleSlotMesh(
                Facts(vertexCount: 0, uvCount: 0, indexCount: 0), MerchantBounds, strict: true));

    [Fact]
    public void IsPlausibleSlotMesh_RejectsAMeshWithNoUvChannelAtAll()
        => Assert.Equal(
            "uv-missing",
            Sts2SpineGeoClipMath.IsPlausibleSlotMesh(
                Facts(hasUvChannel: false, uvCount: 0), MerchantBounds, strict: true));

    [Fact]
    public void IsPlausibleSlotMesh_RejectsAUvChannelThatDoesNotMatchTheVertexCount()
        => Assert.Equal(
            "uv-count-mismatch",
            Sts2SpineGeoClipMath.IsPlausibleSlotMesh(Facts(uvCount: 3), MerchantBounds, strict: true));

    // The live sweep's union uv range was [-0.4845, 1.4963] — a uv outside the unit square does not address an
    // atlas page, so it is not this skeleton's art.
    [Fact]
    public void IsPlausibleSlotMesh_RejectsUvsOutsideTheUnitSquare()
    {
        Assert.Equal(
            "uv-out-of-unit-range",
            Sts2SpineGeoClipMath.IsPlausibleSlotMesh(Facts(uvMin: -0.4845), MerchantBounds, strict: true));
        Assert.Equal(
            "uv-out-of-unit-range",
            Sts2SpineGeoClipMath.IsPlausibleSlotMesh(Facts(uvMax: 1.4963), MerchantBounds, strict: true));

        // …but readback noise at the very edge of the page is not a rejection.
        Assert.Null(Sts2SpineGeoClipMath.IsPlausibleSlotMesh(
            Facts(uvMin: -0.00001, uvMax: 1.00001), MerchantBounds, strict: true));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public void IsPlausibleSlotMesh_RejectsAnIndexBufferThatIsNotAPositiveNumberOfTriangles(int indexCount)
        => Assert.Equal(
            "indices-not-triangles",
            Sts2SpineGeoClipMath.IsPlausibleSlotMesh(Facts(indexCount: indexCount), MerchantBounds, strict: true));

    [Fact]
    public void IsPlausibleSlotMesh_RejectsGeometryThatSitsOutsideTheSkeletonsOwnBounds()
        => Assert.Equal(
            "bbox-outside-skeleton-bounds",
            Sts2SpineGeoClipMath.IsPlausibleSlotMesh(Facts(maxX: 5_000), MerchantBounds, strict: true));

    // The bounds are computed FROM the skeleton's meshes, so a real slot mesh is inside by construction; the 2%
    // slack only covers the fact that they were read at a slightly different instant.
    [Fact]
    public void IsPlausibleSlotMesh_AllowsATwoPercentOverhangButNotMore()
    {
        var span = Math.Max(
            MerchantBounds.MaxX - MerchantBounds.MinX,
            MerchantBounds.MaxY - MerchantBounds.MinY);

        Assert.Null(Sts2SpineGeoClipMath.IsPlausibleSlotMesh(
            Facts(maxX: MerchantBounds.MaxX + (span * 0.01)), MerchantBounds, strict: true));
        Assert.Equal(
            "bbox-outside-skeleton-bounds",
            Sts2SpineGeoClipMath.IsPlausibleSlotMesh(
                Facts(maxX: MerchantBounds.MaxX + (span * 0.05)), MerchantBounds, strict: true));
    }

    // Unreadable bounds say NOTHING about a mesh, so the test is skipped rather than failed: the alternative
    // silently drops every mesh on a rig whose bounds failed to read.
    [Fact]
    public void IsPlausibleSlotMesh_SkipsTheBoundsTestWhenTheSkeletonBoundsAreUnusable()
    {
        Assert.False(default(Sts2SpineGeoClipMath.SkeletonBox).IsUsable);
        Assert.Null(Sts2SpineGeoClipMath.IsPlausibleSlotMesh(
            Facts(maxX: 5_000), default, strict: true));
    }

    // KNOWN BLIND SPOT, pinned rather than papered over. The 256×256 quad at validator 4489 index 33 was the
    // ONLY thing the live probe's first attempt found, and it passes every check this predicate can make: one
    // surface, 2D vertices, 4 of them, uvs spanning exactly [0,1], six indices, and a bounding box comfortably
    // inside the menu rig's own bounds. Separating it from a legitimate region attachment needs the run/stride
    // evidence the live arm gathers — not a per-mesh predicate. Do not "fix" this by tightening the filter.
    [Fact]
    public void IsPlausibleSlotMesh_CannotRejectA256QuadThatSitsInsideTheBounds()
        => Assert.Null(Sts2SpineGeoClipMath.IsPlausibleSlotMesh(
            Facts(uvMin: 0, uvMax: 1, minX: -128, minY: -128, maxX: 128, maxY: 128),
            LiveMenuBounds,
            strict: true));

    // Non-strict is the ESCAPE HATCH: it must reproduce Phase 1 exactly, so a bake can be compared against the
    // recorded Phase-1 numbers by flipping one environment variable. Everything strict added is waived.
    [Fact]
    public void IsPlausibleSlotMesh_NonStrictReproducesThePhase1Filter()
    {
        Assert.Null(Sts2SpineGeoClipMath.IsPlausibleSlotMesh(
            Facts(verticesAreVector2: false, vertexCount: 230, uvCount: 230, indexCount: 690),
            MerchantBounds,
            strict: false));
        Assert.Null(Sts2SpineGeoClipMath.IsPlausibleSlotMesh(
            Facts(hasUvChannel: false, uvCount: 0), MerchantBounds, strict: false));
        Assert.Null(Sts2SpineGeoClipMath.IsPlausibleSlotMesh(
            Facts(uvMin: -0.4845, uvMax: 1.4963), MerchantBounds, strict: false));
        Assert.Null(Sts2SpineGeoClipMath.IsPlausibleSlotMesh(Facts(maxX: 5_000), MerchantBounds, strict: false));

        // …but the three things Phase 1 DID check still reject.
        Assert.Equal(
            "surface-count",
            Sts2SpineGeoClipMath.IsPlausibleSlotMesh(Facts(surfaceCount: 2), MerchantBounds, strict: false));
        Assert.Equal(
            "no-vertices",
            Sts2SpineGeoClipMath.IsPlausibleSlotMesh(Facts(vertexCount: 0), MerchantBounds, strict: false));
        Assert.Equal(
            "indices-not-triangles",
            Sts2SpineGeoClipMath.IsPlausibleSlotMesh(Facts(indexCount: 7), MerchantBounds, strict: false));
        Assert.Equal(
            "uv-count-mismatch",
            Sts2SpineGeoClipMath.IsPlausibleSlotMesh(Facts(uvCount: 3), MerchantBounds, strict: false));
    }

    [Fact]
    public void DescribeSurface_MeasuresTheBoundingBoxAndUvRangeOverBothChannels()
    {
        var facts = Sts2SpineGeoClipMath.DescribeSurface(
            surfaceCount: 1,
            verticesAreVector2: true,
            x: [0, 10, -4],
            y: [1, 2, 30],
            hasUvChannel: true,
            u: [0.25, 0.75, 0.5],
            v: [0.1, 0.9, 0.5],
            indexCount: 3);

        Assert.Equal(3, facts.VertexCount);
        Assert.Equal(-4, facts.MinX);
        Assert.Equal(10, facts.MaxX);
        Assert.Equal(1, facts.MinY);
        Assert.Equal(30, facts.MaxY);
        Assert.Equal(0.1, facts.UvMin, 9);
        Assert.Equal(0.9, facts.UvMax, 9);
    }

    // ── The atlas association fallback ───────────────────────────────────────────────────────────────

    // Page sizes are the merchant's real ones (1652 × 657 and 405 × 509); the regions are stand-ins, because
    // what is under test is the box arithmetic, not any particular rig's packing.
    private const string TwoPageRegionAtlas = """
page-0.png
size: 1652, 657
head
bounds: 100, 200, 120, 140
hair
bounds: 300, 200, 60, 80

page-1.png
size: 405, 509
torso
bounds: 10, 10, 200, 300
""";

    private static readonly (int Width, int Height)[] TwoPageRegionSizes = [(1652, 657), (405, 509)];

    private static Sts2SpineGeoClipAtlas.MeshUvBox BoxOnPage(
        int order,
        double x0,
        double y0,
        double x1,
        double y1,
        int pageWidth,
        int pageHeight)
        => new(order, x0 / pageWidth, y0 / pageHeight, x1 / pageWidth, y1 / pageHeight);

    [Fact]
    public void MatchMeshToAttachmentRegion_TakesTheOneMeshWhoseUvBoxIsTheNamedRegion()
    {
        var match = Sts2SpineGeoClipAtlas.MatchMeshToAttachmentRegion(
            "head",
            Sts2SpineAtlasText.Parse(TwoPageRegionAtlas),
            TwoPageRegionSizes,
            [
                BoxOnPage(0, 300, 200, 360, 280, 1652, 657),
                BoxOnPage(7, 100, 200, 220, 340, 1652, 657),
            ]);

        Assert.Equal(7, match.Order);
        Assert.Equal("uv-region-exact", match.Method);
        Assert.Equal("head", match.RegionName);
        Assert.Equal(1, match.ContainedCount);
    }

    // A region on the SECOND page has to be measured against the second page's size; measuring it against page 0
    // puts every part on the wrong page, which is the bug this asserts against.
    [Fact]
    public void MatchMeshToAttachmentRegion_MeasuresARegionAgainstItsOwnPageSize()
    {
        var match = Sts2SpineGeoClipAtlas.MatchMeshToAttachmentRegion(
            "torso",
            Sts2SpineAtlasText.Parse(TwoPageRegionAtlas),
            TwoPageRegionSizes,
            [BoxOnPage(3, 10, 10, 210, 310, 405, 509)]);

        Assert.Equal(3, match.Order);
        Assert.Equal("uv-region-exact", match.Method);
    }

    // An attachment whose `path` renames it onto a different region names nothing this pass can find. Reported,
    // never guessed — the colour nudge and the elimination pass are still free to resolve it.
    [Fact]
    public void MatchMeshToAttachmentRegion_ReportsNoSuchRegionForARenamedAttachment()
    {
        var match = Sts2SpineGeoClipAtlas.MatchMeshToAttachmentRegion(
            "renamed_by_path",
            Sts2SpineAtlasText.Parse(TwoPageRegionAtlas),
            TwoPageRegionSizes,
            [BoxOnPage(0, 100, 200, 220, 340, 1652, 657)]);

        Assert.Equal(-1, match.Order);
        Assert.Equal("no-such-region", match.Method);
    }

    // Two candidates both land inside the region, so containment alone cannot decide — but one of them IS the
    // region and the other is a fifth of its area, which the score separates by far more than 4×.
    [Fact]
    public void MatchMeshToAttachmentRegion_AcceptsAClearWinnerOnTheMarginWhenSeveralAreContained()
    {
        var match = Sts2SpineGeoClipAtlas.MatchMeshToAttachmentRegion(
            "head",
            Sts2SpineAtlasText.Parse(TwoPageRegionAtlas),
            TwoPageRegionSizes,
            [
                BoxOnPage(1, 140, 240, 180, 300, 1652, 657),
                BoxOnPage(2, 100, 200, 220, 340, 1652, 657),
            ]);

        Assert.Equal(2, match.Order);
        Assert.Equal(2, match.ContainedCount);
        Assert.True(match.Score * 4 <= match.RunnerUpScore);
    }

    // Two candidates with the SAME uv box are genuinely undecidable. Reporting the tie is the whole point: a
    // wrong pairing bakes another slot's art onto this slot for the entire clip.
    [Fact]
    public void MatchMeshToAttachmentRegion_RefusesToPickBetweenTwoIdenticalUvBoxes()
    {
        var match = Sts2SpineGeoClipAtlas.MatchMeshToAttachmentRegion(
            "head",
            Sts2SpineAtlasText.Parse(TwoPageRegionAtlas),
            TwoPageRegionSizes,
            [
                BoxOnPage(4, 100, 200, 220, 340, 1652, 657),
                BoxOnPage(5, 100, 200, 220, 340, 1652, 657),
            ]);

        Assert.Equal(-1, match.Order);
        Assert.Equal("ambiguous", match.Method);
        Assert.Equal(2, match.ContainedCount);
    }

    // …and a near-tie is a tie too: 2 page pixels of corner disagreement is neither a 4× score margin nor the
    // 8-pixel absolute one.
    [Fact]
    public void MatchMeshToAttachmentRegion_RefusesToPickBetweenTwoNearlyEqualCandidates()
    {
        var match = Sts2SpineGeoClipAtlas.MatchMeshToAttachmentRegion(
            "head",
            Sts2SpineAtlasText.Parse(TwoPageRegionAtlas),
            TwoPageRegionSizes,
            [
                BoxOnPage(4, 102, 202, 220, 340, 1652, 657),
                BoxOnPage(5, 100, 200, 218, 338, 1652, 657),
            ]);

        Assert.Equal(-1, match.Order);
        Assert.Equal("ambiguous", match.Method);
    }

    [Fact]
    public void MatchMeshToAttachmentRegion_ReportsNoneWhenEveryMeshIsAlreadyClaimed()
    {
        var match = Sts2SpineGeoClipAtlas.MatchMeshToAttachmentRegion(
            "head", Sts2SpineAtlasText.Parse(TwoPageRegionAtlas), TwoPageRegionSizes, []);

        Assert.Equal(-1, match.Order);
        Assert.Equal("none", match.Method);
        Assert.Equal("head", match.RegionName);
    }

    // A region on a page with no declared size cannot be located at all, so it is "no such region" rather than a
    // division by a zero-sized page.
    [Fact]
    public void MatchMeshToAttachmentRegion_TreatsARegionOnASizelessPageAsNoSuchRegion()
    {
        var match = Sts2SpineGeoClipAtlas.MatchMeshToAttachmentRegion(
            "head",
            Sts2SpineAtlasText.Parse("""
page-0.png
head
bounds: 100, 200, 120, 140
"""),
            [(0, 0)],
            [BoxOnPage(0, 100, 200, 220, 340, 1652, 657)]);

        Assert.Equal(-1, match.Order);
        Assert.Equal("no-such-region", match.Method);
    }

    // An atlas packed from folders spells a region `folder/name`; the association pass and the page resolver must
    // answer that question the same way or they disagree about the same rig.
    [Fact]
    public void MatchMeshToAttachmentRegion_MatchesAFolderQualifiedRegionName()
    {
        var match = Sts2SpineGeoClipAtlas.MatchMeshToAttachmentRegion(
            "head",
            Sts2SpineAtlasText.Parse("""
page-0.png
size: 1652, 657
merchant/head
bounds: 100, 200, 120, 140
"""),
            [(1652, 657)],
            [BoxOnPage(9, 100, 200, 220, 340, 1652, 657)]);

        Assert.Equal(9, match.Order);
        Assert.Equal("merchant/head", match.RegionName);
    }

    // ── The bake report ──────────────────────────────────────────────────────────────────────────────

    private static GeoClipBakeReport SampleReport()
        => new(
            "spine-geoclip-baker/0",
            Owner: "Spirectl.Sts2",
            PoseOnly: false,
            SampleTimeSeconds: 0d,
            SampleTimeSource: Sts2SpineGeoClipMath.SampleSourceWholeClip,
            Slots: 71,
            SlotsEverVisible: 44,
            SlotsVisibleAtAcquisition: 44,
            AcquisitionFrame: 0,
            Windows:
            [
                // One window carried by the WALK arm and one that needed the dense sweep behind it, so the
                // serialization contract is exercised on both shapes — a nested `walk` block present and absent.
                new GeoClipBakeWindow(
                    "A", 16204, 16448, 23, 195, 245, 42_385, 50_000, false, 852, 30, 28.4, 42_385,
                    "config", 50_000, 50_000, new Dictionary<string, int> { ["vertex-type"] = 2 },
                    Sts2SpineGeoClipWalk.ArmWalk,
                    "ok",
                    new GeoClipBakeWalk(
                        ColumnsPlanned: 173, ColumnsScanned: 3, ColumnsSkipped: 42, AnchorProbed: 449,
                        AnchorHypotheses: 1, DeadAnchors: 0, Runs: 1, StrideValidator: 5, StrideIndex: 1,
                        StrideSource: Sts2SpineGeoClipWalk.StrideMeasured, StrideProbed: 48, LineProbed: 45,
                        LineFound: 30, IndexWalkProbed: 310, IndexWalkFound: 28, StopUp: "validator-ceiling",
                        StopDown: "validator-floor", GapSizes: [13], IndexExtentLow: 88, IndexExtentHigh: 130,
                        StopReason: Sts2SpineGeoClipWalk.StopBandClosed, BudgetExhausted: false, Probed: 852,
                        Found: 30, Ms: 28.4)),
                new GeoClipBakeWindow(
                    "B", 16448, 16948, 0, 224, 501, 112_725, 112_725, false, 112_725, 14, 4620.5, 112_725,
                    "auto-raised", 50_000, 112_725, new Dictionary<string, int>(),
                    Sts2SpineGeoClipWalk.ArmDense,
                    "the window holds 2184 candidates, at or below the floor"),
            ],
            MeshesValidated: 44,
            MeshFilterStrict: true,
            Associated: 44,
            Unassociated: 0,
            ByColorFlip: 29,
            ByColorFlipPlusAtlas: 1,
            ByElimination: 1,
            ByAtlasRegion: 13,
            AmbiguousColorFlip: 0,
            AmbiguousAtlasRegion: 0,
            ForeignMeshes: 0,
            StaleMeshFrames: 0,
            Complete: true,
            AtlasFallbackAvailable: true,
            AssociationPairs: [[0, 0], [21, 4]],
            // P7: the shadow arm and the phase profile are carried HERE rather than left at their defaults, so
            // the key allowlist below actually walks them. A block only present on some reports would otherwise
            // ship un-gated the first time a live bake produced one.
            AtlasFirstResolved: 41,
            AtlasFirstAgreed: 38,
            AtlasFirstDisagreed: 3,
            AtlasFirstDisagreements: ["7:12vs4:containment", "9:3vsnone:uv-region-exact"],
            AssocAtlasFirstArmed: true,
            AtlasFirstWidened: 1,
            Profile: new GeoClipBakeProfile(
                TotalMs: 8412.5,
                BlockingMs: 5120.25,
                ParkedMs: 2900.5,
                UnattributedMs: 391.75,
                FramesWaited: 118,
                ForceDraws: 96,
                Phases:
                [
                    new GeoClipPhaseCost(Sts2RenderPhaseProfile.Phase.BakeWarmupWait, 50.5, 3, false),
                    new GeoClipPhaseCost(Sts2RenderPhaseProfile.Phase.BakeSweepProbe, 4620.5, 2, true),
                    new GeoClipPhaseCost(Sts2RenderPhaseProfile.Phase.BakeColorRead, 380.25, 1, true),
                ]),
            // P7 WS-7: the scene-tree pause, carried here at NON-default values for the same reason the block
            // above is — a field only present on some reports would otherwise ship un-gated the first time a
            // paused bake produced one.
            BakePaused: true,
            PauseNote: "running -> paused",
            PauseAssertFailed: 0,
            // Carried at a NON-default value for the same reason the two blocks above are: a field only present
            // on some reports would otherwise ship un-gated the first time a single-pose bake produced one.
            SlotsTransparentAtPose: 2);

    // The report is ADDITIVE: `meta.schema` stays "geoclip/0" and a player that has never heard of `bake` reads
    // exactly the same clip. (The harness's selftest asserts it tolerates unknown keys at every level.)
    [Fact]
    public void Serialize_CarriesTheBakeReportWithoutChangingTheGeoclipV1Contract()
    {
        var result = Sts2SpineGeoClipBuilder.Build(Request(
            new GeoClipFrameSample(0d, [0], [0, 0, 10, 10], [Quad(0, "head")])));

        using var document = JsonDocument.Parse(
            Sts2SpineGeoClipBuilder.Serialize(result.Document with { Bake = SampleReport() }));
        var root = document.RootElement;

        Assert.Equal("geoclip/0", root.GetProperty("meta").GetProperty("schema").GetString());
        foreach (var key in new[] { "meta", "pages", "parts", "frames", "diagnostics" })
        {
            Assert.True(root.TryGetProperty(key, out _), $"{key} is missing");
        }

        var bake = root.GetProperty("bake");
        Assert.Equal("spine-geoclip-baker/0", bake.GetProperty("bakerVersion").GetString());
        // Which copy of the runtime won the one-shot claim. A loadout can hold two, and a single env var reaches
        // both, so the artifact has to be auditable back to the one that produced it.
        Assert.Equal("Spirectl.Sts2", bake.GetProperty("owner").GetString());
        Assert.Equal(44, bake.GetProperty("slotsEverVisible").GetInt32());
        Assert.Equal(44, bake.GetProperty("meshesValidated").GetInt32());
        Assert.True(bake.GetProperty("complete").GetBoolean());
        Assert.Equal(13, bake.GetProperty("byAtlasRegion").GetInt32());
        Assert.Equal(0, bake.GetProperty("staleMeshFrames").GetInt32());

        // Per-window found counts are the proof the split worked, so they have to survive serialization.
        var windows = bake.GetProperty("windows");
        Assert.Equal(2, windows.GetArrayLength());
        Assert.Equal("A", windows[0].GetProperty("name").GetString());
        Assert.Equal(30, windows[0].GetProperty("found").GetInt32());
        Assert.Equal(2, windows[0].GetProperty("rejectedByReason").GetProperty("vertex-type").GetInt32());
        Assert.Equal("B", windows[1].GetProperty("name").GetString());
        Assert.Equal(14, windows[1].GetProperty("found").GetInt32());
        Assert.Equal(0u, windows[1].GetProperty("indexLow").GetUInt32());
        Assert.Equal("auto-raised", windows[1].GetProperty("capSource").GetString());
        Assert.Equal(50_000, windows[1].GetProperty("capAutoRaisedFrom").GetInt32());
        Assert.Equal(112_725, windows[1].GetProperty("capAutoRaisedTo").GetInt32());

        Assert.Equal([0, 0], bake.GetProperty("associationPairs")[0].EnumerateArray().Select(v => v.GetInt32()));
    }

    // THE SCENE-TREE PAUSE'S MANIFEST SURFACE, pinned by exact key AND exact string. These are what a future
    // live arm greps for — in a manifest, not in a log that may not have been kept — so a rename here is a
    // breaking change to the only durable record of whether a bake ran paused, and the assertion is written as
    // literals rather than against the report's own properties so it cannot agree with a rename.
    [Fact]
    public void Serialize_CarriesTheSceneTreePauseFlagNoteAndAssertCount()
    {
        var result = Sts2SpineGeoClipBuilder.Build(Request(
            new GeoClipFrameSample(0d, [0], [0, 0, 10, 10], [Quad(0, "head")])));

        using var paused = JsonDocument.Parse(
            Sts2SpineGeoClipBuilder.Serialize(result.Document with { Bake = SampleReport() }));
        var pausedBake = paused.RootElement.GetProperty("bake");
        Assert.True(pausedBake.GetProperty("bakePaused").GetBoolean());
        Assert.Equal("running -> paused", pausedBake.GetProperty("pauseNote").GetString());
        Assert.Equal(0, pausedBake.GetProperty("pauseAssertFailed").GetInt32());

        // …and the DEFAULT-OFF shape, which is what every artifact baked this round actually carries. The keys
        // are present and default-valued rather than absent, so a reader never has to distinguish "this baker
        // predates the lever" from "the lever was off" by the absence of a key.
        using var unpaused = JsonDocument.Parse(
            Sts2SpineGeoClipBuilder.Serialize(result.Document with
            {
                Bake = SampleReport() with { BakePaused = false, PauseNote = string.Empty },
            }));
        var unpausedBake = unpaused.RootElement.GetProperty("bake");
        Assert.False(unpausedBake.GetProperty("bakePaused").GetBoolean());
        Assert.Equal(string.Empty, unpausedBake.GetProperty("pauseNote").GetString());
        Assert.Equal(0, unpausedBake.GetProperty("pauseAssertFailed").GetInt32());
    }

    // A bake that reported nothing omits the key ENTIRELY rather than writing `"bake": null`: a key that is
    // sometimes null and sometimes an object is harder for a client to reason about than one that is absent.
    [Fact]
    public void Serialize_OmitsTheBakeBlockWhenNothingReportedOne()
    {
        var result = Sts2SpineGeoClipBuilder.Build(Request(
            new GeoClipFrameSample(0d, [0], [0, 0, 10, 10], [Quad(0, "head")])));

        using var document = JsonDocument.Parse(Sts2SpineGeoClipBuilder.Serialize(result.Document));

        Assert.Null(result.Document.Bake);
        Assert.False(document.RootElement.TryGetProperty("bake", out _));
    }

    // ── meta.placement ───────────────────────────────────────────────────────────────────────────────

    private static GeoClipPlacement SamplePlacement()
        => GeoClipPlacement.From(Sts2SceneFitFrame.Place(Sts2SceneFitFrame.Fit(-100f, -200f, 300f, 400f)));

    // ADDITIVE and OPTIONAL, like the bake report: `meta.schema` stays "geoclip/0", so a player that has never
    // heard of `placement` reads exactly the same clip.
    [Fact]
    public void Serialize_CarriesTheExplicitPlacementWithoutChangingTheGeoclipV1Contract()
    {
        var result = Sts2SpineGeoClipBuilder.Build(Request(
            new GeoClipFrameSample(0d, [0], [0, 0, 10, 10], [Quad(0, "head")])) with
        {
            Placement = SamplePlacement(),
        });

        using var document = JsonDocument.Parse(Sts2SpineGeoClipBuilder.Serialize(result.Document));
        var meta = document.RootElement.GetProperty("meta");

        Assert.Equal("geoclip/0", meta.GetProperty("schema").GetString());
        var placement = meta.GetProperty("placement");
        Assert.Equal(428, placement.GetProperty("canvasWidth").GetInt32());
        Assert.Equal(528, placement.GetProperty("canvasHeight").GetInt32());
        Assert.Equal(-164d, placement.GetProperty("localX").GetDouble(), 6);
        Assert.Equal(-264d, placement.GetProperty("localY").GetDouble(), 6);
        Assert.Equal(428d, placement.GetProperty("localWidth").GetDouble(), 6);
        Assert.Equal(528d, placement.GetProperty("localHeight").GetDouble(), 6);
        Assert.Equal(1d, placement.GetProperty("fitScale").GetDouble(), 6);
    }

    // A bake that could not measure a placement omits the key ENTIRELY rather than writing `"placement": null`:
    // a key that is sometimes null and sometimes an object is harder for a client to reason about than an absent
    // one, and this is the default the existing whole-clip artifacts keep.
    [Fact]
    public void Serialize_OmitsPlacementWhenTheBakeMeasuredNone()
    {
        var result = Sts2SpineGeoClipBuilder.Build(Request(
            new GeoClipFrameSample(0d, [0], [0, 0, 10, 10], [Quad(0, "head")])));

        using var document = JsonDocument.Parse(Sts2SpineGeoClipBuilder.Serialize(result.Document));

        Assert.Null(result.Document.Meta.Placement);
        Assert.False(document.RootElement.GetProperty("meta").TryGetProperty("placement", out _));
    }

    // Baker NOTES lead the manifest's diagnostics, so a counter whose MEANING changed (a pose-only bake's
    // "ever visible" is "visible at this pose") says so in the artifact rather than only in a host log a reader
    // of a stray manifest will never see.
    [Fact]
    public void Build_PutsTheBakersOwnNotesAtTheHeadOfTheDiagnostics()
    {
        var result = Sts2SpineGeoClipBuilder.Build(Request(
            new GeoClipFrameSample(0d, [0], [0, 0, 10, 10], [Quad(0, "head")])) with
        {
            Notes = ["POSE-ONLY bake: one frame."],
        });

        Assert.Equal("POSE-ONLY bake: one frame.", result.Document.Diagnostics[0]);
        Assert.Equal(result.Diagnostics, result.Document.Diagnostics);
    }

    // ── The manifest key ALLOWLIST (licensing enforcement) ───────────────────────────────────────────
    //
    // A geoclip ships COMPUTED OUTPUTS ONLY: posed vertex positions, triangle indices, uvs, per-slot tint, blend
    // mode, draw order, the atlas pages those triangles sample, plus placement and bake diagnostics. It must
    // never ship `.skel` / `.json` / `.atlas` rig data, a bone hierarchy, or authoring data. The line is easy to
    // cross by ACCRETION — one convenient extra field at a time — so it is asserted rather than reviewed: every
    // object in the serialized manifest is walked and every key checked against the set allowed at its shape.
    // A future field carrying rig STRUCTURE fails here instead of shipping.

    private static readonly Dictionary<string, string[]> AllowedManifestKeys = new(StringComparer.Ordinal)
    {
        [""] = ["meta", "pages", "parts", "frames", "diagnostics", "bake"],
        ["meta"] =
        [
            "schema", "scene", "node", "anim", "fps", "frameCount", "durationMs", "boundsPerFrame", "placement",
        ],
        ["meta.placement"] =
        [
            "canvasWidth", "canvasHeight", "localX", "localY", "localWidth", "localHeight", "fitScale",
        ],
        ["pages[]"] = ["id", "file", "width", "height", "sha256"],
        ["parts[]"] =
        [
            "id", "slotIndex", "attachmentName", "pageId", "srcRect", "indices", "uvs", "refVerts", "refFrame",
            "rigid", "blendMode",
        ],
        ["frames[]"] = ["t", "drawOrder", "slots"],
        // Keyed by slot INDEX, so its keys are numbers rather than field names; the per-slot objects below are
        // what carry fields.
        ["frames[].slots"] = [],
        ["frames[].slots.*"] = ["part", "color", "xform", "verts"],
        ["bake"] =
        [
            "bakerVersion", "owner", "poseOnly", "sampleTimeSeconds", "sampleTimeSource", "slots",
            "slotsEverVisible", "slotsVisibleAtAcquisition", "acquisitionFrame", "windows", "meshesValidated",
            "meshFilterStrict", "associated", "unassociated", "byColorFlip", "byColorFlipPlusAtlas",
            "byElimination", "byAtlasRegion",
            // Which of the two atlas arms claimed a slot — the plain region match, or the slot-colour tie-break
            // behind it. A count of the BAKER's algorithms, like the rest of this block; it names no slot, no
            // region and no attachment.
            "byAtlasRegionSlotColor",
            "ambiguousColorFlip", "ambiguousAtlasRegion", "foreignMeshes",
            "staleMeshFrames", "complete", "atlasFallbackAvailable", "associationPairs",
            "acquisitionAnim", "batched",
            // The bake-level truncation roll-up. Both are statements about the SWEEP's own budget — whether it
            // enumerated the candidate space it planned — and neither carries anything about the rig.
            "truncated", "truncatedNote",
            // The atlas-first SHADOW arm's counts. Statements about the BAKER's two association algorithms
            // agreeing, not about the rig. `atlasFirstDisagreements` carries slot INDICES and mesh orders only —
            // deliberately not slot names or atlas region names, which are authoring identifiers and stay in the
            // log where the artifact policy allows them.
            "atlasFirstResolved", "atlasFirstAgreed", "atlasFirstDisagreed", "atlasFirstDisagreements",
            // …and whether that arm was ARMED — i.e. whether the atlas DISCOVERED the association above or only
            // shadowed it — plus how many probe groups it had to re-run on the whole read set. Both are
            // statements about which of the baker's algorithms ran and what it cost, not about the rig.
            "assocAtlasFirstArmed", "atlasFirstWidened",
            // Where the bake's own wall clock went. Instrument names and durations; nothing about the rig.
            "profile",
            // Whether the bake ran on a PAUSED scene tree, why, and how many times this process's liveness
            // assertion refuted a pause. `pauseNote` is a fixed vocabulary of lease outcomes carrying env var
            // names and exception TYPE names — no scene path, node name or animation name — and the other two are
            // numbers about the BAKER's own lever. Nothing here says anything about the rig.
            "bakePaused", "pauseNote", "pauseAssertFailed",
            // The OWNERSHIP split the admission rule grades: two counts, plus the provenances behind them.
            // `claimProof` is a fixed vocabulary of the baker's own association methods — `color-flip`,
            // `elimination`, `atlas:` + one of Sts2SpineGeoClipAtlas's five match tokens — with a count each. It
            // deliberately carries no attachment name, atlas region name or slot name: those are authoring
            // identifiers and stay in the log, exactly as `atlasFirstDisagreements` above already decided.
            "claimsProven", "claimsUnproven", "claimProof",
            // How many drawable slots read back fully transparent at a single sampled pose and were therefore
            // not expected to draw. A COUNT of the baker's own expectation rule, in the same family as
            // `ambiguousAtlasRegion` and `foreignMeshes` above; it names no slot, no attachment and no region.
            "slotsTransparentAtPose",
        ],
        ["bake.profile"] =
        [
            "totalMs", "blockingMs", "parkedMs", "unattributedMs", "framesWaited", "forceDraws", "phases",
            "blockingShare", "parkedShare",
            // A count of forced draws the DRAW BUDGET declined to issue — the same class of fact as
            // `forceDraws` and `framesWaited` beside it: a scalar about what the BAKER did, carrying nothing
            // about the rig. Admitted because `forceDraws` alone cannot say which schedule the `blockingMs` in
            // the same object was measured under. See GeoClipDrawBudget.
            "drawsElided",
            // The DENOMINATORS for the two phases that dominate what is left of the bake's blocking time.
            // `bake.profile.phases[]` gives each a total ms and a `calls` count, but `calls` counts the ARM,
            // not the work inside it — so neither can answer "what did one probe cost?" without these. Same
            // class as every scalar above: what the baker did, nothing about the rig.
            "colorReads", "sweepProbes",
        ],
        ["bake.profile.phases[]"] = ["phase", "ms", "calls", "blocking"],
        ["bake.windows[]"] =
        [
            "name", "validatorLow", "validatorHigh", "indexLow", "indexHigh", "validatorSpan", "totalCandidates",
            "cap", "truncated", "probed", "found", "sweepMs", "recommendedCap", "capSource", "capAutoRaisedFrom",
            "capAutoRaisedTo", "rejectedByReason", "arm", "armNote", "walk",
            // The two dense TIERS. All seven are RID-allocator counters about the SWEEP — which index band was
            // tried first, what it cost, and whether it finished — in the same family as `indexLow` / `probed`
            // beside them, and none of them says anything about the rig being baked.
            "tier", "coreIndexLow", "coreIndexHigh", "narrowCandidates", "narrowProbed", "narrowFound",
            "narrowTruncated",
        ],
        // Keyed by rejection REASON, which is an open vocabulary of diagnostic counters, not a field list.
        ["bake.windows[].rejectedByReason"] = [],
        ["bake.windows[].walk"] =
        [
            "columnsPlanned", "columnsScanned", "columnsSkipped", "anchorProbed", "anchorHypotheses",
            "deadAnchors", "runs", "strideValidator", "strideIndex", "strideSource", "strideProbed",
            "lineProbed", "lineFound", "indexWalkProbed", "indexWalkFound", "stopUp", "stopDown", "gapSizes",
            "indexExtentLow", "indexExtentHigh", "stopReason", "budgetExhausted", "probed", "found", "ms",
        ],
    };

    // Shapes whose keys are DATA (a slot index, a rejection reason) rather than field names.
    private static readonly HashSet<string> OpenKeyedShapes =
        new(StringComparer.Ordinal) { "frames[].slots", "bake.windows[].rejectedByReason" };

    private static void WalkManifestKeys(JsonElement element, string shape, List<string> violations)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (!AllowedManifestKeys.TryGetValue(shape, out var allowed))
                {
                    violations.Add($"unknown OBJECT shape '{shape}' in the manifest");
                    return;
                }

                var open = OpenKeyedShapes.Contains(shape);
                foreach (var property in element.EnumerateObject())
                {
                    if (!open && !allowed.Contains(property.Name, StringComparer.Ordinal))
                    {
                        violations.Add($"{shape}.{property.Name}");
                    }

                    WalkManifestKeys(property.Value, open ? shape + ".*" : Join(shape, property.Name), violations);
                }

                return;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    WalkManifestKeys(item, shape + "[]", violations);
                }

                return;
            default:
                return;
        }
    }

    private static string Join(string shape, string name) => shape.Length == 0 ? name : shape + "." + name;

    [Fact]
    public void Serialize_EmitsOnlyTheAllowlistedComputedOutputKeys()
    {
        var result = Sts2SpineGeoClipBuilder.Build(Request(
            new GeoClipFrameSample(
                0d, [0, 1], [0, 0, 10, 10], [Quad(0, "head"), GeoClipSlotSample.Hidden(1, [1, 1, 1, 1], 0)]),
            new GeoClipFrameSample(
                1d / 30d,
                [0, 1],
                [5, 0, 10, 10],
                [Quad(0, "head", dx: 5), GeoClipSlotSample.Hidden(1, [1, 1, 1, 1], 0)])) with
        {
            Placement = SamplePlacement(),
        });

        using var document = JsonDocument.Parse(
            Sts2SpineGeoClipBuilder.Serialize(result.Document with { Bake = SampleReport() }));

        var violations = new List<string>();
        WalkManifestKeys(document.RootElement, string.Empty, violations);

        Assert.True(
            violations.Count == 0,
            "The geoclip manifest carries keys outside the computed-output allowlist. A geoclip must ship only "
            + "computed outputs (posed vertices, indices, uvs, tints, blend mode, draw order, page refs, "
            + "placement, diagnostics) — never rig structure, bone hierarchy or authoring data. Offending keys: "
            + string.Join(", ", violations));
    }

    // The allowlist is only worth having if it FAILS on an unknown key — a walk that silently skipped an
    // unrecognised shape would pass forever. Mutation-tested with a hand-injected rig-structure field.
    [Fact]
    public void ManifestKeyAllowlist_FailsOnAFieldThatCarriesRigStructure()
    {
        using var document = JsonDocument.Parse(
            """{"meta":{"schema":"geoclip/0","bones":[{"name":"root","parent":null}]},"pages":[],"parts":[],"frames":[],"diagnostics":[]}""");

        var violations = new List<string>();
        WalkManifestKeys(document.RootElement, string.Empty, violations);

        Assert.Contains("meta.bones", violations);
    }

    // …and on an unknown key nested inside an allowed one, where a walk that only checked the top level would
    // wave it through.
    [Fact]
    public void ManifestKeyAllowlist_FailsOnAnUnknownKeyNestedInsideAnAllowedObject()
    {
        using var document = JsonDocument.Parse(
            """{"meta":{"schema":"geoclip/0","placement":{"canvasWidth":1,"skeletonJson":"…"}},"pages":[],"parts":[],"frames":[],"diagnostics":[]}""");

        var violations = new List<string>();
        WalkManifestKeys(document.RootElement, string.Empty, violations);

        Assert.Contains("meta.placement.skeletonJson", violations);
    }

    // ── Foreign-candidate diagnostics ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("garbled", 0)]
    [InlineData("-2", 0)]
    [InlineData("0", 0)]
    [InlineData("2", 2)]
    [InlineData("999", Sts2SpineGeoClipForeignCandidateDiagnostics.MaximumLimit)]
    public void ForeignCandidateDiagnostics_LeverIsOffByDefaultAndBounded(string? raw, int expected)
    {
        Assert.Equal(expected, Sts2SpineGeoClipForeignCandidateDiagnostics.ParseLimit(raw));
    }

    // OFF unless a caller arms it. The lever is the caller's argument rather than an environment read inside
    // PlanConfig because that method's purity is what lets RequestLane_PlansABakeWhileTheEnvLaneHoldsTheClaim
    // mean anything; this pins the default so an unarmed request still plans a byte-identical log stream.
    [Fact]
    public void ForeignCandidateDiagnostics_RequestLaneDefaultsOffWhenNoLimitIsArmed()
    {
        var config = Sts2SpineGeoClipRequestLane.PlanConfig(
            new SpineGeoClipBakeRequestSnapshot("res://scene.tscn", null, "idle", null, "/tmp/geoclips"),
            "Spirectl.Sts2",
            out var rejected);

        Assert.Null(rejected);
        Assert.NotNull(config);
        Assert.Equal(0, config!.ForeignCandidateDiagnosticsLimit);
    }

    // …and REACHES THE BAKE when one is. Both lanes converge on BakeRigOnMainThreadAsync, which only ever sees
    // the limit its config carries — and the request lane used to build that config without setting the field at
    // all, so the lane serving /geoclips/ was the one lane that could not report why association left a
    // validated mesh unclaimed. The caller resolves the value through the same ParseLimit the env lane uses, so
    // arming and the cap stay one implementation; this proves what it resolves survives the plan.
    [Theory]
    [InlineData("3", 3)]
    [InlineData("999", Sts2SpineGeoClipForeignCandidateDiagnostics.MaximumLimit)]
    public void ForeignCandidateDiagnostics_RequestLaneCarriesAnArmedLimitIntoTheBakeConfig(string raw, int expected)
    {
        var config = Sts2SpineGeoClipRequestLane.PlanConfig(
            new SpineGeoClipBakeRequestSnapshot("res://scene.tscn", null, "idle", null, "/tmp/geoclips"),
            "Spirectl.Sts2",
            out var rejected,
            Sts2SpineGeoClipForeignCandidateDiagnostics.ParseLimit(raw));

        Assert.Null(rejected);
        Assert.NotNull(config);
        Assert.Equal(expected, config!.ForeignCandidateDiagnosticsLimit);
    }

    [Fact]
    public void ForeignCandidateDiagnostics_OffEmitsNothingAndOnLogsOnlyCappedGenericSummaries()
    {
        var candidates = new[]
        {
            new Sts2SpineGeoClipForeignCandidateDiagnostics.Summary(3, 4, 6, "unique", 1, 2, 5, 8, 0, 0.25, 1, 0.75),
            new Sts2SpineGeoClipForeignCandidateDiagnostics.Summary(7, 8, 12, "ambiguous", -2, -1, 2, 3, 0.1, 0.2, 0.9, 1),
            new Sts2SpineGeoClipForeignCandidateDiagnostics.Summary(9, 12, 18, "none", 0, 0, 9, 9, 0, 0, 1, 1),
        };

        Assert.Empty(Sts2SpineGeoClipForeignCandidateDiagnostics.Format(candidates, 0));

        var lines = Sts2SpineGeoClipForeignCandidateDiagnostics.Format(candidates, 2);

        Assert.Equal(2, lines.Count);
        Assert.Contains("provenance=sweep-order:3", lines[0]);
        Assert.Contains("geometry=vertices:4 indices:6 atlas:unique xy:[1,2]..[5,8] uv:[0,0.25]..[1,0.75]", lines[0]);
        Assert.Contains("provenance=sweep-order:7", lines[1]);
        Assert.Contains("atlas:ambiguous", lines[1]);
        Assert.DoesNotContain("sweep-order:9", string.Join(" ", lines));
    }

    [Fact]
    public void ForeignCandidateDiagnostics_AtlasProvenanceDoesNotExposeRegionIdentity()
    {
        var atlas = Sts2SpineAtlasText.Parse(
            """
            page.png
            size: 100, 100
            format: RGBA8888
            one
            bounds: 10, 10, 20, 20
            two
            bounds: 10, 10, 20, 20
            """);

        Assert.Equal(
            Sts2SpineGeoClipAtlas.UvProvenanceNone,
            Sts2SpineGeoClipAtlas.ClassifyUvProvenance(0.7, 0.7, 0.8, 0.8, atlas, [(100, 100)]));
        Assert.Equal(
            Sts2SpineGeoClipAtlas.UvProvenanceAmbiguous,
            Sts2SpineGeoClipAtlas.ClassifyUvProvenance(0.1, 0.1, 0.3, 0.3, atlas, [(100, 100)]));

        var uniqueAtlas = Sts2SpineAtlasText.Parse(TwoPageAtlas);
        Assert.Equal(
            Sts2SpineGeoClipAtlas.UvProvenanceUnique,
            Sts2SpineGeoClipAtlas.ClassifyUvProvenance(0.1, 0.1, 0.3, 0.3, uniqueAtlas, TwoPageSizes));
    }
}
