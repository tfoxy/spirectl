using Spirectl.Sts2.Embedding;
using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// WS-4: the on-demand structured dump. The live watcher publishes each closed profiler window into the
// Godot-free retained history; an embedder pulls it back through ISpirectlRuntime.GetProducerWalkProfile as a
// `perf-report/1` envelope. This class owns every test that touches the PROCESS-WIDE retained history, so those
// tests stay serialized (xunit parallelizes across classes, not within one).
public sealed class Sts2ProducerWalkProfileDumpTests
{
    [Fact]
    public void RetainedWindowsFoldIntoOneDumpAndTheResetClearsThemForTheNextAbInterval()
    {
        Sts2ProducerWalkProfile.ClearRetained();
        try
        {
            Sts2ProducerWalkProfile.MarkProfilingEnabled(true);
            Sts2ProducerWalkProfile.Publish(Window(captures: 60, emits: 12, busyMs: 90, nodes: 1000));
            Sts2ProducerWalkProfile.Publish(Window(captures: 30, emits: 6, busyMs: 30, nodes: 1100));

            var result = ProducerWalkProfileReport.Capture(new ProducerWalkProfileRequest(Reset: true));

            // One run per measured window, folded metrics on top.
            Assert.Equal(2, System.Text.Json.Nodes.JsonNode.Parse(result.ReportJson)!["runs"]!.AsArray().Count);
            Assert.True(result.Available);
            Assert.Null(result.UnavailableReason);
            Assert.Equal(2, result.Windows);
            Assert.Equal(90, result.Snapshot.Captures);
            Assert.Equal(18, result.Snapshot.Emits);
            Assert.Equal(120, result.Snapshot.BusyMs, 6);
            Assert.Equal(1100, result.Snapshot.Nodes);

            // Reset consumed the history: a second dump measures a fresh interval instead of double counting.
            var afterReset = ProducerWalkProfileReport.Capture(new ProducerWalkProfileRequest());
            Assert.False(afterReset.Available);
            Assert.Equal(0, afterReset.Windows);
        }
        finally
        {
            Sts2ProducerWalkProfile.ClearRetained();
        }
    }

    [Fact]
    public void WindowLimitKeepsTheNewestWindowsAndReadingWithoutResetIsNonDestructive()
    {
        Sts2ProducerWalkProfile.ClearRetained();
        try
        {
            Sts2ProducerWalkProfile.Publish(Window(captures: 1, emits: 0, busyMs: 1, nodes: 1));
            Sts2ProducerWalkProfile.Publish(Window(captures: 2, emits: 0, busyMs: 2, nodes: 2));
            Sts2ProducerWalkProfile.Publish(Window(captures: 3, emits: 0, busyMs: 3, nodes: 3));

            var newest = Sts2ProducerWalkProfile.TakeWindows(limit: 2);
            Assert.Equal([2, 3], newest.Select(window => window.Captures));

            Assert.Equal(3, Sts2ProducerWalkProfile.TakeWindows().Count);
        }
        finally
        {
            Sts2ProducerWalkProfile.ClearRetained();
        }
    }

    [Fact]
    public void HistoryIsBoundedSoALongRunningHostCannotGrowIt()
    {
        Sts2ProducerWalkProfile.ClearRetained();
        try
        {
            for (var i = 0; i < Sts2ProducerWalkProfile.MaxRetainedWindows + 25; i++)
            {
                Sts2ProducerWalkProfile.Publish(Window(captures: i, emits: 0, busyMs: 1, nodes: 1));
            }

            var retained = Sts2ProducerWalkProfile.TakeWindows();
            Assert.Equal(Sts2ProducerWalkProfile.MaxRetainedWindows, retained.Count);
            Assert.Equal(25, retained[0].Captures); // oldest evicted first
        }
        finally
        {
            Sts2ProducerWalkProfile.ClearRetained();
        }
    }

    [Fact]
    public void DumpWithNoDataIsStillASchemaValidReportThatExplainsWhatIsMissing()
    {
        Sts2ProducerWalkProfile.ClearRetained();
        try
        {
            var result = ProducerWalkProfileReport.Capture(new ProducerWalkProfileRequest());

            Assert.False(result.Available);
            Assert.Contains(Sts2ProducerWalkProfile.ProfileEnvVar, result.UnavailableReason);
            Assert.Same(Sts2ProducerWalkProfile.Snapshot.Empty, result.Snapshot);

            var report = System.Text.Json.Nodes.JsonNode.Parse(result.ReportJson)!.AsObject();
            Assert.Equal("perf-report/1", (string?)report["schema"]);
            Assert.Equal("spirectl", (string?)report["repo"]);
            Assert.Equal("producer-walk", (string?)report["profile"]);
            Assert.Equal("producer-walk", (string?)report["scenario"]);
            Assert.Equal(ProducerWalkProfileReport.UnavailableSource, (string?)report["params"]!["source"]);
            Assert.Equal(0, (int?)report["repeats"]);
            Assert.Equal(0, (int?)report["metrics"]!["captures"]);
            Assert.Null(report["metrics"]!["p95CaptureMs"]);
            // No sample was measured, so there is no run to report. The shared validator rejects this shape ON
            // PURPOSE — "measured nothing" must never be mistaken for a measurement.
            Assert.Empty(report["runs"]!.AsArray());

            // Profiler ON but nothing closed yet reads differently from profiler OFF.
            Sts2ProducerWalkProfile.MarkProfilingEnabled(true);
            var enabled = ProducerWalkProfileReport.Capture(new ProducerWalkProfileRequest());
            Assert.Contains("no ~1s window has closed yet", enabled.UnavailableReason);
            Assert.True((bool?)System.Text.Json.Nodes.JsonNode.Parse(enabled.ReportJson)!["params"]!["profileEnabled"]);
        }
        finally
        {
            Sts2ProducerWalkProfile.ClearRetained();
        }
    }

    [Fact]
    public void ReportMapsTheWatcherCountersOntoRecognisableMetricNames()
    {
        Sts2ProducerWalkProfile.ClearRetained();
        try
        {
            Sts2ProducerWalkProfile.Publish(Window(captures: 60, emits: 12, busyMs: 90, nodes: 1000) with
            {
                MaxCaptureMs = 4.25,
                NodesRead = 900,
                SkelElided = 250,
                PrefixRefreshMs = 0.75,
                PrefixRefreshes = 60,
                PrefixChains = 4,
                SuppressWindows = 3,
                SuppressDrops = 17,
                SuppressOpacityWindows = 2,
                SuppressOpacityDrops = 9,
                ReadMsByCategory = [new Sts2ProducerWalkProfile.CategoryCost("text", 6, 200)],
                CaptureMsSamples = [1.0, 1.5, 4.25],
            });

            var result = ProducerWalkProfileReport.Capture(new ProducerWalkProfileRequest(
                EnvKind: "ci",
                EnvLabel: "test-host",
                Parameters: new Dictionary<string, string> { ["treeSize"] = "combat" }));

            var report = System.Text.Json.Nodes.JsonNode.Parse(result.ReportJson)!.AsObject();
            var metrics = report["metrics"]!.AsObject();
            Assert.Equal(60.0, (double?)metrics["capturesPerSec"]);
            Assert.Equal(12.0, (double?)metrics["emitsPerSec"]);
            Assert.Equal(1.5, (double?)metrics["avgCaptureMs"]);
            Assert.Equal(4.25, (double?)metrics["maxCaptureMs"]);
            Assert.Equal(9.0, (double?)metrics["producerBusyPct"]);
            Assert.Equal(1000, (int?)metrics["nodes"]);
            Assert.Equal(900, (int?)metrics["nodesRead"]);
            Assert.Equal(250, (int?)metrics["skelElided"]);
            Assert.Equal(0.75, (double?)metrics["prefixRefreshMs"]);
            Assert.Equal(4, (int?)metrics["prefixChains"]);
            Assert.Equal(4.25, (double?)metrics["p95CaptureMs"]);
            Assert.Equal(6.0, (double?)metrics["readMsByCategory"]!["text"]!["readMs"]);
            Assert.Equal(200, (int?)metrics["readMsByCategory"]!["text"]!["reads"]);
            // Every category is present even when it never fired, so two reports diff column-for-column.
            Assert.Equal(
                Sts2ProducerWalkProfile.CategoryNames,
                metrics["readMsByCategory"]!.AsObject().Select(pair => pair.Key));
            Assert.Equal(3, (int?)metrics["suppressCounters"]!["windows"]);
            Assert.Equal(17, (int?)metrics["suppressCounters"]!["drops"]);
            Assert.Equal(2, (int?)metrics["suppressCounters"]!["opacityWindows"]);
            Assert.Equal(9, (int?)metrics["suppressCounters"]!["opacityDrops"]);

            Assert.Equal("ci", (string?)report["env"]!["kind"]);
            Assert.Equal("test-host", (string?)report["env"]!["label"]);
            Assert.Equal(ProducerWalkProfileReport.LiveSource, (string?)report["params"]!["source"]);
            Assert.Equal("combat", (string?)report["params"]!["treeSize"]);
            Assert.Equal(1, (int?)report["repeats"]);
            Assert.Equal("producer-walk", (string?)report["profile"]);

            // The shared validator requires a NON-EMPTY runs array (one entry per measured sample) and at least
            // one metric leaf above zero; a measured window must satisfy both.
            var run = Assert.Single(report["runs"]!.AsArray());
            Assert.Equal(0, (int?)run!["index"]);
            Assert.Equal(60, (int?)run["captures"]);
            Assert.Equal(1.5, (double?)run["avgCaptureMs"]);
            Assert.Equal(9.0, (double?)run["producerBusyPct"]);
            Assert.Contains(metrics, pair => pair.Value is System.Text.Json.Nodes.JsonValue value
                && value.TryGetValue<double>(out var leaf) && leaf > 0);
        }
        finally
        {
            Sts2ProducerWalkProfile.ClearRetained();
        }
    }

    [Fact]
    public void ParamsRecordTheEffectiveAbLeversSoTwoReportsCanBeCompared()
    {
        var previous = Sts2SceneWatchRuntimeSettings.SuppressTweenedTransforms;
        try
        {
            Sts2SceneWatchRuntimeSettings.SuppressTweenedTransforms = false;
            var parameters = ProducerWalkProfileReport.BuildParams("live-runtime", 3, profilingEnabled: true);

            Assert.Equal("live-runtime", (string?)parameters["source"]);
            Assert.Equal(3, (int?)parameters["windows"]);
            Assert.True((bool?)parameters["profileEnabled"]);
            // The EFFECTIVE value, not the seeded env var: an embedder flips these at run time.
            Assert.False((bool?)parameters["levers"]!["suppressTweenedTransforms"]);
            Assert.NotNull(parameters["levers"]!["emitLocalTransforms"]);
            Assert.NotNull(parameters["envOverrides"]);
        }
        finally
        {
            Sts2SceneWatchRuntimeSettings.SuppressTweenedTransforms = previous;
        }
    }

    private static Sts2ProducerWalkProfile.Snapshot Window(int captures, int emits, double busyMs, int nodes) => new()
    {
        WindowMs = 1000,
        Windows = 1,
        Captures = captures,
        Emits = emits,
        BusyMs = busyMs,
        Nodes = nodes,
    };
}
