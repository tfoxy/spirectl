using System.Text.Json;
using System.Text.Json.Nodes;

namespace Spirectl.Sts2.Live;
/// <summary>
/// The SHARED cross-repo performance-report envelope (<c>perf-report/1</c>). PURE and Godot-free (like
/// <see cref="Sts2CombatBackgroundLayerSelection"/>) so the wire shape is unit-testable without the live host.
/// <para>
/// The envelope is co-owned with the browser-side harness in <c>godot-scene-web</c>
/// (<c>packages/perf-harness/src/report.ts</c>). The coupling is the JSON SHAPE ONLY — there is no build
/// dependency and no cross-repo import in either direction, so both sides carry their own writer and the shape
/// is pinned by tests on each side (<c>Sts2PerfReportEnvelopeTests</c> here).
/// </para>
/// <para>
/// Report-only by construction: an envelope carries numbers and the parameters they were measured under. It
/// deliberately has NO threshold/budget/verdict field — wall-clock pass/fail gates are forbidden in this repo,
/// and this is an instrument, not a gate.
/// </para>
/// </summary>
public static class Sts2PerfReportEnvelope
{
    /// <summary>The <c>schema</c> discriminator every envelope carries. Bump only for a breaking shape change.</summary>
    public const string Schema = "perf-report/1";

    /// <summary>The <c>repo</c> value for every envelope emitted from this repository.</summary>
    public const string Repo = "spirectl";

    /// <summary>
    /// The <c>profile</c> discriminator: WHICH measurement this is, and therefore which metric block the shared
    /// validator expects. The envelope SHELL is shared; the metrics are per-profile — a browser render report
    /// carries <c>initialRenderMs</c>/<c>frameCostMs</c>/…, a host-side producer walk carries none of those.
    /// </summary>
    public static readonly IReadOnlyList<string> Profiles =
        ["browser-render", "producer-walk", "wire-payload", "asset-render"];

    /// <summary>
    /// Where the numbers were measured. <paramref name="Kind"/> is one of <see cref="EnvKinds"/> — an enum, not a
    /// label — and <paramref name="Label"/> identifies the host/runtime (e.g. <c>linux-x64 .NET 10.0.110</c>).
    /// <paramref name="CpuThrottle"/> and <paramref name="Device"/> exist for the browser side's throttled/device
    /// runs and are null for a host-side producer measurement.
    /// </summary>
    public sealed record ReportEnv(string Kind, string? Label = null, double? CpuThrottle = null, string? Device = null)
    {
        /// <summary>An unattributed measurement from this process — i.e. a host machine, not a phone.</summary>
        public static readonly ReportEnv Unknown = new(HostKind);
    }

    /// <summary>A controlled harness run.</summary>
    public const string CiKind = "ci";

    /// <summary>
    /// A real process on a developer/host machine — what every producer-walk capture in this repo is, since the
    /// counters come out of a running game rather than a harness.
    /// </summary>
    public const string HostKind = "host";

    /// <summary>A physical phone.</summary>
    public const string DeviceKind = "device";

    /// <summary>
    /// The <c>env.kind</c> ENUM — a fixed set on the shared validator's side, not a free-form label:
    /// <list type="bullet">
    /// <item><c>ci</c> — a controlled harness run,</item>
    /// <item><c>host</c> — a real process on a developer/host machine, which is what every producer-walk
    /// capture in this repo is,</item>
    /// <item><c>device</c> — a physical phone (CPU throttling refused, battery/thermal block mandatory).</item>
    /// </list>
    /// <c>ci</c> and <c>host</c> are the same hardware class and follow the same rules; the split that decides
    /// comparability is local-box vs phone. Use <c>host</c> rather than <c>ci</c> for a live capture — labelling
    /// a running game's numbers "ci" is simply false.
    /// </summary>
    public static readonly IReadOnlyList<string> EnvKinds = ["ci", "host", "device"];

    private static readonly JsonSerializerOptions CompactOptions = new() { WriteIndented = false };
    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

    /// <summary>
    /// Build the envelope. <paramref name="parameters"/> records WHAT was measured (which
    /// <c>SPIRECTL_SCENE_WATCH_*</c> levers were set, tree size, capture source …) and
    /// <paramref name="metrics"/> the numbers. <paramref name="runs"/> / <paramref name="artifacts"/> default to
    /// the empty collections the shape requires (both keys are always present, never omitted).
    /// <para>
    /// Two shared-validator rules the caller — not this builder — must satisfy for a report to be ACCEPTED:
    /// <c>runs</c> must be NON-EMPTY (one entry per measured sample), and at least one numeric leaf of
    /// <c>metrics</c> must be greater than zero. An all-zero report with no runs is exactly the shape a
    /// measurement that measured NOTHING takes, and the validator rejects it on purpose. This builder still
    /// emits that shape on request, because "we measured nothing" is an honest thing to report — it just must
    /// never be passed off as a measurement.
    /// </para>
    /// </summary>
    public static JsonObject Build(
        string profile,
        string scenario,
        ReportEnv env,
        JsonObject? parameters = null,
        JsonObject? metrics = null,
        int repeats = 1,
        int warmups = 0,
        JsonArray? runs = null,
        JsonObject? artifacts = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(scenario);
        ArgumentNullException.ThrowIfNull(env);
        if (!Profiles.Contains(profile, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"'{profile}' is not a perf-report profile; expected one of {string.Join(", ", Profiles)}.",
                nameof(profile));
        }

        // Same guard as `profile`, for the same reason: env.kind is an ENUM on the validator's side, and a word
        // outside it drops the whole report on the floor over one field. This repo shipped `dev` for a round and
        // every producer-walk envelope it produced was invalid — fail at the source instead.
        if (!EnvKinds.Contains(env.Kind, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"'{env.Kind}' is not a perf-report env.kind; expected one of {string.Join(", ", EnvKinds)}.",
                nameof(env));
        }

        return new JsonObject
        {
            ["schema"] = Schema,
            ["repo"] = Repo,
            ["profile"] = profile,
            ["scenario"] = scenario,
            ["env"] = new JsonObject
            {
                ["kind"] = env.Kind,
                ["label"] = env.Label,
                ["cpuThrottle"] = env.CpuThrottle,
                ["device"] = env.Device,
            },
            ["params"] = parameters ?? [],
            ["repeats"] = repeats,
            ["warmups"] = warmups,
            ["metrics"] = metrics ?? [],
            ["runs"] = runs ?? [],
            ["artifacts"] = artifacts ?? [],
        };
    }

    /// <summary>
    /// Serialize an envelope. Compact (single-line) by default so a shell wrapper can slurp it as the last
    /// JSON line of a command's output; <paramref name="indented"/> for a diff-friendly artifact file.
    /// </summary>
    public static string Serialize(JsonObject report, bool indented = false)
    {
        ArgumentNullException.ThrowIfNull(report);
        return report.ToJsonString(indented ? IndentedOptions : CompactOptions);
    }
}
