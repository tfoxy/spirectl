using Spirectl.Proto.V0;
using Spirectl.Sts2;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2.Live.GameApi;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

public sealed class Sts2CompatibilityTests
{
    private static GameApiRequirement Missing(bool critical = false) => new(
        typeof(string), "NoSuchMember", GameApiMemberKind.Value,
        Note: "the dependent test feature is unavailable", Critical: critical);

    [Fact]
    public void NonCriticalMissingMemberAllowsCompositionAndReachesTheHandshake()
    {
        var report = new Sts2CompatibilityReport("test-lane", "test-build");
        var log = new InMemoryLogStream();
        Sts2GameApiRequirements.Verify([Missing()], report, log);

        using var runtime = BridgeRuntimeBootstrap.CreateScaffold();
        runtime.CompatibilityReport = report;
        var protocol = new BridgeRuntimeProtocolAdapter(runtime);
        var handshake = protocol.HandleHandshake(new HandshakeRequest());

        Assert.NotNull(handshake.Success);
        var issue = Assert.Single(report.Snapshot());
        Assert.False(issue.Critical);
        var diagnostic = handshake.Success.Diagnostic.Fields["degraded"].ListValue.Values.Single().StructValue;
        Assert.Equal("missing_game_member", diagnostic.Fields["code"].StringValue);
        Assert.Equal("System.String.NoSuchMember", diagnostic.Fields["member"].StringValue);
        Assert.Equal("test-lane", diagnostic.Fields["lane"].StringValue);
        Assert.Equal("test-build", diagnostic.Fields["gameBuild"].StringValue);
        Assert.Contains("dependent test feature", diagnostic.Fields["reason"].StringValue);
        Assert.False(diagnostic.Fields["critical"].BoolValue);
    }

    [Fact]
    public void CriticalMissingMemberRefusesAndRecordsTheReason()
    {
        var report = new Sts2CompatibilityReport("test-lane", "test-build");
        var exception = Assert.Throws<InvalidOperationException>(() =>
            Sts2GameApiRequirements.Verify([Missing(critical: true)], report, new InMemoryLogStream()));

        Assert.Contains("refuses to start", exception.Message);
        Assert.Contains("test-lane", exception.Message);
        Assert.Contains("test-build", exception.Message);
        Assert.Contains("System.String.NoSuchMember", exception.Message);
        Assert.True(Assert.Single(report.Snapshot()).Critical);
    }

    [Fact]
    public void MissingMemberLogsOnceAndSurvivesLogEviction()
    {
        var report = new Sts2CompatibilityReport("test-lane", "test-build");
        var log = new InMemoryLogStream(capacity: 2);
        Sts2GameApiRequirements.Verify([Missing()], report, log);
        Sts2GameApiRequirements.Verify([Missing()], report, log);

        var warning = Assert.Single(log.Read(new LogQuery(10, null, BridgeLogLevel.Warn, null)).Entries);
        Assert.Equal("bridge.game-api", warning.Target);
        Assert.Contains("test-lane", warning.Message);
        Assert.Contains("test-build", warning.Message);
        log.Write(BridgeLogLevel.Info, "test", "one");
        log.Write(BridgeLogLevel.Info, "test", "two");
        Assert.Empty(log.Read(new LogQuery(10, null, BridgeLogLevel.Warn, null)).Entries);
        Assert.Single(report.Snapshot());
    }

    [Fact]
    public void ResolvedMemberDoesNotDegradeTheBridge()
    {
        var report = new Sts2CompatibilityReport();
        Sts2GameApiRequirements.Verify(
            [new(typeof(string), nameof(string.StartsWith), GameApiMemberKind.Method, [typeof(string)])],
            report, new InMemoryLogStream());

        Assert.Empty(report.Snapshot());
        Assert.Null(report.DescribeDiagnostic());
    }

    [Fact]
    public void UnloadableMemberDegradesInsteadOfEscapingTheProbe()
    {
        var report = new Sts2CompatibilityReport();
        Sts2GameApiRequirements.Verify(
            [new(null, "member", GameApiMemberKind.Value, OwnerTypeName: "owner")], report, new InMemoryLogStream(),
            _ => throw new TypeLoadException("unavailable type"));

        Assert.Equal("owner.member", Assert.Single(report.Snapshot()).Member);
    }

    [Fact]
    public void ThrowingHookFamilyDoesNotPreventOtherFamiliesFromInstalling()
    {
        var report = new Sts2CompatibilityReport("test-lane", "test-build");
        var log = new InMemoryLogStream();
        var hooks = new Sts2CompatibilityHookInstaller(report, log);
        var installed = new List<string>();
        hooks.Install("before", _ => installed.Add("before"));
        hooks.Install("broken", _ => throw new MissingMethodException("a hook target moved"));
        hooks.Install("after", _ => installed.Add("after"));

        Assert.Equal(["before", "after"], installed);
        var issue = Assert.Single(report.Snapshot());
        Assert.Equal("hook_install_failed", issue.Code);
        Assert.Equal("broken", issue.Member);
        Assert.Equal("test-lane", issue.Lane);
        Assert.Equal("test-build", issue.GameBuild);
        Assert.Contains("MissingMethodException", issue.Reason);
        Assert.Contains("a hook target moved", issue.Reason);
    }

    [Theory]
    [InlineData("System.String.NoSuchMember")]
    [InlineData("System.String.NoSuchMember(Player)")]
    public void MissingHookDependencySkipsOnlyItsFamily(string missingMember)
    {
        var report = new Sts2CompatibilityReport();
        report.Record("missing_game_member", missingMember, "signature changed");
        var hooks = new Sts2CompatibilityHookInstaller(report, new InMemoryLogStream());
        var installed = new List<string>();
        hooks.Install("dependent", _ => installed.Add("dependent"), "System.String.NoSuchMember");
        hooks.Install("unrelated", _ => installed.Add("unrelated"), "System.String.OtherMember");
        hooks.Install("prefix", _ => installed.Add("prefix"), "System.String.NoSuch");
        hooks.Install("other-owner", _ => installed.Add("other-owner"), "Other.String.NoSuchMember");

        Assert.Equal(["unrelated", "prefix", "other-owner"], installed);
        var skipped = Assert.Single(report.Snapshot().Where(issue => issue.Code == "hook_install_skipped"));
        Assert.Equal("dependent", skipped.Member);
        Assert.Contains("System.String.NoSuchMember", skipped.Reason);
    }

    [Fact]
    public void ManifestLoadFailureInstallsNoHookFamilies()
    {
        var report = new Sts2CompatibilityReport();
        report.Record("manifest_load_failed", "GameApiManifest", "unavailable type");
        var hooks = new Sts2CompatibilityHookInstaller(report, new InMemoryLogStream());
        var installed = new List<string>();
        foreach (var family in new[] { "before", "dependent", "after" })
        {
            hooks.Install(family, _ => installed.Add(family));
        }

        Assert.Empty(installed);
        Assert.Equal(3, report.Snapshot().Count(issue => issue.Code == "hook_install_skipped"));
    }

    [Fact]
    public void InstallerWarningsAlsoReachTheReport()
    {
        var report = new Sts2CompatibilityReport();
        var hooks = new Sts2CompatibilityHookInstaller(report, new InMemoryLogStream());
        hooks.Install("partial", log =>
        {
            log.Write(BridgeLogLevel.Warn, "test", "target was not found");
            log.Write(BridgeLogLevel.Warn, "test", "another target was not found");
        });

        var issue = Assert.Single(report.Snapshot());
        Assert.Equal("hook_install_warning", issue.Code);
        Assert.Equal("partial", issue.Member);
        Assert.Equal("target was not found", issue.Reason);
    }

    [Fact]
    public void RetainedHookLoggerDoesNotTreatRuntimeWarningsAsInstallFailures()
    {
        var report = new Sts2CompatibilityReport();
        var log = new InMemoryLogStream();
        var hooks = new Sts2CompatibilityHookInstaller(report, log);
        ILogStream? retained = null;
        hooks.Install("working", stream => retained = stream);
        retained!.Write(BridgeLogLevel.Warn, "test", "runtime warning");

        Assert.Empty(report.Snapshot());
        Assert.Single(log.Read(new LogQuery(10, null, BridgeLogLevel.Warn, null)).Entries);
    }

    [Fact]
    public void StartupSummaryNamesBuildAndDegradationOnlyOnce()
    {
        var report = new Sts2CompatibilityReport("test-lane", "test-build");
        var log = new InMemoryLogStream();
        report.Record("missing_game_member", "member", "reason");
        report.WriteStartupSummary(log);
        report.WriteStartupSummary(log);

        var summary = Assert.Single(log.Read(new LogQuery(10, null, null, "bridge.game-api")).Entries);
        Assert.Equal(BridgeLogLevel.Warn, summary.Level);
        Assert.Contains("test-lane", summary.Message);
        Assert.Contains("test-build", summary.Message);
        Assert.Contains("degraded (1 issue(s))", summary.Message);
    }

    [Fact]
    public void LateReflectionFailuresUseTheConfiguredRuntimeLogOnce()
    {
        var report = new Sts2CompatibilityReport();
        var log = new InMemoryLogStream();
        report.SetGameBuild("test-lane", "test-build", log);
        report.Record("method_signature_miss", "owner.method", "signature changed");
        report.Record("method_signature_miss", "owner.method", "signature changed");

        var warning = Assert.Single(log.Read(new LogQuery(10, null, BridgeLogLevel.Warn, "bridge.game-api")).Entries);
        Assert.Contains("owner.method", warning.Message);
        Assert.Contains("test-lane", warning.Message);
        Assert.Contains("test-build", warning.Message);
    }

    [Fact]
    public void HandshakeReadsNewFailuresWithoutRestartingTheRuntime()
    {
        var report = new Sts2CompatibilityReport();
        using var runtime = BridgeRuntimeBootstrap.CreateScaffold();
        runtime.CompatibilityReport = report;
        var protocol = new BridgeRuntimeProtocolAdapter(runtime);
        Assert.Null(protocol.HandleHandshake(new HandshakeRequest()).Success.Diagnostic);
        report.Record("missing_game_member", "member", "reason");

        Assert.Single(protocol.HandleHandshake(new HandshakeRequest()).Success.Diagnostic.Fields["degraded"].ListValue.Values);
    }
}
