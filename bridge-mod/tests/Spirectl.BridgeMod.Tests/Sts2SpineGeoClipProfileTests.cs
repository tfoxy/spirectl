using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// The geoclip bake's PHASE PROFILE: the manifest-facing projection of a render-phase snapshot, the doctrine
// that says which side of the blocking/parked split each bake phase belongs on, and the pause verdict a later
// workstream's arm has to pass. Pure — no live host, no Godot — so these run in the ordinary suite.
//
// Shares the profiler's collection (and its Reset discipline) because the ambient recorder is process-wide:
// two of these running beside a Sts2RenderPhaseProfileTests case would stamp into each other's render.
[Collection(nameof(RenderPhaseProfileCollection))]
public sealed class Sts2SpineGeoClipProfileTests : IDisposable
{
    public Sts2SpineGeoClipProfileTests() => Sts2RenderPhaseProfile.Reset();

    public void Dispose() => Sts2RenderPhaseProfile.Reset();

    // THE DOCTRINE, restated independently of the table it grades. The expectations below are literals on
    // purpose: a test that iterated GeoClipPhaseDoctrine's own sets would agree with any flip made to them and
    // would prove nothing. Getting one of these backwards is the instrument's fatal failure — a parked phase
    // counted as blocking reports a freeze the bake never caused, and vice versa.
    [Theory]
    [InlineData("bakeWarmupWait", false)]
    [InlineData("bakePoseWait", false)]
    [InlineData("bakeAcquireWait", false)]
    [InlineData("bakeSweepWait", false)]
    [InlineData("bakeProbeSeekWait", false)]
    [InlineData("bakeProbeFrameWait", false)]
    [InlineData("bakeProbeRestoreWait", false)]
    [InlineData("bakeForceDraw", true)]
    [InlineData("bakeAtlasRead", true)]
    [InlineData("bakeSweepProbe", true)]
    [InlineData("bakeColorRead", true)]
    [InlineData("bakeGeometryRead", true)]
    [InlineData("bakePageCollect", true)]
    [InlineData("bakePageWrite", true)]
    [InlineData("bakeManifest", true)]
    public void EachBakePhaseIsOnTheDeclaredSideOfTheSplit(string phase, bool blocking)
    {
        Assert.Equal(blocking, GeoClipPhaseDoctrine.ExpectedBlocking(phase));

        // …and a profile that recorded it that way folds it onto the matching total, with no violation.
        Sts2RenderPhaseProfile.Begin($"doctrine:{phase}");
        Sts2RenderPhaseProfile.Record(phase, 10, blocking);
        var profile = GeoClipBakeProfile.From(Sts2RenderPhaseProfile.Complete());

        Assert.NotNull(profile);
        Assert.Empty(GeoClipPhaseDoctrine.Violations(profile!));
        Assert.Equal(blocking ? 10 : 0, profile!.BlockingMs, 3);
        Assert.Equal(blocking ? 0 : 10, profile.ParkedMs, 3);
    }

    // The doctrine has to be able to FAIL, or checking it is theatre. A phase stamped on the wrong side is
    // named, and one it has no opinion about (a shared name, or a phase from another lane) is not.
    [Fact]
    public void AMisstampedPhaseIsNamedAndAForeignPhaseIsNot()
    {
        Sts2RenderPhaseProfile.Begin("doctrine-violation");
        Sts2RenderPhaseProfile.Record(Sts2RenderPhaseProfile.Phase.BakeSweepWait, 5, blocking: true);
        Sts2RenderPhaseProfile.Record(Sts2RenderPhaseProfile.Phase.SceneLoad, 5, blocking: true);
        Sts2RenderPhaseProfile.Record(Sts2RenderPhaseProfile.Phase.EncodeSave, 5, blocking: false);
        var profile = GeoClipBakeProfile.From(Sts2RenderPhaseProfile.Complete());

        var violation = Assert.Single(GeoClipPhaseDoctrine.Violations(profile!));
        Assert.Equal("bakeSweepWait=blocking", violation);
        Assert.Null(GeoClipPhaseDoctrine.ExpectedBlocking(Sts2RenderPhaseProfile.Phase.SceneLoad));
    }

    [Fact]
    public void ProfileCarriesTheBakeCountersAndEveryPhaseInBakeOrder()
    {
        Sts2RenderPhaseProfile.Begin("bake-counters");
        Sts2RenderPhaseProfile.Record(Sts2RenderPhaseProfile.Phase.BakeManifest, 3);
        Sts2RenderPhaseProfile.Record(Sts2RenderPhaseProfile.Phase.BakeWarmupWait, 7, blocking: false);
        Sts2RenderPhaseProfile.Count(Sts2RenderPhaseProfile.Counter.FramesWaited, 42);
        Sts2RenderPhaseProfile.Count(Sts2RenderPhaseProfile.Counter.BakeForceDraws, 40);
        var profile = GeoClipBakeProfile.From(Sts2RenderPhaseProfile.Complete());

        Assert.Equal(42, profile!.FramesWaited);
        Assert.Equal(40, profile.ForceDraws);
        // Bake order, not alphabetical: the warmup wait precedes the manifest write in every bake.
        Assert.Equal(
            [Sts2RenderPhaseProfile.Phase.BakeWarmupWait, Sts2RenderPhaseProfile.Phase.BakeManifest],
            profile.Phases.Select(phase => phase.Phase));
    }

    // The residual is the instrument's honesty check: TotalMs is measured end-to-end, so bake time no phase
    // claimed stays VISIBLE instead of being defined away. A mapper that clamped it to zero would make the
    // profile self-confirming — every bake would appear fully attributed.
    [Fact]
    public void UnattributedTimeSurvivesTheProjectionWhenPhasesUndercover()
    {
        Sts2RenderPhaseProfile.Begin("bake-residual");
        Thread.Sleep(30); // bake work nobody stamped
        Sts2RenderPhaseProfile.Record(Sts2RenderPhaseProfile.Phase.BakeAtlasRead, 1);
        var profile = GeoClipBakeProfile.From(Sts2RenderPhaseProfile.Complete());

        Assert.True(
            profile!.UnattributedMs > 0,
            "a bake whose phases cover 1ms of a 30ms run must report the gap, not hide it");
        Assert.True(profile.TotalMs > profile.BlockingMs + profile.ParkedMs);
        Assert.True(profile.BlockingShare < 1d);
    }

    // Zero total is the shape of "the profiler was on but nothing was stamped" (and of an empty snapshot).
    // A naive share would be NaN, which System.Text.Json writes as `null` — indistinguishable downstream from a
    // field that was never emitted.
    [Fact]
    public void SharesAreZeroRatherThanNaNWhenNothingWasMeasured()
    {
        var empty = new GeoClipBakeProfile(0, 0, 0, 0, 0, 0, []);

        Assert.Equal(0d, empty.BlockingShare);
        Assert.Equal(0d, empty.ParkedShare);
        Assert.False(double.IsNaN(empty.BlockingShare));
        Assert.False(double.IsNaN(empty.ParkedShare));
    }

    // Null in, null out. Inventing a zeroed profile would let a reader mistake "the profiler was off" for a
    // bake that cost nothing — the same distinction TryTake keeps.
    [Fact]
    public void AnAbsentSnapshotProjectsToNoProfileRatherThanAZeroedOne()
        => Assert.Null(GeoClipBakeProfile.From(null));

    // The pause verdict's truth table. Both clauses are required, and the interesting cell is
    // worthwhile-but-not-harmless: a long bake that spends most of its life letting the game run is exactly the
    // case where "pausing would save time" must NOT be allowed to authorize the freeze on its own.
    [Theory]
    // parkedMs, blockingMs, totalMs, worthwhile, harmless, allowed
    [InlineData(900, 100, 1000, true, false, false)]   // worthwhile, NOT harmless: a long, mostly-parked bake
    [InlineData(100, 900, 1000, false, true, false)]   // harmless, NOT worthwhile: a freeze bought for nothing
    [InlineData(500, 500, 1000, true, false, false)]   // 50% parked clears the payoff bar; 50% blocking misses safety
    [InlineData(450, 550, 1000, true, false, false)]   // exactly at the payoff bar (>=), still short of safety
    [InlineData(60, 20, 100, true, true, true)]        // both, via the SHORT clause: nobody notices a 100ms freeze
    [InlineData(40, 20, 100, false, true, false)]      // short (harmless) but only 40% parked: not worth it
    public void PauseVerdictRequiresBothPayoffAndHarmlessness(
        double parkedMs,
        double blockingMs,
        double totalMs,
        bool worthwhile,
        bool harmless,
        bool allowed)
    {
        var profile = new GeoClipBakeProfile(totalMs, blockingMs, parkedMs, 0, 0, 0, []);

        Assert.Equal(worthwhile, GeoClipPauseVerdict.ArmingWorthwhile(profile));
        Assert.Equal(harmless, GeoClipPauseVerdict.ArmingHarmless(profile));
        Assert.Equal(allowed, GeoClipPauseVerdict.ArmingAllowed(profile));
    }

    // A CONSEQUENCE of the two thresholds, asserted so a later workstream meets it as a stated rule rather than
    // as a puzzling empty result: 0.45 parked + 0.60 blocking is 1.05 of a total that can only be 1.0, so a bake
    // whose shares are consistent can NEVER satisfy both clauses on shares alone. Only the short-bake escape
    // opens the gate — which is the honest reading of "pausing is worth it AND nobody feels it".
    [Fact]
    public void OnlyTheShortBakeClauseCanEverSatisfyBothConditions()
    {
        Assert.True(
            GeoClipPauseVerdict.MinParkedShareToArm + GeoClipPauseVerdict.SafeBlockingShare > 1d,
            "if these ever sum to <= 1, a long bake can satisfy both clauses on shares alone and this rule "
            + "means something different from what its docstring says");

        // The longest bake that can still pass, at the threshold itself.
        var atTheEdge = new GeoClipBakeProfile(
            GeoClipPauseVerdict.SafeTotalMs,
            0,
            GeoClipPauseVerdict.SafeTotalMs * GeoClipPauseVerdict.MinParkedShareToArm,
            0,
            0,
            0,
            []);
        Assert.True(GeoClipPauseVerdict.ArmingAllowed(atTheEdge));

        // One millisecond longer, with the same shares, and it is refused.
        var justOver = atTheEdge with
        {
            TotalMs = GeoClipPauseVerdict.SafeTotalMs + 1,
            ParkedMs = (GeoClipPauseVerdict.SafeTotalMs + 1) * GeoClipPauseVerdict.MinParkedShareToArm,
        };
        Assert.True(GeoClipPauseVerdict.ArmingWorthwhile(justOver));
        Assert.False(GeoClipPauseVerdict.ArmingAllowed(justOver));
    }

    // An unmeasured bake must never authorize the arm: every share is zero, so the payoff clause fails even
    // though the "short enough to be harmless" clause trivially passes.
    [Fact]
    public void AnUnmeasuredProfileNeverArmsThePause()
    {
        var empty = new GeoClipBakeProfile(0, 0, 0, 0, 0, 0, []);

        Assert.True(GeoClipPauseVerdict.ArmingHarmless(empty));
        Assert.False(GeoClipPauseVerdict.ArmingWorthwhile(empty));
        Assert.False(GeoClipPauseVerdict.ArmingAllowed(empty));
    }
}
