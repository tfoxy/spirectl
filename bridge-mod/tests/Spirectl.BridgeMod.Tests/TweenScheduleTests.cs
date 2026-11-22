#if ENABLE_STS2_LIVE_HOST
extern alias GodotLive;
using GVariant = GodotLive::Godot.Variant;
using GColor = GodotLive::Godot.Color;
using GVector2 = GodotLive::Godot.Vector2;
using GVector3 = GodotLive::Godot.Vector3;
using GVector4 = GodotLive::Godot.Vector4;
using Spirectl.Sts2.Live;
#endif
using System.Runtime.CompilerServices;
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

    [Theory]
    [InlineData(TweenValueKind.Unknown)]
    [InlineData(TweenValueKind.Float)]
    [InlineData(TweenValueKind.Int)]
    [InlineData(TweenValueKind.Vector2)]
    [InlineData(TweenValueKind.Vector3)]
    [InlineData(TweenValueKind.Vector4)]
    [InlineData(TweenValueKind.Color)]
    public void FlattenPreservesEachStepsValueKind(TweenValueKind kind)
    {
        var r = new TweenScheduleRecord(0);
        Property(r, 100).Kind = kind;
        Property(r, 100);
        var steps = Assert.Single(r.Flatten()).Steps;
        Assert.Equal(kind, steps[0].Kind);
        Assert.Equal(TweenValueKind.Unknown, steps[1].Kind);
    }

    [Fact]
    public void PositionalStepConstructionWithoutKindDefaultsToUnknown()
    {
        var step = new TweenScheduleStep("position:x", [10], null, 0, 100, 0, 2, false);
        Assert.Equal(TweenValueKind.Unknown, step.Kind);
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
    public void EndBeforeFinalizeEmitsNothingAndEndAfterFinalizeIsIdempotent()
    {
        var hub = new EmbeddableTweenScheduleHub();
        var begins = new List<TweenSchedule>();
        var cancels = new List<long>();
        using var subscription = hub.Subscribe(begins.Add, cancels.Add);
        var early = new TweenScheduleRecord(1);
        Property(early, 100);
        early.End(hub, TweenEndReason.Killed);
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
        normal.End(hub, TweenEndReason.Stopped);
        normal.End(hub, TweenEndReason.Stopped);
        Assert.Equal(begins.Select(b => b.TweenId), cancels);
        Assert.Empty(normal.Emitted);
    }

    [Theory]
    [InlineData(TweenEndReason.Finished)]
    [InlineData(TweenEndReason.Killed)]
    [InlineData(TweenEndReason.Stopped)]
    [InlineData(TweenEndReason.Paused)]
    [InlineData(TweenEndReason.Retimed)]
    public void EveryReasonIsDeliveredOnceToBothOverloads(TweenEndReason reason)
    {
        var hub = new EmbeddableTweenScheduleHub();
        var begins = new List<TweenSchedule>();
        var ends = new List<TweenEnd>();
        var legacy = new List<long>();
        using var subscription = hub.Subscribe(begins.Add, ends.Add);
        using var adapter = hub.Subscribe(_ => { }, legacy.Add);
        var r = new TweenScheduleRecord(0);
        Property(r, 100);
        Property(r, 100).ObjectId = 43;
        r.Publish(hub);
        r.End(hub, reason);
        r.End(hub, TweenEndReason.Killed);
        r.End(hub, TweenEndReason.Finished);
        Assert.Equal(begins.Select(b => new TweenEnd(b.TweenId, reason)), ends);
        Assert.Equal(begins.Select(b => b.TweenId), legacy);
        Assert.Empty(r.Emitted);
    }

    [Theory]
    [InlineData(TweenEndReason.Killed)]
    [InlineData(TweenEndReason.Stopped)]
    [InlineData(TweenEndReason.Paused)]
    [InlineData(TweenEndReason.Retimed)]
    public void InterruptionBeforePublishPreventsAllLaterAnnouncements(TweenEndReason reason)
    {
        var hub = new EmbeddableTweenScheduleHub();
        var begins = new List<TweenSchedule>();
        var ends = new List<TweenEnd>();
        using var subscription = hub.Subscribe(begins.Add, ends.Add);
        var r = new TweenScheduleRecord(0);
        Property(r, 100);
        r.End(hub, reason);
        r.Publish(hub);
        Property(r, 100);
        r.Publish(hub);
        r.End(hub, TweenEndReason.Finished);
        Assert.Empty(begins);
        Assert.Empty(ends);
    }

    [Fact]
    public void ThrowingObserversCannotInterruptOtherObserversOrEndBookkeeping()
    {
        var hub = new EmbeddableTweenScheduleHub();
        using var throwing = hub.Subscribe(_ => throw new InvalidOperationException(),
            (TweenEnd _) => throw new InvalidOperationException());
        var begins = new List<TweenSchedule>();
        var ends = new List<TweenEnd>();
        using var subscription = hub.Subscribe(begins.Add, ends.Add);
        var r = new TweenScheduleRecord(0);
        Property(r, 100);
        Property(r, 100).ObjectId = 43;
        r.Publish(hub);
        r.End(hub, TweenEndReason.Killed);
        r.End(hub, TweenEndReason.Finished);
        Assert.Equal(2, begins.Count);
        Assert.Equal(begins.Select(b => new TweenEnd(b.TweenId, TweenEndReason.Killed)), ends);
        Assert.Empty(r.Emitted);
    }

    [Fact]
    public void ReentrantEndPreventsFurtherTargetAnnouncements()
    {
        var hub = new EmbeddableTweenScheduleHub();
        var r = new TweenScheduleRecord(0);
        Property(r, 100);
        Property(r, 100).ObjectId = 43;
        var ends = new List<TweenEnd>();
        var begins = 0;
        using var subscription = hub.Subscribe(_ =>
        {
            begins++;
            r.End(hub, TweenEndReason.Stopped);
        }, ends.Add);
        r.Publish(hub);
        Assert.Equal(1, begins);
        Assert.Equal(TweenEndReason.Stopped, Assert.Single(ends).Reason);
    }

    [Fact]
    public void TransitionAwayFromNormalTimeEndsEveryLiveTargetOnceAndReturnReplaysNothing()
    {
        var hub = new EmbeddableTweenScheduleHub();
        var registry = new TweenScheduleRegistry();
        var begins = new List<TweenSchedule>();
        var ends = new List<TweenEnd>();
        using var subscription = hub.Subscribe(begins.Add, ends.Add);
        var first = new TweenScheduleRecord(0);
        Property(first, 100);
        Property(first, 100).ObjectId = 43;
        var second = new TweenScheduleRecord(1);
        Property(second, 200);
        first.Publish(hub, 1, registry);
        second.Publish(hub, 1, registry);
        Assert.True(registry.Contains(first));
        Assert.True(registry.Contains(second));
        registry.TimeScaleChanged(hub, 1);
        Assert.Empty(ends);
        registry.TimeScaleChanged(hub, 0.1);
        Assert.Equal(3, begins.Count);
        Assert.Equal(begins.Select(b => new TweenEnd(b.TweenId, TweenEndReason.Retimed)),
            ends.OrderBy(e => e.TweenId));
        Assert.False(registry.Contains(first));
        Assert.False(registry.Contains(second));
        foreach (var scale in new[] { 0.2, 0.5, 0.9, 1.0, 1.0 }) registry.TimeScaleChanged(hub, scale);
        first.Publish(hub, 1, registry);
        second.Publish(hub, 1, registry);
        first.End(hub, TweenEndReason.Finished);
        second.End(hub, TweenEndReason.Killed);
        Assert.Equal(3, begins.Count);
        Assert.Equal(3, ends.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.1)]
    [InlineData(0.999)]
    [InlineData(2)]
    public void PublishAtScaledTimeIsFinalizedWithoutAnnouncementOrReplay(double scale)
    {
        var hub = new EmbeddableTweenScheduleHub();
        var registry = new TweenScheduleRegistry();
        var begins = new List<TweenSchedule>();
        var ends = new List<TweenEnd>();
        using var subscription = hub.Subscribe(begins.Add, ends.Add);
        var r = new TweenScheduleRecord(0);
        Property(r, 100);
        r.Publish(hub, scale, registry);
        Assert.True(r.Finalized);
        Assert.False(registry.Contains(r));
        registry.TimeScaleChanged(hub, 1);
        r.Publish(hub, 1, registry);
        r.End(hub, TweenEndReason.Finished);
        Assert.Empty(begins);
        Assert.Empty(ends);
    }

    [Theory]
    [InlineData(TweenEndReason.Finished)]
    [InlineData(TweenEndReason.Killed)]
    [InlineData(TweenEndReason.Stopped)]
    [InlineData(TweenEndReason.Paused)]
    [InlineData(TweenEndReason.Retimed)]
    public void EveryEndRemovesTheRecordBeforeNotifyingObservers(TweenEndReason reason)
    {
        var hub = new EmbeddableTweenScheduleHub();
        var registry = new TweenScheduleRegistry();
        var r = new TweenScheduleRecord(0);
        Property(r, 100);
        var registeredAtEnd = true;
        using var subscription = hub.Subscribe(_ => { }, (TweenEnd _) => registeredAtEnd = registry.Contains(r));
        r.Publish(hub, 1, registry);
        Assert.True(registry.Contains(r));
        r.End(hub, reason);
        Assert.False(registeredAtEnd);
        Assert.False(registry.Contains(r));
    }

    [Fact]
    public void PublishResynchronizesTimeScaleAfterUnobservedReturnToNormal()
    {
        var hub = new EmbeddableTweenScheduleHub();
        var registry = new TweenScheduleRegistry();
        registry.TimeScaleChanged(hub, 0.5);
        var begins = new List<TweenSchedule>();
        var ends = new List<TweenEnd>();
        using var subscription = hub.Subscribe(begins.Add, ends.Add);
        var r = new TweenScheduleRecord(0);
        Property(r, 100);
        r.Publish(hub, 1, registry);
        registry.TimeScaleChanged(hub, 0.5);
        Assert.Equal(new TweenEnd(Assert.Single(begins).TweenId, TweenEndReason.Retimed), Assert.Single(ends));
    }

    [Fact]
    public void RegistryDoesNotRetainAbandonedRecords()
    {
        var hub = new EmbeddableTweenScheduleHub();
        var registry = new TweenScheduleRegistry();
        using var subscription = hub.Subscribe(_ => { }, (TweenEnd _) => { });
        var abandoned = PublishAbandonedRecord(hub, registry);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(abandoned.IsAlive);
        registry.TimeScaleChanged(hub, 0.5);
        GC.KeepAlive(registry);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference PublishAbandonedRecord(EmbeddableTweenScheduleHub hub, TweenScheduleRegistry registry)
    {
        var r = new TweenScheduleRecord(0);
        Property(r, 100);
        r.Publish(hub, 1, registry);
        Assert.True(registry.Contains(r));
        return new WeakReference(r);
    }

#if ENABLE_STS2_LIVE_HOST
    [Fact]
    public void ScalePostfixesMatchGodotValueParameterTypes()
    {
        var hooks = typeof(Sts2TweenScheduleHooks);
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        var speed = typeof(GodotLive::Godot.Tween).GetMethod(nameof(GodotLive::Godot.Tween.SetSpeedScale))!;
        var time = typeof(GodotLive::Godot.Engine).GetMethod(nameof(GodotLive::Godot.Engine.SetTimeScale))!;
        Assert.Equal(speed.GetParameters()[0].ParameterType,
            hooks.GetMethod("SpeedScale", flags)!.GetParameters()[1].ParameterType);
        Assert.Equal(time.GetParameters()[0].ParameterType,
            hooks.GetMethod("TimeScale", flags)!.GetParameters()[0].ParameterType);
    }

    [Fact]
    public void VariantValueKindsDistinguishScalarsVectorsAndColors()
    {
        using var integer = GVariant.From(1L);
        using var number = GVariant.From(1.5);
        using var vector2 = GVariant.From(new GVector2(1, 2));
        using var vector3 = GVariant.From(new GVector3(1, 2, 3));
        using var vector4 = GVariant.From(new GVector4(1, 2, 3, 4));
        using var color = GVariant.From(new GColor(1, 0, 0, 1));
        using var unsupported = GVariant.From(true);
        using var nil = default(GVariant);
        Assert.Equal(TweenValueKind.Int, Sts2TweenScheduleHooks.ValueKind(integer));
        Assert.Equal(TweenValueKind.Float, Sts2TweenScheduleHooks.ValueKind(number));
        Assert.Equal(TweenValueKind.Vector2, Sts2TweenScheduleHooks.ValueKind(vector2));
        Assert.Equal(TweenValueKind.Vector3, Sts2TweenScheduleHooks.ValueKind(vector3));
        Assert.Equal(TweenValueKind.Vector4, Sts2TweenScheduleHooks.ValueKind(vector4));
        Assert.Equal(TweenValueKind.Color, Sts2TweenScheduleHooks.ValueKind(color));
        Assert.Equal(TweenValueKind.Unknown, Sts2TweenScheduleHooks.ValueKind(unsupported));
        Assert.Equal(TweenValueKind.Unknown, Sts2TweenScheduleHooks.ValueKind(nil));
    }

    [Fact]
    public void VectorComponentsPreserveAllShaderUniformAxes()
    {
        using var vector3 = GVariant.From(new GVector3(1, -2, 3));
        using var vector4 = GVariant.From(new GVector4(4, -5, 6, 7));
        Assert.Equal(new double[] { 1, -2, 3 }, Sts2TweenScheduleHooks.Components(vector3));
        Assert.Equal(new double[] { 4, -5, 6, 7 }, Sts2TweenScheduleHooks.Components(vector4));
    }
#endif

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
            raw.End(new TweenEnd(schedule.TweenId, TweenEndReason.Killed));
            Assert.Equal(1, begins);
            Assert.Equal(schedule.TweenId, cancelled);
        }
        Assert.False(raw.HasSubscribers);
    }
}
