using System.Text.Json;
using System.Text.Json.Nodes;
using Spirectl.Sts2.Live;

// Emit the producer-walk perf report (`perf-report/1`) from the live scene watcher's OWN profiler counters.
//
// This is the offline half of the instrument: the watcher prints one machine-readable
// `[scene-watch][profile-json]` line per ~1s window when SPIRECTL_SCENE_WATCH_PROFILE=1, and this tool folds the
// windows out of a captured game log into the shared envelope. Bridge-host diagnostics retain the same bounded
// counters; embedded runtimes intentionally expose no profiling API.
//
// REPORT ONLY: it never applies a threshold and never fails on a slow number. A non-zero exit means a BAD
// INVOCATION (unreadable input, bad argument) — not a regression.
//
// Prefer `scripts/validate.sh producer-walk-profile --json`, which wraps this.

const int ExitOk = 0;
const int ExitBadUsage = 2;

var logPaths = new List<string>();
var snapshotPaths = new List<string>();
var extraParams = new List<KeyValuePair<string, string>>();
var scenario = Sts2ProducerWalkProfile.ScenarioName;
string? envKind = null;
string? envLabel = null;
string? outPath = null;
var pretty = false;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--help" or "-h":
            PrintUsage();
            return ExitOk;
        case "--log":
            if (!TryTakeValue(args, ref i, out var log))
            {
                return Fail("--log requires a path");
            }

            logPaths.Add(log);
            break;
        case "--snapshot":
            if (!TryTakeValue(args, ref i, out var snapshotPath))
            {
                return Fail("--snapshot requires a path");
            }

            snapshotPaths.Add(snapshotPath);
            break;
        case "--scenario":
            if (!TryTakeValue(args, ref i, out var scenarioValue))
            {
                return Fail("--scenario requires a value");
            }

            scenario = scenarioValue;
            break;
        case "--env-kind":
            if (!TryTakeValue(args, ref i, out var kind))
            {
                return Fail("--env-kind requires a value");
            }

            // env.kind is an ENUM in the shared contract. Rejecting a bad one here (rather than letting the
            // envelope builder throw) turns "the report you just wrote is invalid" into a usage error.
            if (!Sts2PerfReportEnvelope.EnvKinds.Contains(kind, StringComparer.Ordinal))
            {
                return Fail($"--env-kind must be one of {string.Join(", ", Sts2PerfReportEnvelope.EnvKinds)}, got '{kind}'");
            }

            envKind = kind;
            break;
        case "--env-label":
            if (!TryTakeValue(args, ref i, out var label))
            {
                return Fail("--env-label requires a value");
            }

            envLabel = label;
            break;
        case "--param":
            if (!TryTakeValue(args, ref i, out var pair))
            {
                return Fail("--param requires a key=value pair");
            }

            var split = pair.IndexOf('=');
            if (split <= 0)
            {
                return Fail($"--param expects key=value, got '{pair}'");
            }

            extraParams.Add(new KeyValuePair<string, string>(pair[..split], pair[(split + 1)..]));
            break;
        case "--out":
            if (!TryTakeValue(args, ref i, out var output))
            {
                return Fail("--out requires a path");
            }

            outPath = output;
            break;
        case "--pretty":
            pretty = true;
            break;
        default:
            return Fail($"unknown argument: {args[i]}");
    }
}

var windows = new List<Sts2ProducerWalkProfile.Snapshot>();
foreach (var path in logPaths)
{
    if (!File.Exists(path))
    {
        return Fail($"profile log not found: {path}");
    }

    windows.AddRange(Sts2ProducerWalkProfile.ParseLog(File.ReadLines(path)));
}

foreach (var path in snapshotPaths)
{
    if (!File.Exists(path))
    {
        return Fail($"snapshot file not found: {path}");
    }

    var text = File.ReadAllText(path);
    JsonNode? node;
    try
    {
        node = JsonNode.Parse(text);
    }
    catch (JsonException ex)
    {
        return Fail($"snapshot file is not JSON: {path} ({ex.Message})");
    }

    // Accept a bare snapshot, an array of snapshots, or a seam result whose `snapshot` field holds one.
    var candidates = node switch
    {
        JsonArray array => array.Select(item => item?.ToJsonString()),
        JsonObject obj when obj["snapshot"] is JsonNode inner => [inner.ToJsonString()],
        not null => [node.ToJsonString()],
        _ => [],
    };
    foreach (var candidate in candidates)
    {
        var snapshot = candidate is null ? null : Sts2ProducerWalkProfile.FromJson(candidate);
        if (snapshot is null)
        {
            return Fail($"snapshot file does not contain a producer-walk snapshot: {path}");
        }

        windows.Add(snapshot);
    }
}

var source = windows.Count > 0
    ? logPaths.Count > 0 && snapshotPaths.Count == 0 ? "profile-log" : "snapshot"
    : "unavailable";

var parameters = new JsonObject
{
    ["source"] = source,
    ["windows"] = windows.Count,
    ["inputs"] = new JsonArray(logPaths.Concat(snapshotPaths).Select(p => (JsonNode)JsonValue.Create(p)).ToArray()),
};
if (windows.Count == 0)
{
    parameters["note"] =
        $"No producer-walk profiler windows were read. Run the live host with {Sts2ProducerWalkProfile.ProfileEnvVar}=1, "
        + "capture its log while the scene-delta stream is subscribed, and pass it with --log.";
}

foreach (var (key, value) in extraParams)
{
    parameters[key] = value;
}

// The per-window list (not just the fold) so `runs` carries one entry per measured sample.
var report = Sts2ProducerWalkProfile.BuildReport(
    windows,
    new Sts2PerfReportEnvelope.ReportEnv(
        // `host`, not `ci`: these counters come out of a running game on someone's machine. Only a CI runner
        // gets to call itself ci.
        envKind ?? (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CI"))
            ? Sts2PerfReportEnvelope.HostKind
            : Sts2PerfReportEnvelope.CiKind),
        envLabel ?? DefaultEnvLabel()),
    parameters,
    scenario);

if (outPath is not null)
{
    var directory = Path.GetDirectoryName(Path.GetFullPath(outPath));
    if (!string.IsNullOrEmpty(directory))
    {
        Directory.CreateDirectory(directory);
    }

    File.WriteAllText(outPath, Sts2PerfReportEnvelope.Serialize(report, indented: true) + Environment.NewLine);
}

// Always the LAST line of stdout, single-line unless --pretty, so a shell wrapper can slurp it.
Console.WriteLine(Sts2PerfReportEnvelope.Serialize(report, pretty));
return ExitOk;

static bool TryTakeValue(string[] argv, ref int index, out string value)
{
    if (index + 1 >= argv.Length)
    {
        value = string.Empty;
        return false;
    }

    index++;
    value = argv[index];
    return true;
}

static string DefaultEnvLabel() =>
    $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription.Trim()} "
    + $"{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture} "
    + $"{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}";

static int Fail(string message)
{
    Console.Error.WriteLine($"spirectl-perf-report: {message}");
    PrintUsage();
    return ExitBadUsage;
}

static void PrintUsage() => Console.Error.WriteLine(
    """
    Usage: spirectl-perf-report [--log <path>]... [--snapshot <path>]... [options]

    Folds the live scene watcher's producer-walk profiler windows into a `perf-report/1` envelope on stdout.

    Options:
      --log <path>       Captured game log containing [scene-watch][profile-json] lines (repeatable).
      --snapshot <path>  Snapshot JSON or an array of retained bridge profiler snapshots (repeatable).
      --scenario <name>  Report scenario name (default: producer-walk).
      --env-kind <kind>  env.kind: ci | host | device (default: ci under $CI, otherwise host).
      --env-label <text> env.label (default: OS + architecture + .NET description).
      --param <k=v>      Extra params entry, e.g. the A/B lever under test (repeatable).
      --out <path>       Also write an indented copy of the report to this file.
      --pretty           Print the report indented instead of single-line.

    Reports numbers only: it applies no thresholds and never fails because a number moved.
    A non-zero exit means a bad invocation.
    """);
