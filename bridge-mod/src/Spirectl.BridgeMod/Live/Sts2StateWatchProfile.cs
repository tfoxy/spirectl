using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Spirectl.Sts2.Live;
/// <summary>
/// Godot-free self-profiler for the SEMANTIC state watch hub, the sibling of
/// <see cref="Sts2ProducerWalkProfile"/> for the scene-delta watcher. Same shape (bounded per-window
/// accumulator → retained ~1 s window history → shared <c>perf-report/1</c> envelope) so the two can be read side
/// by side, and always compiled so the fold and the report are unit-testable without the game assemblies.
/// <para>
/// It answers the one question an idle-backoff change has to be judged on: how often does the hub actually run
/// the state walk, and how much of that work was avoided? Hence the two counters the producer-walk profile has no
/// analogue for — <c>skippedByBackoff</c> (a tick where a subscriber was not yet due) and <c>revisionWakes</c> (a
/// tick where <see cref="Sts2SemanticStateRevision"/> collapsed the backoff early) — plus
/// <c>fingerprintMs</c>, so the dedup hash can be told apart from the walk it guards.
/// </para>
/// <para>
/// Report only: nothing here compares a number against a budget. Enabled by
/// <c>SPIRECTL_STATE_WATCH_PROFILE=1</c> (or <see cref="Sts2StateWatchRuntimeSettings.Profile"/> flipped live),
/// read by the bridge-owned profile report with <c>Profile = "state-watch"</c>.
/// </para>
/// </summary>
public static class Sts2StateWatchProfile
{
    /// <summary>Profile SELECTOR on <c>ProducerWalkProfileRequest.Profile</c>. See the envelope note below.</summary>
    public const string ProfileName = "state-watch";

    /// <summary>Default <c>scenario</c> of the emitted report.</summary>
    public const string ScenarioName = "state-watch";

    /// <summary>Env var that seeds <see cref="Sts2StateWatchRuntimeSettings.Profile"/>.</summary>
    public const string ProfileEnvVar = "SPIRECTL_STATE_WATCH_PROFILE";

    /// <summary>
    /// <c>cpu.byThread[].thread</c>. Named for the work, not the OS thread: hub captures run on pool threads (a
    /// different one over time), but only ever ONE at a time, so they are one logical lane.
    /// </summary>
    public const string CpuThreadName = "state-watch-capture";

    /// <summary>
    /// The <c>profile</c> field of the emitted envelope. It is <c>producer-walk</c>, not <c>state-watch</c>:
    /// <c>perf-report/1</c> fixes <c>profile</c> to a four-value enum owned by godot-scene-web
    /// (<c>Sts2PerfReportEnvelope.Profiles</c>), and a fifth word would make every report this emits fail the
    /// shared validator outright. A state-hub capture IS a producer walk — the semantic-state producer's — so the
    /// enum value is honest, and <c>scenario</c> (<see cref="ScenarioName"/>) is what tells the two apart.
    /// </summary>
    public const string EnvelopeProfile = Sts2ProducerWalkProfile.ProfileName;

    /// <summary>How much wall clock a window must cover before it closes.</summary>
    public const long MinWindowMs = 1000;

    /// <summary>Bounded per-window sample ring, so percentiles cost a fixed amount of memory.</summary>
    public const int MaxSamplesPerWindow = 256;

    /// <summary>Retained closed windows (~5 minutes at one per second).</summary>
    public const int MaxRetainedWindows = 300;

    /// <summary>One closed measurement window.</summary>
    public sealed record Snapshot
    {
        /// <summary>Wall clock the window covered.</summary>
        public long WindowMs { get; init; }

        /// <summary>Windows folded into this snapshot (1 for a freshly closed one).</summary>
        public int Windows { get; init; }

        /// <summary>State captures that actually ran the walk.</summary>
        public int Captures { get; init; }

        /// <summary>Captures whose fingerprint differed and therefore emitted an event.</summary>
        public int Emits { get; init; }

        /// <summary>Main-thread ticks where a subscriber was skipped because it was not due yet.</summary>
        public int SkippedByBackoff { get; init; }

        /// <summary>Ticks where a semantic-revision bump collapsed an idle subscriber's backoff.</summary>
        public int RevisionWakes { get; init; }

        /// <summary>Summed capture duration (walk + fingerprint), measured per capture.</summary>
        public double BusyMs { get; init; }

        /// <summary>Slowest single capture in the window.</summary>
        public double MaxCaptureMs { get; init; }

        /// <summary>The share of <see cref="BusyMs"/> spent fingerprinting, not walking.</summary>
        public double FingerprintMs { get; init; }

        /// <summary>Per-capture durations, oldest first, bounded by <see cref="MaxSamplesPerWindow"/>.</summary>
        public IReadOnlyList<double> CaptureMsSamples { get; init; } = [];

        /// <summary>Process the captures ran in; null when introspection was denied.</summary>
        public string? ProcessName { get; init; }

        public static readonly Snapshot Empty = new();

        public bool HasData => Windows > 0 || Captures > 0 || SkippedByBackoff > 0;

        public double AvgCaptureMs => Captures > 0 ? BusyMs / Captures : 0.0;

        public double CapturesPerSec => WindowMs > 0 ? Captures * 1000.0 / WindowMs : 0.0;

        public double EmitsPerSec => WindowMs > 0 ? Emits * 1000.0 / WindowMs : 0.0;

        public double SkipsPerSec => WindowMs > 0 ? SkippedByBackoff * 1000.0 / WindowMs : 0.0;

        /// <summary>Share of the window the hub spent inside a capture, as a percentage.</summary>
        public double BusyPct => WindowMs > 0 ? 100.0 * BusyMs / WindowMs : 0.0;

        /// <summary>The same ratio in the shared cross-repo naming: the share of ONE core.</summary>
        public double CpuCoreRatio => WindowMs > 0 ? BusyMs / WindowMs : 0.0;

        /// <summary>1 whenever anything was captured: every capture carries its own timing.</summary>
        public double CpuCoverage => Captures > 0 ? 1.0 : 0.0;

        public int CaptureMsSampleCount => CaptureMsSamples.Count;

        public double? P50CaptureMs => Sts2ProducerWalkProfile.Percentile(CaptureMsSamples, 50);

        public double? P95CaptureMs => Sts2ProducerWalkProfile.Percentile(CaptureMsSamples, 95);

        public double? P99CaptureMs => Sts2ProducerWalkProfile.Percentile(CaptureMsSamples, 99);

        /// <summary>
        /// Project onto the producer-walk snapshot shape, for the shared fields only, so
        /// <c>ProducerWalkProfileResult.Snapshot</c> stays populated for a state-watch dump. The walk-specific
        /// gauges (node counts, per-category read split, suppression counters) are left at zero rather than
        /// filled with a lookalike: this profile never measured them.
        /// </summary>
        public Sts2ProducerWalkProfile.Snapshot ToProducerWalkSnapshot() => new()
        {
            WindowMs = WindowMs,
            Windows = Windows,
            Captures = Captures,
            Emits = Emits,
            BusyMs = BusyMs,
            MaxCaptureMs = MaxCaptureMs,
            CaptureMsSamples = CaptureMsSamples,
            ProcessName = ProcessName,
        };
    }

    /// <summary>
    /// Fold several windows into one: counters sum, <see cref="Snapshot.MaxCaptureMs"/> is the maximum, samples
    /// concatenate so the percentiles stay true percentiles.
    /// </summary>
    public static Snapshot Aggregate(IReadOnlyList<Snapshot> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        if (windows.Count == 0)
        {
            return Snapshot.Empty;
        }

        var samples = new List<double>();
        foreach (var window in windows)
        {
            samples.AddRange(window.CaptureMsSamples);
        }

        return new Snapshot
        {
            Windows = windows.Sum(w => Math.Max(w.Windows, 0)),
            WindowMs = windows.Sum(w => w.WindowMs),
            Captures = windows.Sum(w => w.Captures),
            Emits = windows.Sum(w => w.Emits),
            SkippedByBackoff = windows.Sum(w => w.SkippedByBackoff),
            RevisionWakes = windows.Sum(w => w.RevisionWakes),
            BusyMs = windows.Sum(w => w.BusyMs),
            MaxCaptureMs = windows.Max(w => w.MaxCaptureMs),
            FingerprintMs = windows.Sum(w => w.FingerprintMs),
            CaptureMsSamples = samples,
            // Identity, not a counter: the newest window that recorded one. Null stays null.
            ProcessName = windows.LastOrDefault(w => !string.IsNullOrEmpty(w.ProcessName))?.ProcessName,
        };
    }

    /// <summary>
    /// Per-window accumulator. Unlike the scene watcher's, this one is written from POOL threads (the hub's
    /// refresh loop is single-flight but not pinned to one thread), so every mutator is called under
    /// <see cref="Gate"/> by the static recording API below.
    /// </summary>
    public sealed class Accumulator
    {
        private readonly double[] _samples = new double[MaxSamplesPerWindow];
        private long _windowStartMs;
        private int _sampleWrite;
        private int _sampleCount;
        private int _captures;
        private int _emits;
        private int _skipped;
        private int _revisionWakes;
        private double _busyMs;
        private double _maxCaptureMs;
        private double _fingerprintMs;

        public long ElapsedMs(long nowMs) => _windowStartMs == 0 ? 0 : nowMs - _windowStartMs;

        public void RecordCapture(long nowMs, double captureMs, double fingerprintMs, bool emitted)
        {
            if (_windowStartMs == 0)
            {
                _windowStartMs = nowMs;
            }

            _captures++;
            if (emitted)
            {
                _emits++;
            }

            _busyMs += captureMs;
            _fingerprintMs += fingerprintMs;
            if (captureMs > _maxCaptureMs)
            {
                _maxCaptureMs = captureMs;
            }

            _samples[_sampleWrite] = captureMs;
            _sampleWrite = (_sampleWrite + 1) % MaxSamplesPerWindow;
            if (_sampleCount < MaxSamplesPerWindow)
            {
                _sampleCount++;
            }
        }

        public void RecordSkipped(long nowMs, int count)
        {
            if (count <= 0)
            {
                return;
            }

            if (_windowStartMs == 0)
            {
                _windowStartMs = nowMs;
            }

            _skipped += count;
        }

        public void RecordRevisionWake(long nowMs)
        {
            if (_windowStartMs == 0)
            {
                _windowStartMs = nowMs;
            }

            _revisionWakes++;
        }

        /// <summary>
        /// Close the window once it has covered <paramref name="minWindowMs"/>, returning the snapshot and
        /// resetting every counter. Returns false — and leaves the accumulator untouched — while it is still open.
        /// </summary>
        public bool TryCloseWindow(long nowMs, long minWindowMs, out Snapshot snapshot)
        {
            var elapsed = ElapsedMs(nowMs);
            if (_windowStartMs == 0 || elapsed < minWindowMs)
            {
                snapshot = Snapshot.Empty;
                return false;
            }

            snapshot = Build(elapsed);
            Reset(nowMs);
            return true;
        }

        /// <summary>Read the open window without closing it (diagnostics/tests).</summary>
        public Snapshot Peek(long nowMs) => Build(ElapsedMs(nowMs));

        public void Reset(long windowStartMs = 0)
        {
            _windowStartMs = windowStartMs;
            _sampleWrite = 0;
            _sampleCount = 0;
            _captures = 0;
            _emits = 0;
            _skipped = 0;
            _revisionWakes = 0;
            _busyMs = 0;
            _maxCaptureMs = 0;
            _fingerprintMs = 0;
        }

        private Snapshot Build(long windowMs) => new()
        {
            WindowMs = windowMs,
            Windows = 1,
            Captures = _captures,
            Emits = _emits,
            SkippedByBackoff = _skipped,
            RevisionWakes = _revisionWakes,
            BusyMs = _busyMs,
            MaxCaptureMs = _maxCaptureMs,
            FingerprintMs = _fingerprintMs,
            CaptureMsSamples = SnapshotSamples(),
            ProcessName = CurrentProcessName,
        };

        private double[] SnapshotSamples()
        {
            var ordered = new double[_sampleCount];
            var start = _sampleCount < MaxSamplesPerWindow ? 0 : _sampleWrite;
            for (var i = 0; i < _sampleCount; i++)
            {
                ordered[i] = _samples[(start + i) % MaxSamplesPerWindow];
            }

            return ordered;
        }
    }

    // ---- Live recording + retained window history -------------------------------------------------------

    private static readonly Lock Gate = new();
    private static readonly Queue<Snapshot> RetainedWindows = new();
    private static readonly Accumulator Live = new();

    /// <summary>Milliseconds source; overridable so window closing is deterministic in tests.</summary>
    internal static Func<long> Clock { get; set; } = static () => Environment.TickCount64;

    /// <summary>
    /// Whether the hub should accumulate. Read per capture (not cached) so an embedder flipping
    /// <see cref="Sts2StateWatchRuntimeSettings.Profile"/> at run time takes effect without a restart.
    /// </summary>
    public static bool ProfilingEnabled => Sts2StateWatchRuntimeSettings.Profile;

    /// <summary>Record one state capture. No-op when profiling is off.</summary>
    public static void RecordCapture(double captureMs, double fingerprintMs, bool emitted)
    {
        if (!ProfilingEnabled)
        {
            return;
        }

        var now = Clock();
        Snapshot? closed = null;
        lock (Gate)
        {
            Live.RecordCapture(now, captureMs, fingerprintMs, emitted);
            if (Live.TryCloseWindow(now, MinWindowMs, out var snapshot))
            {
                closed = snapshot;
            }
        }

        if (closed is not null)
        {
            Publish(closed);
        }
    }

    /// <summary>Record subscribers that a tick skipped because their next capture was not due yet.</summary>
    public static void RecordSkippedByBackoff(int count)
    {
        if (count <= 0 || !ProfilingEnabled)
        {
            return;
        }

        var now = Clock();
        lock (Gate)
        {
            Live.RecordSkipped(now, count);
        }
    }

    /// <summary>Record a tick where a semantic-revision bump collapsed the idle backoff.</summary>
    public static void RecordRevisionWake()
    {
        if (!ProfilingEnabled)
        {
            return;
        }

        var now = Clock();
        lock (Gate)
        {
            Live.RecordRevisionWake(now);
        }
    }

    /// <summary>Retain one closed window, evicting the oldest beyond <see cref="MaxRetainedWindows"/>.</summary>
    public static void Publish(Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (Gate)
        {
            RetainedWindows.Enqueue(snapshot);
            while (RetainedWindows.Count > MaxRetainedWindows)
            {
                RetainedWindows.Dequeue();
            }
        }
    }

    /// <summary>
    /// Read the retained windows, newest last. <paramref name="limit"/> &gt; 0 keeps only the newest N;
    /// <paramref name="reset"/> clears the history (and the open window) afterwards, so the next read measures a
    /// fresh interval — the A/B loop.
    /// </summary>
    public static IReadOnlyList<Snapshot> TakeWindows(int limit = 0, bool reset = false)
    {
        lock (Gate)
        {
            var windows = RetainedWindows.ToArray();
            if (reset)
            {
                RetainedWindows.Clear();
                Live.Reset();
            }

            if (limit > 0 && windows.Length > limit)
            {
                windows = windows[^limit..];
            }

            return windows;
        }
    }

    /// <summary>Drop the retained history and the open window (test isolation / a fresh A/B interval).</summary>
    public static void ClearRetained()
    {
        lock (Gate)
        {
            RetainedWindows.Clear();
            Live.Reset();
        }
    }

    /// <summary>
    /// The effective state-watch levers, for <c>params.levers</c>. Read from the live holder rather than from the
    /// env vars that seeded it, because an embedder can flip them at run time and the seeded value would then be
    /// a lie about what was measured.
    /// </summary>
    public static JsonObject BuildLevers() => new()
    {
        ["idleBackoff"] = Sts2StateWatchRuntimeSettings.IdleBackoff,
        ["maxIdleMs"] = (long)Sts2StateWatchRuntimeSettings.MaxIdleInterval.TotalMilliseconds,
        ["maxIdleCeilingMs"] = Sts2StateWatchRuntimeSettings.MaxIdleCeilingMs,
        ["revisionWake"] = Sts2StateWatchRuntimeSettings.RevisionWake,
        ["profile"] = Sts2StateWatchRuntimeSettings.Profile,
    };

    /// <summary>Build the shared <c>perf-report/1</c> envelope over the given windows.</summary>
    public static JsonObject BuildReport(
        IReadOnlyList<Snapshot> windows,
        Sts2PerfReportEnvelope.ReportEnv env,
        JsonObject? parameters = null,
        string scenario = ScenarioName)
    {
        ArgumentNullException.ThrowIfNull(windows);

        var folded = Aggregate(windows);
        var runs = new JsonArray();
        for (var i = 0; i < windows.Count; i++)
        {
            runs.Add(BuildRun(i, windows[i]));
        }

        var stamped = parameters is null ? [] : (JsonObject)parameters.DeepClone();
        stamped["cpuSource"] = CpuSource;
        stamped["cpuCaveat"] = CpuCaveat;

        return Sts2PerfReportEnvelope.Build(
            EnvelopeProfile,
            string.IsNullOrWhiteSpace(scenario) ? ScenarioName : scenario,
            env,
            stamped,
            BuildMetrics(folded),
            repeats: windows.Count,
            warmups: 0,
            runs: runs);
    }

    /// <summary>Single-window convenience overload.</summary>
    public static JsonObject BuildReport(
        Snapshot snapshot,
        Sts2PerfReportEnvelope.ReportEnv env,
        JsonObject? parameters = null,
        string scenario = ScenarioName)
        => BuildReport(snapshot.HasData ? [snapshot] : [], env, parameters, scenario);

    /// <summary><c>params.cpuSource</c>: how the cpu block's ms were obtained.</summary>
    public const string CpuSource = "stopwatch-around-state-capture";

    /// <summary><c>params.cpuCaveat</c>: what the cpu block's ms are and are not.</summary>
    public const string CpuCaveat =
        "cpuMs is WALL time measured around each state capture on the hub's pool thread, not thread CPU time. "
        + "Most of it is spent BLOCKED marshalling the state walk onto the game main thread, so it is an upper "
        + "bound on the hub thread's own CPU and a fair proxy for the main-thread duty the walk imposes; a GC "
        + "pause or preemption inside a capture inflates it further. Never read it as an under-report.";

    /// <summary>The shared cross-repo <c>cpu</c> block, or the reason there is none.</summary>
    public static bool TryBuildCpuBlock(Snapshot snapshot, out JsonObject? cpu, out string? omittedReason)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cpu = null;

        if (string.IsNullOrEmpty(snapshot.ProcessName))
        {
            omittedReason =
                "the snapshot carries no process name, and every cpu row must name the process it was measured "
                + "in — there is no honest name to use here";
            return false;
        }

        if (snapshot.WindowMs <= 0 || snapshot.Captures <= 0)
        {
            omittedReason = $"nothing was captured (windowMs={snapshot.WindowMs}, captures={snapshot.Captures})";
            return false;
        }

        omittedReason = null;
        cpu = new JsonObject
        {
            ["windowMs"] = snapshot.WindowMs,
            ["totalCpuMs"] = snapshot.BusyMs,
            ["totalCoreRatio"] = snapshot.CpuCoreRatio,
            ["cpuCoverage"] = snapshot.CpuCoverage,
            ["byProcess"] = new JsonObject
            {
                [snapshot.ProcessName] = new JsonObject
                {
                    ["cpuMs"] = snapshot.BusyMs,
                    ["wallMs"] = snapshot.WindowMs,
                    ["coreRatio"] = snapshot.CpuCoreRatio,
                    // One logical capture lane: the hub's refresh loop is single-flight.
                    ["threads"] = 1,
                    ["processes"] = 1,
                },
            },
            ["byThread"] = new JsonArray(new JsonObject
            {
                ["process"] = snapshot.ProcessName,
                ["thread"] = CpuThreadName,
                ["cpuMs"] = snapshot.BusyMs,
                ["wallMs"] = snapshot.WindowMs,
                ["coreRatio"] = snapshot.CpuCoreRatio,
                ["instances"] = 1,
            }),
        };
        return true;
    }

    private static JsonObject BuildMetrics(Snapshot snapshot)
    {
        var metrics = new JsonObject
        {
            ["windowMs"] = snapshot.WindowMs,
            ["windows"] = snapshot.Windows,
            ["captures"] = snapshot.Captures,
            ["emits"] = snapshot.Emits,
            ["skippedByBackoff"] = snapshot.SkippedByBackoff,
            ["revisionWakes"] = snapshot.RevisionWakes,
            ["busyMs"] = snapshot.BusyMs,
            ["fingerprintMs"] = snapshot.FingerprintMs,
            ["avgCaptureMs"] = snapshot.AvgCaptureMs,
            ["maxCaptureMs"] = snapshot.MaxCaptureMs,
            ["p50CaptureMs"] = snapshot.P50CaptureMs,
            ["p95CaptureMs"] = snapshot.P95CaptureMs,
            ["p99CaptureMs"] = snapshot.P99CaptureMs,
            ["captureMsSampleCount"] = snapshot.CaptureMsSampleCount,
            ["capturesPerSec"] = snapshot.CapturesPerSec,
            ["emitsPerSec"] = snapshot.EmitsPerSec,
            ["skipsPerSec"] = snapshot.SkipsPerSec,
            ["busyPct"] = snapshot.BusyPct,
        };

        AttachCpu(metrics, snapshot);
        return metrics;
    }

    private static JsonObject BuildRun(int index, Snapshot window)
    {
        var run = new JsonObject
        {
            ["index"] = index,
            ["windowMs"] = window.WindowMs,
            ["captures"] = window.Captures,
            ["emits"] = window.Emits,
            ["skippedByBackoff"] = window.SkippedByBackoff,
            ["revisionWakes"] = window.RevisionWakes,
            ["busyMs"] = window.BusyMs,
            ["fingerprintMs"] = window.FingerprintMs,
            ["avgCaptureMs"] = window.AvgCaptureMs,
            ["maxCaptureMs"] = window.MaxCaptureMs,
            ["p50CaptureMs"] = window.P50CaptureMs,
            ["p95CaptureMs"] = window.P95CaptureMs,
            ["p99CaptureMs"] = window.P99CaptureMs,
            ["capturesPerSec"] = window.CapturesPerSec,
            ["emitsPerSec"] = window.EmitsPerSec,
            ["busyPct"] = window.BusyPct,
        };

        AttachCpu(run, window);
        return run;
    }

    private static void AttachCpu(JsonObject target, Snapshot snapshot)
    {
        if (TryBuildCpuBlock(snapshot, out var cpu, out var reason))
        {
            target["cpu"] = cpu;
        }
        else
        {
            target["cpuOmittedReason"] = reason;
        }
    }

    // Read ONCE: Process.GetCurrentProcess() allocates and touches /proc, and a window closes ~1/s.
    private static readonly Lazy<string?> CurrentProcessNameLazy = new(
        () =>
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                return string.IsNullOrWhiteSpace(process.ProcessName) ? null : process.ProcessName;
            }
            catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or NotSupportedException)
            {
                return null;
            }
        },
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static string? CurrentProcessName => CurrentProcessNameLazy.Value;
}
