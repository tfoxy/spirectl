using System.Reflection;
using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// The asset-render phase breakdown's contract: the blocking/parked split (the whole reason the instrument
// exists), render-order phase reporting, the take-once drain, ambient flow into worker tasks, and the fold.
// Pure — no live host, so these run in the ordinary suite.
[Collection(nameof(RenderPhaseProfileCollection))]
public sealed class Sts2RenderPhaseProfileTests : IDisposable
{
    public Sts2RenderPhaseProfileTests() => Sts2RenderPhaseProfile.Reset();

    public void Dispose() => Sts2RenderPhaseProfile.Reset();

    [Fact]
    public void BlockingAndParkedPhasesFoldSeparately()
    {
        Sts2RenderPhaseProfile.Begin("req-1");
        Spend(Sts2RenderPhaseProfile.Phase.Readback, blocking: true);
        Spend(Sts2RenderPhaseProfile.Phase.WarmupWait, blocking: false);
        var snapshot = Sts2RenderPhaseProfile.Complete();

        Assert.NotNull(snapshot);
        Assert.Equal("req-1", snapshot!.RequestId);
        Assert.True(snapshot.BlockingMs > 0, "a blocking phase must land in the blocking fold");
        Assert.True(snapshot.ParkedMs > 0, "a parked phase must land in the parked fold");
        // The split is the point: a parked phase must never inflate the number that describes the game stall.
        Assert.DoesNotContain(snapshot.Phases.Where(phase => phase.Blocking), phase => phase.Phase == Sts2RenderPhaseProfile.Phase.WarmupWait);
    }

    [Fact]
    public void RepeatedPhaseSumsDurationAndCountsCalls()
    {
        Sts2RenderPhaseProfile.Begin("req-repeat");
        Spend(Sts2RenderPhaseProfile.Phase.BatchWait, blocking: false);
        Spend(Sts2RenderPhaseProfile.Phase.BatchWait, blocking: false);
        Spend(Sts2RenderPhaseProfile.Phase.BatchWait, blocking: false);
        var snapshot = Sts2RenderPhaseProfile.Complete();

        var batch = Assert.Single(snapshot!.Phases, phase => phase.Phase == Sts2RenderPhaseProfile.Phase.BatchWait);
        Assert.Equal(3, batch.Calls);
        Assert.True(batch.Ms > 0);
    }

    [Fact]
    public void PhasesAreReportedInRenderOrderNotAlphabetically()
    {
        Sts2RenderPhaseProfile.Begin("req-order");
        Spend(Sts2RenderPhaseProfile.Phase.EncodeSave);
        Spend(Sts2RenderPhaseProfile.Phase.SceneLoad);
        Spend(Sts2RenderPhaseProfile.Phase.Readback);
        var snapshot = Sts2RenderPhaseProfile.Complete();

        Assert.Equal(
            [Sts2RenderPhaseProfile.Phase.SceneLoad, Sts2RenderPhaseProfile.Phase.Readback, Sts2RenderPhaseProfile.Phase.EncodeSave],
            snapshot!.Phases.Select(phase => phase.Phase));
    }

    [Fact]
    public void UnknownPhaseSortsLastInsteadOfBeingDropped()
    {
        Sts2RenderPhaseProfile.Begin("req-unknown");
        Spend("someFutureLane");
        Spend(Sts2RenderPhaseProfile.Phase.SceneLoad);
        var snapshot = Sts2RenderPhaseProfile.Complete();

        Assert.Equal([Sts2RenderPhaseProfile.Phase.SceneLoad, "someFutureLane"], snapshot!.Phases.Select(phase => phase.Phase));
    }

    [Fact]
    public void CountersAccumulateAndGaugesOverwrite()
    {
        Sts2RenderPhaseProfile.Begin("req-counters");
        Sts2RenderPhaseProfile.Count(Sts2RenderPhaseProfile.Counter.LayerLoads);
        Sts2RenderPhaseProfile.Count(Sts2RenderPhaseProfile.Counter.LayerLoads, 5);
        Sts2RenderPhaseProfile.Set(Sts2RenderPhaseProfile.Counter.OutputBytes, 1234);
        Sts2RenderPhaseProfile.Set(Sts2RenderPhaseProfile.Counter.OutputBytes, 4321);
        var snapshot = Sts2RenderPhaseProfile.Complete();

        Assert.Equal(6, snapshot!.Counter(Sts2RenderPhaseProfile.Counter.LayerLoads));
        Assert.Equal(4321, snapshot.Counter(Sts2RenderPhaseProfile.Counter.OutputBytes));
        Assert.Equal(0, snapshot.Counter(Sts2RenderPhaseProfile.Counter.LanesRetired));
    }

    [Fact]
    public void SnapshotIsTakenExactlyOnce()
    {
        Sts2RenderPhaseProfile.Begin("req-take");
        Spend(Sts2RenderPhaseProfile.Phase.Readback);
        Sts2RenderPhaseProfile.Complete();

        Assert.NotNull(Sts2RenderPhaseProfile.TryTake("req-take"));
        // Second take is null, NOT an empty snapshot: an absent breakdown must read as "not measured".
        Assert.Null(Sts2RenderPhaseProfile.TryTake("req-take"));
        Assert.Null(Sts2RenderPhaseProfile.TryTake("never-rendered"));
        Assert.Null(Sts2RenderPhaseProfile.TryTake(null));
    }

    [Fact]
    public void StampsOutsideARenderAreDroppedRatherThanMisattributed()
    {
        Sts2RenderPhaseProfile.Begin("req-closed");
        Sts2RenderPhaseProfile.Complete();

        // A late stamp (an encoder finishing after the lane returned) must not resurrect the closed render.
        Spend(Sts2RenderPhaseProfile.Phase.EncodeFrame);
        Sts2RenderPhaseProfile.Count(Sts2RenderPhaseProfile.Counter.Frames, 99);

        var snapshot = Sts2RenderPhaseProfile.TryTake("req-closed");
        Assert.NotNull(snapshot);
        Assert.Empty(snapshot!.Phases);
        Assert.Equal(0, snapshot.Counter(Sts2RenderPhaseProfile.Counter.Frames));
        Assert.Null(Sts2RenderPhaseProfile.Complete());
    }

    [Fact]
    public async Task WorkerTaskPhasesAttributeToTheRenderThatSpawnedThem()
    {
        Sts2RenderPhaseProfile.Begin("req-worker");
        // The clip lane offloads crop+encode to background tasks; their cost must still land on this render.
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            Spend(Sts2RenderPhaseProfile.Phase.EncodeFrame, blocking: false);
            Sts2RenderPhaseProfile.Count(Sts2RenderPhaseProfile.Counter.Frames);
        })));
        var snapshot = Sts2RenderPhaseProfile.Complete();

        var encode = Assert.Single(snapshot!.Phases, phase => phase.Phase == Sts2RenderPhaseProfile.Phase.EncodeFrame);
        Assert.Equal(3, encode.Calls);
        Assert.Equal(3, snapshot.Counter(Sts2RenderPhaseProfile.Counter.Frames));
    }

    [Fact]
    public async Task AdoptCarriesTheRecorderAcrossARawThreadHop()
    {
        // The live regression this exists for: an extraction reaches the Godot main thread through a raw
        // SynchronizationContext.Post, which does NOT carry the posting thread's ExecutionContext — so the
        // ambient recorder does not travel and every phase the lane stamps is dropped. The first live run of
        // this profiler reported exactly that: dispatchWait only, and the whole render as unattributed.
        var recorder = Sts2RenderPhaseProfile.Begin("req-hop");
        var hopped = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            // A thread with none of the caller's context — what the main loop's raw delegate invocation looks
            // like. (Flow is suppressed explicitly: an ordinary Thread.Start DOES carry the ExecutionContext,
            // so without this the test would pass for the wrong reason.)
            Spend(Sts2RenderPhaseProfile.Phase.Readback);           // dropped: no ambient recorder here
            using (Sts2RenderPhaseProfile.Adopt(recorder))
            {
                Spend(Sts2RenderPhaseProfile.Phase.EncodeSave);     // recorded
            }

            Spend(Sts2RenderPhaseProfile.Phase.Trim);               // dropped again after the scope closes
            hopped.SetResult();
        });

        using (ExecutionContext.SuppressFlow())
        {
            thread.Start();
        }

        await hopped.Task;
        var snapshot = Sts2RenderPhaseProfile.Complete();

        Assert.Equal([Sts2RenderPhaseProfile.Phase.EncodeSave], snapshot!.Phases.Select(phase => phase.Phase));
    }

    [Fact]
    public void AdoptRestoresThePreviousRecorderSoNothingLeaksToTheNextRender()
    {
        Sts2RenderPhaseProfile.Begin("req-outer");
        var inner = new Sts2RenderPhaseProfile.Recorder("req-inner");
        using (Sts2RenderPhaseProfile.Adopt(inner))
        {
            Spend(Sts2RenderPhaseProfile.Phase.EncodeFrame);
        }

        Spend(Sts2RenderPhaseProfile.Phase.Readback);
        var snapshot = Sts2RenderPhaseProfile.Complete();

        Assert.Equal("req-outer", snapshot!.RequestId);
        Assert.Equal([Sts2RenderPhaseProfile.Phase.Readback], snapshot.Phases.Select(phase => phase.Phase));
        Assert.Equal([Sts2RenderPhaseProfile.Phase.EncodeFrame], inner.ToSnapshot().Phases.Select(phase => phase.Phase));
    }

    [Fact]
    public void TotalIsMeasuredEndToEndSoUnattributedTimeStaysVisible()
    {
        Sts2RenderPhaseProfile.Begin("req-total");
        Thread.Sleep(20); // render work nobody stamped
        Spend(Sts2RenderPhaseProfile.Phase.Readback);
        var snapshot = Sts2RenderPhaseProfile.Complete();

        Assert.True(
            snapshot!.TotalMs > snapshot.BlockingMs + snapshot.ParkedMs,
            "the total must not be defined as the sum of the phases, or the instrument could never report a gap");
        Assert.True(snapshot.UnattributedMs > 0);
    }

    [Fact]
    public void TopBlockingIgnoresACheaperBlockingPhaseAndAnyParkedOne()
    {
        Sts2RenderPhaseProfile.Begin("req-top");
        Spend(Sts2RenderPhaseProfile.Phase.WarmupWait, blocking: false, ms: 40);
        Spend(Sts2RenderPhaseProfile.Phase.EncodeSave, ms: 20);
        Spend(Sts2RenderPhaseProfile.Phase.Readback, ms: 1);
        var snapshot = Sts2RenderPhaseProfile.Complete();

        Assert.Equal(Sts2RenderPhaseProfile.Phase.EncodeSave, snapshot!.TopBlocking?.Phase);
    }

    [Fact]
    public void FoldSumsPhasesAcrossRendersAndKeepsRenderOrder()
    {
        var snapshots = new List<Sts2RenderPhaseProfile.Snapshot>();
        foreach (var index in Enumerable.Range(0, 3))
        {
            Sts2RenderPhaseProfile.Begin($"req-fold-{index}");
            Spend(Sts2RenderPhaseProfile.Phase.EncodeSave);
            Spend(Sts2RenderPhaseProfile.Phase.SceneLoad);
            snapshots.Add(Sts2RenderPhaseProfile.Complete()!);
        }

        var folded = Sts2RenderPhaseProfile.Fold(snapshots);

        Assert.Equal([Sts2RenderPhaseProfile.Phase.SceneLoad, Sts2RenderPhaseProfile.Phase.EncodeSave], folded.Select(phase => phase.Phase));
        Assert.All(folded, phase => Assert.Equal(3, phase.Calls));
        Assert.Empty(Sts2RenderPhaseProfile.Fold([]));
    }

    [Fact]
    public void LogLineCarriesTheTotalsAndEveryPhaseAndCounter()
    {
        Sts2RenderPhaseProfile.Begin("req-log");
        Spend(Sts2RenderPhaseProfile.Phase.Readback);
        Spend(Sts2RenderPhaseProfile.Phase.BatchWait, blocking: false);
        Spend(Sts2RenderPhaseProfile.Phase.BatchWait, blocking: false);
        Sts2RenderPhaseProfile.Set(Sts2RenderPhaseProfile.Counter.OutputBytes, 2048);
        var line = Sts2RenderPhaseProfile.FormatLogLine(Sts2RenderPhaseProfile.Complete()!);

        Assert.Contains("total=", line, StringComparison.Ordinal);
        Assert.Contains("blocking=", line, StringComparison.Ordinal);
        Assert.Contains("parked=", line, StringComparison.Ordinal);
        Assert.Contains(Sts2RenderPhaseProfile.Phase.Readback, line, StringComparison.Ordinal);
        Assert.Contains($"{Sts2RenderPhaseProfile.Phase.BatchWait}=", line, StringComparison.Ordinal);
        Assert.Contains("x2", line, StringComparison.Ordinal);
        Assert.Contains($"{Sts2RenderPhaseProfile.Counter.OutputBytes}=2048", line, StringComparison.Ordinal);
    }

    [Fact]
    public void PhaseOrderHasNoDuplicates()
    {
        // The order list doubles as the report's column set; a duplicate would silently rank one of them wrong.
        Assert.Equal(Sts2RenderPhaseProfile.PhaseOrder.Count, Sts2RenderPhaseProfile.PhaseOrder.Distinct(StringComparer.Ordinal).Count());
    }

    // R21: the shape RenderNodeResultAsync now records for a single-image render — the main thread parks in
    // `encodeWait` while a worker (under the shared encode budget) does normalize+save. All three phases are
    // NON-blocking, so the render's blocking fold is the capture only. Before the offload the same render put
    // ~650ms of encodeSave in the blocking fold, which is the hitch this round exists to remove.
    [Fact]
    public async Task OffloadedSingleImageEncodeRecordsNoBlockingTime()
    {
        Sts2RenderPhaseProfile.Begin("req-offload");
        Spend(Sts2RenderPhaseProfile.Phase.Readback); // the one genuinely main-thread step that remains
        var blockingAfterCapture = Sts2RenderPhaseProfile.Complete()!.BlockingMs;

        Sts2RenderPhaseProfile.Begin("req-offload-2");
        using (Sts2RenderPhaseProfile.Measure(Sts2RenderPhaseProfile.Phase.EncodeWait, blocking: false))
        {
            using var lease = await Sts2RenderEncodeBudget.AcquireAsync();
            await Task.Run(() =>
            {
                Spend(Sts2RenderPhaseProfile.Phase.EncodeNormalize, blocking: false);
                Spend(Sts2RenderPhaseProfile.Phase.EncodeSave, blocking: false, ms: 8);
            });
        }

        var snapshot = Sts2RenderPhaseProfile.Complete();
        Assert.NotNull(snapshot);
        // The encode landed on THIS render even though it ran on a worker thread (ExecutionContext flow).
        var save = Assert.Single(snapshot!.Phases, phase => phase.Phase == Sts2RenderPhaseProfile.Phase.EncodeSave);
        Assert.False(save.Blocking);
        Assert.True(save.Ms > 0);
        Assert.Equal(0, snapshot.BlockingMs);
        Assert.True(snapshot.ParkedMs > 0);
        Assert.True(blockingAfterCapture > 0, "the capture itself still blocks — only the encode moved");
    }

    // P7: Record() attributes a duration that something else already measured — the geoclip sweep hands back a
    // per-pass `Ms`, and the association's colour-read meter times the individual readbacks — without the
    // profiler having to wrap Godot-free code or re-time it more coarsely.
    [Fact]
    public void RecordAttributesAnAlreadyMeasuredDurationWithoutAScope()
    {
        Sts2RenderPhaseProfile.Begin("req-record");
        Sts2RenderPhaseProfile.Record(Sts2RenderPhaseProfile.Phase.BakeSweepProbe, 250.5);
        Sts2RenderPhaseProfile.Record(Sts2RenderPhaseProfile.Phase.BakeSweepProbe, 100.5);
        Sts2RenderPhaseProfile.Record(Sts2RenderPhaseProfile.Phase.BakeSweepWait, 40, blocking: false);
        var snapshot = Sts2RenderPhaseProfile.Complete();

        var probe = Assert.Single(snapshot!.Phases, phase => phase.Phase == Sts2RenderPhaseProfile.Phase.BakeSweepProbe);
        // The DURATION is the caller's, not a re-measurement: 351 ms in a test that ran for microseconds.
        Assert.Equal(351, probe.Ms, 3);
        Assert.Equal(2, probe.Calls);
        Assert.True(probe.Blocking);
        Assert.Equal(351, snapshot.BlockingMs, 3);
        Assert.Equal(40, snapshot.ParkedMs, 3);
    }

    [Fact]
    public void RecordOutsideARenderIsDroppedRatherThanThrowing()
    {
        // A stamp is diagnostics, and diagnostics must not be able to fail a bake — so a Record that arrives
        // before Begin, or after Complete, is a silent drop on both sides.
        Sts2RenderPhaseProfile.Record(Sts2RenderPhaseProfile.Phase.BakeColorRead, 999);

        Sts2RenderPhaseProfile.Begin("req-record-closed");
        Sts2RenderPhaseProfile.Complete();
        Sts2RenderPhaseProfile.Record(Sts2RenderPhaseProfile.Phase.BakeColorRead, 999);

        var snapshot = Sts2RenderPhaseProfile.TryTake("req-record-closed");
        Assert.NotNull(snapshot);
        Assert.Empty(snapshot!.Phases);
    }

    [Fact]
    public void PeekReportsTheRenderSoFarWithoutClosingIt()
    {
        // The geoclip bake writes a profile into EVERY pose's manifest while the rig pass is still running, so
        // it needs the accumulation without the drain. Peek outside a render is null, like TryTake: an absent
        // breakdown reads as "not measured", never as "measured zero".
        Assert.Null(Sts2RenderPhaseProfile.Peek());

        Sts2RenderPhaseProfile.Begin("req-peek");
        Sts2RenderPhaseProfile.Record(Sts2RenderPhaseProfile.Phase.BakeAtlasRead, 12);
        var midway = Sts2RenderPhaseProfile.Peek();
        Assert.Equal(12, midway!.BlockingMs, 3);

        // Still open: a second phase lands on the SAME render, and Complete still returns it.
        Sts2RenderPhaseProfile.Record(Sts2RenderPhaseProfile.Phase.BakePageWrite, 8);
        var final = Sts2RenderPhaseProfile.Complete();
        Assert.Equal("req-peek", final!.RequestId);
        Assert.Equal(20, final.BlockingMs, 3);
        Assert.Null(Sts2RenderPhaseProfile.Peek());
    }

    // Every `Bake*` phase constant must be RANKED. PhaseOrder doubles as the report's column set, and a name
    // missing from it sorts last-and-alphabetically among the unknowns — so the bake's table would silently
    // stop reading top-to-bottom as the thing the bake actually did, which is the only reason the order exists.
    [Fact]
    public void EveryBakePhaseConstantIsInPhaseOrderExactlyOnce()
    {
        var bakePhases = typeof(Sts2RenderPhaseProfile.Phase)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(field => field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .Where(phase => phase.StartsWith("bake", StringComparison.Ordinal))
            .ToList();

        // The reflection is only worth something if it actually found the constants.
        Assert.Equal(15, bakePhases.Count);
        foreach (var phase in bakePhases)
        {
            Assert.Equal(1, Sts2RenderPhaseProfile.PhaseOrder.Count(known => string.Equals(known, phase, StringComparison.Ordinal)));
        }
    }

    private static void Spend(string phase, bool blocking = true, int ms = 2)
    {
        using var _ = Sts2RenderPhaseProfile.Measure(phase, blocking);
        Thread.Sleep(ms);
    }
}

[CollectionDefinition(nameof(RenderPhaseProfileCollection), DisableParallelization = true)]
public sealed class RenderPhaseProfileCollection;
