using Spirectl.Sts2.Core.SceneInspection;
using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// The scene watcher's fault handling (Sts2SceneWatchFaults.cs): the non-finite read checks the capture loop uses to
// keep a NaN/Infinity out of the stream, the backoff between retries of a capture that threw, the rate limiter on
// the fault log, and the log line formats the live QA leg greps godot.log for. All Godot-free; the watcher that
// applies them has no offline seam for the capture loop itself.
public sealed class Sts2SceneWatchFaultsTests
{
    private static RuntimeSceneTransform2DSnapshot Xform(double a, double b, double c, double d, double tx, double ty)
        => new(new RuntimeSceneVector2Snapshot(a, b), new RuntimeSceneVector2Snapshot(c, d), new RuntimeSceneVector2Snapshot(tx, ty));

    private static RuntimeSceneRect2Snapshot Rect(double x, double y, double w, double h)
        => new(new RuntimeSceneVector2Snapshot(x, y), new RuntimeSceneVector2Snapshot(w, h));

    private static RuntimeSceneTextPropertiesSnapshot Text(double? fontSize, RuntimeSceneColorSnapshot? color = null)
        => new(
            Text: "12:34", RawText: null, RichTextEnabled: false, Source: "label", DiagnosticSurface: "test", Font: null,
            FontSize: fontSize, LineHeight: null, TextColor: color, OutlineColor: null, OutlineSize: 2,
            Shadow: null, RichTextSpans: [], Notices: []);

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonFiniteComponent_AnywhereInATransform_IsNotFinite(double bad)
    {
        for (var i = 0; i < 6; i++)
        {
            var v = new double[] { 1, 0, 0, 1, 10, 20 };
            v[i] = bad;
            Assert.False(Sts2SceneFiniteGuard.IsFinite(Xform(v[0], v[1], v[2], v[3], v[4], v[5])), $"component {i}");
        }
    }

    [Fact]
    public void FiniteValues_AndAbsentChannels_AreFinite()
    {
        Assert.True(Sts2SceneFiniteGuard.IsFinite(Xform(0, 0, 0, 0, 0, 0))); // zero scale is degenerate, not non-finite
        Assert.True(Sts2SceneFiniteGuard.IsFinite((RuntimeSceneTransform2DSnapshot?)null));
        Assert.True(Sts2SceneFiniteGuard.IsFinite((RuntimeSceneRect2Snapshot?)null));
        Assert.True(Sts2SceneFiniteGuard.IsFinite((RuntimeSceneColorSnapshot?)null));
        Assert.True(Sts2SceneFiniteGuard.IsFinite((double?)null));
        Assert.True(Sts2SceneFiniteGuard.IsFinite((IReadOnlyList<double>?)null));
        Assert.True(Sts2SceneFiniteGuard.IsFinite(Rect(-5, 0, 64, 32)));
        Assert.True(Sts2SceneFiniteGuard.IsFinite(new RuntimeSceneColorSnapshot(1, 0.5, 0, 1, null)));
        Assert.True(Sts2SceneFiniteGuard.IsFinite(new double[] { 1, 0, 0, 1, -3, 4 }));
    }

    [Fact]
    public void NonFiniteRectColourScalarAndTuple_AreNotFinite()
    {
        Assert.False(Sts2SceneFiniteGuard.IsFinite(Rect(0, 0, double.NaN, 10)));
        Assert.False(Sts2SceneFiniteGuard.IsFinite(Rect(double.PositiveInfinity, 0, 10, 10)));
        Assert.False(Sts2SceneFiniteGuard.IsFinite(new RuntimeSceneColorSnapshot(1, 1, 1, double.NaN, null)));
        Assert.False(Sts2SceneFiniteGuard.IsFinite((double?)double.NaN));
        Assert.False(Sts2SceneFiniteGuard.IsFinite(new double[] { 1, 0, 0, 1, double.NegativeInfinity, 0 }));
    }

    [Fact]
    public void LeanText_DropsOnlyTheNonFiniteFields_AndKeepsTheString()
    {
        var finite = Text(24);
        var dropped = new List<(string Field, string Value)>();
        Assert.Same(finite, Sts2SceneFiniteGuard.DropNonFiniteLeanText(finite, dropped));
        Assert.Empty(dropped);

        var bad = Text(double.NaN, new RuntimeSceneColorSnapshot(1, 1, double.PositiveInfinity, 1, null));
        Assert.False(Sts2SceneFiniteGuard.IsFiniteLeanText(bad));
        var fixedText = Sts2SceneFiniteGuard.DropNonFiniteLeanText(bad, dropped);

        Assert.NotNull(fixedText);
        Assert.True(Sts2SceneFiniteGuard.IsFiniteLeanText(fixedText));
        Assert.Equal("12:34", fixedText!.Text);
        Assert.Null(fixedText.FontSize);
        Assert.Null(fixedText.TextColor);
        Assert.Equal(2, fixedText.OutlineSize); // a finite field is untouched
        Assert.Equal(new[] { "fontSize", "textColor" }, dropped.Select(d => d.Field));
        Assert.Equal("NaN", dropped[0].Value);
    }

    [Fact]
    public void RetryDelay_FirstRetryAtActiveCadence_ThenDoublesToTheCap()
    {
        Assert.Equal(16, Sts2SceneCaptureRetry.DelayMs(1, 16, 128));
        Assert.Equal(32, Sts2SceneCaptureRetry.DelayMs(2, 16, 128));
        Assert.Equal(64, Sts2SceneCaptureRetry.DelayMs(3, 16, 128));
        Assert.Equal(128, Sts2SceneCaptureRetry.DelayMs(4, 16, 128));
        Assert.Equal(128, Sts2SceneCaptureRetry.DelayMs(5, 16, 128));
        // A failure that persists for minutes can never overflow the shift or exceed the cap.
        Assert.Equal(128, Sts2SceneCaptureRetry.DelayMs(int.MaxValue, 16, 128));
        Assert.Equal(0, Sts2SceneCaptureRetry.DelayMs(3, 0, 128));
    }

    [Fact]
    public void RateLimit_FirstOccurrenceLogs_RepeatsAreCountedUntilTheInterval()
    {
        var log = new Sts2RateLimitedLog(keyIntervalMs: 10_000, globalBudget: 100, globalWindowMs: 10_000);

        Assert.Equal(new Sts2RateLimitedLog.Admission(true, 0, 0), log.Admit("a", 1_000));
        Assert.False(log.Admit("a", 2_000).Log);
        Assert.False(log.Admit("a", 10_999).Log);
        // A different key is its own first occurrence.
        Assert.True(log.Admit("b", 3_000).Log);

        var again = log.Admit("a", 11_000);
        Assert.True(again.Log);
        Assert.Equal(2, again.Suppressed);
        Assert.Equal(0, log.Admit("a", 30_000).Suppressed); // the count was reported and reset
    }

    [Fact]
    public void RateLimit_GlobalBudget_BoundsDistinctKeys_AndReportsWhatItDropped()
    {
        var log = new Sts2RateLimitedLog(keyIntervalMs: 10_000, globalBudget: 2, globalWindowMs: 1_000);

        Assert.True(log.Admit("n1", 0).Log);
        Assert.True(log.Admit("n2", 1).Log);
        Assert.False(log.Admit("n3", 2).Log); // budget spent: a VFX with many faulting nodes is not many lines
        Assert.False(log.Admit("n4", 3).Log);

        // Next window: a first occurrence that the budget refused still logs (it was never written), and the line
        // carries how many lines the budget dropped meanwhile.
        var n3 = log.Admit("n3", 1_000);
        Assert.True(n3.Log);
        Assert.Equal(1, n3.Suppressed);
        Assert.Equal(2, n3.BudgetDropped);
    }

    [Fact]
    public void RateLimit_KeyMemoryIsBounded()
    {
        var log = new Sts2RateLimitedLog(keyIntervalMs: 10_000, globalBudget: int.MaxValue, globalWindowMs: 10_000, maxKeys: 4);
        for (var i = 0; i < 100; i++)
        {
            Assert.True(log.Admit($"k{i}", i).Log);
        }
    }

    [Fact]
    public void CaptureFailedLine_NamesTheExceptionSitePhaseAndNode()
    {
        Exception thrown;
        try
        {
            ThrowFromHere();
            throw new InvalidOperationException("unreachable");
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        var line = Sts2SceneWatchFaultFormat.CaptureFailed(
            thrown, "walk", new Sts2SceneWatchFaultFormat.NodeContext("123", "Root/Combat/Doom Vfx", "GpuParticles2D"),
            consecutive: 3, suppressed: 7, budgetDropped: 0, retryMs: 64);

        Assert.StartsWith("SPIRECTL_SCENE_WATCH capture-failed exception=System.ArithmeticException ", line);
        Assert.Contains(" message=\"bad 'value' on two lines\" ", line);
        Assert.Contains($" at={typeof(Sts2SceneWatchFaultsTests).FullName}.{nameof(ThrowFromHere)} ", line);
        Assert.Contains(" phase=walk node=123 path=Root/Combat/Doom_Vfx nodeType=GpuParticles2D ", line);
        Assert.EndsWith(" consecutive=3 suppressed=7 budgetDropped=0 retryMs=64 next=full", line);
        Assert.DoesNotContain('\n', line);
    }

    [Fact]
    public void RecoveredAndNonFiniteLines_HaveTheirContractShape()
    {
        Assert.Equal(
            "SPIRECTL_SCENE_WATCH capture-recovered failures=12 outageMs=2048 resync=full",
            Sts2SceneWatchFaultFormat.CaptureRecovered(12, 2048, full: true));

        var transform = Xform(1, 0, 0, 1, double.NaN, double.PositiveInfinity);
        Assert.Equal(
            "SPIRECTL_SCENE_WATCH non-finite channel=transform node=42 path=Root/Vfx nodeType=Node2D "
            + "value=[1,0,0,1,NaN,Infinity] action=kept-last suppressed=0 budgetDropped=0",
            Sts2SceneWatchFaultFormat.NonFinite(
                "transform", new Sts2SceneWatchFaultFormat.NodeContext("42", "Root/Vfx", "Node2D"),
                Sts2SceneWatchFaultFormat.Value(transform), "kept-last", 0, 0));

        // Unknown context fields print as "-", never as an empty value a `key=value` grep would misread.
        Assert.Contains(
            " node=- path=- nodeType=- ",
            Sts2SceneWatchFaultFormat.NonFinite("viewportPrefix", default, "[NaN]", "kept-last", 0, 0));
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ThrowFromHere() => throw new ArithmeticException("bad \"value\"\non two lines");
}
