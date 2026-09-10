using Spirectl.Sts2;
using Spirectl.Sts2.Core.Artifacts;
using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// P4-WS1: the POSE-ONLY geoclip bake, its EXPLICIT placement, and the ON-DEMAND request lane. Everything here is
// Godot-free on purpose — the bake itself needs a game, but which pose it samples, where it says the clip sits,
// what a request plans, and whether the request lane touches the process-global arm claim are all decidable
// without one, and all four are things a live run would only discover expensively.
public sealed class Sts2SpineGeoClipPoseBakeTests
{
    // ── Which pose a single-frame bake samples ───────────────────────────────────────────────────────
    //
    // The rule itself is Sts2SpineStillFrame's and is tested beside the still renderer
    // (Sts2RenderEncodeBudgetTests). What is tested HERE is that the geoclip baker consumes it through the same
    // entry point rather than through a copy — which is why ChooseSample returns the time AND the branch that
    // produced it: a separate classifier could drift, and a mislabelled source still carries a plausible time.

    [Fact]
    public void ChooseSample_ReportsTheMidBranchForAnOrdinaryClip()
    {
        var sample = Sts2SpineStillFrame.ChooseSample("idle_loop", 13.3333f);

        Assert.Equal(6.66665f, sample.Seconds, 4);
        Assert.Equal(Sts2SpineStillFrame.SampleSourceMid, sample.Source);
    }

    [Fact]
    public void ChooseSample_ReportsTheTerminalBranchForADeathClip()
    {
        var sample = Sts2SpineStillFrame.ChooseSample("die", 2f);

        Assert.Equal(2f, sample.Seconds, 4);
        Assert.Equal(Sts2SpineStillFrame.SampleSourceTerminalEnd, sample.Source);
    }

    // An explicit `&t=` wins over BOTH heuristics — including the terminal one, whose default would be the end.
    [Fact]
    public void ChooseSample_ReportsTheRequestedBranchWhenATimeWasAskedFor()
    {
        Assert.Equal(
            Sts2SpineStillFrame.SampleSourceRequested,
            Sts2SpineStillFrame.ChooseSample("idle_loop", 2f, 0.25f).Source);
        Assert.Equal(
            Sts2SpineStillFrame.SampleSourceRequested,
            Sts2SpineStillFrame.ChooseSample("die", 2f, 0.1f).Source);
    }

    // A degenerate duration has exactly one sample whatever was asked for, and it says so rather than claiming
    // the request won: `sampleTimeSource=degenerate` with `sampleTimeSeconds=0` is the honest pair.
    [Fact]
    public void ChooseSample_ReportsTheDegenerateBranchAheadOfAnyRequest()
    {
        var sample = Sts2SpineStillFrame.ChooseSample("animation", 0f, 5f);

        Assert.Equal(0f, sample.Seconds);
        Assert.Equal(Sts2SpineStillFrame.SampleSourceDegenerate, sample.Source);
    }

    // The projection must never disagree with the pair it projects from.
    [Theory]
    [InlineData("idle_loop", 2f, null)]
    [InlineData("die_loop", 2f, null)]
    [InlineData("attack", 2f, 0.5f)]
    [InlineData("attack", 0f, 0.5f)]
    [InlineData(null, 3.5f, null)]
    public void ChooseSampleTime_IsExactlyChooseSamplesSeconds(string? anim, float duration, float? requested)
        => Assert.Equal(
            Sts2SpineStillFrame.ChooseSample(anim, duration, requested).Seconds,
            Sts2SpineStillFrame.ChooseSampleTime(anim, duration, requested));

    // ── The arming spec's pose selectors ─────────────────────────────────────────────────────────────

    [Fact]
    public void ParseTargets_ReadsAnExplicitPoseFlag()
    {
        var target = Assert.Single(Sts2SpineGeoClipSpec.ParseTargets("res://a.tscn?anim=idle_loop&pose=1"));

        Assert.True(target.PoseOnly);
        Assert.Null(target.SampleTimeSeconds);
    }

    // Asking for ONE animation time is asking for ONE pose: a whole-clip bake has no use for a single `t`.
    [Fact]
    public void ParseTargets_TreatsAnExplicitTimeAsAPoseRequest()
    {
        var target = Assert.Single(Sts2SpineGeoClipSpec.ParseTargets("res://a.tscn?anim=die&t=1.75"));

        Assert.True(target.PoseOnly);
        Assert.Equal(1.75d, target.SampleTimeSeconds!.Value, 6);
    }

    // …but the implication is not a trap: `pose=0` turns it off and KEEPS the time, whichever order they appear
    // in, so an operator can bake a whole clip while pinning a `t` for the report.
    [Theory]
    [InlineData("res://a.tscn?anim=die&t=1.75&pose=0")]
    [InlineData("res://a.tscn?anim=die&pose=0&t=1.75")]
    public void ParseTargets_LetsAnExplicitPoseZeroOverrideTheImplication(string spec)
    {
        var target = Assert.Single(Sts2SpineGeoClipSpec.ParseTargets(spec));

        Assert.False(target.PoseOnly);
        Assert.Equal(1.75d, target.SampleTimeSeconds!.Value, 6);
    }

    // A garbled or negative time is DROPPED rather than clamped: the mid/terminal heuristic is a better answer
    // than a number nobody meant. Dropping it also drops the implied pose-only, which is the honest reading of a
    // spec that asked for nothing legible.
    [Theory]
    [InlineData("res://a.tscn?anim=idle_loop&t=abc")]
    [InlineData("res://a.tscn?anim=idle_loop&t=-1")]
    public void ParseTargets_IgnoresAnUnusableTime(string spec)
    {
        var target = Assert.Single(Sts2SpineGeoClipSpec.ParseTargets(spec));

        Assert.Null(target.SampleTimeSeconds);
        Assert.False(target.PoseOnly);
    }

    // THE REGRESSION GUARD for the existing one-shot lane: a spec that names neither selector parses exactly as
    // it always did, so every recorded whole-clip recipe keeps working byte-identically.
    [Fact]
    public void ParseTargets_LeavesTheWholeClipSpecFormUnchanged()
    {
        var targets = Sts2SpineGeoClipSpec.ParseTargets(
            "res://scenes/creature_visuals/byrdonis.tscn?anim=idle_loop;"
            + "res://scenes/merchant/characters/ironclad_merchant.tscn?anim=idle_loop");

        Assert.Equal(2, targets.Count);
        Assert.All(targets, target => Assert.False(target.PoseOnly));
        Assert.All(targets, target => Assert.Null(target.SampleTimeSeconds));
    }

    // The run-wide default and the per-target opt-in compose as an OR, and the per-target time wins over it.
    [Fact]
    public void GeoClipConfig_ComposesTheRunWideDefaultWithThePerTargetOptIn()
    {
        var plain = new GeoClipTarget("res://a.tscn", null, "idle_loop", "res://a.tscn?anim=idle_loop");
        var posed = plain with { PoseOnly = true, SampleTimeSeconds = 0.5d };

        var wholeClip = Config(false, null);
        Assert.False(wholeClip.IsPoseOnly(plain));
        Assert.True(wholeClip.IsPoseOnly(posed));
        Assert.Null(wholeClip.RequestedSampleTime(plain));
        Assert.Equal(0.5d, wholeClip.RequestedSampleTime(posed)!.Value, 6);

        var poseOnly = Config(true, 1.25d);
        Assert.True(poseOnly.IsPoseOnly(plain));
        Assert.Equal(1.25d, poseOnly.RequestedSampleTime(plain)!.Value, 6);
        Assert.Equal(0.5d, poseOnly.RequestedSampleTime(posed)!.Value, 6);
    }

    // ── The placement algebra ────────────────────────────────────────────────────────────────────────

    // A known bounds rect in, known placement out. Below MaxSceneSize nothing is scaled, so the canvas is the
    // bounds plus a 64px margin on every side and the node origin sits at that margin minus the bounds origin.
    [Fact]
    public void Fit_SizesTheCanvasToTheBoundsPlusPaddingWhenNothingNeedsScaling()
    {
        var frame = Sts2SceneFitFrame.Fit(-100f, -200f, 300f, 400f);

        Assert.Equal(428, frame.ViewportWidth);
        Assert.Equal(528, frame.ViewportHeight);
        Assert.Equal(164f, frame.NodePositionX);
        Assert.Equal(264f, frame.NodePositionY);
        Assert.Equal(1f, frame.NodeScale);

        var placement = Sts2SceneFitFrame.Place(frame);
        Assert.Equal(428, placement.CanvasWidth);
        Assert.Equal(528, placement.CanvasHeight);
        Assert.Equal(-164d, placement.LocalX, 6);
        Assert.Equal(-264d, placement.LocalY, 6);
        Assert.Equal(428d, placement.LocalWidth, 6);
        Assert.Equal(528d, placement.LocalHeight, 6);
        Assert.Equal(1d, placement.FitScale, 6);
    }

    // THE FIT-SCALE BRANCH. A rig longer than MaxSceneSize on either side is scaled UNIFORMLY to fit rather than
    // clamped per dimension — a per-dimension clamp crops the overflow, which is how an oversized rig with a large
    // negative offset used to lose its right/bottom edge.
    [Fact]
    public void Fit_ScalesAnOversizedRigDownUniformlyInsteadOfCroppingIt()
    {
        var frame = Sts2SceneFitFrame.Fit(-2000f, -1000f, 4096f, 2048f);

        Assert.Equal(0.5f, frame.NodeScale);
        Assert.Equal(2176, frame.ViewportWidth);   // 4096 * 0.5 + 2*64
        Assert.Equal(1152, frame.ViewportHeight);  // 2048 * 0.5 + 2*64
        Assert.Equal(1064f, frame.NodePositionX);  // 64 - (-2000 * 0.5)
        Assert.Equal(564f, frame.NodePositionY);

        var placement = Sts2SceneFitFrame.Place(frame);
        Assert.Equal(-2128d, placement.LocalX, 6);
        Assert.Equal(-1128d, placement.LocalY, 6);
        Assert.Equal(4352d, placement.LocalWidth, 6);
        Assert.Equal(2304d, placement.LocalHeight, 6);
        Assert.Equal(0.5d, placement.FitScale, 6);
    }

    // The whole point of emitting placement: the CLIENT's derivation (scale = localWidth/canvasWidth, then
    // inverse = 1/scale, offset = -local*inverse) must return exactly the fit the bake used. If this ever fails,
    // a geoclip is drawn at the wrong scale or the wrong place and no test of the bake itself would notice.
    [Theory]
    [InlineData(-100f, -200f, 300f, 400f)]
    [InlineData(-2000f, -1000f, 4096f, 2048f)]
    [InlineData(0f, 0f, 1f, 1f)]
    [InlineData(350.5f, -18.25f, 1023.75f, 2047.5f)]
    public void Place_InvertsExactlyToTheFitTheClientWillDerive(float x, float y, float width, float height)
    {
        var frame = Sts2SceneFitFrame.Fit(x, y, width, height);
        var placement = Sts2SceneFitFrame.Place(frame);

        // deriveGeoclipFit, transcribed.
        var scale = placement.LocalWidth / placement.CanvasWidth;
        var inverse = 1d / scale;
        var offsetX = -placement.LocalX * inverse;
        var offsetY = -placement.LocalY * inverse;

        Assert.Equal(frame.NodeScale, inverse, 5);
        Assert.Equal(frame.NodePositionX, offsetX, 4);
        Assert.Equal(frame.NodePositionY, offsetY, 4);
        Assert.Equal(frame.NodeScale, placement.FitScale, 5);

        // …and therefore canvasX = skelX * fitScale + nodePositionX maps the bounds onto the padded canvas: the
        // left edge lands exactly on the margin, and the right edge within one pixel of the far margin (the
        // canvas width is CEILINGed, so the rig can sit up to a pixel short of it — never past it).
        Assert.Equal(Sts2SceneFitFrame.Padding, (x * inverse) + offsetX, 3);
        var right = ((x + width) * inverse) + offsetX;
        var farMargin = frame.ViewportWidth - Sts2SceneFitFrame.Padding;
        Assert.InRange(right, farMargin - 1d, farMargin + 1e-3);
    }

    // The degenerate answer: the default square with the node parked at the padding origin and no fit scale.
    [Fact]
    public void Fallback_IsTheDefaultSquareAtThePaddingOrigin()
    {
        var placement = Sts2SceneFitFrame.Place(Sts2SceneFitFrame.Fallback());

        Assert.Equal(Sts2SceneFitFrame.DefaultSceneSize, placement.CanvasWidth);
        Assert.Equal(Sts2SceneFitFrame.DefaultSceneSize, placement.CanvasHeight);
        Assert.Equal(-Sts2SceneFitFrame.Padding, placement.LocalX, 6);
        Assert.Equal(-Sts2SceneFitFrame.Padding, placement.LocalY, 6);
        Assert.Equal(1d, placement.FitScale, 6);
    }

    // A zero or negative node scale would put the clip at infinity (1/0). Treated as 1 for the same reason the
    // baked path does: visibly wrong beats invisible.
    [Theory]
    [InlineData(0f)]
    [InlineData(-2f)]
    public void Place_TreatsANonPositiveFitScaleAsOne(float nodeScale)
    {
        var placement = Sts2SceneFitFrame.Place(400, 300, 40f, 30f, nodeScale);

        Assert.Equal(1d, placement.FitScale, 6);
        Assert.Equal(-40d, placement.LocalX, 6);
        Assert.Equal(400d, placement.LocalWidth, 6);
    }

    // ── The on-demand request lane ───────────────────────────────────────────────────────────────────

    [Fact]
    public void PlanConfig_TurnsARequestIntoASinglePoseOnlyTarget()
    {
        var config = Sts2SpineGeoClipRequestLane.PlanConfig(
            new SpineGeoClipBakeRequestSnapshot(
                "res://scenes/creature_visuals/byrdonis.tscn",
                "Visuals",
                "idle_loop",
                SampleTimeSeconds: null,
                OutputDirectory: "/tmp/geoclips"),
            "Spirectl.Sts2",
            out var rejected);

        Assert.Null(rejected);
        Assert.NotNull(config);
        Assert.Equal("Spirectl.Sts2", config!.Owner);
        Assert.Equal("/tmp/geoclips", config.OutDir);
        Assert.True(config.PoseOnly);

        var target = Assert.Single(config.Targets);
        Assert.Equal("res://scenes/creature_visuals/byrdonis.tscn", target.ScenePath);
        Assert.Equal("Visuals", target.NodePath);
        Assert.Equal("idle_loop", target.Animation);
        Assert.True(config.IsPoseOnly(target));
        Assert.Null(config.RequestedSampleTime(target));

        // `Raw` is the env-lane spec this request would have been written as, so both lanes label their log lines
        // in one vocabulary and a request can be replayed from a log by hand.
        Assert.Equal(
            "res://scenes/creature_visuals/byrdonis.tscn?anim=idle_loop&node=Visuals&pose=1", target.Raw);
        Assert.Equal(Sts2SpineGeoClipSpec.ParseTargets(target.Raw)[0].PoseOnly, target.PoseOnly);
    }

    [Fact]
    public void PlanConfig_CarriesAnExplicitSampleTimeThrough()
    {
        var config = Sts2SpineGeoClipRequestLane.PlanConfig(
            new SpineGeoClipBakeRequestSnapshot("res://a.tscn", null, "die", 1.5d, "/tmp/g"),
            "Spirectl.Sts2",
            out _);

        var target = Assert.Single(config!.Targets);
        Assert.Equal(1.5d, config.RequestedSampleTime(target)!.Value, 6);
        Assert.Equal("res://a.tscn?anim=die&pose=1&t=1.5", target.Raw);
    }

    // A whole-clip request is possible, just not the default: `Fps`/`MaxFrames` only drive the frame clock there.
    [Fact]
    public void PlanConfig_HonoursAWholeClipRequest()
    {
        var config = Sts2SpineGeoClipRequestLane.PlanConfig(
            new SpineGeoClipBakeRequestSnapshot(
                "res://a.tscn", null, "idle_loop", null, "/tmp/g", Fps: 15, MaxFrames: 120, PoseOnly: false),
            "Spirectl.Sts2",
            out _);

        Assert.False(config!.PoseOnly);
        Assert.Equal(15, config.Fps);
        Assert.Equal(120, config.MaxFrames);
        Assert.False(config.IsPoseOnly(config.Targets[0]));
    }

    [Theory]
    [InlineData("", "idle_loop", "/tmp/g", Sts2SpineGeoClipRequestLane.FieldScene)]
    [InlineData("res://a.tscn", " ", "/tmp/g", Sts2SpineGeoClipRequestLane.FieldAnimation)]
    [InlineData("res://a.tscn", "idle_loop", "", Sts2SpineGeoClipRequestLane.FieldOutputDirectory)]
    public void PlanConfig_NamesTheFieldItRejectedOn(string scene, string anim, string outDir, string expected)
    {
        var config = Sts2SpineGeoClipRequestLane.PlanConfig(
            new SpineGeoClipBakeRequestSnapshot(scene, null, anim, null, outDir),
            "Spirectl.Sts2",
            out var rejected);

        Assert.Null(config);
        Assert.Equal(expected, rejected);
    }

    // A garbled requested time is dropped, exactly as the spec parser drops one: the heuristic is a better answer.
    [Theory]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void PlanConfig_DropsAnUnusableSampleTime(double seconds)
    {
        var config = Sts2SpineGeoClipRequestLane.PlanConfig(
            new SpineGeoClipBakeRequestSnapshot("res://a.tscn", null, "idle_loop", seconds, "/tmp/g"),
            "Spirectl.Sts2",
            out _);

        Assert.Null(config!.RequestedSampleTime(config.Targets[0]));
    }

    [Fact]
    public void ToSnapshot_CarriesTheCountersAndTheChosenPoseOnSuccess()
    {
        var snapshot = Sts2SpineGeoClipRequestLane.ToSnapshot(new GeoClipBakeOutcome(
            Success: true,
            ManifestPath: "/tmp/g/byrdonis--Visuals--idle_loop/manifest.json",
            PageFileNames: ["page-0.png"],
            PartCount: 28,
            FrameCount: 1,
            SampleTimeSeconds: 6.6667d,
            SampleTimeSource: Sts2SpineStillFrame.SampleSourceMid,
            ElapsedMs: 4210.5d,
            Slots: 30,
            SlotsVisible: 28,
            Associated: 28,
            Unassociated: 0,
            ForeignMeshes: 0,
            Complete: true,
            FailureReason: null));

        Assert.True(snapshot.Success);
        Assert.Null(snapshot.Error);
        Assert.Equal("page-0.png", Assert.Single(snapshot.PageFileNames));
        Assert.Equal(1, snapshot.FrameCount);
        Assert.Equal(28, snapshot.PartCount);
        Assert.Equal(Sts2SpineStillFrame.SampleSourceMid, snapshot.SampleTimeSource);
        Assert.True(snapshot.Complete);
    }

    // A failure is STRUCTURED, never a bare string a caller has to pattern-match.
    [Fact]
    public void ToSnapshot_ReportsAFailureAsAStructuredError()
    {
        var snapshot = Sts2SpineGeoClipRequestLane.ToSnapshot(
            GeoClipBakeOutcome.Failed("the skeleton exposed no slots."));

        Assert.False(snapshot.Success);
        Assert.NotNull(snapshot.Error);
        Assert.Equal(AssetExtractFailureCode.RuntimeFailure, snapshot.Error!.Code);
        Assert.Contains("no slots", snapshot.Error.Message, StringComparison.Ordinal);
        Assert.Equal("bake", Assert.Single(snapshot.Error.Details).Field);
    }

    [Fact]
    public void NotSupported_IsTheNotImplementedAnswerEveryNonLiveProviderGives()
    {
        var snapshot = SpineGeoClipBakeResultSnapshot.NotSupported("no live host here.");

        Assert.False(snapshot.Success);
        Assert.Equal(AssetExtractFailureCode.NotImplemented, snapshot.Error!.Code);
        Assert.Equal("spine-geoclip-bake", Assert.Single(snapshot.Error.Details).Value);
    }

    // ── Arm-claim discipline ─────────────────────────────────────────────────────────────────────────

    // THE COEXISTENCE PROPERTY. With the env lane genuinely holding the process-global claim — the state a host
    // is in whenever SPIRECTL_SPINE_GEOCLIP_BAKE was set at launch — a second ENV arm stands down, and the
    // REQUEST lane plans a bake anyway. If the request lane claimed, an armed host would refuse every on-demand
    // bake for the life of the process; if it stood down, it would refuse them silently.
    [Fact]
    public void RequestLane_PlansABakeWhileTheEnvLaneHoldsTheClaim()
    {
        var key = Sts2OneShotArmClaim.ClaimKeyPrefix + Sts2OneShotArmClaim.SpineGeoClipBakeSubsystem;
        var parked = AppDomain.CurrentDomain.GetData(key) as string;
        try
        {
            AppDomain.CurrentDomain.SetData(key, null);

            // The env lane arms, exactly as Install() does.
            Assert.True(Sts2OneShotArmClaim.TryClaim(
                Sts2OneShotArmClaim.SpineGeoClipBakeSubsystem, "Spirectl.Sts2", out var firstOwner));
            Assert.Null(firstOwner);

            // The embedded copy's env lane finds it taken and stands down — the claim doing its job.
            Assert.False(Sts2OneShotArmClaim.TryClaim(
                Sts2OneShotArmClaim.SpineGeoClipBakeSubsystem, "CouchCoop.Spirectl", out var secondOwner));
            Assert.Equal("Spirectl.Sts2", secondOwner);

            // …and in that same state the request lane plans a bake, from EITHER copy.
            foreach (var owner in new[] { "Spirectl.Sts2", "CouchCoop.Spirectl" })
            {
                var config = Sts2SpineGeoClipRequestLane.PlanConfig(
                    new SpineGeoClipBakeRequestSnapshot("res://a.tscn", null, "idle_loop", null, "/tmp/g"),
                    owner,
                    out var rejected);

                Assert.Null(rejected);
                Assert.NotNull(config);
                Assert.Equal(owner, config!.Owner);
            }

            // The claim is UNCHANGED by the request lane: it neither took the slot nor released it.
            Assert.Equal("Spirectl.Sts2", AppDomain.CurrentDomain.GetData(key) as string);
        }
        finally
        {
            AppDomain.CurrentDomain.SetData(key, parked);
        }
    }

    // ── The off-screen extraction marker ─────────────────────────────────────────────────────────────

    // The marker a host's freeze walk skips an extraction subtree by. Prefix-matched because Godot uniquifies
    // duplicate sibling names, so two concurrent extractions do not both get the bare name.
    [Theory]
    [InlineData("Sts2OffscreenExtraction", true)]
    [InlineData("Sts2OffscreenExtraction2", true)]
    [InlineData("@Sts2OffscreenExtraction@3", true)]
    [InlineData("SubViewport", false)]
    [InlineData("Sts2Offscreen", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsExtractionSubtreeRoot_MatchesTheMarkerAndNothingElse(string? name, bool expected)
        => Assert.Equal(expected, Sts2OffscreenExtraction.IsExtractionSubtreeRoot(name));

    // The scope guard, stated as a test: a plain SubViewport is NOT an extraction subtree. An exemption that
    // widened to "every SubViewport" would un-freeze whatever the GAME renders off screen and give back the CPU
    // win a suspender exists for.
    [Fact]
    public void IsExtractionSubtreeRoot_DoesNotMatchAnOrdinarySubViewportName()
    {
        Assert.False(Sts2OffscreenExtraction.IsExtractionSubtreeRoot("SubViewport"));
        Assert.False(Sts2OffscreenExtraction.IsExtractionSubtreeRoot("@SubViewport@17"));
        Assert.False(Sts2OffscreenExtraction.IsExtractionSubtreeRoot("VfxPotionFlashViewport"));
    }

    private static GeoClipConfig Config(bool poseOnly, double? sampleTime)
        => new(
            "Spirectl.Sts2",
            "<spec>",
            "/tmp/g",
            [],
            Fps: 30,
            StartDelaySeconds: 0,
            CandidateCap: 50_000,
            IndexSlack: 64,
            MaxFrames: 900,
            WaitSeconds: 300,
            WindowBCap: null,
            StrictMeshFilter: true,
            poseOnly,
            sampleTime);
}
