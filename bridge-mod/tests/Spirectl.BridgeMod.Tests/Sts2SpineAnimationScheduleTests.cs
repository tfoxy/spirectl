using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

public sealed class Sts2SpineAnimationScheduleTests
{
    [Fact]
    public void PublicScheduleResolutionIncludesQueuedReturnAndOneShotRest()
    {
        var attack = new Sts2SpineAnimationSchedules.Schedule(42, "attack", false, 500, "idle", true, 1000);
        Assert.Equal(("attack", 0.0, false, false), Sts2SpineAnimationSchedules.ResolveAt(attack, 999));
        Assert.Equal(("attack", 0.499, false, false), Sts2SpineAnimationSchedules.ResolveAt(attack, 1499));
        Assert.Equal(("idle", 0.0, true, false), Sts2SpineAnimationSchedules.ResolveAt(attack, 1500));
        Assert.Equal(("idle", 2.0, true, false), Sts2SpineAnimationSchedules.ResolveAt(attack, 3500));
        Assert.Equal(("attack", 0.5, false, true), Sts2SpineAnimationSchedules.ResolveAt(attack with { NextAnim = null }, 3500));
        Assert.Equal(("attack", 0.0, false, false), Sts2SpineAnimationSchedules.ResolveAt(
            attack with { NextAnim = null, CurrentDurMsec = 0 }, 1000));
    }

    [Theory]
    [InlineData("idle", 2f, null, 1f)]
    [InlineData("attack", 2f, null, 1f)]
    [InlineData("defeat", 2f, null, 2f)]
    [InlineData("animation", 2f, 0f, 0f)]
    [InlineData("die", 2f, 0.375f, 0.375f)]
    public void PublicSampleUsesTheStillFrameChooser(string animation, float duration, float? paused, float expected)
        => Assert.Equal(expected, Sts2SpineAnimationSchedules.ChooseSampleTime(animation, duration, paused));

    [Fact]
    public void PlaybackScaleObserversCanUnsubscribeAndCannotInterruptEachOther()
    {
        var seen = new List<(ulong, float)>();
        void Broken(ulong _, float __) => throw new InvalidOperationException();
        void Observe(ulong id, float scale) => seen.Add((id, scale));
        Sts2SpineAnimationSchedules.PlaybackScaleChanged += Broken;
        Sts2SpineAnimationSchedules.PlaybackScaleChanged += Observe;
        try
        {
            Sts2SpineAnimationSchedules.NotifyPlaybackScaleChanged(42, 0);
            Sts2SpineAnimationSchedules.NotifyPlaybackScaleChanged(42, 0);
            Sts2SpineAnimationSchedules.NotifyPlaybackScaleChanged(42, 0.5f);
            Assert.Equal(new[] { (42UL, 0f), (42UL, 0f), (42UL, 0.5f) }, seen);
        }
        finally
        {
            Sts2SpineAnimationSchedules.PlaybackScaleChanged -= Broken;
            Sts2SpineAnimationSchedules.PlaybackScaleChanged -= Observe;
        }
        Sts2SpineAnimationSchedules.NotifyPlaybackScaleChanged(42, 1);
        Assert.Equal(3, seen.Count);
    }

    [Fact]
    public void ObserversCannotInterruptEachOtherAndCanUnsubscribe()
    {
        var schedule = new Sts2SpineAnimationSchedules.Schedule(42, "attack", false, 500, "idle", true, 1234);
        var seen = new List<Sts2SpineAnimationSchedules.Schedule>();
        void Broken(Sts2SpineAnimationSchedules.Schedule _) => throw new InvalidOperationException("observer failure");
        void Observe(Sts2SpineAnimationSchedules.Schedule value) => seen.Add(value);
        Sts2SpineAnimationSchedules.Recorded += Broken;
        Sts2SpineAnimationSchedules.Recorded += Observe;
        try
        {
            Sts2SpineAnimationSchedules.NotifyRecorded(schedule);
            Assert.Equal(schedule, Assert.Single(seen));
        }
        finally
        {
            Sts2SpineAnimationSchedules.Recorded -= Broken;
            Sts2SpineAnimationSchedules.Recorded -= Observe;
        }
        Sts2SpineAnimationSchedules.NotifyRecorded(schedule);
        Assert.Single(seen);
    }

#if ENABLE_STS2_LIVE_HOST
    [Fact]
    public void EachScheduleWriteFiresOnceWithItsRecordedValues()
    {
        const ulong id = ulong.MaxValue - 42;
        var seen = new List<Sts2SpineAnimationSchedules.Schedule>();
        void Observe(Sts2SpineAnimationSchedules.Schedule value)
        {
            if (value.InstanceId == id) seen.Add(value);
        }
        Sts2SpineAnimationSchedules.Recorded += Observe;
        try
        {
            Sts2SpineInspector.RecordScheduledAnimation(id, "attack", false, 500, "idle", true, 1234);
            Assert.Equal(new(id, "attack", false, 500, "idle", true, 1234), Assert.Single(seen));
            Assert.Equal(seen[0], Sts2SpineAnimationSchedules.ReadRecorded(id));

            Sts2SpineInspector.RecordDirectAnimation(id, "idle", true, 900, 2000);
            Assert.Equal(2, seen.Count);
            Assert.Equal(new(id, "idle", true, 900, null, false, 2000), seen[^1]);
            Sts2SpineInspector.RecordDirectAnimation(id, "idle", true, 900, 2100);
            Assert.Equal(2, seen.Count); // Reasserting a loop does not record a new schedule.

            Sts2SpineInspector.RecordDirectAnimation(id, "attack", false, 500, 3000);
            Assert.Equal(3, seen.Count);
            Sts2SpineInspector.RecordQueuedAnimation(id, "idle", true);
            Assert.Equal(4, seen.Count);
            Assert.Equal(new(id, "attack", false, 500, "idle", true, 3000), seen[^1]);
            Sts2SpineInspector.RecordQueuedAnimation(id, "another_idle", true);
            Sts2SpineInspector.RecordScheduledAnimation(id, " ", false, 0, null, false, 4000);
            Sts2SpineInspector.RecordDirectAnimation(id, "", false, 0, 4000);
            Assert.Equal(4, seen.Count); // Ignored requests do not notify.
        }
        finally
        {
            Sts2SpineAnimationSchedules.Recorded -= Observe;
            Sts2SpineInspector.Forget(id, null);
        }
        Assert.Null(Sts2SpineAnimationSchedules.ReadRecorded(id));
    }
#endif
}
