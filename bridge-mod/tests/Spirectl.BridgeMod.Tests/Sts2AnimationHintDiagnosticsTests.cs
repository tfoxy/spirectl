using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// R13 hint-emission observability. The hint hooks decline SILENTLY by design, so these counters are the only
// evidence a host has for "which gate ate the hint". Two properties matter and both are asserted here: an increment
// is never lost under concurrency (the hooks run on the game main thread but an embedder reads from its own
// background thread), and the families are independent so a stalled producer can be diagnosed against a live one.
//
// The counters are PROCESS-GLOBAL and monotonic with no reset — that is deliberate (a reset would let a reader miss
// activity between samples), so every assertion here is a DELTA from a baseline captured in the test, never an
// absolute. Anything else would break the moment another test in this assembly touched the same family.
public sealed class Sts2AnimationHintDiagnosticsTests
{
    private const int Workers = 8;
    private const int PerWorker = 500;

    [Fact]
    public void EmittedIncrementsSurviveConcurrentRecording()
    {
        var counters = new Sts2AnimationHintFamilyCounters();

        Parallel.For(0, Workers, _ =>
        {
            for (var i = 0; i < PerWorker; i++)
            {
                counters.RecordEmitted();
            }
        });

        Assert.Equal(Workers * PerWorker, counters.Emitted);
    }

    [Fact]
    public void EveryBucketCountsIndependentlyUnderConcurrentRecording()
    {
        var counters = new Sts2AnimationHintFamilyCounters();

        Parallel.For(0, Workers, _ =>
        {
            for (var i = 0; i < PerWorker; i++)
            {
                counters.RecordEmitted();
                counters.RecordEarlyOutNoSubscribers();
                counters.RecordEarlyOutLeverOff();
                counters.RecordEarlyOutNoResolver();
                counters.RecordResolveNull();
                counters.RecordPublishFailed();
                counters.RecordResolveParked();
                counters.RecordResolveRetryHit();
                counters.RecordResolveRetryDropped();
                counters.RecordResolveNotReplayable();
                counters.RecordResolveSingular();
            }
        });

        var expected = Workers * PerWorker;
        Assert.Equal(expected, counters.Emitted);
        Assert.Equal(expected, counters.EarlyOutNoSubscribers);
        Assert.Equal(expected, counters.EarlyOutLeverOff);
        Assert.Equal(expected, counters.EarlyOutNoResolver);
        Assert.Equal(expected, counters.ResolveNull);
        Assert.Equal(expected, counters.PublishFailed);
        Assert.Equal(expected, counters.ResolveParked);
        Assert.Equal(expected, counters.ResolveRetryHit);
        Assert.Equal(expected, counters.ResolveRetryDropped);
        Assert.Equal(expected, counters.ResolveNotReplayable);
        Assert.Equal(expected, counters.ResolveSingular);
    }

    [Fact]
    public void FreshCountersStartAtZeroAndOnlyTheRecordedBucketMoves()
    {
        var counters = new Sts2AnimationHintFamilyCounters();

        Assert.Equal(0, counters.Emitted);
        Assert.Equal(0, counters.EarlyOutNoSubscribers);
        Assert.Equal(0, counters.EarlyOutLeverOff);
        Assert.Equal(0, counters.EarlyOutNoResolver);
        Assert.Equal(0, counters.ResolveNull);
        Assert.Equal(0, counters.PublishFailed);
        Assert.Equal(0, counters.ResolveParked);
        Assert.Equal(0, counters.ResolveRetryHit);
        Assert.Equal(0, counters.ResolveRetryDropped);
        Assert.Equal(0, counters.ResolveNotReplayable);
        Assert.Equal(0, counters.ResolveSingular);

        counters.RecordEarlyOutLeverOff();

        // The whole point of one bucket per gate: a lever-off decline must not look like a missing subscriber.
        Assert.Equal(1, counters.EarlyOutLeverOff);
        Assert.Equal(0, counters.Emitted);
        Assert.Equal(0, counters.EarlyOutNoSubscribers);
        Assert.Equal(0, counters.EarlyOutNoResolver);
        Assert.Equal(0, counters.ResolveNull);
        Assert.Equal(0, counters.PublishFailed);
        Assert.Equal(0, counters.ResolveParked);
        Assert.Equal(0, counters.ResolveRetryHit);
        Assert.Equal(0, counters.ResolveRetryDropped);
        Assert.Equal(0, counters.ResolveNotReplayable);
        Assert.Equal(0, counters.ResolveSingular);
    }

    [Fact]
    public void TheResolveNullAttributionBucketsMoveIndependentlyOfTheLegacyBucket()
    {
        // R14. The five attribution buckets EXPLAIN a resolve miss; they do not replace it. The hooks still record
        // the legacy ResolveNull on every one of these paths, so a reader comparing an old host against a new one
        // sees the same ResolveNull count either way — which is why these have to be separate counters and why
        // recording one must never touch it.
        var counters = new Sts2AnimationHintFamilyCounters();

        counters.RecordResolveParked();
        counters.RecordResolveNotReplayable();
        counters.RecordResolveSingular();

        Assert.Equal(1, counters.ResolveParked);
        Assert.Equal(1, counters.ResolveNotReplayable);
        Assert.Equal(1, counters.ResolveSingular);
        Assert.Equal(0, counters.ResolveNull);
        Assert.Equal(0, counters.ResolveRetryHit);
        Assert.Equal(0, counters.ResolveRetryDropped);
    }

    [Fact]
    public void AParkedResolveIsAccountedForByExactlyOneOutcomeBucket()
    {
        // The retry contract an operator reads these with: every parked resolve ends as either a hit or a drop, so
        // `ResolveParked - (RetryHit + RetryDropped)` is the number still waiting for a reconcile — and a park that
        // is silently losing entries would show up as that difference never returning to zero.
        var counters = new Sts2AnimationHintFamilyCounters();

        for (var i = 0; i < 5; i++)
        {
            counters.RecordResolveParked();
        }

        counters.RecordResolveRetryHit();
        counters.RecordResolveRetryHit();
        counters.RecordResolveRetryHit();
        counters.RecordResolveRetryDropped();
        counters.RecordResolveRetryDropped();

        Assert.Equal(5, counters.ResolveParked);
        Assert.Equal(5, counters.ResolveRetryHit + counters.ResolveRetryDropped);
    }

    [Fact]
    public void RecordEmittedReturnsThePostIncrementCount()
    {
        // The hooks log a milestone at the 1st / 25th / 500th / 10000th hint off this return value alone, so it has
        // to be the count INCLUDING the call that produced it, and it has to advance by exactly one per call.
        var counters = new Sts2AnimationHintFamilyCounters();

        Assert.Equal(1, counters.RecordEmitted());
        Assert.Equal(2, counters.RecordEmitted());
        Assert.Equal(3, counters.RecordEmitted());
        Assert.Equal(3, counters.Emitted);
    }

    [Fact]
    public void RecordEmittedReturnsEveryCountExactlyOnceUnderConcurrency()
    {
        var counters = new Sts2AnimationHintFamilyCounters();
        var seen = new System.Collections.Concurrent.ConcurrentBag<long>();

        Parallel.For(0, Workers, _ =>
        {
            for (var i = 0; i < PerWorker; i++)
            {
                seen.Add(counters.RecordEmitted());
            }
        });

        // A milestone that is handed out twice (or skipped) would double-log or silently lose the "first hint"
        // line, which is the one an operator actually looks for.
        var expected = Workers * PerWorker;
        Assert.Equal(expected, seen.Count);
        Assert.Equal(expected, seen.Distinct().Count());
        Assert.Equal(1, seen.Min());
        Assert.Equal(expected, seen.Max());
    }

    [Fact]
    public void TheThreeSharedFamiliesAreDistinctInstances()
    {
        Assert.NotSame(Sts2AnimationHintDiagnostics.CardFlight, Sts2AnimationHintDiagnostics.CardDiscard);
        Assert.NotSame(Sts2AnimationHintDiagnostics.CardFlight, Sts2AnimationHintDiagnostics.HandTween);
        Assert.NotSame(Sts2AnimationHintDiagnostics.CardDiscard, Sts2AnimationHintDiagnostics.HandTween);

        // Stable identity, so an embedder may cache the reference it polls.
        Assert.Same(Sts2AnimationHintDiagnostics.CardFlight, Sts2AnimationHintDiagnostics.CardFlight);
    }

    [Fact]
    public void RecordingOnOneSharedFamilyLeavesTheOthersAlone()
    {
        // DELTAS from a baseline, never absolutes: these are the process-wide instances the live hooks feed, so
        // another test (or a hook that ran earlier in this process) may already have moved them.
        var flightBefore = Snapshot(Sts2AnimationHintDiagnostics.CardFlight);
        var discardBefore = Snapshot(Sts2AnimationHintDiagnostics.CardDiscard);
        var handBefore = Snapshot(Sts2AnimationHintDiagnostics.HandTween);

        Sts2AnimationHintDiagnostics.CardFlight.RecordEmitted();
        Sts2AnimationHintDiagnostics.CardFlight.RecordResolveNull();
        // R14: a shuffle flight's parked retry must not be charged to the discard family, which shares the whole
        // park + drain implementation with it.
        Sts2AnimationHintDiagnostics.CardFlight.RecordResolveParked();
        Sts2AnimationHintDiagnostics.CardFlight.RecordResolveRetryHit();
        Sts2AnimationHintDiagnostics.HandTween.RecordEarlyOutNoSubscribers();

        AssertDelta(
            flightBefore,
            Snapshot(Sts2AnimationHintDiagnostics.CardFlight),
            emitted: 1,
            resolveNull: 1,
            resolveParked: 1,
            resolveRetryHit: 1);
        AssertDelta(discardBefore, Snapshot(Sts2AnimationHintDiagnostics.CardDiscard));
        AssertDelta(
            handBefore,
            Snapshot(Sts2AnimationHintDiagnostics.HandTween),
            earlyOutNoSubscribers: 1);
    }

    [Fact]
    public void TheUnwiredDiscardFamilyIsRecordableAheadOfItsHook()
    {
        // CardDiscard exists before the discard-flight hook does; nothing should have fed it yet, but the surface
        // must already behave like the others so the hook lands as a one-line change.
        var before = Snapshot(Sts2AnimationHintDiagnostics.CardDiscard);

        Sts2AnimationHintDiagnostics.CardDiscard.RecordPublishFailed();

        AssertDelta(before, Snapshot(Sts2AnimationHintDiagnostics.CardDiscard), publishFailed: 1);
    }

    private static long[] Snapshot(Sts2AnimationHintFamilyCounters counters) =>
    [
        counters.Emitted,
        counters.EarlyOutNoSubscribers,
        counters.EarlyOutLeverOff,
        counters.EarlyOutNoResolver,
        counters.ResolveNull,
        counters.PublishFailed,
        counters.ResolveParked,
        counters.ResolveRetryHit,
        counters.ResolveRetryDropped,
        counters.ResolveNotReplayable,
        counters.ResolveSingular,
    ];

    private static void AssertDelta(
        long[] before,
        long[] after,
        long emitted = 0,
        long earlyOutNoSubscribers = 0,
        long earlyOutLeverOff = 0,
        long earlyOutNoResolver = 0,
        long resolveNull = 0,
        long publishFailed = 0,
        long resolveParked = 0,
        long resolveRetryHit = 0,
        long resolveRetryDropped = 0,
        long resolveNotReplayable = 0,
        long resolveSingular = 0)
    {
        long[] expected =
        [
            emitted,
            earlyOutNoSubscribers,
            earlyOutLeverOff,
            earlyOutNoResolver,
            resolveNull,
            publishFailed,
            resolveParked,
            resolveRetryHit,
            resolveRetryDropped,
            resolveNotReplayable,
            resolveSingular,
        ];

        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], after[i] - before[i]);
        }
    }
}
