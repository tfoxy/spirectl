#pragma warning disable IDE0130 // Test doubles intentionally live in game namespaces for reflection.
#pragma warning disable IDE0290 // Explicit constructors keep reflection-backed fields easy to scan.
#pragma warning disable CS0414 // Reflection diagnostics intentionally read these private test fields.

namespace MegaCrit.Sts2.addons.mega_text;

public sealed class MegaLabel
{
    private readonly int? _lastAdjustedSize;
    private readonly int? _lastSetSize;
    private readonly int? _maxFontSize;
    private readonly int? _minFontSize;
    private readonly int? _themeFontSize;

    public MegaLabel(
        string? text = null,
        int? lastAdjustedSize = 30,
        int? lastSetSize = 24,
        int? maxFontSize = 80,
        int? minFontSize = 12,
        int? themeFontSize = null,
        double? spacingGlyph = null,
        double? letterSpacing = null)
    {
        Text = text;
        LetterSpacing = letterSpacing;
        _lastAdjustedSize = lastAdjustedSize;
        _lastSetSize = lastSetSize;
        _maxFontSize = maxFontSize;
        _minFontSize = minFontSize;
        _themeFontSize = themeFontSize;
        LabelSettings = new TestLabelSettings(spacingGlyph);
    }

    public string? Text { get; }

    public double? LetterSpacing { get; }

    public object LabelSettings { get; }

    public bool HasThemeFontSize(string name)
        => name == "font_size" && _themeFontSize is not null;

    public int GetThemeFontSize(string name)
        => name == "font_size" ? _themeFontSize ?? 0 : 0;

    // The engine's line-query surface. `LineRanges` is opt-in so every existing test double behaves exactly as
    // before; when set, the node answers `GetLineCount`/`GetLineRange` the way a real label does.
    //
    // The return type is a STRUCT with int fields, which is the whole point of the double: Godot's
    // `get_line_range` returns `Vector2I`, whereas every other line query in the same probe loop returns a float
    // `Vector2`. The shared reader is the only thing that has to cope with that difference.
    public (int, int)[]? LineRanges { get; init; }

    public int GetLineCount() => LineRanges?.Length ?? 0;

    public FakeVector2I GetLineRange(int line) => new(LineRanges![line].Item1, LineRanges![line].Item2);
}

/// <summary>Godot's `Vector2I` as the reflection reader sees it: a struct with public int X/Y fields.</summary>
public struct FakeVector2I(int x, int y)
{
    public int X = x;
    public int Y = y;
}

public sealed class MegaRichTextLabel
{
    private readonly int? _lastAdjustedSize;
    private readonly int? _lastSetSize;
    private readonly int? _maxFontSize;
    private readonly int? _minFontSize;
    private readonly int? _themeFontSize;
    private readonly bool _isAutoSizeEnabled;
    private readonly bool _isVerticallyBound;
    private readonly bool _isHorizontallyBound;

    public MegaRichTextLabel(
        string? text = null,
        int? lastAdjustedSize = 22,
        int? lastSetSize = 24,
        int? maxFontSize = 48,
        int? minFontSize = 12,
        int? themeFontSize = 24,
        bool autoSizeEnabled = true,
        bool isVerticallyBound = true,
        bool isHorizontallyBound = false)
    {
        Text = text;
        _lastAdjustedSize = lastAdjustedSize;
        _lastSetSize = lastSetSize;
        _maxFontSize = maxFontSize;
        _minFontSize = minFontSize;
        _themeFontSize = themeFontSize;
        _isAutoSizeEnabled = autoSizeEnabled;
        _isVerticallyBound = isVerticallyBound;
        _isHorizontallyBound = isHorizontallyBound;
    }

    public string? Text { get; }

    public bool HasThemeColor(string name)
        => name is "default_color" or "font_shadow_color";

    public TestColor GetThemeColor(string name)
        => name switch
        {
            "default_color" => new TestColor(0.9, 0.8, 0.7, 1),
            "font_shadow_color" => new TestColor(0, 0, 0, 0.45),
            _ => new TestColor(1, 1, 1, 1),
        };

    public bool HasThemeConstant(string name)
        => name is "line_separation" or "shadow_offset_x" or "shadow_offset_y";

    public int GetThemeConstant(string name)
        => name switch
        {
            "line_separation" => 29,
            "shadow_offset_x" => 2,
            "shadow_offset_y" => 3,
            _ => 0,
        };

    public bool HasThemeFontSize(string name)
        => name == "normal_font_size" && _themeFontSize is not null;

    public int GetThemeFontSize(string name)
        => name == "normal_font_size" ? _themeFontSize ?? 0 : 0;

    public bool HasThemeFont(string name)
        => name == "normal_font";

    public TestFontVariation? GetThemeFont(string name)
        => name == "normal_font" ? new(1) : null;

    public int GetLineCount()
        => 2;

    public double GetLineAscent(int index)
        => index < 2 ? 18 : 0;

    public double GetLineDescent(int index)
        => index < 2 ? 6 : 0;

    public TestVector2 GetLineSize(int index)
        => index switch
        {
            0 => new TestVector2(120, 24),
            1 => new TestVector2(96, 24),
            _ => new TestVector2(0, 0),
        };

    public double GetLineWidth(int index)
        => index == 0 ? 120 : index == 1 ? 96 : 0;
}

public sealed class TestLabelSettings(double? spacingGlyph = null)
{
    public TestFontVariation? Font { get; } = spacingGlyph.HasValue ? new TestFontVariation(spacingGlyph.Value) : null;

    public TestColor FontColor { get; } = new(0.8, 0.7, 0.6, 1);

    public TestColor OutlineColor { get; } = new(0.1, 0.05, 0.02, 1);

    public double OutlineSize => 2;

    public TestColor ShadowColor { get; } = new(0, 0, 0, 0.5);

    public TestVector2 ShadowOffset { get; } = new(4, 5);

    public double ShadowSize => 6;

    public int StackedShadowCount => 1;

    public TestColor GetStackedShadowColor(int index)
        => index == 0 ? new TestColor(0, 0, 0, 0.25) : new TestColor(0, 0, 0, 0);

    public TestVector2 GetStackedShadowOffset(int index)
        => index == 0 ? new TestVector2(1, 2) : new TestVector2(0, 0);

    public double GetStackedShadowOutlineSize(int index)
        => index == 0 ? 0.75 : 0;
}

public sealed class TestFontVariation(double spacingGlyph)
{
    public string ResourcePath => "res://themes/kreon_bold_glyph_space_one.tres";

    public string ResourceName => "kreon_bold_glyph_space_one";

    public double SpacingGlyph => spacingGlyph;

    public double GetAscent(int fontSize)
        => fontSize * 0.86;

    public double GetDescent(int fontSize)
        => fontSize * 0.24;

    public double GetHeight(int fontSize)
        => fontSize * 1.1;
}

public sealed record TestColor(double R, double G, double B, double A);

public sealed record TestVector2(double X, double Y);
