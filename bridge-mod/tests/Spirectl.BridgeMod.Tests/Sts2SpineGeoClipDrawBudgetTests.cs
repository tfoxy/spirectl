using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// The geoclip bake's DRAW BUDGET: which of its awaited engine frames are closed with a RenderingServer.ForceDraw.
//
// WHY THIS IS GRADED OFFLINE. A forced draw was measured live at 15.2-16.3 ms per call across four rigs
// (geoclip-knights-stage2b phase split), and a pose-only bake issued eight to ten of them — 75-85 % of everything
// the bake blocked the Godot main thread for. The lever removes the ones nothing downstream depends on. The
// baker itself is Godot-typed and `validate.sh bridge-tests` does not compile it, so the DECISION is kept here,
// in the Godot-free core, where the schedule it produces can be asserted without a game.
//
// The one rule the whole lever rests on: a bake resumes from `SceneTree.ProcessFrame` after every node's
// `_process` has run but before the engine closes that iteration with its own draw, and the host's main loop
// keeps drawing between two awaits. So an extra forced draw buys IMMEDIACY, and exactly one thing in the bake
// needs it — a RID bracket, which must not be taken before a mesh the awaited frame minted has been drawn.
public sealed class Sts2SpineGeoClipDrawBudgetTests
{
    // ── The parse ────────────────────────────────────────────────────────────────────────────────────

    // KILL-SWITCH POLARITY, the same as the frame-cap lease's and deliberately not the pause lever's: default ON,
    // and only a word the parser recognises turns it off. A typo therefore costs a slower bake, never a
    // different artifact — which is the opposite trade from the pause, where a typo would freeze the game.
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("1", true)]
    [InlineData("on", true)]
    [InlineData("yes", true)]
    [InlineData("maybe", true)]
    [InlineData("offf", true)]
    [InlineData("0", false)]
    [InlineData("off", false)]
    [InlineData(" OFF ", false)]
    [InlineData("false", false)]
    [InlineData("No", false)]
    public void Elision_DefaultsOnAndOnlyAKnownNegativeTurnsItOff(string? raw, bool expected)
        => Assert.Equal(expected, GeoClipDrawBudget.Parse(raw, null).ElideSettleDraws);

    [Theory]
    [InlineData(null, true)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("off", false)]
    public void ProbeDraws_AreOnByDefaultAndHaveTheirOwnSwitch(string? raw, bool expected)
        => Assert.Equal(expected, GeoClipDrawBudget.Parse(null, raw).DrawProbeFrames);

    [Fact]
    public void EnvNames_AreTheOnesTheRoundRecordsAgainst()
    {
        Assert.Equal("SPIRECTL_SPINE_GEOCLIP_BAKE_DRAW_ELISION", GeoClipDrawBudget.ElisionEnv);
        Assert.Equal("SPIRECTL_SPINE_GEOCLIP_BAKE_PROBE_DRAW", GeoClipDrawBudget.ProbeDrawEnv);
    }

    // The two switches are INDEPENDENT: killing the elision must not also change the probe frames' schedule, or a
    // bisection that turns the lever off would be turning two changes off at once and could not attribute either.
    [Fact]
    public void TheTwoSwitchesAreIndependent()
    {
        var elisionOffProbeOn = GeoClipDrawBudget.Parse("0", "1");
        Assert.False(elisionOffProbeOn.ElideSettleDraws);
        Assert.True(elisionOffProbeOn.DrawProbeFrames);

        var elisionOnProbeOff = GeoClipDrawBudget.Parse("1", "0");
        Assert.True(elisionOnProbeOff.ElideSettleDraws);
        Assert.False(elisionOnProbeOff.DrawProbeFrames);
    }

    // ── The schedule ─────────────────────────────────────────────────────────────────────────────────

    // THE ONE LOAD-BEARING DRAW. `bracketMid` and `bracketPost` are taken immediately after these settles, and a
    // mesh minted by the last awaited frame is only certainly allocated below the bracket once that frame has
    // been drawn. So the last frame draws even with the lever fully armed; the leading ones do not.
    [Fact]
    public void BracketSettle_DrawsOnlyItsLastFrameWhenArmed()
    {
        var armed = GeoClipDrawBudget.Parse(null, null);

        Assert.False(armed.ForceDrawAt(GeoClipDrawSite.BracketSettle, 0, 3));
        Assert.False(armed.ForceDrawAt(GeoClipDrawSite.BracketSettle, 1, 3));
        Assert.True(armed.ForceDrawAt(GeoClipDrawSite.BracketSettle, 2, 3));

        Assert.False(armed.ForceDrawAt(GeoClipDrawSite.BracketSettle, 0, 2));
        Assert.True(armed.ForceDrawAt(GeoClipDrawSite.BracketSettle, 1, 2));

        // A one-frame settle is ALL last frame: the bracket still gets its draw.
        Assert.True(armed.ForceDrawAt(GeoClipDrawSite.BracketSettle, 0, 1));
    }

    // A pose read — PASS 1's skeleton state, PASS 3's mesh geometry — needs the awaited frame and nothing else.
    // The offline geometry probe ran that exact seek-await-read loop both ways and its `no-force-draw` and
    // `with-force-draw` arms reported identical per-mesh tracking on both recorded rigs, in every round since
    // Phase 0.
    [Fact]
    public void PoseRead_NeverDrawsWhenArmed()
    {
        var armed = GeoClipDrawBudget.Parse(null, null);
        Assert.False(armed.ForceDrawAt(GeoClipDrawSite.PoseRead, 0, 1));
        Assert.False(armed.ForceDrawAt(GeoClipDrawSite.PoseRead, 0, 4));
        Assert.False(armed.ForceDrawAt(GeoClipDrawSite.PoseRead, 3, 4));
    }

    // The association probe is NOT part of the elision. Its readback is not evidence about the bake's cost but
    // about which mesh belongs to which slot, and a stale read there does not make a bake slow — it drops or
    // mis-assigns a claim. Left drawn by default so the lever cannot change what a bake CLAIMS, only how long it
    // takes to claim it.
    [Fact]
    public void ProbeFrames_KeepTheirDrawEvenWithTheElisionFullyArmed()
    {
        var armed = GeoClipDrawBudget.Parse("1", null);
        Assert.True(armed.ForceDrawAt(GeoClipDrawSite.ProbeFrame, 0, 1));
        Assert.True(armed.ForceDrawAt(GeoClipDrawSite.ProbeFrame, 0, 2));

        var probesElided = GeoClipDrawBudget.Parse("1", "0");
        Assert.False(probesElided.ForceDrawAt(GeoClipDrawSite.ProbeFrame, 0, 1));

        // …and the probe switch does not reach the other two sites.
        Assert.False(probesElided.ForceDrawAt(GeoClipDrawSite.BracketSettle, 0, 3));
        Assert.True(probesElided.ForceDrawAt(GeoClipDrawSite.BracketSettle, 2, 3));
    }

    // THE KILL SWITCH RESTORES THE PRE-LEVER SCHEDULE EXACTLY. Every awaited frame at every site draws again, so
    // a bisection that sets the env var to 0 is comparing against the schedule every recorded shape was produced
    // under — not against a third, half-elided one.
    [Fact]
    public void Disarmed_EveryFrameAtEverySiteDrawsAgain()
    {
        foreach (var disarmed in new[] { GeoClipDrawBudget.Parse("0", null), GeoClipDrawBudget.EveryFrame })
        {
            Assert.False(disarmed.ElideSettleDraws);
            for (var frame = 0; frame < 3; frame += 1)
            {
                Assert.True(disarmed.ForceDrawAt(GeoClipDrawSite.BracketSettle, frame, 3));
                Assert.True(disarmed.ForceDrawAt(GeoClipDrawSite.PoseRead, frame, 3));
                Assert.True(disarmed.ForceDrawAt(GeoClipDrawSite.ProbeFrame, frame, 3));
            }
        }
    }

    // ── The schedule this round is predicted against ─────────────────────────────────────────────────

    // The bake's frame plan for a POSE-ONLY, single-animation target — the only shape the on-demand geoclip route
    // ever asks for — written out as the sites it visits in order, so the draw COUNT the round's prediction rests
    // on is asserted rather than recounted by hand from the baker. Measured baseline: ten forced draws on
    // ironclad (which pays two colour-probe frames at idle_loop) and eight on the three knights that pay none.
    private static (GeoClipDrawSite Site, int Frames)[] PoseOnlyPlan(int probeFrames) =>
    [
        (GeoClipDrawSite.BracketSettle, 3),   // warmup, ending at `bracketMid`
        (GeoClipDrawSite.PoseRead, 1),        // PASS 1 rest bounds at t=0
        (GeoClipDrawSite.PoseRead, 1),        // PASS 1 at the sampled pose
        (GeoClipDrawSite.BracketSettle, 2),   // acquisition re-seek, ending at `bracketPost`
        (GeoClipDrawSite.ProbeFrame, probeFrames),
        (GeoClipDrawSite.PoseRead, 1),        // PASS 3 geometry at the sampled pose
    ];

    private static int ForcedDraws(GeoClipDrawBudget budget, int probeFrames)
    {
        var draws = 0;
        foreach (var (site, frames) in PoseOnlyPlan(probeFrames))
        {
            for (var frame = 0; frame < frames; frame += 1)
            {
                if (budget.ForceDrawAt(site, frame, frames))
                {
                    draws += 1;
                }
            }
        }

        return draws;
    }

    [Fact]
    public void PoseOnlyBake_DrawsTenTimesDisarmedAndFourTimesArmed_OnARigThatProbes()
    {
        Assert.Equal(10, ForcedDraws(GeoClipDrawBudget.EveryFrame, probeFrames: 2));
        Assert.Equal(4, ForcedDraws(GeoClipDrawBudget.Parse(null, null), probeFrames: 2));
        Assert.Equal(2, ForcedDraws(GeoClipDrawBudget.Parse("1", "0"), probeFrames: 2));
    }

    [Fact]
    public void PoseOnlyBake_DrawsEightTimesDisarmedAndTwiceArmed_OnARigThatDoesNot()
    {
        Assert.Equal(8, ForcedDraws(GeoClipDrawBudget.EveryFrame, probeFrames: 0));
        Assert.Equal(2, ForcedDraws(GeoClipDrawBudget.Parse(null, null), probeFrames: 0));
    }

    // A WHOLE-CLIP bake is where the same rule pays most: PASS 1 and PASS 3 each visit every sampled frame, so
    // the elision removes two draws per frame of the clip rather than a fixed six. Asserted so a later reader
    // does not have to infer that the win scales with the frame count.
    [Fact]
    public void WholeClipBake_ElidesTwoDrawsPerSampledFrame()
    {
        var armed = GeoClipDrawBudget.Parse(null, null);
        const int Frames = 30;

        var disarmedDraws = 0;
        var armedDraws = 0;
        for (var frame = 0; frame < Frames; frame += 1)
        {
            // PASS 1 and PASS 3 each await one frame per sample.
            disarmedDraws += 2;
            armedDraws += (armed.ForceDrawAt(GeoClipDrawSite.PoseRead, 0, 1) ? 1 : 0) * 2;
        }

        Assert.Equal(60, disarmedDraws);
        Assert.Equal(0, armedDraws);
    }

    // ── The schedule reaches the artifact ────────────────────────────────────────────────────────────

    // ELIDED DRAWS ARE COUNTED, NOT SILENTLY DROPPED. `forceDraws=4` on its own cannot distinguish a bake whose
    // budget removed six from a rig that only ever needed four, and the two have very different blocking
    // numbers — so the projection that lands in the bake report carries both.
    [Fact]
    public void BakeProfile_CarriesBothTheDrawsIssuedAndTheDrawsElided()
    {
        var snapshot = new Sts2RenderPhaseProfile.Snapshot(
            "req",
            TotalMs: 300,
            BlockingMs: 120,
            ParkedMs: 150,
            Phases: [],
            Counters: new Dictionary<string, long>(StringComparer.Ordinal)
            {
                [Sts2RenderPhaseProfile.Counter.FramesWaited] = 9,
                [Sts2RenderPhaseProfile.Counter.BakeForceDraws] = 4,
                [Sts2RenderPhaseProfile.Counter.BakeDrawsElided] = 6,
            });

        var profile = GeoClipBakeProfile.From(snapshot);
        Assert.NotNull(profile);
        Assert.Equal(9, profile.FramesWaited);
        Assert.Equal(4, profile.ForceDraws);
        Assert.Equal(6, profile.DrawsElided);
    }

    // A bake recorded before the lever existed — or one whose lever is off — reports zero elided, which is the
    // honest pre-lever value rather than a missing field.
    [Fact]
    public void BakeProfile_ReportsZeroElidedWhenNothingWasElided()
    {
        var profile = GeoClipBakeProfile.From(new Sts2RenderPhaseProfile.Snapshot(
            "req",
            TotalMs: 300,
            BlockingMs: 200,
            ParkedMs: 90,
            Phases: [],
            Counters: new Dictionary<string, long>(StringComparer.Ordinal)
            {
                [Sts2RenderPhaseProfile.Counter.BakeForceDraws] = 10,
            }));

        Assert.NotNull(profile);
        Assert.Equal(10, profile.ForceDraws);
        Assert.Equal(0, profile.DrawsElided);
    }

    // The lever has to be legible in the bake log, because the phase profile beside it is only interpretable
    // against the schedule that produced it.
    [Fact]
    public void Note_NamesBothSwitches()
    {
        Assert.Equal("elideSettleDraws=1 probeFrameDraws=1", GeoClipDrawBudget.Parse(null, null).Note);
        Assert.Equal("elideSettleDraws=0 probeFrameDraws=1", GeoClipDrawBudget.EveryFrame.Note);
        Assert.Equal("elideSettleDraws=1 probeFrameDraws=0", GeoClipDrawBudget.Parse("1", "0").Note);
    }
}
