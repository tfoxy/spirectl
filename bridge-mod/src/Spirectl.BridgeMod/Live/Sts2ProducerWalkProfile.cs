using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Spirectl.Sts2.Live;
/// <summary>
/// Godot-free aggregation + structured dump for the live scene watcher's EXISTING producer-walk self-profiler
/// (<see cref="Sts2RuntimeSceneWatcher"/>'s <c>RecordCaptureProfile</c>/<c>RecordNodeProfile</c>, enabled with
/// <c>SPIRECTL_SCENE_WATCH_PROFILE=1</c>). PURE and always compiled (like
/// <see cref="Sts2CombatBackgroundLayerSelection"/>) so the aggregation, the log format and the report shape are
/// unit-testable without the live host; the Godot-typed walk that FEEDS it stays in the live-host glob.
/// <para>
/// This is NOT a second measurement path. The counters are exactly the ones the watcher already accumulated in
/// its <c>_prof*</c> fields — capture count/emit count/busy ms/max ms, the per-category read+diff split, the
/// elided frozen-spine reads, the viewport-prefix refresh cost and the four suppression counters. What was
/// missing is that they were only ever rendered as two human <c>GD.Print</c> lines per second that nobody could
/// diff. This type owns those two lines VERBATIM (<see cref="FormatLogLines"/>) plus a third machine-readable
/// <c>[scene-watch][profile-json]</c> line, retains a bounded window history, and maps a snapshot into the shared
/// cross-repo <see cref="Sts2PerfReportEnvelope"/>.
/// </para>
/// <para>
/// Report-only: nothing here compares a number against a budget. The producer walk has historically been ~89% of
/// headless active-play CPU, so an A/B lever flip (any <c>SPIRECTL_SCENE_WATCH_*</c> kill-switch) wants a
/// diffable before/after report — never a wall-clock threshold that fails a build.
/// </para>
/// </summary>
public static class Sts2ProducerWalkProfile
{
    /// <summary>The <c>profile</c> discriminator every producer-walk report carries (selects the metric block).</summary>
    public const string ProfileName = "producer-walk";

    /// <summary>The default <c>scenario</c> name for a producer-walk report.</summary>
    public const string ScenarioName = "producer-walk";

    /// <summary>The environment variable that turns the watcher's self-profiler on (zero overhead when unset).</summary>
    public const string ProfileEnvVar = "SPIRECTL_SCENE_WATCH_PROFILE";

    /// <summary>Prefix of the human profiler lines the watcher has always printed.</summary>
    public const string LogLinePrefix = "[scene-watch][profile] ";

    /// <summary>Prefix of the machine-readable snapshot line (the diffable half of the same counters).</summary>
    public const string JsonLinePrefix = "[scene-watch][profile-json] ";

    /// <summary>
    /// The <c>cpu.byThread[].thread</c> name for the producer walk. A ROLE name, not an OS thread name: the walk
    /// is a synchronous pass that runs on whichever host thread calls <c>Capture()</c> (the Godot main thread in
    /// the live host), and the counters below price the walk, not everything that thread does.
    /// </summary>
    public const string CpuThreadName = "producer-walk";

    /// <summary>
    /// How <c>cpu.totalCpuMs</c> was obtained, recorded in <c>params.cpuSource</c> so a reader never has to guess
    /// whether a cross-repo CPU number is a sample, a scheduler reading, or a back-computed one.
    /// </summary>
    public const string CpuSource = "stopwatch-around-capture";

    /// <summary>
    /// The caveat that belongs next to any comparison of this block with a browser <c>cpu.byThread</c> entry,
    /// recorded in <c>params.cpuCaveat</c>. See <see cref="TryBuildCpuBlock"/> for the full reasoning.
    /// </summary>
    public const string CpuCaveat =
        "cpuMs is ON-THREAD WALL time summed by Stopwatch inside Capture(), not scheduler-attributed CPU time. "
        + "The walk does no I/O and never blocks, so the two coincide except when the thread is preempted or a GC "
        + "pause lands inside a capture: this is an UPPER BOUND on the walk's CPU, never an under-report. Note "
        + "the direction differs from a browser cpu block, whose totalCpuMs is a LOWER bound (untraced work "
        + "counts as zero); cpuCoverage is 1 here because every capture carries a reading by construction.";

    /// <summary>
    /// Per-capture durations kept per window for percentiles. A ~1s window at the watcher's active rate holds
    /// well under this, so in practice every capture is sampled; a longer/faster window keeps the most recent
    /// <see cref="MaxSamplesPerWindow"/> (see <see cref="Snapshot.CaptureMsSamples"/>).
    /// </summary>
    public const int MaxSamplesPerWindow = 256;

    /// <summary>How many closed windows the static holder retains for an on-demand dump (~5 minutes at 1/s).</summary>
    public const int MaxRetainedWindows = 300;

    /// <summary>
    /// Coarse read+diff cost buckets, in the watcher's <c>NodeCategory</c> ORDER — the watcher casts its enum to
    /// an index into this array, so the two must not drift.
    /// </summary>
    public static readonly IReadOnlyList<string> CategoryNames =
        ["spineSkel", "spineRoot", "text", "particle", "sprite", "control", "other"];

    private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    /// <summary>Read+diff cost attributed to one node category over the window.</summary>
    public sealed record CategoryCost(string Category, double ReadMs, int Reads);

    /// <summary>
    /// One closed profiler window (or, after <see cref="Aggregate"/>, several folded together). The stored
    /// fields are raw counters; every rate/average/percentile is DERIVED, so folding windows is a plain sum and
    /// a serialized snapshot round-trips.
    /// </summary>
    public sealed record Snapshot
    {
        /// <summary>Wall-clock span the counters cover.</summary>
        public long WindowMs { get; init; }

        /// <summary>How many profiler windows are folded into this snapshot (1 for a freshly closed window).</summary>
        public int Windows { get; init; }

        /// <summary>Capture (producer walk) passes that ran.</summary>
        public int Captures { get; init; }

        /// <summary>Captures that produced a delta to dispatch.</summary>
        public int Emits { get; init; }

        /// <summary>Total main-thread time spent inside <c>Capture()</c>.</summary>
        public double BusyMs { get; init; }

        /// <summary>Slowest single capture in the window.</summary>
        public double MaxCaptureMs { get; init; }

        /// <summary>Tracked node count at window close (a gauge, not a sum).</summary>
        public int Nodes { get; init; }

        /// <summary>Nodes that actually went through <c>ReadVolatile</c>.</summary>
        public int NodesRead { get; init; }

        /// <summary>Skeleton-leaf reads skipped by the frozen-spine elision.</summary>
        public int SkelElided { get; init; }

        /// <summary>Time spent re-evaluating viewport→screen prefixes.</summary>
        public double PrefixRefreshMs { get; init; }

        /// <summary>Capture passes that ran a prefix refresh.</summary>
        public int PrefixRefreshes { get; init; }

        /// <summary>Viewport prefix chains at window close (a gauge, not a sum).</summary>
        public int PrefixChains { get; init; }

        /// <summary>
        /// Name of the process the window was measured IN, stamped by the <see cref="Accumulator"/> at window
        /// close. Travels with the measurement (through the log line, through an offline fold) so a report built
        /// by the CLI does not name the CLI as the process that ran the walk. Null on a snapshot that predates
        /// this field or was hand-built; the <c>cpu.byThread</c> entry then omits <c>process</c> rather than
        /// guessing one.
        /// </summary>
        public string? ProcessName { get; init; }

        /// <summary>Tween transform-suppression windows opened.</summary>
        public int SuppressWindows { get; init; }

        /// <summary>Transform changes withheld because a node was mid-replay.</summary>
        public int SuppressDrops { get; init; }

        /// <summary>Opacity-suppression windows opened.</summary>
        public int SuppressOpacityWindows { get; init; }

        /// <summary>Opacity changes withheld because a node was mid-fade.</summary>
        public int SuppressOpacityDrops { get; init; }

        /// <summary>Per-category read+diff split, always in <see cref="CategoryNames"/> order.</summary>
        public IReadOnlyList<CategoryCost> ReadMsByCategory { get; init; } = [];

        /// <summary>
        /// The per-capture durations the percentiles below are computed from (bounded by
        /// <see cref="MaxSamplesPerWindow"/> per window). Carried so folded windows yield TRUE percentiles
        /// instead of an average-of-percentiles.
        /// </summary>
        public IReadOnlyList<double> CaptureMsSamples { get; init; } = [];

        /// <summary>Nothing measured. Serializes to a schema-valid all-zero report.</summary>
        public static readonly Snapshot Empty = new();

        [JsonIgnore]
        public bool HasData => Windows > 0 || Captures > 0;

        public double AvgCaptureMs => Captures > 0 ? BusyMs / Captures : 0.0;

        public double CapturesPerSec => WindowMs > 0 ? Captures * 1000.0 / WindowMs : 0.0;

        public double EmitsPerSec => WindowMs > 0 ? Emits * 1000.0 / WindowMs : 0.0;

        /// <summary>Producer share of ONE core: the fraction of wall-clock the main thread sat inside the walk.</summary>
        public double ProducerBusyPct => WindowMs > 0 ? 100.0 * BusyMs / WindowMs : 0.0;

        /// <summary>
        /// The SAME measurement as <see cref="ProducerBusyPct"/> in the cross-repo <c>cpu</c> naming: the fraction
        /// of ONE core the walk occupied over the window. Deliberately a second spelling of one number rather than
        /// a second measurement — see <see cref="TryBuildCpuBlock"/>.
        /// </summary>
        public double CpuCoreRatio => WindowMs > 0 ? BusyMs / WindowMs : 0.0;

        /// <summary>
        /// <c>cpu.cpuCoverage</c>: the share of the measured work whose CPU is actually attributed. Every
        /// <c>RecordCapture</c> carries its own Stopwatch reading into <see cref="BusyMs"/>, so for a window with
        /// captures it is exactly 1 — not a placeholder, a statement that there is no timed-work gap here (unlike
        /// a browser trace, where a task without a <c>tdur</c> leaves its CPU unknown). It falls to 0 only when
        /// nothing was captured, and such a window carries no <c>cpu</c> block at all.
        /// </summary>
        public double CpuCoverage => Captures > 0 ? 1.0 : 0.0;

        public int CaptureMsSampleCount => CaptureMsSamples.Count;

        public double? P50CaptureMs => Percentile(CaptureMsSamples, 50);

        public double? P95CaptureMs => Percentile(CaptureMsSamples, 95);

        public double? P99CaptureMs => Percentile(CaptureMsSamples, 99);
    }

    /// <summary>
    /// Nearest-rank percentile over an unsorted sample set; null when there is nothing to rank. Nearest-rank
    /// (not interpolated) so a reported value is always a REAL observed capture duration.
    /// </summary>
    public static double? Percentile(IReadOnlyList<double> samples, double percentile)
    {
        if (samples is null || samples.Count == 0)
        {
            return null;
        }

        var sorted = samples.ToArray();
        Array.Sort(sorted);
        var rank = (int)Math.Ceiling(percentile / 100.0 * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }

    /// <summary>
    /// Fold several windows into one snapshot: counters sum, <see cref="Snapshot.MaxCaptureMs"/> is the maximum,
    /// the gauges (<see cref="Snapshot.Nodes"/>, <see cref="Snapshot.PrefixChains"/>) take the LAST window's
    /// value, and the retained samples concatenate so the percentiles stay true percentiles.
    /// </summary>
    public static Snapshot Aggregate(IReadOnlyList<Snapshot> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        if (windows.Count == 0)
        {
            return Snapshot.Empty;
        }

        var categoryMs = new double[CategoryNames.Count];
        var categoryReads = new int[CategoryNames.Count];
        var samples = new List<double>();
        var folded = new Snapshot
        {
            Windows = windows.Sum(w => Math.Max(w.Windows, 0)),
            WindowMs = windows.Sum(w => w.WindowMs),
            Captures = windows.Sum(w => w.Captures),
            Emits = windows.Sum(w => w.Emits),
            BusyMs = windows.Sum(w => w.BusyMs),
            MaxCaptureMs = windows.Max(w => w.MaxCaptureMs),
            Nodes = windows[^1].Nodes,
            NodesRead = windows.Sum(w => w.NodesRead),
            SkelElided = windows.Sum(w => w.SkelElided),
            PrefixRefreshMs = windows.Sum(w => w.PrefixRefreshMs),
            PrefixRefreshes = windows.Sum(w => w.PrefixRefreshes),
            PrefixChains = windows[^1].PrefixChains,
            SuppressWindows = windows.Sum(w => w.SuppressWindows),
            SuppressDrops = windows.Sum(w => w.SuppressDrops),
            SuppressOpacityWindows = windows.Sum(w => w.SuppressOpacityWindows),
            SuppressOpacityDrops = windows.Sum(w => w.SuppressOpacityDrops),
            // Identity, not a counter: the newest window that actually recorded one. Null stays null so a fold of
            // unstamped snapshots reports "unknown process" by OMITTING the field, never by inventing one.
            ProcessName = windows.LastOrDefault(w => !string.IsNullOrEmpty(w.ProcessName))?.ProcessName,
        };

        foreach (var window in windows)
        {
            samples.AddRange(window.CaptureMsSamples);
            foreach (var cost in window.ReadMsByCategory)
            {
                var index = IndexOfCategory(cost.Category);
                if (index < 0)
                {
                    continue;
                }

                categoryMs[index] += cost.ReadMs;
                categoryReads[index] += cost.Reads;
            }
        }

        return folded with
        {
            ReadMsByCategory = BuildCategoryCosts(categoryMs, categoryReads),
            CaptureMsSamples = samples,
        };
    }

    /// <summary>
    /// The watcher's once-per-window log output: the two HUMAN lines it has always printed (byte-identical, so
    /// existing logs and eyeballs keep working) plus one machine-readable snapshot line that
    /// <see cref="ParseLog"/> reads back.
    /// </summary>
    public static IReadOnlyList<string> FormatLogLines(Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var categories = new System.Text.StringBuilder();
        foreach (var name in CategoryNames)
        {
            var cost = snapshot.ReadMsByCategory.FirstOrDefault(c => c.Category == name);
            categories.Append($"{name}={cost?.ReadMs ?? 0.0:0.0}ms/{cost?.Reads ?? 0} ");
        }

        return
        [
            LogLinePrefix
                + $"captures/s={snapshot.CapturesPerSec:0.0} emits/s={snapshot.EmitsPerSec:0.0} "
                + $"avg_capture_ms={snapshot.AvgCaptureMs:0.000} max_capture_ms={snapshot.MaxCaptureMs:0.000} "
                + $"producer_busy%={snapshot.ProducerBusyPct:0.0} nodes={snapshot.Nodes} "
                + $"suppress_windows={snapshot.SuppressWindows} suppress_drops={snapshot.SuppressDrops} "
                + $"suppress_opacity_windows={snapshot.SuppressOpacityWindows} suppress_opacity_drops={snapshot.SuppressOpacityDrops}",
            LogLinePrefix
                + $"nodes_read={snapshot.NodesRead} skel_elided={snapshot.SkelElided} "
                + $"prefix_refresh_ms={snapshot.PrefixRefreshMs:0.000}/{snapshot.PrefixRefreshes} "
                + $"prefix_chains={snapshot.PrefixChains} read_ms_by_cat: {categories}",
            JsonLinePrefix + ToJson(snapshot),
        ];
    }

    /// <summary>Compact single-line snapshot JSON (the payload of the <c>[scene-watch][profile-json]</c> line).</summary>
    public static string ToJson(Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return JsonSerializer.Serialize(snapshot, SnapshotJsonOptions);
    }

    /// <summary>Parse one snapshot JSON payload; null when it is not a readable snapshot.</summary>
    public static Snapshot? FromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Snapshot>(json, SnapshotJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Recover every snapshot from a captured game log. Only the machine-readable
    /// <c>[scene-watch][profile-json]</c> lines are read (any launcher/timestamp prefix on the line is
    /// tolerated); the two human lines are for humans and are deliberately NOT reverse-engineered, so a report
    /// never carries a value that was reconstructed rather than measured.
    /// </summary>
    public static IReadOnlyList<Snapshot> ParseLog(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var snapshots = new List<Snapshot>();
        foreach (var line in lines)
        {
            if (line is null)
            {
                continue;
            }

            var marker = line.IndexOf(JsonLinePrefix, StringComparison.Ordinal);
            if (marker < 0)
            {
                continue;
            }

            var payload = line[(marker + JsonLinePrefix.Length)..].Trim();
            var snapshot = FromJson(payload);
            if (snapshot is not null)
            {
                snapshots.Add(snapshot);
            }
        }

        return snapshots;
    }

    /// <summary>
    /// Map the measured windows into the shared <c>perf-report/1</c> envelope, under the <c>producer-walk</c>
    /// profile. Metric names deliberately stay recognisable as the watcher's own counters so a report can be
    /// read next to the raw log lines.
    /// <para>
    /// <c>metrics</c> is the FOLD of every window; <c>runs</c> carries one entry per window, i.e. one entry per
    /// measured sample (the shared validator rejects an empty <c>runs</c> array). An empty
    /// <paramref name="windows"/> therefore produces an all-zero report with no runs — the honest shape of
    /// "nothing was measured", which that same validator rejects on purpose.
    /// </para>
    /// </summary>
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

        // HOW the shared `cpu` block was obtained, stamped on every report (cloned, so a caller's params object is
        // never mutated). It rides in `params` rather than in `metrics` because the validator's anti-degeneracy
        // rule only looks at numeric leaves, and this is the sentence that stops a reader treating an on-thread
        // wall number as a scheduler CPU number.
        var stamped = parameters is null ? [] : (JsonObject)parameters.DeepClone();
        stamped["cpuSource"] = CpuSource;
        stamped["cpuCaveat"] = CpuCaveat;

        return Sts2PerfReportEnvelope.Build(
            ProfileName,
            scenario,
            env,
            stamped,
            BuildMetrics(folded),
            // One profiler window is one measured repeat. 0 means nothing was captured — the report is emitted
            // anyway (all-zero, with `params.source` saying why) rather than faking a run.
            repeats: windows.Count,
            warmups: 0,
            runs: runs);
    }

    /// <summary>Single-window convenience overload (one measured sample).</summary>
    public static JsonObject BuildReport(
        Snapshot snapshot,
        Sts2PerfReportEnvelope.ReportEnv env,
        JsonObject? parameters = null,
        string scenario = ScenarioName)
        => BuildReport(snapshot.HasData ? [snapshot] : [], env, parameters, scenario);

    /// <summary>
    /// The SHARED cross-repo <c>cpu</c> block, in the naming every <c>perf-report/1</c> profile uses so
    /// "CPU per unit of work" can be read across repos without unit translation:
    /// <c>{ windowMs, totalCpuMs, totalCoreRatio, cpuCoverage, byProcess{…}, byThread[…] }</c>.
    /// <para>
    /// NOT a second measurement. Every number here is a counter the watcher already accumulated, re-spelled:
    /// <c>windowMs</c>/<c>wallMs</c> are <see cref="Snapshot.WindowMs"/> (the window's measured wall clock),
    /// <c>totalCpuMs</c>/<c>cpuMs</c> are <see cref="Snapshot.BusyMs"/> (the Stopwatch time summed per capture
    /// INSIDE <c>Capture()</c>), and the ratios are <c>BusyMs / WindowMs</c> — i.e. exactly
    /// <see cref="Snapshot.ProducerBusyPct"/> / 100. Nothing is back-computed from the percentage: the ms come
    /// from the timer, and the ratio comes from the ms.
    /// </para>
    /// <para>
    /// CAVEAT, stated because this block is meant to be read next to a browser <c>byThread</c> entry (which
    /// carries scheduler CPU time): <c>cpuMs</c> is ON-THREAD WALL time inside the walk, not thread CPU time.
    /// The walk is a synchronous tree read with no I/O and no blocking wait, so the two coincide except when the
    /// thread is preempted or a GC pause lands inside a capture — both of which INFLATE this number. Treat it as
    /// an upper bound on the walk's CPU, never an under-report — note this is the OPPOSITE direction from a
    /// browser block, whose <c>totalCpuMs</c> is a lower bound. The same sentence is carried in
    /// <c>params.cpuCaveat</c> of every report so it reaches a reader who never opens this file.
    /// </para>
    /// <para>
    /// <c>byProcess</c> is a single real entry, not the empty object that would technically satisfy the shared
    /// validator: the walk ran in exactly one process, and <c>{}</c> would read as "no process burned CPU".
    /// Returns FALSE when the snapshot carries no <see cref="Snapshot.ProcessName"/> (a log captured before that
    /// stamp existed) — the contract requires a named process on every row, and there is no honest name to put
    /// there, so the whole block is withheld with a reason rather than keyed on an invented one.
    /// </para>
    /// <para>
    /// No <c>gpu</c> block, ever: a producer walk touches no GPU, and a fabricated zero would be
    /// indistinguishable from a real measurement of an idle one. The shared validator rejects one outright.
    /// </para>
    /// </summary>
    public static bool TryBuildCpuBlock(Snapshot snapshot, out JsonObject? cpu, out string? omittedReason)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cpu = null;

        if (string.IsNullOrEmpty(snapshot.ProcessName))
        {
            omittedReason =
                "the snapshot carries no process name (a profiler log captured before the stamp existed), and "
                + "every cpu row must name the process it was measured in — there is no honest name to use here";
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
                    // One walk, on one thread, in one process — counted, not assumed: the walk is a synchronous
                    // pass and the counters above come from exactly one caller of Capture().
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

    // Attach the shared cpu block, or the reason there is none. Used for `metrics` and for every `runs` entry, so
    // the two never disagree about what a missing block means.
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

    private static JsonObject BuildMetrics(Snapshot snapshot)
    {
        var byCategory = new JsonObject();
        foreach (var name in CategoryNames)
        {
            var cost = snapshot.ReadMsByCategory.FirstOrDefault(c => c.Category == name);
            byCategory[name] = new JsonObject
            {
                ["readMs"] = cost?.ReadMs ?? 0.0,
                ["reads"] = cost?.Reads ?? 0,
            };
        }

        var metrics = new JsonObject
        {
            ["windowMs"] = snapshot.WindowMs,
            ["windows"] = snapshot.Windows,
            ["captures"] = snapshot.Captures,
            ["emits"] = snapshot.Emits,
            ["busyMs"] = snapshot.BusyMs,
            ["avgCaptureMs"] = snapshot.AvgCaptureMs,
            ["maxCaptureMs"] = snapshot.MaxCaptureMs,
            ["p50CaptureMs"] = snapshot.P50CaptureMs,
            ["p95CaptureMs"] = snapshot.P95CaptureMs,
            ["p99CaptureMs"] = snapshot.P99CaptureMs,
            ["captureMsSampleCount"] = snapshot.CaptureMsSampleCount,
            ["capturesPerSec"] = snapshot.CapturesPerSec,
            ["emitsPerSec"] = snapshot.EmitsPerSec,
            ["producerBusyPct"] = snapshot.ProducerBusyPct,
            ["nodes"] = snapshot.Nodes,
            ["nodesRead"] = snapshot.NodesRead,
            ["skelElided"] = snapshot.SkelElided,
            ["prefixRefreshMs"] = snapshot.PrefixRefreshMs,
            ["prefixRefreshes"] = snapshot.PrefixRefreshes,
            ["prefixChains"] = snapshot.PrefixChains,
            ["readMsByCategory"] = byCategory,
            ["suppressCounters"] = new JsonObject
            {
                ["windows"] = snapshot.SuppressWindows,
                ["drops"] = snapshot.SuppressDrops,
                ["opacityWindows"] = snapshot.SuppressOpacityWindows,
                ["opacityDrops"] = snapshot.SuppressOpacityDrops,
            },
        };

        // The same BusyMs/WindowMs pair as `busyMs`/`producerBusyPct` above, in the shared cross-repo naming.
        AttachCpu(metrics, snapshot);
        return metrics;
    }

    // One measured sample: the ~1s profiler window, flattened. Deliberately a compact row rather than the whole
    // metrics block — a 5-minute capture is 300 of these and they exist to be scanned/plotted, not re-folded.
    private static JsonObject BuildRun(int index, Snapshot window)
    {
        var run = new JsonObject
        {
            ["index"] = index,
            ["windowMs"] = window.WindowMs,
            ["captures"] = window.Captures,
            ["emits"] = window.Emits,
            ["busyMs"] = window.BusyMs,
            ["avgCaptureMs"] = window.AvgCaptureMs,
            ["maxCaptureMs"] = window.MaxCaptureMs,
            ["p50CaptureMs"] = window.P50CaptureMs,
            ["p95CaptureMs"] = window.P95CaptureMs,
            ["p99CaptureMs"] = window.P99CaptureMs,
            ["capturesPerSec"] = window.CapturesPerSec,
            ["emitsPerSec"] = window.EmitsPerSec,
            ["producerBusyPct"] = window.ProducerBusyPct,
            ["nodes"] = window.Nodes,
            ["nodesRead"] = window.NodesRead,
            ["skelElided"] = window.SkelElided,
        };

        // Full block (not a compacted variant) so `runs[i].cpu` and `metrics.cpu` are the same shape and validate
        // identically — a per-run block missing `byThread` would be a second, weaker contract.
        AttachCpu(run, window);
        return run;
    }

    /// <summary>
    /// Per-window accumulator for the watcher's hot path. NOT thread-safe by design: it is written only from the
    /// game main thread inside the capture loop, exactly where the <c>_prof*</c> fields it replaces were.
    /// </summary>
    public sealed class Accumulator
    {
        private readonly double[] _samples = new double[MaxSamplesPerWindow];
        private readonly double[] _categoryMs = new double[7];
        private readonly int[] _categoryReads = new int[7];
        private long _windowStartMs;
        private int _sampleWrite;
        private int _sampleCount;
        private int _captures;
        private int _emits;
        private double _busyMs;
        private double _maxCaptureMs;
        private int _nodesRead;
        private int _skelElided;
        private double _prefixRefreshMs;
        private int _prefixRefreshes;
        private int _suppressWindows;
        private int _suppressDrops;
        private int _suppressOpacityWindows;
        private int _suppressOpacityDrops;

        /// <summary>Wall-clock ms since the open window started; 0 before the first capture is recorded.</summary>
        public long ElapsedMs(long nowMs) => _windowStartMs == 0 ? 0 : nowMs - _windowStartMs;

        public void RecordCapture(long nowMs, double captureMs, bool emitted)
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

        /// <summary>Attribute one node's read+diff cost. <paramref name="categoryIndex"/> indexes <see cref="CategoryNames"/>.</summary>
        public void RecordNode(int categoryIndex, long stopwatchTicks)
        {
            _nodesRead++;
            if ((uint)categoryIndex >= (uint)_categoryMs.Length)
            {
                return;
            }

            _categoryMs[categoryIndex] += TicksToMs(stopwatchTicks);
            _categoryReads[categoryIndex]++;
        }

        public void RecordSkelElided() => _skelElided++;

        public void RecordPrefixRefresh(long stopwatchTicks)
        {
            _prefixRefreshMs += TicksToMs(stopwatchTicks);
            _prefixRefreshes++;
        }

        public void RecordSuppressWindow() => _suppressWindows++;

        public void RecordSuppressDrop() => _suppressDrops++;

        public void RecordSuppressOpacityWindow() => _suppressOpacityWindows++;

        public void RecordSuppressOpacityDrop() => _suppressOpacityDrops++;

        /// <summary>
        /// Close the window once it has covered <paramref name="minWindowMs"/>, returning the snapshot and
        /// RESETTING every counter (the next window starts at <paramref name="nowMs"/>). Returns false — and
        /// leaves the accumulator untouched — while the window is still open.
        /// </summary>
        public bool TryCloseWindow(long nowMs, long minWindowMs, int nodes, int prefixChains, out Snapshot snapshot)
        {
            var elapsed = ElapsedMs(nowMs);
            if (_windowStartMs == 0 || elapsed < minWindowMs)
            {
                snapshot = Snapshot.Empty;
                return false;
            }

            snapshot = Build(elapsed, nodes, prefixChains);
            Reset(nowMs);
            return true;
        }

        /// <summary>Read the open window without closing or resetting it (diagnostics/tests).</summary>
        public Snapshot Peek(long nowMs, int nodes, int prefixChains) => Build(ElapsedMs(nowMs), nodes, prefixChains);

        /// <summary>Drop everything accumulated so far and (re)start the window at <paramref name="windowStartMs"/>.</summary>
        public void Reset(long windowStartMs = 0)
        {
            _windowStartMs = windowStartMs;
            _sampleWrite = 0;
            _sampleCount = 0;
            _captures = 0;
            _emits = 0;
            _busyMs = 0;
            _maxCaptureMs = 0;
            _nodesRead = 0;
            _skelElided = 0;
            _prefixRefreshMs = 0;
            _prefixRefreshes = 0;
            _suppressWindows = 0;
            _suppressDrops = 0;
            _suppressOpacityWindows = 0;
            _suppressOpacityDrops = 0;
            Array.Clear(_categoryMs);
            Array.Clear(_categoryReads);
        }

        private Snapshot Build(long windowMs, int nodes, int prefixChains) => new()
        {
            WindowMs = windowMs,
            Windows = 1,
            Captures = _captures,
            Emits = _emits,
            BusyMs = _busyMs,
            MaxCaptureMs = _maxCaptureMs,
            Nodes = nodes,
            NodesRead = _nodesRead,
            SkelElided = _skelElided,
            PrefixRefreshMs = _prefixRefreshMs,
            PrefixRefreshes = _prefixRefreshes,
            PrefixChains = prefixChains,
            SuppressWindows = _suppressWindows,
            SuppressDrops = _suppressDrops,
            SuppressOpacityWindows = _suppressOpacityWindows,
            SuppressOpacityDrops = _suppressOpacityDrops,
            ReadMsByCategory = BuildCategoryCosts(_categoryMs, _categoryReads),
            CaptureMsSamples = SnapshotSamples(),
            ProcessName = CurrentProcessName,
        };

        // Oldest→newest order of the bounded ring.
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

    // ---- Retained window history -------------------------------------------------------------------------
    // The watcher publishes each closed window here from the game main thread; an embedder reads it from a
    // bridge diagnostic reader, so the queue is locked. Bounded, so a
    // long-running host cannot grow it without limit.

    private static readonly Lock RetainedGate = new();
    private static readonly Queue<Snapshot> RetainedWindows = new();
    private static bool _profilingEnabled;

    /// <summary>True once a live watcher has reported that its self-profiler is enabled.</summary>
    public static bool ProfilingEnabled
    {
        get
        {
            lock (RetainedGate)
            {
                return _profilingEnabled;
            }
        }
    }

    /// <summary>Called by the watcher at construction so an embedder can tell "profiler off" from "no data yet".</summary>
    public static void MarkProfilingEnabled(bool enabled)
    {
        lock (RetainedGate)
        {
            _profilingEnabled = enabled;
        }
    }

    /// <summary>Retain one closed window, evicting the oldest beyond <see cref="MaxRetainedWindows"/>.</summary>
    public static void Publish(Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (RetainedGate)
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
    /// <paramref name="reset"/> clears the history afterwards so the next read measures a fresh interval (the
    /// A/B loop: flip a lever, reset, play, read).
    /// </summary>
    public static IReadOnlyList<Snapshot> TakeWindows(int limit = 0, bool reset = false)
    {
        lock (RetainedGate)
        {
            var windows = RetainedWindows.ToArray();
            if (reset)
            {
                RetainedWindows.Clear();
            }

            if (limit > 0 && windows.Length > limit)
            {
                windows = windows[^limit..];
            }

            return windows;
        }
    }

    /// <summary>Drop the retained history and the profiling flag (test isolation / a fresh A/B interval).</summary>
    public static void ClearRetained()
    {
        lock (RetainedGate)
        {
            RetainedWindows.Clear();
            _profilingEnabled = false;
        }
    }

    private static IReadOnlyList<CategoryCost> BuildCategoryCosts(IReadOnlyList<double> categoryMs, IReadOnlyList<int> reads)
    {
        var costs = new List<CategoryCost>(CategoryNames.Count);
        for (var i = 0; i < CategoryNames.Count; i++)
        {
            costs.Add(new CategoryCost(
                CategoryNames[i],
                i < categoryMs.Count ? categoryMs[i] : 0.0,
                i < reads.Count ? reads[i] : 0));
        }

        return costs;
    }

    private static int IndexOfCategory(string category)
    {
        for (var i = 0; i < CategoryNames.Count; i++)
        {
            if (string.Equals(CategoryNames[i], category, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static double TicksToMs(long ticks) => 1000.0 * ticks / Stopwatch.Frequency;

    // Read ONCE: Process.GetCurrentProcess() allocates and touches /proc, and a window closes ~1/s in the live
    // host's capture loop. Best-effort — a host that denies process introspection leaves the field null, which
    // the report renders as "no process recorded" rather than as a placeholder name.
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
