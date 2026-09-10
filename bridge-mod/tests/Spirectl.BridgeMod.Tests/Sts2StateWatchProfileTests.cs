using System.Text.Json.Nodes;
using Spirectl.Sts2.Embedding;
using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// The state-hub self-profiler: its window accumulator, its fold, the perf-report/1 envelope it emits, and the
// profile SELECTOR that routes GetProducerWalkProfile at it. The envelope assertions matter most: the shared
// validator (godot-scene-web) drops a report over one bad enum value, and the two counters this profile exists
// for - skippedByBackoff and revisionWakes - are worthless if they never reach the JSON.
public sealed class Sts2StateWatchProfileTests
{
    [Fact]
    public void AccumulatorClosesAWindowOnceItHasCoveredTheMinimum()
    {
        var accumulator = new Sts2StateWatchProfile.Accumulator();
        accumulator.RecordCapture(1_000, captureMs: 4.0, fingerprintMs: 1.0, emitted: true);
        accumulator.RecordCapture(1_200, captureMs: 6.0, fingerprintMs: 2.0, emitted: false);
        accumulator.RecordSkipped(1_300, 17);
        accumulator.RecordRevisionWake(1_400);

        Assert.False(accumulator.TryCloseWindow(1_500, Sts2StateWatchProfile.MinWindowMs, out _));
        Assert.True(accumulator.TryCloseWindow(2_000, Sts2StateWatchProfile.MinWindowMs, out var window));

        Assert.Equal(1_000, window.WindowMs);
        Assert.Equal(1, window.Windows);
        Assert.Equal(2, window.Captures);
        Assert.Equal(1, window.Emits);
        Assert.Equal(17, window.SkippedByBackoff);
        Assert.Equal(1, window.RevisionWakes);
        Assert.Equal(10.0, window.BusyMs, 6);
        Assert.Equal(3.0, window.FingerprintMs, 6);
        Assert.Equal(6.0, window.MaxCaptureMs, 6);
        Assert.Equal([4.0, 6.0], window.CaptureMsSamples);
        Assert.Equal(2.0, window.CapturesPerSec, 6);
        Assert.Equal(1.0, window.EmitsPerSec, 6);
        Assert.Equal(17.0, window.SkipsPerSec, 6);
        Assert.Equal(1.0, window.BusyPct, 6);

        // Closing reset it: the next window starts empty at the close instant.
        Assert.Equal(0, accumulator.Peek(2_000).Captures);
    }

    [Fact]
    public void AggregateSumsCountersAndConcatenatesSamples()
    {
        var first = new Sts2StateWatchProfile.Snapshot
        {
            WindowMs = 1_000,
            Windows = 1,
            Captures = 3,
            Emits = 1,
            SkippedByBackoff = 90,
            RevisionWakes = 2,
            BusyMs = 12,
            MaxCaptureMs = 7,
            FingerprintMs = 2,
            CaptureMsSamples = [1.0, 4.0, 7.0],
            ProcessName = "SlayTheSpire2",
        };
        var second = first with
        {
            Captures = 2,
            Emits = 2,
            SkippedByBackoff = 10,
            RevisionWakes = 1,
            BusyMs = 8,
            MaxCaptureMs = 3,
            FingerprintMs = 1,
            CaptureMsSamples = [2.0, 3.0],
        };

        var folded = Sts2StateWatchProfile.Aggregate([first, second]);

        Assert.Equal(2, folded.Windows);
        Assert.Equal(2_000, folded.WindowMs);
        Assert.Equal(5, folded.Captures);
        Assert.Equal(3, folded.Emits);
        Assert.Equal(100, folded.SkippedByBackoff);
        Assert.Equal(3, folded.RevisionWakes);
        Assert.Equal(20.0, folded.BusyMs, 6);
        Assert.Equal(3.0, folded.FingerprintMs, 6);
        Assert.Equal(7.0, folded.MaxCaptureMs, 6);
        Assert.Equal(5, folded.CaptureMsSampleCount);
        Assert.Equal("SlayTheSpire2", folded.ProcessName);
        Assert.Equal(7.0, folded.P95CaptureMs);
    }

    [Fact]
    public void AggregateOfNothingIsEmptyNotFabricated()
    {
        var folded = Sts2StateWatchProfile.Aggregate([]);
        Assert.False(folded.HasData);
        Assert.Equal(0, folded.Captures);
        Assert.Null(folded.P50CaptureMs);
    }

    // The shared-field projection that keeps ProducerWalkProfileResult.Snapshot populated for a state-watch dump.
    // The walk-only gauges must stay ZERO rather than be filled with a lookalike.
    [Fact]
    public void ProducerWalkProjectionCarriesTheSharedFieldsOnly()
    {
        var snapshot = new Sts2StateWatchProfile.Snapshot
        {
            WindowMs = 1_000,
            Windows = 1,
            Captures = 4,
            Emits = 2,
            SkippedByBackoff = 80,
            BusyMs = 20,
            MaxCaptureMs = 9,
            CaptureMsSamples = [1.0, 9.0],
            ProcessName = "SlayTheSpire2",
        };

        var projected = snapshot.ToProducerWalkSnapshot();

        Assert.Equal(1_000, projected.WindowMs);
        Assert.Equal(4, projected.Captures);
        Assert.Equal(2, projected.Emits);
        Assert.Equal(20.0, projected.BusyMs, 6);
        Assert.Equal(9.0, projected.MaxCaptureMs, 6);
        Assert.Equal(2, projected.CaptureMsSampleCount);
        Assert.Equal("SlayTheSpire2", projected.ProcessName);
        Assert.Equal(0, projected.Nodes);
        Assert.Equal(0, projected.NodesRead);
        Assert.Empty(projected.ReadMsByCategory);
    }

    [Fact]
    public void ReportIsASchemaValidEnvelopeCarryingTheBackoffCounters()
    {
        var window = new Sts2StateWatchProfile.Snapshot
        {
            WindowMs = 1_000,
            Windows = 1,
            Captures = 3,
            Emits = 1,
            SkippedByBackoff = 91,
            RevisionWakes = 2,
            BusyMs = 45,
            MaxCaptureMs = 22,
            FingerprintMs = 1.5,
            CaptureMsSamples = [10.0, 13.0, 22.0],
            ProcessName = "SlayTheSpire2",
        };

        var report = Sts2StateWatchProfile.BuildReport(
            [window],
            new Sts2PerfReportEnvelope.ReportEnv("host", "linux"),
            ProducerWalkProfileReport.BuildStateWatchParams("live-runtime", 1, true));

        Assert.Equal("perf-report/1", (string?)report["schema"]);
        Assert.Equal("spirectl", (string?)report["repo"]);
        // The enum godot-scene-web owns has no "state-watch"; scenario is what separates the two dumps.
        Assert.Equal("producer-walk", (string?)report["profile"]);
        Assert.Equal("state-watch", (string?)report["scenario"]);
        Assert.Equal(1, (int?)report["repeats"]);

        var metrics = report["metrics"]!.AsObject();
        Assert.Equal(91, (int?)metrics["skippedByBackoff"]);
        Assert.Equal(2, (int?)metrics["revisionWakes"]);
        Assert.Equal(1.5, (double?)metrics["fingerprintMs"]);
        Assert.Equal(3, (int?)metrics["captures"]);
        Assert.Equal(13.0, (double?)metrics["p50CaptureMs"]);
        Assert.Equal(22.0, (double?)metrics["p95CaptureMs"]);

        // The shared cpu block, in the cross-repo naming.
        var cpu = metrics["cpu"]!.AsObject();
        Assert.Equal(45.0, (double?)cpu["totalCpuMs"]);
        Assert.Equal(0.045, (double)cpu["totalCoreRatio"]!, 6);
        Assert.Equal(
            "state-watch-capture",
            (string?)cpu["byThread"]!.AsArray()[0]!["thread"]);

        // Anti-degeneracy: the shared validator requires a non-empty runs array.
        Assert.Single(report["runs"]!.AsArray());
        Assert.Equal(91, (int?)report["runs"]![0]!["skippedByBackoff"]);

        var levers = report["params"]!["levers"]!.AsObject();
        Assert.True(levers.ContainsKey("idleBackoff"));
        Assert.True(levers.ContainsKey("maxIdleMs"));
        Assert.True(levers.ContainsKey("revisionWake"));
        Assert.NotNull((string?)report["params"]!["cpuCaveat"]);
    }

    [Fact]
    public void ReportWithoutAProcessNameOmitsTheCpuBlockWithAReason()
    {
        var window = new Sts2StateWatchProfile.Snapshot
        {
            WindowMs = 1_000,
            Windows = 1,
            Captures = 1,
            BusyMs = 5,
            CaptureMsSamples = [5.0],
            ProcessName = null,
        };

        var report = Sts2StateWatchProfile.BuildReport(
            [window],
            new Sts2PerfReportEnvelope.ReportEnv("host", "linux"));

        var metrics = report["metrics"]!.AsObject();
        Assert.False(metrics.ContainsKey("cpu"));
        Assert.Contains("process name", (string?)metrics["cpuOmittedReason"]);
    }

    [Fact]
    public void LiveRecordingClosesAndRetainsWindowsWhenProfilingIsOn()
    {
        using var scope = new ProfileScope();
        Sts2StateWatchRuntimeSettings.Profile = true;

        scope.Now = 10_000;
        Sts2StateWatchProfile.RecordCapture(captureMs: 5, fingerprintMs: 1, emitted: true);
        Sts2StateWatchProfile.RecordSkippedByBackoff(40);
        Sts2StateWatchProfile.RecordRevisionWake();
        Assert.Empty(Sts2StateWatchProfile.TakeWindows());

        // Crossing the window minimum on the next capture closes and retains it.
        scope.Now = 11_100;
        Sts2StateWatchProfile.RecordCapture(captureMs: 7, fingerprintMs: 2, emitted: false);

        var windows = Sts2StateWatchProfile.TakeWindows();
        var window = Assert.Single(windows);
        Assert.Equal(2, window.Captures);
        Assert.Equal(1, window.Emits);
        Assert.Equal(40, window.SkippedByBackoff);
        Assert.Equal(1, window.RevisionWakes);
        Assert.Equal(1_100, window.WindowMs);

        // Reset is the A/B loop: the next dump measures a fresh interval.
        Assert.NotEmpty(Sts2StateWatchProfile.TakeWindows(reset: true));
        Assert.Empty(Sts2StateWatchProfile.TakeWindows());
    }

    [Fact]
    public void LiveRecordingIsInertWhenProfilingIsOff()
    {
        using var scope = new ProfileScope();
        Sts2StateWatchRuntimeSettings.Profile = false;

        scope.Now = 10_000;
        Sts2StateWatchProfile.RecordCapture(captureMs: 5, fingerprintMs: 1, emitted: true);
        scope.Now = 12_000;
        Sts2StateWatchProfile.RecordCapture(captureMs: 5, fingerprintMs: 1, emitted: true);

        Assert.Empty(Sts2StateWatchProfile.TakeWindows());
    }

    [Fact]
    public void RequestSelectsTheStateWatchHolder()
    {
        using var scope = new ProfileScope();
        Sts2StateWatchRuntimeSettings.Profile = true;
        Sts2StateWatchProfile.Publish(new Sts2StateWatchProfile.Snapshot
        {
            WindowMs = 1_000,
            Windows = 1,
            Captures = 6,
            Emits = 2,
            SkippedByBackoff = 88,
            BusyMs = 30,
            MaxCaptureMs = 9,
            CaptureMsSamples = [4.0, 9.0],
            ProcessName = "SlayTheSpire2",
        });

        var result = ProducerWalkProfileReport.Capture(new ProducerWalkProfileRequest(
            Profile: Sts2StateWatchProfile.ProfileName));

        Assert.True(result.Available);
        Assert.Null(result.UnavailableReason);
        Assert.Equal(1, result.Windows);
        Assert.NotNull(result.StateWatchSnapshot);
        Assert.Equal(88, result.StateWatchSnapshot!.SkippedByBackoff);
        // The shared fields are projected onto the producer-walk shape so both are readable.
        Assert.Equal(6, result.Snapshot.Captures);

        var report = JsonNode.Parse(result.ReportJson)!;
        Assert.Equal("state-watch", (string?)report["scenario"]);
        Assert.Equal(88, (int?)report["metrics"]!["skippedByBackoff"]);
    }

    [Fact]
    public void StateWatchDumpDoesNotDrainTheProducerWalkHistory()
    {
        using var scope = new ProfileScope();
        Sts2ProducerWalkProfile.Publish(new Sts2ProducerWalkProfile.Snapshot
        {
            WindowMs = 1_000,
            Windows = 1,
            Captures = 2,
            BusyMs = 4,
            ProcessName = "SlayTheSpire2",
        });

        _ = ProducerWalkProfileReport.Capture(new ProducerWalkProfileRequest(
            Reset: true,
            Profile: Sts2StateWatchProfile.ProfileName));

        Assert.Single(Sts2ProducerWalkProfile.TakeWindows());
    }

    [Fact]
    public void UnavailableStateWatchDumpExplainsWhichLeverIsMissing()
    {
        using var scope = new ProfileScope();
        Sts2StateWatchRuntimeSettings.Profile = false;

        var result = ProducerWalkProfileReport.Capture(new ProducerWalkProfileRequest(
            Profile: Sts2StateWatchProfile.ProfileName));

        Assert.False(result.Available);
        Assert.Contains(Sts2StateWatchProfile.ProfileEnvVar, result.UnavailableReason);
        // Still a schema-valid envelope, not an exception and not a fabricated run.
        Assert.Equal("state-watch", (string?)JsonNode.Parse(result.ReportJson)!["scenario"]);
    }

    [Fact]
    public void UnknownProfileFailsInsteadOfReturningTheOtherProfilesNumbers()
    {
        using var scope = new ProfileScope();

        var result = ProducerWalkProfileReport.Capture(new ProducerWalkProfileRequest(Profile: "browser-render"));

        Assert.False(result.Available);
        Assert.Contains("producer-walk", result.UnavailableReason);
        Assert.Contains("state-watch", result.UnavailableReason);
        Assert.Null(result.StateWatchSnapshot);
    }

    [Fact]
    public void DefaultRequestStillReadsTheProducerWalkHolder()
    {
        using var scope = new ProfileScope();
        Sts2ProducerWalkProfile.Publish(new Sts2ProducerWalkProfile.Snapshot
        {
            WindowMs = 1_000,
            Windows = 1,
            Captures = 5,
            BusyMs = 10,
            ProcessName = "SlayTheSpire2",
        });

        var result = ProducerWalkProfileReport.Capture(new ProducerWalkProfileRequest());

        Assert.True(result.Available);
        Assert.Equal(5, result.Snapshot.Captures);
        Assert.Null(result.StateWatchSnapshot);
        Assert.Equal("producer-walk", (string?)JsonNode.Parse(result.ReportJson)!["scenario"]);
    }

    // The never-miss budget is not negotiable from an env var or an embedder.
    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(120, 120)]
    [InlineData(400, 400)]
    [InlineData(30_000, 400)]
    public void MaxIdleIntervalIsClampedToTheCeiling(int requestedMs, int expectedMs)
    {
        using var scope = new ProfileScope();
        Sts2StateWatchRuntimeSettings.MaxIdleInterval = TimeSpan.FromMilliseconds(requestedMs);
        Assert.Equal(expectedMs, (int)Sts2StateWatchRuntimeSettings.MaxIdleInterval.TotalMilliseconds);
        Assert.Equal(expectedMs, Sts2StateWatchRuntimeSettings.ClampMaxIdleMs(requestedMs));
    }

    [Fact]
    public void SemanticRevisionOnlyEverIncreases()
    {
        var before = Sts2SemanticStateRevision.Current;
        var bumped = Sts2SemanticStateRevision.Bump();
        Assert.Equal(before + 1, bumped);
        Assert.Equal(bumped, Sts2SemanticStateRevision.Current);
        Assert.Equal(bumped + 1, Sts2SemanticStateRevision.Bump());
    }

    /// <summary>
    /// Both profile holders and the state-watch levers are process-global; save, clear and restore them so one
    /// test cannot read another's windows. The profiler clock is pinned so window closing is deterministic.
    /// </summary>
    private sealed class ProfileScope : IDisposable
    {
        private readonly Func<long> _clock = Sts2StateWatchProfile.Clock;
        private readonly bool _profile = Sts2StateWatchRuntimeSettings.Profile;
        private readonly TimeSpan _maxIdle = Sts2StateWatchRuntimeSettings.MaxIdleInterval;

        public ProfileScope()
        {
            Sts2StateWatchProfile.ClearRetained();
            Sts2ProducerWalkProfile.ClearRetained();
            Sts2StateWatchProfile.Clock = () => Now;
        }

        public long Now { get; set; } = 10_000;

        public void Dispose()
        {
            Sts2StateWatchProfile.ClearRetained();
            Sts2ProducerWalkProfile.ClearRetained();
            Sts2StateWatchProfile.Clock = _clock;
            Sts2StateWatchRuntimeSettings.Profile = _profile;
            Sts2StateWatchRuntimeSettings.MaxIdleInterval = _maxIdle;
        }
    }
}
