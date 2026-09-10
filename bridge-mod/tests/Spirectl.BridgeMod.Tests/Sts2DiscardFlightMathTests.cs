using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// R13 hand→discard CARD FLY — the pure algebra the producer and the browser client both replay
// (Sts2CardFlightMath's Discard* half). The shared curve/integrator is already pinned by
// Sts2CardFlightMathTests; what is tested here is only what the discard does DIFFERENTLY: the arc branch taken
// against the live viewport, the two extra channels the card face carries, and the replayability gate's extra
// clause. Expectations are stated as the on-screen contract ("the shrink completes a third of the way through the
// arc"), so a change to either side that breaks parity fails here rather than on a device.
public sealed class Sts2DiscardFlightMathTests
{
    // The fly's scalar ranges, as box corners: every assertion below that claims a bound holds "for every flight
    // that can spawn" is checked at all sixteen.
    private static readonly double[] ControlOffsets = [100, 400];
    private static readonly double[] Speeds = [1.1, 1.25];
    private static readonly double[] Accels = [2, 2.5];
    private static readonly double[] Durations = [1, 1.75];

    [Fact]
    public void DiscardArcDir_ArcsUpAboveTheMiddleOfTheScreen_AndDownBelowIt()
    {
        // Above the half-way line: a flat upward arc, the drawn offset ignored entirely.
        Assert.Equal(-500, Sts2CardFlightMath.DiscardArcDir(endY: 100, controlPointOffset: 400, viewportHeight: 1080));
        Assert.Equal(-500, Sts2CardFlightMath.DiscardArcDir(endY: 100, controlPointOffset: 100, viewportHeight: 1080));

        // Below it: downward, and only here does the offset widen the arc.
        Assert.Equal(600, Sts2CardFlightMath.DiscardArcDir(endY: 900, controlPointOffset: 100, viewportHeight: 1080));
        Assert.Equal(900, Sts2CardFlightMath.DiscardArcDir(endY: 900, controlPointOffset: 400, viewportHeight: 1080));
    }

    [Fact]
    public void DiscardArcDir_TakesTheThresholdFromTheLiveViewport_NotADesignSpaceConstant()
    {
        // The same landing point flips branches with the screen it is landing on: 600 is BELOW the middle of a
        // 1080-tall viewport but ABOVE the middle of a 1440-tall one.
        Assert.Equal(600, Sts2CardFlightMath.DiscardArcDir(endY: 600, controlPointOffset: 100, viewportHeight: 1080));
        Assert.Equal(-500, Sts2CardFlightMath.DiscardArcDir(endY: 600, controlPointOffset: 100, viewportHeight: 1440));
    }

    [Fact]
    public void DiscardArcDir_MatchesTheShuffleArcOnTheShufflesOwnViewport()
    {
        // Same shape, different source for the threshold — so on a 1080-tall screen the two agree exactly.
        foreach (var endY in new double[] { 0, 539.9, 540, 540.1, 1080 })
        {
            Assert.Equal(
                Sts2CardFlightMath.ArcDir(endY, 250),
                Sts2CardFlightMath.DiscardArcDir(endY, 250, viewportHeight: 1080));
        }
    }

    [Fact]
    public void DiscardBodyRampWeight_CompletesAtOneThirdOfTheArc()
    {
        const double duration = 1.5;

        Assert.Equal(0, Sts2CardFlightMath.DiscardBodyRampWeight(0, duration));
        Assert.Equal(0.5, Sts2CardFlightMath.DiscardBodyRampWeight(duration / 6, duration), 12);

        // A third of the way through the arc the card is fully dark and fully shrunk; the remaining two thirds
        // change nothing about the face.
        Assert.Equal(1, Sts2CardFlightMath.DiscardBodyRampWeight(duration / 3, duration), 12);
        Assert.Equal(1, Sts2CardFlightMath.DiscardBodyRampWeight(duration, duration));
    }

    [Fact]
    public void DiscardBodyRampWeight_IsClampedAtBothEnds_AndNeverNaN()
    {
        Assert.Equal(0, Sts2CardFlightMath.DiscardBodyRampWeight(-1, 1.5));
        Assert.Equal(1, Sts2CardFlightMath.DiscardBodyRampWeight(10, 1.5));
        // A degenerate duration must not leak a NaN into the replayed scale/colour.
        Assert.Equal(0, Sts2CardFlightMath.DiscardBodyRampWeight(0, 0));
        Assert.Equal(1, Sts2CardFlightMath.DiscardBodyRampWeight(1, 0));
    }

    [Fact]
    public void DiscardArcScale_ShrinksFromFullSizeToATenth()
    {
        Assert.Equal(1.0, Sts2CardFlightMath.DiscardArcScale(0), 12);
        Assert.Equal(0.55, Sts2CardFlightMath.DiscardArcScale(0.5), 12);
        Assert.Equal(0.1, Sts2CardFlightMath.DiscardArcScale(1), 12);
    }

    [Fact]
    public void DiscardPopScale_ReachesZeroAtFourTenths_AndStaysThere()
    {
        Assert.Equal(0.1, Sts2CardFlightMath.DiscardPopScale(0), 12);
        Assert.Equal(0.05, Sts2CardFlightMath.DiscardPopScale(0.2), 12);

        // The kink: the card is GONE at 0.4, exactly.
        Assert.Equal(0, Sts2CardFlightMath.DiscardPopScale(0.4), 12);
        Assert.True(Sts2CardFlightMath.DiscardPopScale(0.399) > 0);

        // …and never comes back, however far past the end the caller integrates.
        Assert.Equal(0, Sts2CardFlightMath.DiscardPopScale(0.41));
        Assert.Equal(0, Sts2CardFlightMath.DiscardPopScale(1));
        Assert.Equal(0, Sts2CardFlightMath.DiscardPopScale(50));
    }

    [Fact]
    public void DiscardPopScale_StartsWhereTheArcShrinkEnded()
    {
        // The two phases have to meet, or the card would jump size the moment it lands on the pile.
        Assert.Equal(Sts2CardFlightMath.DiscardArcScale(1), Sts2CardFlightMath.DiscardPopScale(0), 12);
    }

    [Fact]
    public void IsDiscardReplayable_AcceptsEveryDrawTheGameCanMake()
    {
        foreach (var speed in Speeds)
        {
            foreach (var accel in Accels)
            {
                foreach (var duration in Durations)
                {
                    Assert.True(Sts2CardFlightMath.IsDiscardReplayable(speed, accel, duration, scale0: 1, rot0: 0.3));
                }
            }
        }
    }

    [Fact]
    public void IsDiscardReplayable_RejectsANonFiniteSeedAngle()
    {
        // Everything else is a legal draw, so only the seed can be at fault: a non-finite starting angle would
        // poison every replayed frame, not just the first, so the fly must fall back to streaming.
        Assert.False(Sts2CardFlightMath.IsDiscardReplayable(1.2, 2.25, 1.5, scale0: 1, rot0: double.NaN));
        Assert.False(Sts2CardFlightMath.IsDiscardReplayable(1.2, 2.25, 1.5, scale0: 1, rot0: double.PositiveInfinity));
        Assert.False(Sts2CardFlightMath.IsDiscardReplayable(1.2, 2.25, 1.5, scale0: 1, rot0: double.NegativeInfinity));
    }

    [Fact]
    public void IsDiscardReplayable_StillRejectsEverythingTheSharedGateRejects()
    {
        Assert.False(Sts2CardFlightMath.IsDiscardReplayable(0, 2.25, 1.5, 1, 0));
        Assert.False(Sts2CardFlightMath.IsDiscardReplayable(1.2, double.NaN, 1.5, 1, 0));
        Assert.False(Sts2CardFlightMath.IsDiscardReplayable(1.2, 2.25, 0, 1, 0));
        Assert.False(Sts2CardFlightMath.IsDiscardReplayable(1.2, 2.25, 1.5, 0, 0));
    }

    [Fact]
    public void SuppressWindow_NeverExceedsFourPointThreeSeconds_ForTheGamesDurationCeiling()
    {
        // The longest fly the game can draw lasts 1.75 of pseudo-time; the analytic window for it is 4.3s, well
        // inside the stuck-window backstop, so the cap only ever fires on a garbage field read.
        Assert.Equal(4300, Sts2CardFlightMath.SuppressWindowMs(1.75));
        foreach (var duration in Durations)
        {
            Assert.InRange(Sts2CardFlightMath.SuppressWindowMs(duration), 1, 4300);
        }
    }

    [Theory]
    [InlineData(1.0 / 60)]
    [InlineData(1.0 / 30)]
    [InlineData(1.0 / 240)]
    public void SuppressWindow_OutlastsTheWholeFly_AtEveryCornerOfTheDrawBox(double dt)
    {
        foreach (var offset in ControlOffsets)
        {
            foreach (var speed in Speeds)
            {
                foreach (var accel in Accels)
                {
                    foreach (var duration in Durations)
                    {
                        var (arc, pop) = SimulateWallClock(speed, accel, duration, dt);
                        var windowSeconds = Sts2CardFlightMath.SuppressWindowMs(duration) / 1000.0;

                        // The window has to cover the arc, the landing shrink AND the trail's fade, or the card
                        // un-freezes while the consumer is still replaying it.
                        Assert.True(
                            arc + pop + Sts2CardFlightMath.FadeOutSeconds <= windowSeconds,
                            $"offset={offset} speed={speed} accel={accel} duration={duration} dt={dt}: "
                            + $"fly ran {arc + pop + Sts2CardFlightMath.FadeOutSeconds:F3}s, window {windowSeconds:F3}s");

                        // …and the bound is not vacuous: both phases really are shorter than the pseudo-time
                        // duration, because the integrator always runs at least as fast as 1.1x real time.
                        Assert.True(arc < duration, $"arc {arc:F3}s should be under duration {duration}");
                        Assert.True(pop < duration, $"pop {pop:F3}s should be under duration {duration}");
                    }
                }
            }
        }
    }

    // The fly's two phases in WALL-CLOCK seconds, stepped the way the host does: check the pseudo-time exit
    // condition, then advance one rendered frame. Phase 1 accelerates; phase 2 keeps the speed phase 1 left behind.
    private static (double Arc, double Pop) SimulateWallClock(double speed0, double accel, double duration, double dt)
    {
        var time = 0.0;
        var speed = speed0;
        var arc = 0.0;
        while (time / duration <= 1.0)
        {
            arc += dt;
            (time, speed) = Sts2CardFlightMath.StepAccelerating(time, speed, accel, dt);
        }

        time = 0.0;
        var pop = 0.0;
        while (time / duration <= 1.0)
        {
            pop += dt;
            time = Sts2CardFlightMath.StepConstant(time, speed, dt);
        }

        return (arc, pop);
    }
}
