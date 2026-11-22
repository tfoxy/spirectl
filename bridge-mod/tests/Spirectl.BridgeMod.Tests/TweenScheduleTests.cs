using Spirectl.Sts2.Embedding;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

public sealed class TweenScheduleTests
{
    private static TweenScheduleRecord.Step Property(TweenScheduleRecord r, double duration, double delay = 0)
    {
        var s = r.Append(duration);
        s.Property = "position:x";
        s.ObjectId = 42;
        s.To = [10];
        s.DelayMs = delay;
        return s;
    }

    [Fact]
    public void DefaultIsOneRunRatherThanInfiniteLoops()
    {
        var r = new TweenScheduleRecord(0);
        Property(r, 100);
        Assert.Equal(1, Assert.Single(r.Flatten()).Loops);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(-1)]
    public void ExplicitLoopsPreserveTheGodotCount(int loops)
    {
        var r = new TweenScheduleRecord(0) { Loops = loops };
        Property(r, 100);
        Assert.Equal(loops, Assert.Single(r.Flatten()).Loops);
    }

    [Fact]
    public void SequentialAndDelayAdvanceFromEndOfPreviousGroup()
    {
        var r = new TweenScheduleRecord(123);
        Property(r, 100, 20);
        Property(r, 50, 10);
        var result = Assert.Single(r.Flatten());
        Assert.Equal(123UL, result.StartedUsec);
        Assert.Equal(new double[] { 20, 130 }, result.Steps.Select(s => s.StartMs));
    }

    [Fact]
    public void ParallelUsesGroupStartAndChainUsesLongestDelayedEnd()
    {
        var r = new TweenScheduleRecord(0);
        Property(r, 100);
        r.PendingParallel = true;
        Property(r, 200, 30);
        Property(r, 50);
        Assert.Equal(new double[] { 0, 30, 230 }, Assert.Single(r.Flatten()).Steps.Select(s => s.StartMs));
    }

    [Fact]
    public void PersistentParallelAndOneShotChainMatchAppendSemantics()
    {
        var r = new TweenScheduleRecord(0) { DefaultParallel = true, PendingParallel = true };
        Property(r, 100);
        Property(r, 200);
        r.PendingParallel = false;
        Property(r, 40);
        Property(r, 80);
        Assert.Equal(new double[] { 0, 0, 200, 200 }, Assert.Single(r.Flatten()).Steps.Select(s => s.StartMs));
    }

    [Fact]
    public void IntervalMethodAndCallbackAdvanceTimeWithoutAnnouncements()
    {
        var r = new TweenScheduleRecord(0);
        Property(r, 100);
        r.Append(30);
        r.Append(0).DelayMs = 20;
        r.Append(40).DelayMs = 10;
        Property(r, 50);
        Assert.Equal(new double[] { 0, 200 }, Assert.Single(r.Flatten()).Steps.Select(s => s.StartMs));
    }

    [Fact]
    public void TargetsShareTimelineButHaveUniqueIdsAndPreserveEndpointsAndFlags()
    {
        var r = new TweenScheduleRecord(1234) { Trans = 7, Ease = 1, Loops = 2 };
        var a = Property(r, 100);
        a.From = [3];
        a.Relative = true;
        var b = Property(r, 100);
        b.ObjectId = 43;
        b.Trans = 4;
        b.Ease = 0;
        var result = r.Flatten();
        Assert.Equal(2, result.Count);
        Assert.NotEqual(result[0].TweenId, result[1].TweenId);
        Assert.Equal(42UL, result[0].ObjectId);
        Assert.Equal(2, result[0].Loops);
        Assert.Equal(new double[] { 3 }, result[0].Steps[0].From);
        Assert.True(result[0].Steps[0].Relative);
        Assert.Equal(7, result[0].Steps[0].Trans);
        Assert.Equal(1, result[0].Steps[0].Ease);
        Assert.Null(result[1].Steps[0].From);
        Assert.Equal(4, result[1].Steps[0].Trans);
        Assert.Equal(100, result[1].Steps[0].StartMs);
    }

    [Fact]
    public void DefaultsApplyOnlyToSubsequentlyAppendedTweeners()
    {
        var r = new TweenScheduleRecord(0);
        Property(r, 100);
        r.Trans = 7;
        r.Ease = 1;
        Property(r, 100);
        var steps = Assert.Single(r.Flatten()).Steps;
        Assert.Equal(0, steps[0].Trans);
        Assert.Equal(2, steps[0].Ease);
        Assert.Equal(7, steps[1].Trans);
        Assert.Equal(1, steps[1].Ease);
    }

    [Fact]
    public void FromCurrentClearsExplicitFromAndIgnoresImplicitSamples()
    {
        var r = new TweenScheduleRecord(0);
        var step = Property(r, 100);
        step.From = [3];
        step.From = null;
        Assert.Null(Assert.Single(r.Flatten()).Steps[0].From);
    }

    [Fact]
    public void CancelBeforeFinalizeEmitsNothingAndCancelAfterFinalizeIsIdempotent()
    {
        var hub = new EmbeddableTweenScheduleHub();
        var begins = new List<TweenSchedule>();
        var cancels = new List<long>();
        using var subscription = hub.Subscribe(begins.Add, cancels.Add);
        var early = new TweenScheduleRecord(1);
        Property(early, 100);
        early.Cancel(hub);
        early.Publish(hub);
        Assert.Empty(begins);
        Assert.Empty(cancels);
        var normal = new TweenScheduleRecord(2);
        Property(normal, 100);
        var other = Property(normal, 100);
        other.ObjectId = 43;
        normal.Publish(hub);
        normal.Publish(hub);
        Assert.Equal(2, begins.Count);
        normal.Cancel(hub);
        normal.Cancel(hub);
        Assert.Equal(begins.Select(b => b.TweenId), cancels);
        Assert.Empty(normal.Emitted);
    }

    [Fact]
    public void RawHubDoesNotSubscribeToOrPublishOldHints()
    {
        var raw = new EmbeddableTweenScheduleHub();
        var hints = new EmbeddableAnimationHintHub();
        var begins = 0;
        long cancelled = 0;
        Assert.False(raw.HasSubscribers);
        using (raw.Subscribe(_ => begins++, id => cancelled = id))
        {
            Assert.True(raw.HasSubscribers);
            Assert.False(hints.HasSubscribers);
            var r = new TweenScheduleRecord(0);
            Property(r, 100);
            var schedule = Assert.Single(r.Flatten());
            raw.Publish(schedule);
            raw.Cancel(schedule.TweenId);
            Assert.Equal(1, begins);
            Assert.Equal(schedule.TweenId, cancelled);
        }
        Assert.False(raw.HasSubscribers);
    }
}
