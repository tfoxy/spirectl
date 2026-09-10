using System.Text.Json.Nodes;
using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// WS-4: the shared cross-repo `perf-report/1` envelope. The coupling with godot-scene-web's harness
// (packages/perf-harness/src/report.ts) is the JSON SHAPE ONLY — no build dependency either way — so this file
// is spirectl's half of the contract: every key present, in the agreed spelling, with no threshold/verdict field.
public sealed class Sts2PerfReportEnvelopeTests
{
    [Fact]
    public void EnvelopeCarriesEveryAgreedTopLevelKey()
    {
        var report = Sts2PerfReportEnvelope.Build(
            "producer-walk",
            "producer-walk",
            new Sts2PerfReportEnvelope.ReportEnv("ci", "linux-x64 .NET"));

        // `profile` sits right after `repo`: the envelope SHELL is shared across repos, the metrics block is
        // per-profile, and this is the discriminator the shared validator selects it with.
        Assert.Equal(
            ["schema", "repo", "profile", "scenario", "env", "params", "repeats", "warmups", "metrics", "runs", "artifacts"],
            report.Select(pair => pair.Key));
        Assert.Equal("perf-report/1", (string?)report["schema"]);
        Assert.Equal("spirectl", (string?)report["repo"]);
        Assert.Equal("producer-walk", (string?)report["profile"]);
        Assert.Equal("producer-walk", (string?)report["scenario"]);
        Assert.Equal(1, (int?)report["repeats"]);
        Assert.Equal(0, (int?)report["warmups"]);
        Assert.Empty(report["params"]!.AsObject());
        Assert.Empty(report["metrics"]!.AsObject());
        Assert.Empty(report["runs"]!.AsArray());
        Assert.Empty(report["artifacts"]!.AsObject());
    }

    [Fact]
    public void EnvKeepsTheFourAgreedFieldsWithExplicitNullsForTheBrowserOnlyOnes()
    {
        var report = Sts2PerfReportEnvelope.Build(
            "producer-walk",
            "producer-walk",
            new Sts2PerfReportEnvelope.ReportEnv("ci", "host-label"));

        var env = report["env"]!.AsObject();
        Assert.Equal(["kind", "label", "cpuThrottle", "device"], env.Select(pair => pair.Key));
        Assert.Equal("ci", (string?)env["kind"]);
        Assert.Equal("host-label", (string?)env["label"]);
        // Present-but-null (not omitted): a host-side producer measurement has no throttle or device.
        Assert.Null(env["cpuThrottle"]);
        Assert.Null(env["device"]);
    }

    [Theory]
    [InlineData("ci")]
    [InlineData("host")]
    [InlineData("device")]
    public void EveryAgreedEnvKindIsEmittedVerbatim(string kind)
    {
        // env.kind is an ENUM in the shared validator, and each value is emitted as-is — there is no mapping
        // layer and no shadow field. `host` is the one this repo's live captures use: they come out of a
        // running game on a developer's machine, which is neither a harness run nor a phone.
        var env = Sts2PerfReportEnvelope
            .Build("producer-walk", "producer-walk", new Sts2PerfReportEnvelope.ReportEnv(kind, "host-label"))["env"]!
            .AsObject();

        Assert.Equal(["kind", "label", "cpuThrottle", "device"], env.Select(pair => pair.Key));
        Assert.Equal(kind, (string?)env["kind"]);
    }

    [Theory]
    [InlineData("dev")]
    [InlineData("live")]
    [InlineData("Host")]
    public void AnEnvKindOutsideTheEnumIsRejectedRatherThanSilentlyEmitted(string kind)
    {
        // A word outside the enum drops the whole report on the floor over one field — this repo shipped `dev`
        // for a round and every envelope it produced was invalid. Same guard as `profile`, same reason.
        var error = Assert.Throws<ArgumentException>(() => Sts2PerfReportEnvelope.Build(
            "producer-walk",
            "producer-walk",
            new Sts2PerfReportEnvelope.ReportEnv(kind, "host-label")));

        Assert.Contains("host", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDefaultEnvKindIsHostOffACiRunnerAndCiOnOne()
    {
        // A live in-process dump is a running game on someone's box. Calling that `ci` was the workaround the
        // shared enum grew a third value to make unnecessary.
        Assert.Contains(
            Spirectl.Sts2.Embedding.ProducerWalkProfileReport.DefaultEnvKind(),
            Sts2PerfReportEnvelope.EnvKinds);
        Assert.Equal("host", Sts2PerfReportEnvelope.ReportEnv.Unknown.Kind);
    }

    [Fact]
    public void ThrottleAndDeviceRoundTripWhenTheyAreMeaningful()
    {
        var report = Sts2PerfReportEnvelope.Build(
            "browser-render",
            "producer-walk",
            new Sts2PerfReportEnvelope.ReportEnv("device", "pixel", CpuThrottle: 4, Device: "Pixel 7a"));

        var env = report["env"]!.AsObject();
        Assert.Equal(4d, (double?)env["cpuThrottle"]);
        Assert.Equal("Pixel 7a", (string?)env["device"]);
    }

    [Fact]
    public void ParamsMetricsRunsAndArtifactsAreCarriedThroughVerbatim()
    {
        var report = Sts2PerfReportEnvelope.Build(
            "producer-walk",
            "producer-walk",
            Sts2PerfReportEnvelope.ReportEnv.Unknown,
            parameters: new JsonObject { ["lever"] = "SPIRECTL_SCENE_WATCH_SUPPRESS_TWEENED=0" },
            metrics: new JsonObject { ["avgCaptureMs"] = 1.25 },
            repeats: 12,
            warmups: 2,
            runs: [new JsonObject { ["avgCaptureMs"] = 1.5 }],
            artifacts: new JsonObject { ["log"] = ".sts2/perf-reports/producer-walk.log" });

        Assert.Equal("SPIRECTL_SCENE_WATCH_SUPPRESS_TWEENED=0", (string?)report["params"]!["lever"]);
        Assert.Equal(1.25, (double?)report["metrics"]!["avgCaptureMs"]);
        Assert.Equal(12, (int?)report["repeats"]);
        Assert.Equal(2, (int?)report["warmups"]);
        Assert.Equal(1.5, (double?)report["runs"]!.AsArray()[0]!["avgCaptureMs"]);
        Assert.Equal(".sts2/perf-reports/producer-walk.log", (string?)report["artifacts"]!["log"]);
    }

    [Fact]
    public void EnvelopeHasNoThresholdOrVerdictField()
    {
        // Report-only by construction: an instrument reports numbers, it does not gate a build.
        var report = Sts2PerfReportEnvelope.Build("producer-walk", "producer-walk", Sts2PerfReportEnvelope.ReportEnv.Unknown);

        foreach (var forbidden in new[] { "threshold", "thresholds", "budget", "budgets", "verdict", "pass", "failed", "status" })
        {
            Assert.False(report.ContainsKey(forbidden));
        }
    }

    [Fact]
    public void SerializeIsSingleLineByDefaultAndIndentedOnRequest()
    {
        var report = Sts2PerfReportEnvelope.Build("producer-walk", "producer-walk", Sts2PerfReportEnvelope.ReportEnv.Unknown);

        var compact = Sts2PerfReportEnvelope.Serialize(report);
        Assert.DoesNotContain('\n', compact);
        Assert.StartsWith("{\"schema\":\"perf-report/1\"", compact);

        Assert.Contains('\n', Sts2PerfReportEnvelope.Serialize(report, indented: true));
    }

    [Fact]
    public void ScenarioIsRequired()
    {
        Assert.Throws<ArgumentException>(() =>
            Sts2PerfReportEnvelope.Build("producer-walk", "  ", Sts2PerfReportEnvelope.ReportEnv.Unknown));
    }

    [Theory]
    [InlineData("browser-render")]
    [InlineData("producer-walk")]
    [InlineData("wire-payload")]
    [InlineData("asset-render")]
    public void EveryAgreedProfileIsAccepted(string profile)
    {
        var report = Sts2PerfReportEnvelope.Build(profile, "scenario", Sts2PerfReportEnvelope.ReportEnv.Unknown);

        Assert.Equal(profile, (string?)report["profile"]);
    }

    [Fact]
    public void AnUnknownProfileIsRejectedRatherThanSilentlyEmitted()
    {
        // A typo here would produce a report the shared validator drops on the floor, so fail at the source.
        var error = Assert.Throws<ArgumentException>(() =>
            Sts2PerfReportEnvelope.Build("producer_walk", "scenario", Sts2PerfReportEnvelope.ReportEnv.Unknown));

        Assert.Contains("producer-walk", error.Message);
    }
}
