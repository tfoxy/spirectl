using Spirectl.Sts2.Core.Artifacts;
using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

/// <summary>
/// THE WHOLE-CLIP GUARD on the dummy rendering backend, and why arming the headless mesh-pool re-mint made one
/// necessary rather than optional.
///
/// <para>The re-mint corrects the mesh pool at the ACQUISITION pose. On a single-pose bake that is the whole
/// artifact, and the result is byte-identical to a real renderer's. On a WHOLE-CLIP bake it is one frame of many
/// — and the damage of arming it there is not "no better", it is WORSE, because acquisition is exactly what the
/// two admission rules grade. Measured on this build, four knight rigs, `--headless`, eight clips, two headless
/// processes agreeing byte-for-byte:</para>
///
/// <code>
///   arm                        clips admitted   per-frame vertex scalars   worst per-frame slot xform
///   real renderer (gamescope)        8 of 8               180 208                       reference
///   headless, re-mint UNARMED        0 of 8                     0        (refuses: complete=false x8)
///   headless, re-mint ARMED          7 of 8                     0                       5 782.99 px
/// </code>
///
/// <para>i.e. arming converts seven SAFE 404s into published artifacts that carry none of the deformation the
/// real renderer records, and whose reference geometry is up to 3 965.83 px out. Through the shipped
/// `/geoclips/` route with a caller asking for 16 frames, all four rigs answered 200, wrote a `.complete`
/// marker, and were served on a second request in 1-2 ms. And couch's geoclip store key does not carry the
/// frame count, so that artifact then answers the ordinary single-pose request too — measured: seeded into a
/// fresh host's store, the shipped `frames=1` request served the 16-frame headless bytes verbatim without
/// baking. Evidence: `.sts2/research/data/geoclip-mfguard-*/` in the CouchCoop checkout.</para>
/// </summary>
public class Sts2SpineGeoClipWholeClipGuardTests
{
    private static GeoClipConfig Plan(bool poseOnly, int? maxFrames, params string[] animations)
    {
        var config = Sts2SpineGeoClipRequestLane.PlanConfig(
            new SpineGeoClipBakeRequestSnapshot(
                "res://scenes/creature_visuals/ironclad.tscn",
                "Visuals",
                animations.Length == 0 ? "idle_loop" : animations[0],
                SampleTimeSeconds: null,
                OutputDirectory: "/tmp/geoclips",
                Fps: 15,
                MaxFrames: maxFrames,
                PoseOnly: poseOnly,
                AnimationNames: animations.Length > 1 ? animations : null),
            "Spirectl.Sts2",
            out var rejected);
        Assert.Null(rejected);
        Assert.NotNull(config);
        return config!;
    }

    // ── The rule itself ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AWholeClipBakeIsRefusedOnTheDummyBackend()
    {
        var reason = Sts2SpineGeoClipHeadlessRemint.WholeClipRefusal(
            dummyRenderer: true, poseOnly: false, frameCap: 16);

        Assert.NotNull(reason);
        // It has to say WHAT was refused and WHAT to do instead: this reaches an operator as a 404 body.
        Assert.Contains("16 frame cap", reason, StringComparison.Ordinal);
        Assert.Contains("maxFrames=1", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSinglePoseBakeIsNotTouchedOnEitherBackend()
    {
        // THE NON-REGRESSION. The shipped route is pose-only and its headless bake is verified byte-identical to
        // a real renderer's; the guard must return before it can have an opinion about it.
        Assert.Null(Sts2SpineGeoClipHeadlessRemint.WholeClipRefusal(true, poseOnly: true, frameCap: 1));
        Assert.Null(Sts2SpineGeoClipHeadlessRemint.WholeClipRefusal(false, poseOnly: true, frameCap: 1));
    }

    [Fact]
    public void AWholeClipBakeIsFineOnAnyBackendThatCanWriteAMeshInPlace()
    {
        // Not a blanket ban on clips: a real display server — a lavapipe/llvmpipe software rasterizer included —
        // bakes whole clips byte-identically to the GPU, max and RMS 0.0 px over 221 frames
        // (geoclip-swrast-hardening-20260909T160000Z, cases 1 and 3).
        Assert.Null(Sts2SpineGeoClipHeadlessRemint.WholeClipRefusal(false, poseOnly: false, frameCap: 48));
    }

    // ── The rule as this lane applies it ─────────────────────────────────────────────────────────────

    [Fact]
    public void TheRequestLaneRefusesAWholeClipRequestOnTheDummyBackend()
    {
        var config = Plan(poseOnly: false, maxFrames: 16);

        var refusal = Sts2SpineGeoClipRequestLane.RefuseWholeClipOnDummyBackend(config, dummyRenderer: true);

        Assert.NotNull(refusal);
        Assert.Equal(Sts2SpineGeoClipRequestLane.WholeClipOnDummyBackendNote, refusal!.Note);
        Assert.All(refusal.Poses, pose => Assert.False(pose.Success));
        Assert.All(refusal.Poses, pose => Assert.False(pose.Complete));
    }

    [Fact]
    public void TheRefusalReachesTheCALLERAsAFailureAndNotAsAnIncompleteBake()
    {
        // THE LOAD-BEARING DISTINCTION. A completeness REFUSAL is remembered — spirectl memoises it and couch
        // writes a durable receipt filed under a store key that does NOT carry the frame count, so one whole-clip
        // ask would answer the single-pose request for ever on that host. A FAILURE is remembered by neither.
        var refusal = Sts2SpineGeoClipRequestLane.RefuseWholeClipOnDummyBackend(
            Plan(poseOnly: false, maxFrames: 16),
            dummyRenderer: true);

        var snapshot = Sts2SpineGeoClipRequestLane.ToSnapshot(refusal!);

        Assert.False(snapshot.Success);
        Assert.False(snapshot.Complete);
        Assert.Null(snapshot.ManifestPath);
        // …and therefore nothing to memoise: RefusalReasonFor is null for a result that did not succeed.
        Assert.Null(Sts2SpineGeoClipRequestLane.RefusalReasonFor(snapshot));
    }

    [Fact]
    public void EveryPoseOfARigRequestIsFailed()
    {
        // couch's rig lane grades each pose on its own (CouchCoopGeoclipProvider.IncompletenessReason(pose)), so
        // one adoptable pose in the list would be adopted while the rest were thrown away.
        var refusal = Sts2SpineGeoClipRequestLane.RefuseWholeClipOnDummyBackend(
            Plan(poseOnly: false, maxFrames: 16, "idle_loop", "hurt", "attack"),
            dummyRenderer: true);

        Assert.NotNull(refusal);
        Assert.Equal(3, refusal!.Poses.Count);
        Assert.All(refusal.Poses, pose => Assert.False(pose.Success));
        Assert.Equal(
            ["idle_loop", "hurt", "attack"],
            refusal.Poses.Select(pose => pose.AnimationName).ToArray());
    }

    [Fact]
    public void TheShippedSinglePoseRequestPassesThroughUntouched()
    {
        // Exactly the command CouchCoopGeoclipProvider builds: MaxFrames = SinglePoseFrames = 1, PoseOnly true.
        Assert.Null(Sts2SpineGeoClipRequestLane.RefuseWholeClipOnDummyBackend(
            Plan(poseOnly: true, maxFrames: 1),
            dummyRenderer: true));
    }

    [Fact]
    public void AWholeClipRequestOnARealRendererPassesThrough()
    {
        Assert.Null(Sts2SpineGeoClipRequestLane.RefuseWholeClipOnDummyBackend(
            Plan(poseOnly: false, maxFrames: 16),
            dummyRenderer: false));
    }

    // ── The wiring ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheOnDemandLaneCallsTheGuardBeforeItBakes()
    {
        // A SOURCE-TEXT test, for the same reason Sts2SpineGeoClipProbeSuppressionTests is one: the call site is
        // inside the baker's main-thread delegate and needs a running game to reach, so nothing else in this
        // suite can tell a wired guard from an unwired one — and an unwired guard is precisely the regression
        // this file exists to stop. Same shape as that file: prove the ORDER, and refuse to pass vacuously.
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), BakerSource));

        var guard = source.IndexOf(
            "Sts2SpineGeoClipRequestLane.RefuseWholeClipOnDummyBackend(", StringComparison.Ordinal);
        var bake = source.IndexOf(
            "return await BakeRigOnMainThreadAsync(config, config.Targets, logStream);", StringComparison.Ordinal);

        Assert.True(guard >= 0, $"{BakerSource} no longer calls RefuseWholeClipOnDummyBackend; a whole-clip bake "
            + "on the dummy backend would be published with every frame at the acquisition pose.");
        Assert.True(bake >= 0, "the on-demand lane's bake call moved; this test can no longer prove the order.");
        Assert.True(guard < bake, "the whole-clip guard must run BEFORE the bake, not after it.");
    }

    private const string BakerSource = "bridge-mod/src/Spirectl.Sts2/Live/Sts2SpineGeoClipBaker.cs";

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "spirectl.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find repository root.");
    }
}
