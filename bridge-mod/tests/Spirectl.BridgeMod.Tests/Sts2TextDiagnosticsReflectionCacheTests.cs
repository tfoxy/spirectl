using System.Diagnostics;
using System.Text.Json;
using Spirectl.Sts2.Common;
using Xunit;
using Xunit.Abstractions;

namespace Spirectl.BridgeMod.Tests;

// The cache toggle and the scan counter are process-global, so these tests must not interleave with each other or
// with another class's Describe calls: a non-parallel collection runs after every parallel one has finished.
[CollectionDefinition(nameof(Sts2TextDiagnosticsReflectionCacheTests), DisableParallelization = true)]
public sealed class Sts2TextDiagnosticsReflectionCacheCollection;

[Collection(nameof(Sts2TextDiagnosticsReflectionCacheTests))]
public sealed class Sts2TextDiagnosticsReflectionCacheTests(ITestOutputHelper output) : IDisposable
{
    private readonly bool _cacheBefore = Sts2RuntimeSceneTextDiagnostics.CacheReflection;

    public void Dispose() => Sts2RuntimeSceneTextDiagnostics.CacheReflection = _cacheBefore;

    // One of every text double, in the configurations the probes branch on: plain and rich, node-owned line ranges
    // (best-effort GetLineRange), per-line indexed metrics, theme lookups by string and by StringName-typed key,
    // label-settings shadows with stacked entries, a throwing member, and a non-text object.
    private static object[] Scene() =>
    [
        new MegaCrit.Sts2.UI.MegaLabel("Choose carefully"),
        new MegaCrit.Sts2.UI.MegaRichTextLabel("[wave]Hello[/wave]", "{wave}Hello{/wave}", richTextEnabled: true),
        new MegaCrit.Sts2.UI.ThrowingMegaLabel(),
        new MegaCrit.Sts2.addons.mega_text.MegaLabel("El Monarca"),
        new MegaCrit.Sts2.addons.mega_text.MegaLabel(
            "80/80", lastAdjustedSize: null, lastSetSize: null, maxFontSize: 100, minFontSize: 8, themeFontSize: 28),
        new MegaCrit.Sts2.addons.mega_text.MegaLabel(
            "Damage ALL other enemies equal to the damage dealt.", themeFontSize: 24)
        {
            LineRanges = [(0, 24), (25, 51)],
        },
        new MegaCrit.Sts2.addons.mega_text.MegaRichTextLabel("Description"),
        new MegaCrit.Sts2.addons.mega_text.MegaRichTextLabel(
            "[sine]星のしおり [/sine]", parsedText: "星のしおり ", lineRanges: [(0, 6)]),
        new StringNameThemedLabel("Keyed theme"),
        new object(),
    ];

    private static string DescribeAll(IEnumerable<object> nodes, bool lean)
        => JsonSerializer.Serialize(DescribeEach(nodes, lean));

    private static object?[] DescribeEach(IEnumerable<object> nodes, bool lean)
        => [.. nodes.Select(node => (object?)Sts2RuntimeSceneTextDiagnostics.Describe(node, lean))];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CachedDescribeIsIdenticalToTheUncachedPath(bool lean)
    {
        Sts2RuntimeSceneTextDiagnostics.CacheReflection = false;
        var uncached = DescribeAll(Scene(), lean);

        Sts2RuntimeSceneTextDiagnostics.CacheReflection = true;
        var cold = DescribeAll(Scene(), lean);
        var warm = DescribeAll(Scene(), lean);

        Assert.Equal(uncached, cold);
        Assert.Equal(uncached, warm);
        Assert.Contains("Keyed theme", uncached);
        if (!lean)
        {
            Assert.Contains("\"RangeStart\":25", uncached); // the scene really exercises the line-range path
        }
    }

    [Fact]
    public void AWarmCacheScansNoMemberTablesForNewInstancesOfKnownTypes()
    {
        Sts2RuntimeSceneTextDiagnostics.CacheReflection = true;
        _ = DescribeAll(Scene(), lean: false);

        var before = Sts2RuntimeSceneTextDiagnostics.ReflectionScans;
        _ = DescribeAll(Scene(), lean: false);
        Assert.Equal(before, Sts2RuntimeSceneTextDiagnostics.ReflectionScans);

        Sts2RuntimeSceneTextDiagnostics.CacheReflection = false;
        _ = DescribeAll(Scene(), lean: false);
        Assert.True(Sts2RuntimeSceneTextDiagnostics.ReflectionScans > before);
    }

    [Fact]
    public void AStringNameThemeKeyIsConvertedOncePerKey()
    {
        Sts2RuntimeSceneTextDiagnostics.CacheReflection = true;
        _ = Sts2RuntimeSceneTextDiagnostics.Describe(new StringNameThemedLabel("warm"), lean: false);

        var constructed = StringNameThemedLabel.StringName.Constructed;
        var described = Sts2RuntimeSceneTextDiagnostics.Describe(new StringNameThemedLabel("again"), lean: false);
        Assert.Equal("#336699FF", described?.TextColor?.Html);
        Assert.Equal(constructed, StringNameThemedLabel.StringName.Constructed);

        Sts2RuntimeSceneTextDiagnostics.CacheReflection = false;
        _ = Sts2RuntimeSceneTextDiagnostics.Describe(new StringNameThemedLabel("uncached"), lean: false);
        Assert.True(StringNameThemedLabel.StringName.Constructed > constructed);
    }

    // Timing note, not a gate: a synthetic keyframe of text nodes described cold-uncached and warm-cached. The
    // member-table scan count is the figure that carries over to the game (a live Godot type's method table is
    // far larger than these doubles'); wall time is printed, never asserted.
    [Fact]
    public void TimingNoteScansAndWallTimeBeforeAndAfter()
    {
        const int Repeats = 200;
        var nodes = Enumerable.Range(0, Repeats).SelectMany(_ => Scene()).ToArray();

        Sts2RuntimeSceneTextDiagnostics.CacheReflection = false;
        var scans0 = Sts2RuntimeSceneTextDiagnostics.ReflectionScans;
        var before = Stopwatch.StartNew();
        var uncachedResults = DescribeEach(nodes, lean: false);
        before.Stop();
        var uncached = JsonSerializer.Serialize(uncachedResults);
        var uncachedScans = Sts2RuntimeSceneTextDiagnostics.ReflectionScans - scans0;

        Sts2RuntimeSceneTextDiagnostics.CacheReflection = true;
        _ = DescribeAll(Scene(), lean: false); // warm: one instance of each type
        var scans1 = Sts2RuntimeSceneTextDiagnostics.ReflectionScans;
        var after = Stopwatch.StartNew();
        var cachedResults = DescribeEach(nodes, lean: false);
        after.Stop();
        var cached = JsonSerializer.Serialize(cachedResults);
        var cachedScans = Sts2RuntimeSceneTextDiagnostics.ReflectionScans - scans1;

        Assert.Equal(uncached, cached);
        Assert.Equal(0, cachedScans);
        output.WriteLine(
            $"nodes={nodes.Length} member-table scans before={uncachedScans} after={cachedScans} "
            + $"elapsed before={before.Elapsed.TotalMilliseconds:F1}ms after={after.Elapsed.TotalMilliseconds:F1}ms");
    }
}

/// <summary>A label whose theme methods take StringName-typed keys, as the engine's own do.</summary>
public sealed class StringNameThemedLabel(string text) : MegaCrit.Sts2.UI.MegaLabel(text)
{
    public bool HasThemeColor(StringName name, StringName themeType)
        => name.ToString() == "font_color" && themeType.ToString().Length == 0;

    public TestColorValue GetThemeColor(StringName name, StringName themeType)
        => name.ToString() == "font_color" && themeType.ToString().Length == 0
            ? new(0.2, 0.4, 0.6, 1)
            : new(1, 1, 1, 1);

    // The probe matches the parameter type by its simple name, so the double is named exactly that.
    public sealed class StringName
    {
        private static long _constructed;
        private readonly string _value;

        public StringName(string value)
        {
            Interlocked.Increment(ref _constructed);
            _value = value;
        }

        public static long Constructed => Interlocked.Read(ref _constructed);

        public override string ToString() => _value;
    }
}

public sealed record TestColorValue(double R, double G, double B, double A);
