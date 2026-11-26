using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

public sealed class Sts2LiveIntrospectionTests
{
    [Fact]
    public void ExactArityWinsOverOptionalOverloadWithoutReportingDrift()
    {
        var target = new ExactTarget();
        Assert.Equal("exact", Sts2LiveIntrospection.InvokeMethod(target, "Read", 7));
        Assert.DoesNotContain(Sts2CompatibilityReport.Process.Snapshot(), issue =>
            issue.Member == $"{typeof(ExactTarget).FullName}.Read");
    }

    [Fact]
    public void SingleOptionalOverloadSuppliesAllDefaultsForInvokeAndTryInvoke()
    {
        var target = new OptionalTarget();
        Assert.Equal("7:True:default", Sts2LiveIntrospection.InvokeMethod(target, "Read", 7));
        Assert.True(Sts2LiveIntrospection.TryInvokeMethod(target, "Read", 9));
        Assert.Equal("9:True:default", target.Last);
        Assert.Equal("10:False:custom", Sts2LiveIntrospection.InvokeMethod(target, "Read", 10, false, "custom"));
        var issue = Assert.Single(Sts2CompatibilityReport.Process.Snapshot().Where(issue =>
            issue.Member == $"{typeof(OptionalTarget).FullName}.Read"));
        Assert.Equal("method_signature_miss", issue.Code);
        Assert.Contains("optional trailing", issue.Reason);
    }

    [Fact]
    public void ParameterlessFallbackCanUseAnOptionalOverload()
    {
        var target = new OptionalTarget();
        Assert.True(Sts2LiveIntrospection.TryInvokeParameterlessMethod(target, "Refresh"));
        Assert.Equal("refresh", target.Last);
    }

    [Theory]
    [InlineData("Ambiguous", true)]
    [InlineData("Required", true)]
    [InlineData("WrongType", true)]
    [InlineData("Absent", false)]
    public void UnsafeFallbackIsNotInvokedAndOnlyExistingNamesReportDrift(string method, bool reportsDrift)
    {
        var target = new UnsafeTarget();
        Assert.Null(Sts2LiveIntrospection.InvokeMethod(target, method, 7));
        Assert.False(Sts2LiveIntrospection.TryInvokeMethod(target, method, 7));
        Assert.Equal(0, target.Calls);
        var issues = Sts2CompatibilityReport.Process.Snapshot().Where(issue =>
            issue.Member == $"{typeof(UnsafeTarget).FullName}.{method}");
        Assert.Equal(reportsDrift ? 1 : 0, issues.Count());
    }

    [Fact]
    public void OptionalNameProbesWithNoMatchingNamesDoNotReportDrift()
    {
        var target = new ExactTarget();
        Assert.False(Sts2LiveIntrospection.TryInvokeParameterlessMethod(target,
            "OnUnfocus", "OnBlur", "OnUnhover", "OnMouseExit"));
        Assert.DoesNotContain(Sts2CompatibilityReport.Process.Snapshot(), issue =>
            issue.Member.StartsWith($"{typeof(ExactTarget).FullName}.On", StringComparison.Ordinal));
    }

    [Fact]
    public void ExactHitDoesNotAllocateReflectionOrDiagnosticStateAfterWarmup()
    {
        var target = new ExactTarget();
        object?[] arguments = [7];
        for (var i = 0; i < 20; i++)
        {
            Sts2LiveIntrospection.InvokeMethod(target, "Read", arguments);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            Sts2LiveIntrospection.InvokeMethod(target, "Read", arguments);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private sealed class ExactTarget
    {
        public string Read(int _) => "exact";
        public string Read(int _, bool _1 = true) => "optional";
    }

    private sealed class OptionalTarget
    {
        public string? Last { get; private set; }
        public string Read(int value, bool enabled = true, string label = "default")
            => Last = $"{value}:{enabled}:{label}";
        public void Refresh(bool enabled = true) => Last = enabled ? "refresh" : "disabled";
    }

    private sealed class UnsafeTarget
    {
        public int Calls { get; private set; }
        public void Ambiguous(int _, bool _1 = false) => Calls++;
        public void Ambiguous(int _, string _1 = "") => Calls++;
        public void Required(int _, bool _1) => Calls++;
        public void WrongType(string _, bool _1 = false) => Calls++;
    }
}
