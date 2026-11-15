using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using MegaCrit.Sts2.addons.mega_text;
using Spirectl.Sts2.Common;
using Xunit;
using Xunit.Abstractions;

namespace Spirectl.BridgeMod.Tests;

// Shares the reflection-cache tests' non-parallel collection: both flip process-global switches and read
// process-global counters.
[Collection(nameof(Sts2TextDiagnosticsReflectionCacheTests))]
public sealed class Sts2TextParagraphCacheTests(ITestOutputHelper output) : IDisposable
{
    private readonly bool _cacheBefore = Sts2RuntimeSceneTextDiagnostics.CacheParagraphs;

    public void Dispose()
    {
        Sts2RuntimeSceneTextDiagnostics.CacheParagraphs = _cacheBefore;
        Sts2RuntimeSceneTextDiagnostics.ClearParagraphCacheForTests();
    }

    private static readonly object FontA = GodotDoubles.NewFont();
    private static readonly object FontB = GodotDoubles.NewFont();

    private static ParagraphLabel Label(
        string text = "Deal 6 damage. Apply 2 Vulnerable.",
        object? font = null,
        double fontSize = 24,
        double width = 120,
        string? wrap = "WordSmart",
        string? overrun = null,
        string? alignment = "Center")
        => new(text, font ?? FontA, fontSize, width, wrap, overrun, alignment);

    private static ParagraphLabel[] Scene() =>
    [
        Label(),
        Label("Block 5."),
        Label("Gain 1 Strength. Exhaust.", width: 80),
        Label("Unwrapped single line text", wrap: null),
        Label("Overrun text that is cut", overrun: "TrimEllipsis"),
        Label("Left aligned", alignment: "Left"),
        Label("Other font", font: FontB),
        Label("Bigger", fontSize: 32),
    ];

    private static string DescribeAll(IEnumerable<object> nodes)
        => JsonSerializer.Serialize(nodes.Select(node => Sts2RuntimeSceneTextDiagnostics.Describe(node, lean: false)).ToArray());

    [Fact]
    public void ACacheHitIsIdenticalToAMiss()
    {
        Sts2RuntimeSceneTextDiagnostics.CacheParagraphs = false;
        var uncached = DescribeAll(Scene());

        Sts2RuntimeSceneTextDiagnostics.CacheParagraphs = true;
        Sts2RuntimeSceneTextDiagnostics.ClearParagraphCacheForTests();
        var shapes = Sts2RuntimeSceneTextDiagnostics.ParagraphShapes;
        var cold = DescribeAll(Scene());
        Assert.Equal(Scene().Length, Sts2RuntimeSceneTextDiagnostics.ParagraphShapes - shapes);

        shapes = Sts2RuntimeSceneTextDiagnostics.ParagraphShapes;
        var warm = DescribeAll(Scene()); // new node instances, same inputs: every paragraph is a hit
        Assert.Equal(0, Sts2RuntimeSceneTextDiagnostics.ParagraphShapes - shapes);

        Assert.Equal(uncached, cold);
        Assert.Equal(uncached, warm);
        Assert.Contains("godot-text-paragraph", uncached); // the scene really reaches the paragraph probe
        Assert.Contains("\"RangeStart\":", uncached);
    }

    public static TheoryData<string> ChangedFields() =>
        ["text", "font", "fontSize", "width", "wrap", "overrun", "alignment"];

    [Theory]
    [MemberData(nameof(ChangedFields))]
    public void AnyChangedKeyFieldMisses(string field)
    {
        Sts2RuntimeSceneTextDiagnostics.CacheParagraphs = true;
        Sts2RuntimeSceneTextDiagnostics.ClearParagraphCacheForTests();
        _ = Sts2RuntimeSceneTextDiagnostics.Describe(Label(), lean: false);

        var changed = field switch
        {
            "text" => Label(text: "Deal 7 damage. Apply 2 Vulnerable."),
            "font" => Label(font: FontB),
            "fontSize" => Label(fontSize: 25),
            "width" => Label(width: 121),
            "wrap" => Label(wrap: "Arbitrary"),
            "overrun" => Label(overrun: "TrimChar"),
            "alignment" => Label(alignment: "Right"),
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        var shapes = Sts2RuntimeSceneTextDiagnostics.ParagraphShapes;
        var cached = JsonSerializer.Serialize(Sts2RuntimeSceneTextDiagnostics.Describe(changed, lean: false));
        Assert.Equal(1, Sts2RuntimeSceneTextDiagnostics.ParagraphShapes - shapes);

        Sts2RuntimeSceneTextDiagnostics.CacheParagraphs = false;
        Assert.Equal(cached, JsonSerializer.Serialize(Sts2RuntimeSceneTextDiagnostics.Describe(changed, lean: false)));
    }

    [Fact]
    public void KeyEqualityCoversEveryShapingInput()
    {
        static Sts2RuntimeSceneTextDiagnostics.ParagraphKey Key(
            object? font = null, string text = "t", int size = 24, double width = 100, string? breaks = "b",
            string? justify = "j", string? overrun = "o", string? align = "a")
            => new(font ?? FontA, text, size, width, breaks, justify, overrun, align);

        Assert.Equal(Key(), Key());
        Assert.Equal(Key().GetHashCode(), Key().GetHashCode());
        Assert.NotEqual(Key(), Key(font: FontB));
        Assert.NotEqual(Key(), Key(font: GodotDoubles.NewFont())); // same font TYPE, different resource
        Assert.NotEqual(Key(), Key(text: "u"));
        Assert.NotEqual(Key(), Key(size: 25));
        Assert.NotEqual(Key(), Key(width: 100.5));
        Assert.NotEqual(Key(), Key(breaks: null));
        Assert.NotEqual(Key(), Key(justify: null));
        Assert.NotEqual(Key(), Key(overrun: null));
        Assert.NotEqual(Key(), Key(align: null));
    }

    [Fact]
    public void AFailedShapeIsNotRemembered()
    {
        Sts2RuntimeSceneTextDiagnostics.CacheParagraphs = true;
        Sts2RuntimeSceneTextDiagnostics.ClearParagraphCacheForTests();
        var shapes = Sts2RuntimeSceneTextDiagnostics.ParagraphShapes;
        _ = Sts2RuntimeSceneTextDiagnostics.Describe(Label(text: FakeTextParagraph.RefusedText), lean: false);
        _ = Sts2RuntimeSceneTextDiagnostics.Describe(Label(text: FakeTextParagraph.RefusedText), lean: false);
        Assert.Equal(2, Sts2RuntimeSceneTextDiagnostics.ParagraphShapes - shapes);
        Assert.Equal(0, Sts2RuntimeSceneTextDiagnostics.ParagraphCacheCount);
    }

    [Fact]
    public void TheParagraphCacheIsBounded()
    {
        Sts2RuntimeSceneTextDiagnostics.CacheParagraphs = true;
        Sts2RuntimeSceneTextDiagnostics.ClearParagraphCacheForTests();
        for (var i = 0; i < Sts2RuntimeSceneTextDiagnostics.ParagraphCacheCapacity + 50; i++)
        {
            _ = Sts2RuntimeSceneTextDiagnostics.Describe(Label(text: $"Label {i}"), lean: false);
        }

        Assert.Equal(Sts2RuntimeSceneTextDiagnostics.ParagraphCacheCapacity, Sts2RuntimeSceneTextDiagnostics.ParagraphCacheCount);
    }

    [Fact]
    public void LruEvictsTheLeastRecentlyUsedEntry()
    {
        var cache = new Sts2BoundedLruCache<string, int>(2);
        cache.Set("a", 1);
        cache.Set("b", 2);
        Assert.True(cache.TryGet("a", out _)); // "a" is now the most recent
        cache.Set("c", 3);                     // evicts "b"
        Assert.False(cache.TryGet("b", out _));
        Assert.True(cache.TryGet("a", out var a) && a == 1);
        Assert.True(cache.TryGet("c", out var c) && c == 3);
        Assert.Equal(2, cache.Count);
    }

    // Timing note, not a gate: a swap-back keyframe re-describes the labels the previous keyframe already shaped.
    // The figure that carries over to the game is the number of paragraphs shaped (each a native TextParagraph
    // build + layout); the doubles' shaping is trivial, so wall time is printed only.
    [Fact]
    public void TimingNoteShapesPerKeyframeBeforeAndAfter()
    {
        var keyframe = Enumerable.Range(0, 300).SelectMany(_ => Scene()).ToArray();

        Sts2RuntimeSceneTextDiagnostics.CacheParagraphs = false;
        var shapes0 = Sts2RuntimeSceneTextDiagnostics.ParagraphShapes;
        var before = Stopwatch.StartNew();
        var uncached = keyframe.Select(n => Sts2RuntimeSceneTextDiagnostics.Describe(n, lean: false)).ToArray();
        before.Stop();
        var uncachedShapes = Sts2RuntimeSceneTextDiagnostics.ParagraphShapes - shapes0;

        Sts2RuntimeSceneTextDiagnostics.CacheParagraphs = true;
        Sts2RuntimeSceneTextDiagnostics.ClearParagraphCacheForTests();
        var shapes1 = Sts2RuntimeSceneTextDiagnostics.ParagraphShapes;
        var first = Stopwatch.StartNew();
        var firstKeyframe = keyframe.Select(n => Sts2RuntimeSceneTextDiagnostics.Describe(n, lean: false)).ToArray();
        first.Stop();
        var firstShapes = Sts2RuntimeSceneTextDiagnostics.ParagraphShapes - shapes1;

        var shapes2 = Sts2RuntimeSceneTextDiagnostics.ParagraphShapes;
        var again = Stopwatch.StartNew();
        var swapBack = keyframe.Select(n => Sts2RuntimeSceneTextDiagnostics.Describe(n, lean: false)).ToArray();
        again.Stop();
        var swapBackShapes = Sts2RuntimeSceneTextDiagnostics.ParagraphShapes - shapes2;

        Assert.Equal(JsonSerializer.Serialize(uncached), JsonSerializer.Serialize(firstKeyframe));
        Assert.Equal(JsonSerializer.Serialize(uncached), JsonSerializer.Serialize(swapBack));
        Assert.Equal(0, swapBackShapes);
        output.WriteLine(
            $"labels={keyframe.Length} distinct={Scene().Length} shapes uncached={uncachedShapes} "
            + $"first-keyframe={firstShapes} swap-back={swapBackShapes} "
            + $"elapsed uncached={before.Elapsed.TotalMilliseconds:F1}ms first={first.Elapsed.TotalMilliseconds:F1}ms "
            + $"swap-back={again.Elapsed.TotalMilliseconds:F1}ms");
    }
}

/// <summary>
/// Godot-shaped doubles in a dynamic assembly whose name contains "Godot": the paragraph probe only resolves
/// <c>Godot.TextParagraph</c> from a font living in such an assembly, exactly as it does against the engine.
/// </summary>
internal static class GodotDoubles
{
    private static readonly Type FontType;

    static GodotDoubles()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("GodotSharp.TestDoubles"), AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("GodotSharp.TestDoubles");
        FontType = Derive(module, "Godot.FontFile", typeof(FakeFont));
        _ = Derive(module, "Godot.TextParagraph", typeof(FakeTextParagraph));
    }

    internal static object NewFont() => Activator.CreateInstance(FontType)!;

    private static Type Derive(ModuleBuilder module, string name, Type baseType)
    {
        var builder = module.DefineType(name, TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class, baseType);
        builder.DefineDefaultConstructor(MethodAttributes.Public);
        return builder.CreateType();
    }
}

public class FakeFont
{
    public string ResourcePath => "res://fonts/fake.ttf";

    public string ResourceName => "fake";

    public double GetAscent(int fontSize) => fontSize * 0.8;

    public double GetDescent(int fontSize) => fontSize * 0.2;

    public double GetHeight(int fontSize) => fontSize * 1.0;
}

// The paragraph double keeps the engine's parameter lists (the probe binds by arity and position), so some
// parameters are deliberately unused.
#pragma warning disable IDE0060
/// <summary>A deterministic paragraph: fixed advance per character, greedy wrap at the width when wrapping.</summary>
public class FakeTextParagraph
{
    public const string RefusedText = "refused by the shaper";

    private readonly List<(int Start, int End)> _lines = [];
    private int _fontSize;

    public double Width { get; set; }

    public int MaxLinesVisible { get; set; }

    public string? BreakFlags { get; set; }

    public string? JustificationFlags { get; set; }

    public string? TextOverrunBehavior { get; set; }

    public string? Alignment { get; set; }

    public void Clear() => _lines.Clear();

    public bool? AddString(string text, object font, int fontSize, string language = "", object? meta = null)
    {
        if (text == RefusedText)
        {
            return null;
        }

        _fontSize = fontSize;
        var advance = fontSize * 0.5;
        var perLine = BreakFlags is null || Width <= 0 ? text.Length : Math.Max(1, (int)(Width / advance));
        for (var start = 0; start < text.Length; start += perLine)
        {
            _lines.Add((start, Math.Min(text.Length, start + perLine)));
        }

        return true;
    }

    public TestVector2 GetSize()
        => new(_lines.Count == 0 ? 0 : _lines.Max(line => (line.End - line.Start) * _fontSize * 0.5), _lines.Count * _fontSize);

    public int GetLineCount() => _lines.Count;

    public TestVector2 GetLineSize(int index) => new(GetLineWidth(index), _fontSize);

    public FakeVector2I GetLineRange(int index) => new(_lines[index].Start, _lines[index].End);

    public double GetLineAscent(int index) => _fontSize * 0.8;

    public double GetLineDescent(int index) => _fontSize * 0.2;

    public double GetLineWidth(int index)
        => (_lines[index].End - _lines[index].Start) * _fontSize * 0.5 + (Alignment == "Center" ? 0.25 : 0);
}
#pragma warning restore IDE0060

/// <summary>A label that reaches the paragraph probe: a font member, size, width and the recipe inputs.</summary>
public sealed class ParagraphLabel(
    string text, object font, double fontSize, double width, string? wrap, string? overrun, string? alignment)
    : MegaCrit.Sts2.UI.MegaLabel(text)
{
    public object Font { get; } = font;

    public double FontSize { get; } = fontSize;

    public string? AutowrapMode { get; } = wrap;

    public string? TextOverrunBehavior { get; } = overrun;

    public string? HorizontalAlignment { get; } = alignment;

    public double GetContentWidth() => width;
}
