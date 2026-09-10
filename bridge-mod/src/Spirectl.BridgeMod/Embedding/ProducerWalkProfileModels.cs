using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Spirectl.Sts2.Live;

namespace Spirectl.Sts2.Embedding;
/// <summary>
/// Ask the runtime for the live scene watcher's producer-walk profile (the counters the watcher already
/// accumulates under <c>SPIRECTL_SCENE_WATCH_PROFILE=1</c>).
/// </summary>
/// <param name="WindowLimit">Keep only the newest N retained ~1s windows (0 = every retained window).</param>
/// <param name="Reset">
/// Clear the retained history after reading, so the next dump measures a FRESH interval. This is the A/B loop:
/// flip a <c>SPIRECTL_SCENE_WATCH_*</c> lever, dump with <c>Reset</c>, play the same scenario, dump again.
/// </param>
/// <param name="Scenario">Report scenario name; defaults to <c>producer-walk</c>.</param>
/// <param name="EnvKind">
/// <c>env.kind</c>, one of the shared enum <c>ci</c> | <c>host</c> | <c>device</c>; defaults to
/// <see cref="ProducerWalkProfileReport.DefaultEnvKind"/>.
/// </param>
/// <param name="EnvLabel">Host/runtime label; defaults to the current OS + .NET description.</param>
/// <param name="Parameters">Extra <c>params</c> entries recorded alongside the levers (scenario notes, tree size, …).</param>
/// <param name="Profile">
/// WHICH producer to read. <c>producer-walk</c> (the default,
/// <see cref="Sts2ProducerWalkProfile.ProfileName"/>) is the live scene-delta watcher's walk; <c>state-watch</c>
/// (<see cref="Sts2StateWatchProfile.ProfileName"/>) is the semantic state hub's capture pacing and cost —
/// captures/s, emits/s, busy ms, the backoff skips and revision wakes, and the fingerprint's share of the work.
/// An unknown value fails the dump with a reason rather than quietly returning the other profile's numbers.
/// <para>
/// Both dumps report themselves as <c>profile: "producer-walk"</c> in the envelope, because <c>perf-report/1</c>
/// fixes that field to a four-value enum godot-scene-web owns; <c>scenario</c> is what separates them.
/// </para>
/// </param>
public sealed record ProducerWalkProfileRequest(
    int WindowLimit = 0,
    bool Reset = false,
    string Scenario = Sts2ProducerWalkProfile.ScenarioName,
    string? EnvKind = null,
    string? EnvLabel = null,
    IReadOnlyDictionary<string, string>? Parameters = null,
    string Profile = Sts2ProducerWalkProfile.ProfileName);

/// <summary>
/// The producer-walk profile dump. <see cref="ReportJson"/> is always a schema-valid
/// <c>perf-report/1</c> envelope — when nothing was measured it is an all-zero report whose
/// <c>params.source</c> says why, never a fabricated run.
/// </summary>
/// <param name="Available">True when at least one profiler window was retained.</param>
/// <param name="UnavailableReason">Why there is no data (profiler off / no window closed yet).</param>
/// <param name="Snapshot">The folded counters.</param>
/// <param name="Windows">How many ~1s profiler windows were folded in.</param>
/// <param name="ReportJson">Compact single-line <c>perf-report/1</c> envelope.</param>
/// <param name="StateWatchSnapshot">
/// The state-hub counters, present only for a <c>state-watch</c> dump. <see cref="Snapshot"/> still carries the
/// fields the two profiles share (window, captures, emits, busy/max ms, the capture-ms samples), projected from
/// this one; the counters with no producer-walk analogue — <c>SkippedByBackoff</c>, <c>RevisionWakes</c>,
/// <c>FingerprintMs</c> — live only here and in <see cref="ReportJson"/>.
/// </param>
public sealed record ProducerWalkProfileResult(
    bool Available,
    string? UnavailableReason,
    Sts2ProducerWalkProfile.Snapshot Snapshot,
    int Windows,
    string ReportJson,
    Sts2StateWatchProfile.Snapshot? StateWatchSnapshot = null);

/// <summary>
/// Builds a <see cref="ProducerWalkProfileResult"/> from bridge-owned retained diagnostic windows. Without a
/// live host it returns a schema-valid empty report explaining that no producer walk ran.
/// </summary>
public static class ProducerWalkProfileReport
{
    /// <summary><c>params.source</c> for a dump taken from a live in-process watcher.</summary>
    public const string LiveSource = "live-runtime";

    /// <summary><c>params.source</c> when no window was ever retained.</summary>
    public const string UnavailableSource = "unavailable";

    public static ProducerWalkProfileResult Capture(ProducerWalkProfileRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.Equals(request.Profile, Sts2StateWatchProfile.ProfileName, StringComparison.Ordinal))
        {
            return CaptureStateWatch(request);
        }

        if (!string.IsNullOrWhiteSpace(request.Profile)
            && !string.Equals(request.Profile, Sts2ProducerWalkProfile.ProfileName, StringComparison.Ordinal))
        {
            // Named a profile that does not exist. Returning the default one's numbers under the requested name
            // would be the worst possible answer: a real-looking measurement of the wrong thing.
            var unknownEnv = new Sts2PerfReportEnvelope.ReportEnv(
                request.EnvKind ?? DefaultEnvKind(),
                request.EnvLabel ?? DefaultEnvLabel());
            return new ProducerWalkProfileResult(
                false,
                $"'{request.Profile}' is not a profile this runtime measures; expected "
                    + $"'{Sts2ProducerWalkProfile.ProfileName}' or '{Sts2StateWatchProfile.ProfileName}'.",
                Sts2ProducerWalkProfile.Snapshot.Empty,
                0,
                Sts2PerfReportEnvelope.Serialize(Sts2ProducerWalkProfile.BuildReport(
                    Sts2ProducerWalkProfile.Snapshot.Empty,
                    unknownEnv,
                    BuildParams(UnavailableSource, 0, false, request.Parameters))));
        }

        var windows = Sts2ProducerWalkProfile.TakeWindows(request.WindowLimit, request.Reset);
        var snapshot = Sts2ProducerWalkProfile.Aggregate(windows);
        var profilingEnabled = Sts2ProducerWalkProfile.ProfilingEnabled;
        var available = windows.Count > 0;
        var reason = available
            ? null
            : profilingEnabled
                ? "The producer-walk profiler is enabled but no ~1s window has closed yet; let the live scene stream run and dump again."
                : $"The producer-walk profiler is off. Start the live host with {Sts2ProducerWalkProfile.ProfileEnvVar}=1 and subscribe to the scene-delta stream.";

        var parameters = BuildParams(
            available ? LiveSource : UnavailableSource,
            windows.Count,
            profilingEnabled,
            request.Parameters);

        var env = new Sts2PerfReportEnvelope.ReportEnv(
            request.EnvKind ?? DefaultEnvKind(),
            request.EnvLabel ?? DefaultEnvLabel());

        // The per-window list (not just the fold) so `runs` carries one entry per measured sample.
        var report = Sts2ProducerWalkProfile.BuildReport(
            windows,
            env,
            parameters,
            string.IsNullOrWhiteSpace(request.Scenario) ? Sts2ProducerWalkProfile.ScenarioName : request.Scenario);

        return new ProducerWalkProfileResult(
            available,
            reason,
            snapshot,
            windows.Count,
            Sts2PerfReportEnvelope.Serialize(report));
    }

    /// <summary>
    /// The <c>params</c> block: WHAT the numbers were measured under. <c>levers</c> is every effective
    /// <see cref="Sts2SceneWatchRuntimeSettings"/> value (an embedder can flip these at run time, so the seeded
    /// env var alone is not the truth) and <c>envOverrides</c> is every <c>SPIRECTL_*</c> variable actually set
    /// in the process — so a lever added later shows up in reports without touching this code.
    /// </summary>
    public static JsonObject BuildParams(
        string source,
        int windows,
        bool profilingEnabled,
        IReadOnlyDictionary<string, string>? extra = null)
    {
        var parameters = new JsonObject
        {
            ["source"] = source,
            ["windows"] = windows,
            ["profileEnabled"] = profilingEnabled,
            ["levers"] = new JsonObject
            {
                ["suppressTweenedTransforms"] = Sts2SceneWatchRuntimeSettings.SuppressTweenedTransforms,
                ["suppressTweenedOpacity"] = Sts2SceneWatchRuntimeSettings.SuppressTweenedOpacity,
                ["cancelKilledTweens"] = Sts2SceneWatchRuntimeSettings.CancelKilledTweens,
                ["cancelledRiseResync"] = Sts2SceneWatchRuntimeSettings.CancelledRiseResync,
                ["tweenImplicitStart"] = Sts2SceneWatchRuntimeSettings.TweenImplicitStart,
                ["emitLocalTransforms"] = Sts2SceneWatchRuntimeSettings.EmitLocalTransforms,
                ["emitReparents"] = Sts2SceneWatchRuntimeSettings.EmitReparents,
                ["reparentHoldTweened"] = Sts2SceneWatchRuntimeSettings.ReparentHoldTweened,
                ["restOverlayStream"] = Sts2SceneWatchRuntimeSettings.RestOverlayStream,
                ["restOverlayPromptDenylist"] = Sts2SceneWatchRuntimeSettings.RestOverlayPromptDenylist,
                ["tweenParentLive"] = Sts2SceneWatchRuntimeSettings.TweenParentLive,
                ["streamRichRoleFonts"] = Sts2SceneWatchRuntimeSettings.StreamRichRoleFonts,
                ["pruneUnmappedViewportContent"] = Sts2SceneWatchRuntimeSettings.PruneUnmappedViewportContent,
                ["honorStreamSkipMetadata"] = Sts2SceneWatchRuntimeSettings.HonorStreamSkipMetadata,
                ["refreshViewportPrefixes"] = Sts2SceneWatchRuntimeSettings.RefreshViewportPrefixes,
                ["line2DGeometryScope"] = Sts2SceneWatchRuntimeSettings.Line2DGeometryScope.ToString(),
            },
            ["envOverrides"] = SpirectlEnvOverrides(),
        };

        if (extra is not null)
        {
            foreach (var (key, value) in extra)
            {
                if (!string.IsNullOrWhiteSpace(key))
                {
                    parameters[key] = value;
                }
            }
        }

        return parameters;
    }

    /// <summary>Best-effort host/runtime label for <c>env.label</c>.</summary>
    public static string DefaultEnvLabel() =>
        $"{RuntimeInformation.OSDescription.Trim()} {RuntimeInformation.ProcessArchitecture} {RuntimeInformation.FrameworkDescription}";

    /// <summary>
    /// <c>env.kind</c> (one of <see cref="Sts2PerfReportEnvelope.EnvKinds"/>): <c>ci</c> under a CI runner,
    /// otherwise <c>host</c> — a live in-process dump comes out of a running game on someone's machine, which is
    /// exactly what <c>host</c> means. It is never <c>device</c>; that value belongs to a phone-side run.
    /// </summary>
    public static string DefaultEnvKind() =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CI"))
            ? Sts2PerfReportEnvelope.HostKind
            : Sts2PerfReportEnvelope.CiKind;

    /// <summary>
    /// The <c>state-watch</c> arm: the same retained-window fold over
    /// <see cref="Sts2StateWatchProfile"/>'s history, with the state-hub levers in <c>params.levers</c>.
    /// </summary>
    private static ProducerWalkProfileResult CaptureStateWatch(ProducerWalkProfileRequest request)
    {
        var windows = Sts2StateWatchProfile.TakeWindows(request.WindowLimit, request.Reset);
        var snapshot = Sts2StateWatchProfile.Aggregate(windows);
        var profilingEnabled = Sts2StateWatchProfile.ProfilingEnabled;
        var available = windows.Count > 0;
        var reason = available
            ? null
            : profilingEnabled
                ? "The state-watch profiler is enabled but no ~1s window has closed yet; let a state subscription "
                    + "run and dump again."
                : $"The state-watch profiler is off. Start the host with {Sts2StateWatchProfile.ProfileEnvVar}=1 "
                    + "(or set Sts2StateWatchRuntimeSettings.Profile) and subscribe to current state.";

        var parameters = BuildStateWatchParams(
            available ? LiveSource : UnavailableSource,
            windows.Count,
            profilingEnabled,
            request.Parameters);

        var env = new Sts2PerfReportEnvelope.ReportEnv(
            request.EnvKind ?? DefaultEnvKind(),
            request.EnvLabel ?? DefaultEnvLabel());

        // A caller that left Scenario at its producer-walk default meant "whatever this profile calls itself";
        // one that named a scenario meant it.
        var scenario = string.IsNullOrWhiteSpace(request.Scenario)
            || string.Equals(request.Scenario, Sts2ProducerWalkProfile.ScenarioName, StringComparison.Ordinal)
                ? Sts2StateWatchProfile.ScenarioName
                : request.Scenario;

        var report = Sts2StateWatchProfile.BuildReport(windows, env, parameters, scenario);

        return new ProducerWalkProfileResult(
            available,
            reason,
            snapshot.ToProducerWalkSnapshot(),
            windows.Count,
            Sts2PerfReportEnvelope.Serialize(report),
            snapshot);
    }

    /// <summary>
    /// <c>params</c> for a state-watch dump: the effective
    /// <see cref="Sts2StateWatchRuntimeSettings"/> levers (read live, not from the env vars that seeded them)
    /// plus every <c>SPIRECTL_*</c> variable set in the process, so an A/B pair diffs cleanly.
    /// </summary>
    public static JsonObject BuildStateWatchParams(
        string source,
        int windows,
        bool profilingEnabled,
        IReadOnlyDictionary<string, string>? extra = null)
    {
        var parameters = new JsonObject
        {
            ["source"] = source,
            ["windows"] = windows,
            ["profileEnabled"] = profilingEnabled,
            ["levers"] = Sts2StateWatchProfile.BuildLevers(),
            ["envOverrides"] = SpirectlEnvOverrides(),
        };

        if (extra is not null)
        {
            foreach (var (key, value) in extra)
            {
                if (!string.IsNullOrWhiteSpace(key))
                {
                    parameters[key] = value;
                }
            }
        }

        return parameters;
    }

    private static JsonObject SpirectlEnvOverrides()
    {
        var set = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name
                && name.StartsWith("SPIRECTL_", StringComparison.Ordinal)
                && entry.Value is string value)
            {
                set[name] = value;
            }
        }

        // Sorted, so two reports diff cleanly instead of on enumeration order.
        var overrides = new JsonObject();
        foreach (var (name, value) in set)
        {
            overrides[name] = value;
        }

        return overrides;
    }
}
