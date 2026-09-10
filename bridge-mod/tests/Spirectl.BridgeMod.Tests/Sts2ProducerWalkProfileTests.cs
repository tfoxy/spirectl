using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// WS-4 producer-walk instrument: the Godot-free half of the live scene watcher's OWN self-profiler
// (SPIRECTL_SCENE_WATCH_PROFILE=1). Pins the per-window aggregation the watcher feeds, the derived
// rates/averages/percentiles, the multi-window fold, reset semantics, and the log-line format + parse round
// trip that turns the once/sec GD.Print output into something diffable.
public sealed class Sts2ProducerWalkProfileTests
{
    private const long WindowMs = 1000;

    [Fact]
    public void WindowStaysOpenUntilItCoversTheWindowLength()
    {
        var accumulator = new Sts2ProducerWalkProfile.Accumulator();
        accumulator.RecordCapture(1_000, 1.0, emitted: true);

        Assert.False(accumulator.TryCloseWindow(1_500, WindowMs, nodes: 10, prefixChains: 1, out var early));
        Assert.Same(Sts2ProducerWalkProfile.Snapshot.Empty, early);

        accumulator.RecordCapture(1_900, 3.0, emitted: false);

        Assert.True(accumulator.TryCloseWindow(2_000, WindowMs, nodes: 10, prefixChains: 1, out var closed));
        Assert.Equal(1000, closed.WindowMs);
        Assert.Equal(2, closed.Captures);
        Assert.Equal(1, closed.Emits);
        Assert.Equal(4.0, closed.BusyMs, 6);
        Assert.Equal(3.0, closed.MaxCaptureMs, 6);
        Assert.Equal(10, closed.Nodes);
        Assert.Equal(1, closed.PrefixChains);
        Assert.Equal(1, closed.Windows);
    }

    [Fact]
    public void ClosedWindowDerivesRatesAveragesAndBusyShare()
    {
        var accumulator = new Sts2ProducerWalkProfile.Accumulator();
        for (var i = 0; i < 10; i++)
        {
            accumulator.RecordCapture(1_000 + i, 2.0, emitted: i % 2 == 0);
        }

        Assert.True(accumulator.TryCloseWindow(3_000, WindowMs, nodes: 0, prefixChains: 0, out var snapshot));

        // 10 captures over a 2s window, 2ms each => 5 captures/s, 2.5 emits/s, 20ms of 2000ms = 1% of one core.
        Assert.Equal(2000, snapshot.WindowMs);
        Assert.Equal(5.0, snapshot.CapturesPerSec, 6);
        Assert.Equal(2.5, snapshot.EmitsPerSec, 6);
        Assert.Equal(2.0, snapshot.AvgCaptureMs, 6);
        Assert.Equal(1.0, snapshot.ProducerBusyPct, 6);
    }

    [Fact]
    public void EmptySnapshotReportsZeroesInsteadOfDividingByAnEmptyWindow()
    {
        var empty = Sts2ProducerWalkProfile.Snapshot.Empty;

        Assert.False(empty.HasData);
        Assert.Equal(0.0, empty.AvgCaptureMs);
        Assert.Equal(0.0, empty.CapturesPerSec);
        Assert.Equal(0.0, empty.EmitsPerSec);
        Assert.Equal(0.0, empty.ProducerBusyPct);
        Assert.Null(empty.P50CaptureMs);
        Assert.Null(empty.P95CaptureMs);
        Assert.Null(empty.P99CaptureMs);
    }

    [Fact]
    public void PercentilesAreNearestRankOverRealObservedSamples()
    {
        var samples = Enumerable.Range(1, 100).Select(value => (double)value).ToArray();

        Assert.Equal(50.0, Sts2ProducerWalkProfile.Percentile(samples, 50));
        Assert.Equal(95.0, Sts2ProducerWalkProfile.Percentile(samples, 95));
        Assert.Equal(99.0, Sts2ProducerWalkProfile.Percentile(samples, 99));
        Assert.Equal(1.0, Sts2ProducerWalkProfile.Percentile(samples, 0));
        Assert.Equal(100.0, Sts2ProducerWalkProfile.Percentile(samples, 100));
        Assert.Null(Sts2ProducerWalkProfile.Percentile([], 50));
    }

    [Fact]
    public void CapturePercentilesComeFromTheWindowsOwnSamples()
    {
        var accumulator = new Sts2ProducerWalkProfile.Accumulator();
        for (var i = 1; i <= 100; i++)
        {
            accumulator.RecordCapture(1_000 + i, i, emitted: false);
        }

        Assert.True(accumulator.TryCloseWindow(2_500, WindowMs, nodes: 0, prefixChains: 0, out var snapshot));

        Assert.Equal(100, snapshot.CaptureMsSampleCount);
        Assert.Equal(50.0, snapshot.P50CaptureMs);
        Assert.Equal(95.0, snapshot.P95CaptureMs);
        Assert.Equal(99.0, snapshot.P99CaptureMs);
        Assert.Equal(100.0, snapshot.MaxCaptureMs);
    }

    [Fact]
    public void SampleRingKeepsTheMostRecentCapturesWhenAWindowOverflows()
    {
        var accumulator = new Sts2ProducerWalkProfile.Accumulator();
        var total = Sts2ProducerWalkProfile.MaxSamplesPerWindow + 50;
        for (var i = 1; i <= total; i++)
        {
            accumulator.RecordCapture(1_000 + i, i, emitted: false);
        }

        Assert.True(accumulator.TryCloseWindow(3_000, WindowMs, nodes: 0, prefixChains: 0, out var snapshot));

        // Bounded, ordered oldest→newest, and holding the TAIL of the window — while the summed counters
        // (captures/busy/max) still cover every capture, so nothing is silently under-reported.
        Assert.Equal(Sts2ProducerWalkProfile.MaxSamplesPerWindow, snapshot.CaptureMsSampleCount);
        Assert.Equal(51.0, snapshot.CaptureMsSamples[0]);
        Assert.Equal(total, snapshot.CaptureMsSamples[^1]);
        Assert.Equal(total, snapshot.Captures);
        Assert.Equal(total, snapshot.MaxCaptureMs);
    }

    [Fact]
    public void NodeReadsAreAttributedToTheirCategoryInWatcherOrder()
    {
        var accumulator = new Sts2ProducerWalkProfile.Accumulator();
        accumulator.RecordCapture(1_000, 1.0, emitted: true);
        var oneMs = System.Diagnostics.Stopwatch.Frequency / 1000;
        accumulator.RecordNode(0, oneMs * 3); // spineSkel
        accumulator.RecordNode(2, oneMs);     // text
        accumulator.RecordNode(2, oneMs);     // text
        accumulator.RecordNode(99, oneMs);    // out of range: counted as a read, attributed to nothing
        accumulator.RecordSkelElided();
        accumulator.RecordSkelElided();

        Assert.True(accumulator.TryCloseWindow(2_000, WindowMs, nodes: 4, prefixChains: 0, out var snapshot));

        Assert.Equal(4, snapshot.NodesRead);
        Assert.Equal(2, snapshot.SkelElided);
        Assert.Equal(Sts2ProducerWalkProfile.CategoryNames, snapshot.ReadMsByCategory.Select(cost => cost.Category));
        Assert.Equal(3.0, Category(snapshot, "spineSkel").ReadMs, 1);
        Assert.Equal(1, Category(snapshot, "spineSkel").Reads);
        Assert.Equal(2.0, Category(snapshot, "text").ReadMs, 1);
        Assert.Equal(2, Category(snapshot, "text").Reads);
        Assert.Equal(0.0, Category(snapshot, "sprite").ReadMs);
        Assert.Equal(0, Category(snapshot, "sprite").Reads);
    }

    [Fact]
    public void SuppressionAndPrefixCountersRideAlongInTheSameWindow()
    {
        var accumulator = new Sts2ProducerWalkProfile.Accumulator();
        accumulator.RecordCapture(1_000, 1.0, emitted: true);
        accumulator.RecordSuppressWindow();
        accumulator.RecordSuppressDrop();
        accumulator.RecordSuppressDrop();
        accumulator.RecordSuppressOpacityWindow();
        accumulator.RecordSuppressOpacityDrop();
        accumulator.RecordPrefixRefresh(System.Diagnostics.Stopwatch.Frequency / 1000);

        Assert.True(accumulator.TryCloseWindow(2_000, WindowMs, nodes: 0, prefixChains: 3, out var snapshot));

        Assert.Equal(1, snapshot.SuppressWindows);
        Assert.Equal(2, snapshot.SuppressDrops);
        Assert.Equal(1, snapshot.SuppressOpacityWindows);
        Assert.Equal(1, snapshot.SuppressOpacityDrops);
        Assert.Equal(1, snapshot.PrefixRefreshes);
        Assert.Equal(1.0, snapshot.PrefixRefreshMs, 1);
        Assert.Equal(3, snapshot.PrefixChains);
    }

    [Fact]
    public void ClosingAWindowResetsEveryCounterForTheNextOne()
    {
        var accumulator = new Sts2ProducerWalkProfile.Accumulator();
        accumulator.RecordCapture(1_000, 5.0, emitted: true);
        accumulator.RecordNode(4, System.Diagnostics.Stopwatch.Frequency / 1000);
        accumulator.RecordSuppressDrop();
        accumulator.RecordSkelElided();
        Assert.True(accumulator.TryCloseWindow(2_000, WindowMs, nodes: 7, prefixChains: 2, out _));

        // The next window starts at the close time, so nothing from the previous one leaks forward.
        Assert.Equal(500, accumulator.ElapsedMs(2_500));
        accumulator.RecordCapture(2_500, 1.0, emitted: false);
        Assert.True(accumulator.TryCloseWindow(3_000, WindowMs, nodes: 7, prefixChains: 2, out var second));

        Assert.Equal(1, second.Captures);
        Assert.Equal(0, second.Emits);
        Assert.Equal(1.0, second.BusyMs, 6);
        Assert.Equal(1.0, second.MaxCaptureMs, 6);
        Assert.Equal(0, second.NodesRead);
        Assert.Equal(0, second.SkelElided);
        Assert.Equal(0, second.SuppressDrops);
        Assert.Equal(0.0, Category(second, "sprite").ReadMs);
        Assert.Single(second.CaptureMsSamples);
    }

    [Fact]
    public void ExplicitResetDropsAnOpenWindow()
    {
        var accumulator = new Sts2ProducerWalkProfile.Accumulator();
        accumulator.RecordCapture(1_000, 4.0, emitted: true);
        accumulator.Reset();

        Assert.Equal(0, accumulator.ElapsedMs(9_000));
        Assert.False(accumulator.TryCloseWindow(9_000, WindowMs, nodes: 0, prefixChains: 0, out _));

        var peeked = accumulator.Peek(9_000, nodes: 0, prefixChains: 0);
        Assert.Equal(0, peeked.Captures);
        Assert.Empty(peeked.CaptureMsSamples);
    }

    [Fact]
    public void AggregateSumsCountersKeepsTheWorstCaseAndRecomputesTruePercentiles()
    {
        var first = new Sts2ProducerWalkProfile.Snapshot
        {
            WindowMs = 1000,
            Windows = 1,
            Captures = 2,
            Emits = 1,
            BusyMs = 3,
            MaxCaptureMs = 2,
            Nodes = 100,
            NodesRead = 90,
            SkelElided = 5,
            PrefixRefreshMs = 0.5,
            PrefixRefreshes = 2,
            PrefixChains = 1,
            SuppressWindows = 1,
            SuppressDrops = 2,
            SuppressOpacityWindows = 3,
            SuppressOpacityDrops = 4,
            ReadMsByCategory = [new Sts2ProducerWalkProfile.CategoryCost("text", 1.5, 30)],
            CaptureMsSamples = [1, 2],
        };
        var second = first with
        {
            MaxCaptureMs = 9,
            Nodes = 120,
            PrefixChains = 4,
            ReadMsByCategory = [new Sts2ProducerWalkProfile.CategoryCost("text", 0.5, 10)],
            CaptureMsSamples = [9],
        };

        var folded = Sts2ProducerWalkProfile.Aggregate([first, second]);

        Assert.Equal(2, folded.Windows);
        Assert.Equal(2000, folded.WindowMs);
        Assert.Equal(4, folded.Captures);
        Assert.Equal(2, folded.Emits);
        Assert.Equal(6, folded.BusyMs, 6);
        Assert.Equal(9, folded.MaxCaptureMs, 6);
        Assert.Equal(180, folded.NodesRead);
        Assert.Equal(10, folded.SkelElided);
        Assert.Equal(1.0, folded.PrefixRefreshMs, 6);
        Assert.Equal(4, folded.PrefixRefreshes);
        Assert.Equal(2, folded.SuppressWindows);
        Assert.Equal(4, folded.SuppressDrops);
        Assert.Equal(6, folded.SuppressOpacityWindows);
        Assert.Equal(8, folded.SuppressOpacityDrops);
        // Gauges take the LAST window's reading rather than summing into a meaningless total.
        Assert.Equal(120, folded.Nodes);
        Assert.Equal(4, folded.PrefixChains);
        Assert.Equal(2.0, Category(folded, "text").ReadMs, 6);
        Assert.Equal(40, Category(folded, "text").Reads);
        // Percentiles come from the CONCATENATED samples, not from averaging each window's percentile.
        Assert.Equal([1.0, 2.0, 9.0], folded.CaptureMsSamples);
        Assert.Equal(2.0, folded.P50CaptureMs);
        Assert.Equal(9.0, folded.P99CaptureMs);
    }

    [Fact]
    public void AggregateOfNothingIsTheEmptySnapshot()
    {
        Assert.Same(Sts2ProducerWalkProfile.Snapshot.Empty, Sts2ProducerWalkProfile.Aggregate([]));
    }

    [Fact]
    public void LogLinesKeepTheTwoHumanLinesAndAddOneMachineReadableLine()
    {
        var snapshot = SampleSnapshot();

        var lines = Sts2ProducerWalkProfile.FormatLogLines(snapshot);

        Assert.Equal(3, lines.Count);
        Assert.Equal(
            "[scene-watch][profile] captures/s=60.0 emits/s=12.0 avg_capture_ms=1.500 max_capture_ms=4.250 "
            + "producer_busy%=9.0 nodes=1200 suppress_windows=3 suppress_drops=17 "
            + "suppress_opacity_windows=2 suppress_opacity_drops=9",
            lines[0]);
        Assert.Equal(
            "[scene-watch][profile] nodes_read=900 skel_elided=250 prefix_refresh_ms=0.750/60 prefix_chains=4 "
            + "read_ms_by_cat: spineSkel=12.0ms/300 spineRoot=0.0ms/0 text=6.0ms/200 particle=0.0ms/0 "
            + "sprite=0.0ms/0 control=0.0ms/0 other=0.0ms/0 ",
            lines[1]);
        Assert.StartsWith(Sts2ProducerWalkProfile.JsonLinePrefix, lines[2]);
    }

    [Fact]
    public void LogLinesRoundTripThroughTheJsonLine()
    {
        var snapshot = SampleSnapshot();

        var parsed = Sts2ProducerWalkProfile.ParseLog(Sts2ProducerWalkProfile.FormatLogLines(snapshot));

        var only = Assert.Single(parsed);
        Assert.Equal(snapshot.WindowMs, only.WindowMs);
        Assert.Equal(snapshot.Captures, only.Captures);
        Assert.Equal(snapshot.Emits, only.Emits);
        Assert.Equal(snapshot.BusyMs, only.BusyMs, 6);
        Assert.Equal(snapshot.MaxCaptureMs, only.MaxCaptureMs, 6);
        Assert.Equal(snapshot.Nodes, only.Nodes);
        Assert.Equal(snapshot.NodesRead, only.NodesRead);
        Assert.Equal(snapshot.SkelElided, only.SkelElided);
        Assert.Equal(snapshot.PrefixRefreshMs, only.PrefixRefreshMs, 6);
        Assert.Equal(snapshot.PrefixChains, only.PrefixChains);
        Assert.Equal(snapshot.SuppressDrops, only.SuppressDrops);
        Assert.Equal(snapshot.CaptureMsSamples, only.CaptureMsSamples);
        Assert.Equal(
            snapshot.ReadMsByCategory.Select(cost => (cost.Category, cost.ReadMs, cost.Reads)),
            only.ReadMsByCategory.Select(cost => (cost.Category, cost.ReadMs, cost.Reads)));
    }

    [Fact]
    public void ParseLogReadsSnapshotLinesOutOfNoisyGameLogsAndIgnoresTheHumanLines()
    {
        var snapshot = SampleSnapshot();
        var lines = Sts2ProducerWalkProfile.FormatLogLines(snapshot);
        var log = new List<string>
        {
            "Godot Engine v4.x - https://godotengine.org",
            lines[0],
            lines[1],
            // A launcher-prefixed line still parses: the marker is searched for, not anchored at column 0.
            $"2026-08-13T00:00:00Z [game] {lines[2]}",
            "[scene-watch][profile-json] {not json",
            "some other log line",
            lines[2],
        };

        var parsed = Sts2ProducerWalkProfile.ParseLog(log);

        Assert.Equal(2, parsed.Count);
        Assert.All(parsed, entry => Assert.Equal(snapshot.Captures, entry.Captures));
    }

    [Fact]
    public void ParseLogOfALogWithoutProfilerLinesYieldsNothingRatherThanGuessing()
    {
        Assert.Empty(Sts2ProducerWalkProfile.ParseLog(["hello", "[scene-watch] not the profiler", string.Empty]));
        Assert.Null(Sts2ProducerWalkProfile.FromJson("   "));
        Assert.Null(Sts2ProducerWalkProfile.FromJson("{"));
    }

    // ---- the shared cross-repo `cpu` block ---------------------------------------------------------------
    // One naming, one meaning, in all three repos that emit perf-report/1. The point of these tests is that the
    // block is a RE-SPELLING of counters the watcher already had — not a second measurement, and above all not a
    // number back-computed from producerBusyPct.

    [Fact]
    public void CpuBlockIsTheWatchersOwnCountersInTheSharedNaming()
    {
        var snapshot = SampleSnapshot(); // 90ms of capture time inside a 1000ms window

        Assert.True(Sts2ProducerWalkProfile.TryBuildCpuBlock(snapshot, out var cpu, out _));

        Assert.Equal(1000, (long?)cpu!["windowMs"]);
        Assert.Equal(90.0, (double?)cpu["totalCpuMs"]);
        Assert.Equal(0.09, (double?)cpu["totalCoreRatio"]);
        // The ms come from the timer and the ratio from the ms — the identity below is what makes this the SAME
        // measurement as the human log line's producer_busy%, rather than a second one that could drift from it.
        Assert.Equal(snapshot.BusyMs, (double?)cpu["totalCpuMs"]);
        Assert.Equal(snapshot.WindowMs, (long?)cpu["windowMs"]);
        Assert.Equal(snapshot.ProducerBusyPct / 100.0, (double)cpu["totalCoreRatio"]!, 12);
    }

    [Fact]
    public void CpuBlockCarriesOneProducerThreadAndOneProcessEntryAndNeverAGpuBlock()
    {
        Assert.True(Sts2ProducerWalkProfile.TryBuildCpuBlock(SampleSnapshot(), out var cpu, out _));

        var entry = Assert.Single(cpu!["byThread"]!.AsArray()).AsObject();
        Assert.Equal("SlayTheSpire2", (string?)entry["process"]);
        Assert.Equal(Sts2ProducerWalkProfile.CpuThreadName, (string?)entry["thread"]);
        Assert.Equal(90.0, (double?)entry["cpuMs"]);
        Assert.Equal(1000, (long?)entry["wallMs"]);
        Assert.Equal(0.09, (double?)entry["coreRatio"]);

        // `byProcess` is a REAL entry, not the empty object that would technically pass: the walk ran in one
        // process, and `{}` would read as "no process burned CPU".
        var process = cpu["byProcess"]!.AsObject();
        var only = Assert.Single(process);
        Assert.Equal("SlayTheSpire2", only.Key);
        Assert.Equal(90.0, (double?)only.Value!["cpuMs"]);
        Assert.Equal(1, (int?)only.Value["processes"]);

        // Coverage is 1 because every capture carries its own reading — a statement about this instrument, not
        // a filler value. A producer walk touches no GPU, and the shared validator rejects a gpu block outright.
        Assert.Equal(1.0, (double?)cpu["cpuCoverage"]);
        Assert.False(cpu.ContainsKey("gpu"));
    }

    [Fact]
    public void AnUnstampedSnapshotWithholdsTheWholeBlockRatherThanNamingAProcess()
    {
        // Hand-built or pre-stamping snapshots exist (old captured logs parse into them). Every cpu row must
        // name its process, and "unknown" would read like a process called unknown — so there is no block.
        Assert.False(Sts2ProducerWalkProfile.TryBuildCpuBlock(
            SampleSnapshot() with { ProcessName = null }, out var cpu, out var reason));

        Assert.Null(cpu);
        Assert.Contains("no process name", reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyWindowCarriesNoCpuBlockAtAll()
    {
        // windowMs 0 / captures 0 is the shape of a measurement that measured nothing; the shared validator
        // demands windowMs > 0, and a zeroed block would be a claim about an interval that never happened.
        // Stamped, so this exercises the empty-window branch rather than the missing-process one.
        Assert.False(Sts2ProducerWalkProfile.TryBuildCpuBlock(
            Sts2ProducerWalkProfile.Snapshot.Empty with { ProcessName = "SlayTheSpire2" },
            out var cpu,
            out var reason));

        Assert.Null(cpu);
        Assert.Contains("nothing was captured", reason!, StringComparison.Ordinal);
        Assert.False(Sts2ProducerWalkProfile.TryBuildCpuBlock(Sts2ProducerWalkProfile.Snapshot.Empty, out _, out _));
    }

    [Fact]
    public void TheWindowStampsTheProcessItWasMeasuredInSoAnOfflineFoldDoesNotNameTheFolder()
    {
        var accumulator = new Sts2ProducerWalkProfile.Accumulator();
        accumulator.RecordCapture(1_000, 5.0, emitted: true);
        Assert.True(accumulator.TryCloseWindow(2_000, WindowMs, nodes: 1, prefixChains: 0, out var closed));

        Assert.False(string.IsNullOrEmpty(closed.ProcessName));
        // It survives the log round trip, which is the whole point: the CLI that folds a captured game log is a
        // different process from the game that produced it.
        var parsed = Assert.Single(Sts2ProducerWalkProfile.ParseLog(Sts2ProducerWalkProfile.FormatLogLines(closed)));
        Assert.Equal(closed.ProcessName, parsed.ProcessName);
    }

    [Fact]
    public void AFoldTakesTheNewestRecordedProcessAndNeverInventsOne()
    {
        var stamped = SampleSnapshot() with { ProcessName = "SlayTheSpire2" };
        var unstamped = SampleSnapshot() with { ProcessName = null };

        Assert.Equal("SlayTheSpire2", Sts2ProducerWalkProfile.Aggregate([stamped, unstamped]).ProcessName);
        Assert.Null(Sts2ProducerWalkProfile.Aggregate([unstamped, unstamped]).ProcessName);
    }

    [Fact]
    public void ReportCarriesTheCpuBlockInMetricsAndInEveryRunWithTheSameShape()
    {
        var report = Sts2ProducerWalkProfile.BuildReport(
            [SampleSnapshot(), SampleSnapshot() with { BusyMs = 45 }],
            new Sts2PerfReportEnvelope.ReportEnv("host", "test"));

        var metricsCpu = report["metrics"]!["cpu"]!.AsObject();
        Assert.Equal(2000, (long?)metricsCpu["windowMs"]);
        Assert.Equal(135.0, (double?)metricsCpu["totalCpuMs"]);

        var runs = report["runs"]!.AsArray();
        Assert.Equal(2, runs.Count);
        foreach (var run in runs)
        {
            var runCpu = run!["cpu"]!.AsObject();
            // Identical shape to metrics.cpu: a compacted per-run variant would be a second, weaker contract.
            Assert.Equal(metricsCpu.Select(pair => pair.Key), runCpu.Select(pair => pair.Key));
            Assert.Single(runCpu["byThread"]!.AsArray());
        }

        Assert.Equal(45.0, (double?)runs[1]!["cpu"]!["totalCpuMs"]);
    }

    [Fact]
    public void ReportStampsHowTheCpuNumberWasObtainedWithoutMutatingTheCallersParams()
    {
        var callerParams = new System.Text.Json.Nodes.JsonObject { ["source"] = "profile-log" };

        var report = Sts2ProducerWalkProfile.BuildReport(
            [SampleSnapshot()],
            new Sts2PerfReportEnvelope.ReportEnv("host", "test"),
            callerParams);

        var parameters = report["params"]!.AsObject();
        Assert.Equal("profile-log", (string?)parameters["source"]);
        Assert.Equal(Sts2ProducerWalkProfile.CpuSource, (string?)parameters["cpuSource"]);
        // The caveat travels WITH the number: a reader comparing this to a browser byThread entry must be told
        // that cpuMs is on-thread wall time, i.e. an upper bound, before they treat it as scheduler CPU.
        Assert.Contains("UPPER BOUND", (string?)parameters["cpuCaveat"]);
        Assert.False(callerParams.ContainsKey("cpuSource"));
    }

    private static Sts2ProducerWalkProfile.CategoryCost Category(Sts2ProducerWalkProfile.Snapshot snapshot, string name)
        => snapshot.ReadMsByCategory.Single(cost => cost.Category == name);

    private static Sts2ProducerWalkProfile.Snapshot SampleSnapshot() => new()
    {
        WindowMs = 1000,
        Windows = 1,
        Captures = 60,
        Emits = 12,
        BusyMs = 90,
        MaxCaptureMs = 4.25,
        Nodes = 1200,
        NodesRead = 900,
        SkelElided = 250,
        PrefixRefreshMs = 0.75,
        PrefixRefreshes = 60,
        PrefixChains = 4,
        SuppressWindows = 3,
        SuppressDrops = 17,
        SuppressOpacityWindows = 2,
        SuppressOpacityDrops = 9,
        ReadMsByCategory =
        [
            new Sts2ProducerWalkProfile.CategoryCost("spineSkel", 12, 300),
            new Sts2ProducerWalkProfile.CategoryCost("spineRoot", 0, 0),
            new Sts2ProducerWalkProfile.CategoryCost("text", 6, 200),
            new Sts2ProducerWalkProfile.CategoryCost("particle", 0, 0),
            new Sts2ProducerWalkProfile.CategoryCost("sprite", 0, 0),
            new Sts2ProducerWalkProfile.CategoryCost("control", 0, 0),
            new Sts2ProducerWalkProfile.CategoryCost("other", 0, 0),
        ],
        CaptureMsSamples = [1.0, 1.5, 4.25],
        ProcessName = "SlayTheSpire2",
    };
}
