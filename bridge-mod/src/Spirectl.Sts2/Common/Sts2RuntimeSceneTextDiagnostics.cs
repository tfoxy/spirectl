using Spirectl.Sts2.Core.SceneInspection;
using System.Collections.Concurrent;
using System.Reflection;

namespace Spirectl.Sts2.Common;

public static class Sts2RuntimeSceneTextDiagnostics
{
    private const string LabelThemeType = "Label";
    private const string RichTextLabelThemeType = "RichTextLabel";
    private const int MaxStackedShadowDiagnostics = 16;

    // Member resolution here is deterministic per (Type, memberName) but is otherwise re-run every frame per label
    // on the lean mirror path (FindProperty/FindField walk the type hierarchy; InvokeThemeMethod enumerates
    // GetMethods). Memoize each resolution — including a "not found" null — so the per-tick text read collapses to
    // dictionary lookups. ConcurrentDictionary because Describe is also called off the watcher thread. Default ON;
    // SPIRECTL_SCENE_WATCH_CACHE_TEXT_REFLECTION=0 restores the uncached path for A/B.
    private static readonly bool CacheReflection =
        (System.Environment.GetEnvironmentVariable("SPIRECTL_SCENE_WATCH_CACHE_TEXT_REFLECTION") ?? "1")
            .Trim().ToLowerInvariant() is not ("0" or "false" or "off" or "no");

    private static readonly ConcurrentDictionary<(Type, string), PropertyInfo?> PropertyCache = new();
    private static readonly ConcurrentDictionary<(Type, string), FieldInfo?> FieldCache = new();
    private static readonly ConcurrentDictionary<(Type, string), MethodInfo?> ParameterlessMethodCache = new();
    private static readonly ConcurrentDictionary<(Type, string), MethodInfo[]> ThemeMethodsByName = new();

    // lean=true (the mirror): compute only the cheap fields a thin-client renders — text string, color,
    // font size, and horizontal/vertical alignment — and SKIP every expensive probe (per-line layout
    // metrics, rendered metrics via a TextParagraph layout, recipe, font weight/style, rich-text spans,
    // line height, outline, letter spacing, font-resource resolution). This also avoids GetLineHeight /
    // GetMethods enumeration entirely on the game main thread.
    public static RuntimeSceneTextPropertiesSnapshot? Describe(object node, bool lean = false)
    {
        var source = DescribeSource(node);
        if (source is null)
        {
            return null;
        }

        var notices = new List<RuntimeScenePropertyNoticeSnapshot>
        {
            new(
                Code: "dev_scene_text",
                Field: "properties.text",
                Message: "Text diagnostics are developer runtime scene internals."),
        };

        var text = NormalizeText(
            ProbeParameterlessMethod(node, "GetFormattedText", "properties.text.GetFormattedText", notices)?.ToString()
            ?? ProbeMember(node, "Text", "properties.text.Text", notices)?.ToString()
            ?? ProbeMember(node, "BbcodeText", "properties.text.BbcodeText", notices)?.ToString());
        var rawText = NormalizeText(
            ProbeParameterlessMethod(node, "GetRawText", "properties.text.GetRawText", notices)?.ToString()
            ?? ProbeMember(node, "RawText", "properties.text.RawText", notices)?.ToString()
            ?? ProbeMember(node, "BbcodeText", "properties.text.BbcodeText", notices)?.ToString());
        var richTextEnabled = lean
            ? null
            : ProbeBoolMember(node, "RichTextEnabled", "properties.text.RichTextEnabled", notices)
                ?? ProbeBoolMember(node, "BbcodeEnabled", "properties.text.BbcodeEnabled", notices)
                ?? ProbeBoolMember(node, "BbcodeTextEnabled", "properties.text.BbcodeTextEnabled", notices);
        var themeType = ThemeTypeForSource(source);
        var labelSettings = ProbeMember(node, "LabelSettings", "properties.text.LabelSettings", notices);
        // Font resource resolution only feeds weight/style + rendered metrics, all skipped in lean.
        var fontCandidate = lean
            ? null
            : ProbeResourceCandidate(
                notices,
                (labelSettings, "Font", "properties.text.LabelSettings.Font"),
                (node, "LabelFont", "properties.text.LabelFont"),
                (node, "Font", "properties.text.Font"),
                (node, "ThemeFont", "properties.text.ThemeFont"))
                ?? ProbeThemeResourceCandidate(node, ThemeFontKey(source), themeType, "properties.text.theme.font", notices);
        var font = fontCandidate?.Ref;
        var appliedFontSize = ProbeMegaTextAppliedFontSize(node, source, notices);
        var themeFontSize = ProbeThemeFontSize(node, ThemeFontSizeKey(source), themeType, "properties.text.theme.fontSize", notices);
        var configuredMinFontSize = source is "mega-label" or "mega-rich-text-label"
            ? FirstPositive(
                ProbeDoubleMember(node, "MinFontSize", "properties.text.megaText.MinFontSize", notices),
                ProbeDoubleMember(node, "_minFontSize", "properties.text.megaText._minFontSize", notices))
            : null;
        var configuredMaxFontSize = source is "mega-label" or "mega-rich-text-label"
            ? FirstPositive(
                ProbeDoubleMember(node, "MaxFontSize", "properties.text.megaText.MaxFontSize", notices),
                ProbeDoubleMember(node, "_maxFontSize", "properties.text.megaText._maxFontSize", notices))
            : null;
        var configuredFallbackFontSize = FirstPositive(configuredMaxFontSize, configuredMinFontSize);
        var fontSizeProbe = FirstPositiveWithSource(
            (appliedFontSize, "mega-text._lastSetSize"),
            (ProbeDoubleMember(labelSettings, "FontSize", "properties.text.LabelSettings.FontSize", notices), "label-settings.FontSize"),
            (ProbeDoubleMember(node, "LabelFontSize", "properties.text.LabelFontSize", notices), "node.LabelFontSize"),
            (ProbeDoubleMember(node, "FontSizeOverride", "properties.text.FontSizeOverride", notices), "node.FontSizeOverride"),
            (themeFontSize, $"theme:{ThemeFontSizeKey(source)}"),
            (configuredFallbackFontSize, "mega-text.configured-font-size-fallback"),
            (ProbeDoubleMember(node, "ThemeFontSize", "properties.text.ThemeFontSize", notices), "node.ThemeFontSize"),
            (ProbeDoubleMember(node, "FontSize", "properties.text.FontSize", notices), "node.FontSize"));
        var fontSize = fontSizeProbe.Value;
        // lineHeight feeds layout/line metrics + the GetLineHeight probe — none of which the mirror uses.
        var lineHeight = lean
            ? null
            : ProbeLineHeight(node, notices)
                ?? ProbeDoubleMember(labelSettings, "LineHeight", "properties.text.LabelSettings.LineHeight", notices)
                ?? ProbeDoubleMember(labelSettings, "LineSpacing", "properties.text.LabelSettings.LineSpacing", notices)
                ?? ProbeDoubleMember(node, "LineHeight", "properties.text.LineHeight", notices)
                ?? ProbeDoubleMember(node, "LineSeparation", "properties.text.LineSeparation", notices)
                ?? ProbeThemeDouble(node, ThemeLineSpacingKey(source), themeType, "properties.text.theme.lineHeight", notices);
        var letterSpacing = lean
            ? null
            : ProbeDoubleMember(node, "LetterSpacing", "properties.text.LetterSpacing", notices)
                ?? ProbeDoubleMember(node, "FontSpacing", "properties.text.FontSpacing", notices)
                ?? ProbeDoubleMember(node, "FontLetterSpacing", "properties.text.FontLetterSpacing", notices)
                ?? ProbeThemeDouble(node, "font_spacing", themeType, "properties.text.theme.letterSpacing", notices);
        var textColor = ProbeColorMember(node, "FontColor", "properties.text.FontColor", notices)
            ?? ProbeColorMember(node, "DefaultColor", "properties.text.DefaultColor", notices)
            ?? ProbeColorMember(labelSettings, "FontColor", "properties.text.LabelSettings.FontColor", notices)
            ?? ProbeThemeColor(node, ThemeFontColorKey(source), themeType, "properties.text.theme.textColor", notices)
            ?? ProbeColorMember(node, "Modulate", "properties.text.Modulate", notices)
            ?? ProbeColorMember(node, "SelfModulate", "properties.text.SelfModulate", notices);
        // Outline color/size are read even in lean (per-tick): the game recolors a label's outline at runtime
        // (e.g. the combat HP label turns its outline blue while blocking), so a static once-on-add probe
        // would freeze it. These are cheap color/theme lookups — like `textColor` above, which lean already
        // probes — and `Describe` early-returns for non-text nodes, so the cost stays bounded to labels. The
        // expensive font/shadow/metrics reflection remains lean-skipped.
        var outlineColor = ProbeColorMember(node, "FontOutlineColor", "properties.text.FontOutlineColor", notices)
                ?? ProbeColorMember(node, "OutlineColor", "properties.text.OutlineColor", notices)
                ?? ProbeColorMember(labelSettings, "OutlineColor", "properties.text.LabelSettings.OutlineColor", notices)
                ?? ProbeThemeColor(node, "font_outline_color", themeType, "properties.text.theme.outlineColor", notices);
        var outlineSize = ProbeDoubleMember(node, "OutlineSize", "properties.text.OutlineSize", notices)
                ?? ProbeDoubleMember(node, "FontOutlineSize", "properties.text.FontOutlineSize", notices)
                ?? ProbeDoubleMember(labelSettings, "OutlineSize", "properties.text.LabelSettings.OutlineSize", notices)
                ?? ProbeThemeDouble(node, "outline_size", themeType, "properties.text.theme.outlineSize", notices);
        var shadow = lean ? null : ProbeTextShadow(node, labelSettings, themeType, notices);
        var richTextSpans = lean ? [] : ExtractRichTextColorSpans(rawText ?? text);
        var fontWeight = lean ? null : ProbeFontWeight(node, labelSettings, fontCandidate?.Resource, font, notices);
        var fontStyle = lean ? null : ProbeFontStyle(node, labelSettings, fontCandidate?.Resource, font, notices);
        // Lean layout keeps alignment (the mirror reads it) but skips the per-line loop.
        var layout = ProbeTextLayout(node, text ?? rawText, fontSize, lineHeight, notices, lean);
        var recipe = lean ? null : ProbeTextRecipe(node, source, richTextEnabled, labelSettings, themeType, notices);
        var renderedMetrics = lean
            ? null
            : ProbeRenderedTextMetrics(
                node,
                fontCandidate?.Resource,
                text ?? rawText,
                fontSize,
                layout,
                recipe,
                richTextEnabled,
                notices);

        if (text is null
            && rawText is null
            && richTextEnabled is null
            && font is null
            && fontSize is null
            && lineHeight is null
            && letterSpacing is null
            && textColor is null
            && outlineColor is null
            && outlineSize is null
            && shadow is null
            && richTextSpans.Count == 0)
        {
            return notices.Count > 1
                ? new RuntimeSceneTextPropertiesSnapshot(null, null, null, source, "dev.runtime_scene.text", null, null, null, null, null, null, null, [], notices)
                : null;
        }

        return new RuntimeSceneTextPropertiesSnapshot(
            Text: text,
            RawText: rawText is not null && !string.Equals(rawText, text, StringComparison.Ordinal) ? rawText : null,
            RichTextEnabled: richTextEnabled,
            Source: source,
            DiagnosticSurface: "dev.runtime_scene.text",
            Font: font,
            FontSize: fontSize,
            LineHeight: lineHeight,
            TextColor: textColor,
            OutlineColor: outlineColor,
            OutlineSize: outlineSize,
            Shadow: shadow,
            RichTextSpans: richTextSpans,
            Notices: notices,
            LetterSpacing: letterSpacing,
            FontWeight: fontWeight,
            FontStyle: fontStyle,
            Layout: layout,
            Recipe: recipe,
            FontSizeSource: fontSizeProbe.Source,
            AppliedFontSize: appliedFontSize,
            ThemeFontSize: themeFontSize,
            ConfiguredMinFontSize: configuredMinFontSize,
            ConfiguredMaxFontSize: configuredMaxFontSize,
            RenderedMetrics: renderedMetrics);
    }

    private static string? DescribeSource(object node)
    {
        for (var current = node.GetType(); current is not null; current = current.BaseType)
        {
            var fullName = current.FullName ?? current.Name;
            if (string.Equals(fullName, "MegaCrit.Sts2.UI.MegaRichTextLabel", StringComparison.Ordinal))
            {
                return "mega-rich-text-label";
            }

            if (string.Equals(fullName, "MegaCrit.Sts2.addons.mega_text.MegaRichTextLabel", StringComparison.Ordinal))
            {
                return "mega-rich-text-label";
            }

            if (string.Equals(fullName, "MegaCrit.Sts2.UI.MegaLabel", StringComparison.Ordinal))
            {
                return "mega-label";
            }

            if (string.Equals(fullName, "MegaCrit.Sts2.addons.mega_text.MegaLabel", StringComparison.Ordinal))
            {
                return "mega-label";
            }

            if (string.Equals(fullName, "Godot.RichTextLabel", StringComparison.Ordinal))
            {
                return "godot-rich-text-label";
            }

            if (string.Equals(fullName, "Godot.Label", StringComparison.Ordinal))
            {
                return "godot-label";
            }
        }

        return null;
    }

    private static string ThemeTypeForSource(string source)
        => source is "mega-rich-text-label" or "godot-rich-text-label" ? RichTextLabelThemeType : LabelThemeType;

    private static string ThemeFontKey(string source)
        => source is "mega-rich-text-label" or "godot-rich-text-label" ? "normal_font" : "font";

    private static string ThemeFontSizeKey(string source)
        => source is "mega-rich-text-label" or "godot-rich-text-label" ? "normal_font_size" : "font_size";

    private static string ThemeLineSpacingKey(string source)
        => source is "mega-rich-text-label" or "godot-rich-text-label" ? "line_separation" : "line_spacing";

    private static string ThemeFontColorKey(string source)
        => source is "mega-rich-text-label" or "godot-rich-text-label" ? "default_color" : "font_color";

    private static string? NormalizeText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static object? ProbeMember(object? target, string memberName, string noticeField, List<RuntimeScenePropertyNoticeSnapshot> notices)
    {
        if (target is null)
        {
            return null;
        }

        var property = FindProperty(target.GetType(), memberName);
        if (property is not null)
        {
            try
            {
                return property.GetValue(target);
            }
            catch (Exception ex)
            {
                notices.Add(Inaccessible(noticeField, ex));
                return null;
            }
        }

        var field = FindField(target.GetType(), memberName);
        if (field is not null)
        {
            try
            {
                return field.GetValue(target);
            }
            catch (Exception ex)
            {
                notices.Add(Inaccessible(noticeField, ex));
                return null;
            }
        }

        return null;
    }

    private static bool? ProbeBoolMember(object? target, string memberName, string noticeField, List<RuntimeScenePropertyNoticeSnapshot> notices)
        => ProbeMember(target, memberName, noticeField, notices) is bool value ? value : null;

    private static double? ProbeMegaTextAppliedFontSize(
        object node,
        string source,
        List<RuntimeScenePropertyNoticeSnapshot> notices)
    {
        if (source is not ("mega-label" or "mega-rich-text-label"))
        {
            return null;
        }

        return FirstPositive(ProbeDoubleMember(node, "_lastSetSize", "properties.text.megaText._lastSetSize", notices));
    }

    private static double? ProbeMegaTextConfiguredFontSizeFallback(
        object node,
        string source,
        List<RuntimeScenePropertyNoticeSnapshot> notices)
    {
        if (source is not ("mega-label" or "mega-rich-text-label"))
        {
            return null;
        }

        return FirstPositive(
            ProbeDoubleMember(node, "MaxFontSize", "properties.text.megaText.MaxFontSize", notices),
            ProbeDoubleMember(node, "_maxFontSize", "properties.text.megaText._maxFontSize", notices),
            ProbeDoubleMember(node, "MinFontSize", "properties.text.megaText.MinFontSize", notices),
            ProbeDoubleMember(node, "_minFontSize", "properties.text.megaText._minFontSize", notices));
    }

    private static RuntimeSceneTextRecipeSnapshot? ProbeTextRecipe(
        object node,
        string source,
        bool? richTextEnabled,
        object? labelSettings,
        string themeType,
        List<RuntimeScenePropertyNoticeSnapshot> notices)
    {
        var autoSizeEnabled = source is "mega-label" or "mega-rich-text-label"
            ? ProbeBoolMember(node, "AutoSizeEnabled", "properties.text.recipe.AutoSizeEnabled", notices)
                ?? ProbeBoolMember(node, "_isAutoSizeEnabled", "properties.text.recipe._isAutoSizeEnabled", notices)
                ?? ProbeBoolMember(node, "IsAutoSizeEnabled", "properties.text.recipe.IsAutoSizeEnabled", notices)
                ?? true
            : (bool?)null;
        var minFontSize = source is "mega-label" or "mega-rich-text-label"
            ? FirstPositive(
                ProbeDoubleMember(node, "MinFontSize", "properties.text.recipe.MinFontSize", notices),
                ProbeDoubleMember(node, "_minFontSize", "properties.text.recipe._minFontSize", notices))
            : null;
        var maxFontSize = source is "mega-label" or "mega-rich-text-label"
            ? FirstPositive(
                ProbeDoubleMember(node, "MaxFontSize", "properties.text.recipe.MaxFontSize", notices),
                ProbeDoubleMember(node, "_maxFontSize", "properties.text.recipe._maxFontSize", notices))
            : null;
        var nominalFontSize = FirstPositive(
            ProbeDoubleMember(labelSettings, "FontSize", "properties.text.recipe.LabelSettings.FontSize", notices),
            ProbeThemeFontSize(node, ThemeFontSizeKey(source), themeType, "properties.text.recipe.theme.fontSize", notices),
            ProbeDoubleMember(node, "FontSize", "properties.text.recipe.FontSize", notices));
        var wrapMode = ProbeMember(node, "AutowrapMode", "properties.text.recipe.AutowrapMode", notices)?.ToString()
            ?? ProbeMember(node, "WrapMode", "properties.text.recipe.WrapMode", notices)?.ToString();
        var breakFlags = source is "mega-rich-text-label"
            ? "mandatory,word-bound"
            : wrapMode;
        var justificationFlags = source is "mega-rich-text-label"
            ? "kashida,word-bound"
            : null;
        var textOverrunBehavior = ProbeMember(node, "TextOverrunBehavior", "properties.text.recipe.TextOverrunBehavior", notices)?.ToString()
            ?? (source is "mega-rich-text-label" ? "trim-char" : null);
        var horizontallyBound = source switch
        {
            "mega-label" => (bool?)true,
            "mega-rich-text-label" => ProbeBoolMember(node, "IsHorizontallyBound", "properties.text.recipe.IsHorizontallyBound", notices)
                ?? ProbeBoolMember(node, "_isHorizontallyBound", "properties.text.recipe._isHorizontallyBound", notices),
            _ => null,
        };
        var verticallyBound = source switch
        {
            "mega-label" => (bool?)true,
            "mega-rich-text-label" => ProbeBoolMember(node, "IsVerticallyBound", "properties.text.recipe.IsVerticallyBound", notices)
                ?? ProbeBoolMember(node, "_isVerticallyBound", "properties.text.recipe._isVerticallyBound", notices),
            _ => null,
        };

        if (autoSizeEnabled is null
            && minFontSize is null
            && maxFontSize is null
            && nominalFontSize is null
            && richTextEnabled is null
            && wrapMode is null
            && breakFlags is null
            && justificationFlags is null
            && textOverrunBehavior is null
            && horizontallyBound is null
            && verticallyBound is null)
        {
            return null;
        }

        return new RuntimeSceneTextRecipeSnapshot(
            Source: source,
            AutoSizeEnabled: autoSizeEnabled,
            MinFontSizePx: minFontSize,
            MaxFontSizePx: maxFontSize,
            NominalFontSizePx: nominalFontSize,
            RichTextEnabled: richTextEnabled,
            WrapMode: wrapMode,
            BreakFlags: breakFlags,
            JustificationFlags: justificationFlags,
            TextOverrunBehavior: textOverrunBehavior,
            HorizontallyBound: horizontallyBound,
            VerticallyBound: verticallyBound);
    }

    private static double? ProbeLineHeight(object node, List<RuntimeScenePropertyNoticeSnapshot> notices)
    {
        // GetLineHeight(line) asserts 0 <= line < GetLineCount() in Godot's C++ (text_paragraph
        // get_line_descent) and logs an error to stderr otherwise — which FLOODS when the live scene tree
        // is polled at high frequency (the mirror) over labels with no laid-out lines. The old probe passed
        // index 0 (out of range when there are 0 lines) and index -1 (always out of range for
        // RichTextLabel), so gate every indexed probe on the live line count and never pass an
        // out-of-range index. A parameterless GetLineHeight (if a node exposes one) stays valid.
        var parameterless = ProbeDoubleParameterlessMethod(node, "GetLineHeight", "properties.text.GetLineHeight", notices);
        if (parameterless is > 0)
        {
            return parameterless;
        }

        var lineCount = ProbeIntParameterlessMethod(node, "GetLineCount", "properties.text.GetLineCount", notices);
        if (lineCount is null or <= 0)
        {
            return null;
        }

        var lastIndex = lineCount.Value - 1;
        return FirstPositive(
            ProbeDoubleIndexedMethod(node, "GetLineHeight", 0, "properties.text.GetLineHeight[0]", notices),
            lastIndex > 0
                ? ProbeDoubleIndexedMethod(node, "GetLineHeight", lastIndex, $"properties.text.GetLineHeight[{lastIndex}]", notices)
                : null);
    }

    private sealed record RuntimeResourceCandidate(object Resource, RuntimeSceneResourceRefSnapshot Ref);

    private static string? ProbeFontWeight(
        object node,
        object? labelSettings,
        object? fontResource,
        RuntimeSceneResourceRefSnapshot? font,
        List<RuntimeScenePropertyNoticeSnapshot> notices)
    {
        var weight = FirstPositive(
            ProbeDoubleMember(labelSettings, "FontWeight", "properties.text.LabelSettings.FontWeight", notices),
            ProbeDoubleMember(labelSettings, "Weight", "properties.text.LabelSettings.Weight", notices),
            ProbeDoubleMember(labelSettings, "VariationWeight", "properties.text.LabelSettings.VariationWeight", notices),
            ProbeDoubleMember(node, "FontWeight", "properties.text.FontWeight", notices),
            ProbeDoubleMember(node, "Weight", "properties.text.Weight", notices),
            ProbeDoubleMember(node, "VariationWeight", "properties.text.VariationWeight", notices),
            ProbeDoubleMember(fontResource, "Weight", "properties.text.Font.Weight", notices),
            ProbeDoubleMember(fontResource, "FontWeight", "properties.text.Font.FontWeight", notices),
            ProbeDoubleMember(fontResource, "VariationWeight", "properties.text.Font.VariationWeight", notices),
            ProbeDoubleMember(fontResource, "VariationOpentypeWeight", "properties.text.Font.VariationOpentypeWeight", notices));
        if (weight is > 0)
        {
            return Math.Round(weight.Value).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var embolden = FirstPositive(
            ProbeDoubleMember(fontResource, "VariationEmbolden", "properties.text.Font.VariationEmbolden", notices),
            ProbeDoubleMember(fontResource, "Embolden", "properties.text.Font.Embolden", notices),
            ProbeDoubleMember(fontResource, "FontEmbolden", "properties.text.Font.FontEmbolden", notices));
        if (embolden is > 0)
        {
            return "700";
        }

        var baseFont = ProbeResourceRef(fontResource, "BaseFont", "properties.text.Font.BaseFont", notices)
            ?? ProbeResourceRef(fontResource, "BaseFontData", "properties.text.Font.BaseFontData", notices)
            ?? ProbeResourceRef(fontResource, "FontData", "properties.text.Font.FontData", notices);
        var source = $"{font?.ResourcePath} {font?.ResourceName} {font?.ResourceType} {baseFont?.ResourcePath} {baseFont?.ResourceName} {baseFont?.ResourceType}";
        if (source.Contains("bold", StringComparison.OrdinalIgnoreCase)
            || source.Contains("semi-bold", StringComparison.OrdinalIgnoreCase)
            || source.Contains("semibold", StringComparison.OrdinalIgnoreCase))
        {
            return "700";
        }

        return null;
    }

    private static string? ProbeFontStyle(
        object node,
        object? labelSettings,
        object? fontResource,
        RuntimeSceneResourceRefSnapshot? font,
        List<RuntimeScenePropertyNoticeSnapshot> notices)
    {
        var style = ProbeMember(labelSettings, "FontStyle", "properties.text.LabelSettings.FontStyle", notices)?.ToString()
            ?? ProbeMember(labelSettings, "Style", "properties.text.LabelSettings.Style", notices)?.ToString()
            ?? ProbeMember(node, "FontStyle", "properties.text.FontStyle", notices)?.ToString()
            ?? ProbeMember(node, "Style", "properties.text.Style", notices)?.ToString()
            ?? ProbeMember(fontResource, "FontStyle", "properties.text.Font.FontStyle", notices)?.ToString()
            ?? ProbeMember(fontResource, "Style", "properties.text.Font.Style", notices)?.ToString();
        if (!string.IsNullOrWhiteSpace(style)
            && style.Contains("italic", StringComparison.OrdinalIgnoreCase))
        {
            return "italic";
        }

        var baseFont = ProbeResourceRef(fontResource, "BaseFont", "properties.text.Font.BaseFont", notices)
            ?? ProbeResourceRef(fontResource, "BaseFontData", "properties.text.Font.BaseFontData", notices)
            ?? ProbeResourceRef(fontResource, "FontData", "properties.text.Font.FontData", notices);
        var source = $"{font?.ResourcePath} {font?.ResourceName} {font?.ResourceType} {baseFont?.ResourcePath} {baseFont?.ResourceName} {baseFont?.ResourceType}";
        return source.Contains("italic", StringComparison.OrdinalIgnoreCase) ? "italic" : null;
    }

    private static RuntimeSceneTextLayoutSnapshot? ProbeTextLayout(
        object node,
        string? text,
        double? fontSize,
        double? lineHeight,
        List<RuntimeScenePropertyNoticeSnapshot> notices,
        bool lean = false)
    {
        var horizontalAlignment = ProbeMember(node, "HorizontalAlignment", "properties.text.HorizontalAlignment", notices)?.ToString()
            ?? ProbeMember(node, "Align", "properties.text.Align", notices)?.ToString();
        var verticalAlignment = ProbeMember(node, "VerticalAlignment", "properties.text.VerticalAlignment", notices)?.ToString()
            ?? ProbeMember(node, "Valign", "properties.text.Valign", notices)?.ToString();

        // Lean: the mirror only needs alignment; skip the cheap-but-not-free metric probes and the
        // expensive per-line ProbeTextLines loop entirely.
        if (lean)
        {
            return horizontalAlignment is null && verticalAlignment is null
                ? null
                : new RuntimeSceneTextLayoutSnapshot(
                    HorizontalAlignment: horizontalAlignment,
                    VerticalAlignment: verticalAlignment,
                    BaselineOffsetPx: null,
                    AscentPx: null,
                    DescentPx: null,
                    LineHeightPx: null,
                    ContentWidthPx: null,
                    ContentHeightPx: null,
                    ClipContents: null,
                    Lines: []);
        }

        var baselineOffset = FirstPositive(
            ProbeDoubleMember(node, "BaselineOffset", "properties.text.BaselineOffset", notices),
            ProbeDoubleParameterlessMethod(node, "GetBaselineOffset", "properties.text.GetBaselineOffset", notices));
        var ascent = FirstPositive(
            ProbeDoubleMember(node, "Ascent", "properties.text.Ascent", notices),
            ProbeDoubleParameterlessMethod(node, "GetAscent", "properties.text.GetAscent", notices));
        var descent = FirstPositive(
            ProbeDoubleMember(node, "Descent", "properties.text.Descent", notices),
            ProbeDoubleParameterlessMethod(node, "GetDescent", "properties.text.GetDescent", notices));
        var contentWidth = FirstPositive(
            ProbeDoubleParameterlessMethod(node, "GetContentWidth", "properties.text.GetContentWidth", notices),
            ProbeDoubleParameterlessMethod(node, "GetMinimumSizeX", "properties.text.GetMinimumSizeX", notices));
        var contentHeight = FirstPositive(
            ProbeDoubleParameterlessMethod(node, "GetContentHeight", "properties.text.GetContentHeight", notices),
            ProbeDoubleParameterlessMethod(node, "GetMinimumSizeY", "properties.text.GetMinimumSizeY", notices));
        var clipContents = ProbeBoolMember(node, "ClipContents", "properties.text.ClipContents", notices);
        var lines = ProbeTextLines(node, text, lineHeight ?? fontSize, notices);

        if (horizontalAlignment is null
            && verticalAlignment is null
            && baselineOffset is null
            && ascent is null
            && descent is null
            && lineHeight is null
            && contentWidth is null
            && contentHeight is null
            && clipContents is null
            && lines.Count == 0)
        {
            return null;
        }

        return new RuntimeSceneTextLayoutSnapshot(
            HorizontalAlignment: horizontalAlignment,
            VerticalAlignment: verticalAlignment,
            BaselineOffsetPx: baselineOffset,
            AscentPx: ascent,
            DescentPx: descent,
            LineHeightPx: lineHeight,
            ContentWidthPx: contentWidth,
            ContentHeightPx: contentHeight,
            ClipContents: clipContents,
            Lines: lines);
    }

    private static IReadOnlyList<RuntimeSceneTextLineSnapshot> ProbeTextLines(
        object node,
        string? text,
        double? lineHeight,
        List<RuntimeScenePropertyNoticeSnapshot> notices)
    {
        var lineCount = ProbeIntParameterlessMethod(node, "GetLineCount", "properties.text.GetLineCount", notices)
            ?? ProbeIntMember(node, "LineCount", "properties.text.LineCount", notices);
        if (lineCount is null or <= 0)
        {
            return [];
        }

        var explicitLines = text?.Split('\n');
        var lines = new List<RuntimeSceneTextLineSnapshot>();
        var maxLines = Math.Min(lineCount.Value, 64);
        var start = 0;
        for (var index = 0; index < maxLines; index++)
        {
            var lineText = ProbeIndexedMethod(node, "GetLineText", index, $"properties.text.GetLineText[{index}]", notices)?.ToString()
                ?? (explicitLines is not null && index < explicitLines.Length ? explicitLines[index] : null);
            var width = ProbeDoubleIndexedMethod(node, "GetLineWidth", index, $"properties.text.GetLineWidth[{index}]", notices);
            var height = FirstPositive(
                ProbeDoubleIndexedMethod(node, "GetLineHeight", index, $"properties.text.GetLineHeight[{index}]", notices),
                lineHeight);
            var end = lineText is null ? (int?)null : start + lineText.Length;
            lines.Add(new RuntimeSceneTextLineSnapshot(
                Index: index,
                Text: lineText,
                X: null,
                Y: height is > 0 ? index * height.Value : null,
                BaselineY: null,
                Width: width,
                Height: height,
                Start: lineText is null ? null : start,
                End: end));
            if (end.HasValue)
            {
                start = end.Value + 1;
            }
        }

        return lines;
    }

    private static RuntimeSceneTextRenderedMetricsSnapshot? ProbeRenderedTextMetrics(
        object node,
        object? fontResource,
        string? text,
        double? fontSize,
        RuntimeSceneTextLayoutSnapshot? layout,
        RuntimeSceneTextRecipeSnapshot? recipe,
        bool? richTextEnabled,
        List<RuntimeScenePropertyNoticeSnapshot> notices)
    {
        if (fontSize is not > 0)
        {
            return null;
        }

        var fontSizePx = (int)Math.Round(fontSize.Value);
        var fontAscent = ProbeDoubleMethod(fontResource, "GetAscent", fontSizePx, "properties.text.renderedMetrics.font.GetAscent", notices);
        var fontDescent = ProbeDoubleMethod(fontResource, "GetDescent", fontSizePx, "properties.text.renderedMetrics.font.GetDescent", notices);
        var fontHeight = ProbeDoubleMethod(fontResource, "GetHeight", fontSizePx, "properties.text.renderedMetrics.font.GetHeight", notices);
        var paragraph = ProbeParagraphMetrics(fontResource, text, fontSizePx, layout, recipe, notices);
        var nodeLineMetrics = ProbeNodeRenderedLineMetrics(node, layout, notices);

        // WHOSE LINE RANGES ARE TRUSTWORTHY — the one decision in this method that can produce WRONG WORDS rather
        // than a wrong number, so it is spelt out rather than left to the metric preference above.
        //
        // `ProbeParagraphMetrics` reconstructs a `TextParagraph` from `text`. For a plain Label that IS the drawn
        // string and the reconstruction is exact. For a RichTextLabel, `Text` is the BBCODE — the paragraph is
        // built over `[b]Strike[/b] deals 6`, its line ranges index into THAT, and a consumer slicing the label's
        // real content at those offsets gets neither the right words nor the right breaks. The node's own
        // `GetLineRange` is the only correct answer there, because only the node has the item tree.
        //
        // So a rich node takes the NODE's lines, and if it has none the paragraph's metrics are kept with their
        // ranges STRIPPED: the ascent and width are still honest measurements of the same font at the same size,
        // and it is only the offsets that were addressing the wrong string.
        var rich = richTextEnabled == true;
        var parsedText = rich
            ? NormalizeText(ProbeParameterlessMethod(node, "GetParsedText", "properties.text.GetParsedText", notices)?.ToString())
            : null;
        var lines = rich
            ? (nodeLineMetrics.Count > 0 ? nodeLineMetrics : WithoutRanges(paragraph?.Lines))
            : (paragraph?.Lines.Count > 0 ? paragraph.Lines : nodeLineMetrics);

        // A basis is only claimed when a range actually survived, and a "parsed" basis is only claimed when the
        // parsed string is in hand — otherwise the ranges name a string the consumer was never given, which is
        // the same wrong-words failure by a longer route.
        var hasRanges = lines.Any(line => line.RangeStart.HasValue);
        string? rangeBasis = null;
        string? rangeSource = null;
        if (hasRanges)
        {
            if (!rich && text is not null)
            {
                rangeBasis = "text";
                rangeSource = text;
            }
            else if (rich && parsedText is not null)
            {
                rangeBasis = "parsed";
                rangeSource = parsedText;
            }
            else
            {
                lines = WithoutRanges(lines);
            }
        }

        var sourceParts = new List<string>();
        if (fontAscent.HasValue || fontDescent.HasValue || fontHeight.HasValue)
        {
            sourceParts.Add("godot-font");
        }
        if (paragraph is not null)
        {
            sourceParts.Add("godot-text-paragraph");
        }
        else if (nodeLineMetrics.Count > 0)
        {
            sourceParts.Add("node-line-metrics");
        }

        if (sourceParts.Count == 0)
        {
            return null;
        }

        return new RuntimeSceneTextRenderedMetricsSnapshot(
            MetricSource: string.Join(",", sourceParts),
            FontAscentPx: fontAscent,
            FontDescentPx: fontDescent,
            FontHeightPx: fontHeight,
            ParagraphSizeWidthPx: paragraph?.ParagraphSizeWidthPx,
            ParagraphSizeHeightPx: paragraph?.ParagraphSizeHeightPx,
            Lines: lines,
            RangeBasis: rangeBasis,
            ParsedText: rangeBasis == "parsed" ? parsedText : null,
            RangeSourceLength: rangeSource?.Length,
            RangeSourceHash: rangeSource is null ? null : Fnv1a32(rangeSource));
    }

    /// <summary>
    /// FNV-1a, 32-bit, over the string's UTF-16 CODE UNITS — the staleness hash a consumer re-computes.
    /// </summary>
    /// <remarks>
    /// Chosen because it is short enough to be re-implemented exactly on the other side of the wire without a
    /// library, and because every step is defined on integers: no encoding choice, no locale, no floating point.
    /// The code-unit basis is the one thing a port must match — a consumer iterating Unicode CODE POINTS instead
    /// would agree on all of ASCII and diverge on the first character outside the BMP, which is precisely the
    /// silent, data-dependent disagreement this hash exists to prevent. The result is masked to 32 bits and
    /// returned as a signed int, so both sides can carry it in a plain integer field.
    /// </remarks>
    internal static int Fnv1a32(string value)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var unit in value)
            {
                hash ^= unit;
                hash *= 16777619u;
            }

            return (int)hash;
        }
    }

    /// <summary>
    /// The same lines with their character ranges removed — metrics kept, offsets dropped.
    /// </summary>
    /// <remarks>
    /// Used where the ranges were computed against a string the consumer will not be given. Dropping them is not
    /// a loss of information: a consumer with no ranges lays the label out itself, which is what it did before
    /// this channel existed. Keeping them would be the loss, because they address the wrong string.
    /// </remarks>
    private static IReadOnlyList<RuntimeSceneTextRenderedLineMetricsSnapshot> WithoutRanges(
        IReadOnlyList<RuntimeSceneTextRenderedLineMetricsSnapshot>? lines)
        => lines is null
            ? []
            : [.. lines.Select(line => line with { RangeStart = null, RangeEnd = null })];

    private sealed record ParagraphMetricsProbe(
        double? ParagraphSizeWidthPx,
        double? ParagraphSizeHeightPx,
        IReadOnlyList<RuntimeSceneTextRenderedLineMetricsSnapshot> Lines);

    private static ParagraphMetricsProbe? ProbeParagraphMetrics(
        object? fontResource,
        string? text,
        int fontSizePx,
        RuntimeSceneTextLayoutSnapshot? layout,
        RuntimeSceneTextRecipeSnapshot? recipe,
        List<RuntimeScenePropertyNoticeSnapshot> notices)
    {
        if (fontResource is null || string.IsNullOrEmpty(text))
        {
            return null;
        }

        var paragraphType = FindType("Godot.TextParagraph", fontResource);
        if (paragraphType is null)
        {
            return null;
        }

        object? paragraph = null;
        try
        {
            paragraph = Activator.CreateInstance(paragraphType);
            if (paragraph is null)
            {
                return null;
            }

            InvokeBestEffort(paragraph, "Clear");
            SetMemberBestEffort(paragraph, "Width", layout?.ContentWidthPx ?? 0);
            SetMemberBestEffort(paragraph, "MaxLinesVisible", -1);
            if (!string.IsNullOrWhiteSpace(recipe?.BreakFlags))
            {
                SetMemberBestEffort(paragraph, "BreakFlags", recipe.BreakFlags);
            }
            if (!string.IsNullOrWhiteSpace(recipe?.JustificationFlags))
            {
                SetMemberBestEffort(paragraph, "JustificationFlags", recipe.JustificationFlags);
            }
            if (!string.IsNullOrWhiteSpace(recipe?.TextOverrunBehavior))
            {
                SetMemberBestEffort(paragraph, "TextOverrunBehavior", recipe.TextOverrunBehavior);
            }
            if (!string.IsNullOrWhiteSpace(layout?.HorizontalAlignment))
            {
                SetMemberBestEffort(paragraph, "Alignment", layout.HorizontalAlignment);
            }

            if (InvokeBestEffort(paragraph, "AddString", text, fontResource, fontSizePx) is null)
            {
                return null;
            }

            var paragraphSizeResult = InvokeBestEffort(paragraph, "GetSize");
            var paragraphSize = paragraphSizeResult is null ? null : TryReadVector2(paragraphSizeResult);
            var lineCount = ToInt(InvokeBestEffort(paragraph, "GetLineCount")) ?? 0;
            if (lineCount <= 0 && paragraphSize is null)
            {
                return null;
            }

            var lines = new List<RuntimeSceneTextRenderedLineMetricsSnapshot>();
            for (var index = 0; index < Math.Min(lineCount, 64); index++)
            {
                var lineSizeResult = InvokeBestEffort(paragraph, "GetLineSize", index);
                var lineSize = lineSizeResult is null ? null : TryReadVector2(lineSizeResult);
                // THE WRAP ITSELF, and the reason this probe is worth more than its metrics. The paragraph above
                // was built with the node's own width, break flags, justification flags, overrun behaviour and
                // alignment, so `GetLineRange` is the engine's real line breaking rather than an approximation of
                // it. Read through TryReadVector2 because Godot returns a Vector2I here and the reader's ToDouble
                // already accepts an int — the range is then narrowed back to the integers it always was.
                var range = ReadLineRange(paragraph, index);
                lines.Add(new RuntimeSceneTextRenderedLineMetricsSnapshot(
                    Index: index,
                    ParagraphAscentPx: ToDouble(InvokeBestEffort(paragraph, "GetLineAscent", index)),
                    ParagraphDescentPx: ToDouble(InvokeBestEffort(paragraph, "GetLineDescent", index)),
                    ParagraphLineSizeWidthPx: lineSize?.X,
                    ParagraphLineSizeHeightPx: lineSize?.Y,
                    ParagraphLineWidthPx: ToDouble(InvokeBestEffort(paragraph, "GetLineWidth", index)),
                    RangeStart: range?.Start,
                    RangeEnd: range?.End));
            }

            return new ParagraphMetricsProbe(paragraphSize?.X, paragraphSize?.Y, lines);
        }
        catch (Exception ex)
        {
            notices.Add(Inaccessible("properties.text.renderedMetrics.godotTextParagraph", ex));
            return null;
        }
        finally
        {
            if (paragraph is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    private static IReadOnlyList<RuntimeSceneTextRenderedLineMetricsSnapshot> ProbeNodeRenderedLineMetrics(
        object node,
        RuntimeSceneTextLayoutSnapshot? layout,
        List<RuntimeScenePropertyNoticeSnapshot> notices)
    {
        var lineCount = ProbeIntParameterlessMethod(node, "GetLineCount", "properties.text.renderedMetrics.GetLineCount", notices)
            ?? layout?.Lines.Count
            ?? 0;
        if (lineCount <= 0)
        {
            return [];
        }

        var lines = new List<RuntimeSceneTextRenderedLineMetricsSnapshot>();
        for (var index = 0; index < Math.Min(lineCount, 64); index++)
        {
            var lineSize = ProbeVector2Method(node, "GetLineSize", index, $"properties.text.renderedMetrics.GetLineSize[{index}]", notices);
            // The node's OWN wrap result. This is the only route for a RichTextLabel, whose item tree cannot be
            // rebuilt from outside the node — a TextParagraph reconstruction would lay out the markup-stripped
            // string and silently disagree with what the label actually drew.
            var range = ReadLineRange(node, index);
            lines.Add(new RuntimeSceneTextRenderedLineMetricsSnapshot(
                Index: index,
                ParagraphAscentPx: ProbeDoubleMethod(node, "GetLineAscent", index, $"properties.text.renderedMetrics.GetLineAscent[{index}]", notices),
                ParagraphDescentPx: ProbeDoubleMethod(node, "GetLineDescent", index, $"properties.text.renderedMetrics.GetLineDescent[{index}]", notices),
                ParagraphLineSizeWidthPx: lineSize?.X,
                ParagraphLineSizeHeightPx: lineSize?.Y,
                ParagraphLineWidthPx: ProbeDoubleMethod(node, "GetLineWidth", index, $"properties.text.renderedMetrics.GetLineWidth[{index}]", notices),
                RangeStart: range?.Start,
                RangeEnd: range?.End));
        }

        // A line with a RANGE and no metrics is still a line — that is the ordinary shape for a RichTextLabel,
        // which answers `GetLineRange` but none of the paragraph metric calls. Leaving `RangeStart` out of this
        // predicate would have dropped exactly the population the wrap channel exists for.
        return [.. lines
            .Where(line => line.ParagraphAscentPx.HasValue
                || line.ParagraphDescentPx.HasValue
                || line.ParagraphLineSizeWidthPx.HasValue
                || line.ParagraphLineSizeHeightPx.HasValue
                || line.ParagraphLineWidthPx.HasValue
                || line.RangeStart.HasValue)];
    }

    /// <summary>
    /// One line's half-open character range, from whatever object can answer <c>GetLineRange</c> — a
    /// <c>TextParagraph</c> the paragraph probe built, or a node (a <c>RichTextLabel</c>) that exposes its own.
    /// </summary>
    /// <remarks>
    /// Godot returns a <c>Vector2I</c>, which <see cref="TryReadVector2"/> reads through its <c>X</c>/<c>Y</c>
    /// members exactly as it reads a <c>Vector2</c>. The result is narrowed back to integers here rather than
    /// carried as doubles, because a character offset is not a measurement and a consumer will index a string
    /// with it.
    ///
    /// DEGENERATE RANGES ARE REJECTED, not clamped. A negative start or an end before the start is an answer the
    /// probe does not understand, and passing it on would hand a consumer a slice specification that silently
    /// produces the wrong words. Null means "lay this one out yourself", which every consumer must already
    /// handle for the nodes that answer nothing at all.
    /// </remarks>
    private static (int Start, int End)? ReadLineRange(object target, int index)
    {
        var result = InvokeBestEffort(target, "GetLineRange", index);
        if (result is null)
        {
            return null;
        }

        var vector = TryReadVector2(result);
        if (vector is null)
        {
            return null;
        }

        var start = (int)Math.Round(vector.X);
        var end = (int)Math.Round(vector.Y);
        return start >= 0 && end >= start ? (start, end) : null;
    }

    private static double? FirstPositive(params double?[] values)
        => values.FirstOrDefault(value => value is > 0);

    private sealed record FontSizeProbe(double? Value, string? Source);

    private static FontSizeProbe FirstPositiveWithSource(params (double? Value, string Source)[] values)
    {
        foreach (var (value, source) in values)
        {
            if (value is > 0)
            {
                return new FontSizeProbe(value, source);
            }
        }

        return new FontSizeProbe(null, null);
    }

    private static int? ProbeIntMember(object? target, string memberName, string noticeField, List<RuntimeScenePropertyNoticeSnapshot> notices)
        => ProbeMember(target, memberName, noticeField, notices) is { } value ? ToInt(value) : null;

    private static double? ProbeDoubleMember(object? target, string memberName, string noticeField, List<RuntimeScenePropertyNoticeSnapshot> notices)
    {
        var value = ProbeMember(target, memberName, noticeField, notices);
        return ToDouble(value);
    }

    private static double? ProbeDoubleParameterlessMethod(object target, string methodName, string noticeField, List<RuntimeScenePropertyNoticeSnapshot> notices)
        => ProbeParameterlessMethod(target, methodName, noticeField, notices) is { } value ? ToDouble(value) : null;

    private static int? ProbeIntParameterlessMethod(object target, string methodName, string noticeField, List<RuntimeScenePropertyNoticeSnapshot> notices)
        => ProbeParameterlessMethod(target, methodName, noticeField, notices) is { } value ? ToInt(value) : null;

    private static double? ProbeDoubleIndexedMethod(
        object target,
        string methodName,
        int index,
        string noticeField,
        List<RuntimeScenePropertyNoticeSnapshot> notices)
        => ProbeIndexedMethod(target, methodName, index, noticeField, notices) is { } value ? ToDouble(value) : null;

    private static RuntimeSceneResourceRefSnapshot? ProbeResourceRef(object? target, string memberName, string noticeField, List<RuntimeScenePropertyNoticeSnapshot> notices)
    {
        var resource = ProbeMember(target, memberName, noticeField, notices);
        if (resource is null)
        {
            return null;
        }

        return new RuntimeSceneResourceRefSnapshot(
            Field: memberName,
            ResourcePath: ProbeMember(resource, "ResourcePath", $"{noticeField}.ResourcePath", notices)?.ToString() ?? string.Empty,
            ResourceType: resource.GetType().FullName ?? resource.GetType().Name,
            ResourceName: ProbeMember(resource, "ResourceName", $"{noticeField}.ResourceName", notices)?.ToString() ?? string.Empty);
    }

    private static RuntimeResourceCandidate? ProbeResourceCandidate(
        List<RuntimeScenePropertyNoticeSnapshot> notices,
        params (object? Target, string MemberName, string NoticeField)[] candidates)
    {
        foreach (var (target, memberName, noticeField) in candidates)
        {
            var resource = ProbeMember(target, memberName, noticeField, notices);
            if (resource is null)
            {
                continue;
            }

            return new RuntimeResourceCandidate(
                resource,
                ResourceRef(resource, memberName, noticeField, notices));
        }

        return null;
    }

    private static RuntimeSceneResourceRefSnapshot ResourceRef(
        object resource,
        string field,
        string noticeField,
        List<RuntimeScenePropertyNoticeSnapshot> notices)
        => new(
            Field: field,
            ResourcePath: ProbeMember(resource, "ResourcePath", $"{noticeField}.ResourcePath", notices)?.ToString() ?? string.Empty,
            ResourceType: resource.GetType().FullName ?? resource.GetType().Name,
            ResourceName: ProbeMember(resource, "ResourceName", $"{noticeField}.ResourceName", notices)?.ToString() ?? string.Empty);

    private static RuntimeSceneColorSnapshot? ProbeColorMember(object? target, string memberName, string noticeField, List<RuntimeScenePropertyNoticeSnapshot> notices)
    {
        var color = ProbeMember(target, memberName, noticeField, notices);
        if (color is null)
        {
            return null;
        }

        return TryReadColor(color);
    }

    private static RuntimeSceneTextShadowSnapshot? ProbeTextShadow(
        object node,
        object? labelSettings,
        string themeType,
        List<RuntimeScenePropertyNoticeSnapshot> notices)
    {
        var color = ProbeColorMember(labelSettings, "ShadowColor", "properties.text.LabelSettings.ShadowColor", notices);
        var offset = ProbeVector2Member(labelSettings, "ShadowOffset", "properties.text.LabelSettings.ShadowOffset", notices);
        var size = ProbeDoubleMember(labelSettings, "ShadowSize", "properties.text.LabelSettings.ShadowSize", notices);
        var outlineSize = ProbeDoubleMember(labelSettings, "ShadowOutlineSize", "properties.text.LabelSettings.ShadowOutlineSize", notices);
        var source = color is not null || offset is not null || size is not null || outlineSize is not null
            ? "label-settings"
            : null;

        color ??= ProbeColorMember(node, "FontShadowColor", "properties.text.FontShadowColor", notices)
            ?? ProbeColorMember(node, "ShadowColor", "properties.text.ShadowColor", notices);
        if (source is null && color is not null)
        {
            source = "node-member";
        }

        if (color is null)
        {
            color = ProbeThemeColor(node, "font_shadow_color", themeType, "properties.text.theme.shadow.color", notices);
            if (color is not null)
            {
                source = "theme:font_shadow_color";
            }
        }

        offset ??= ProbeVector2Member(node, "ShadowOffset", "properties.text.ShadowOffset", notices);
        if (source is null && offset is not null)
        {
            source = "node-member";
        }

        if (offset is null)
        {
            var offsetX = ProbeThemeDouble(node, "shadow_offset_x", themeType, "properties.text.theme.shadow.offsetX", notices);
            var offsetY = ProbeThemeDouble(node, "shadow_offset_y", themeType, "properties.text.theme.shadow.offsetY", notices);
            if (offsetX.HasValue || offsetY.HasValue)
            {
                offset = new RuntimeSceneVector2Snapshot(offsetX ?? 0, offsetY ?? 0);
                source ??= "theme:shadow_offset";
            }
        }

        size ??= ProbeThemeDouble(node, "shadow_size", themeType, "properties.text.theme.shadow.size", notices);
        outlineSize ??= ProbeThemeDouble(node, "shadow_outline_size", themeType, "properties.text.theme.shadow.outlineSize", notices);
        source ??= size.HasValue || outlineSize.HasValue ? "theme:shadow" : null;

        var stacked = ProbeStackedTextShadows(labelSettings, notices);
        if (color is null && offset is null && size is null && outlineSize is null && stacked.Count == 0)
        {
            return null;
        }

        return new RuntimeSceneTextShadowSnapshot(
            Color: color,
            Offset: offset,
            Size: size,
            OutlineSize: outlineSize,
            Source: source ?? "unknown",
            StackedShadows: stacked);
    }

    private static IReadOnlyList<RuntimeSceneStackedTextShadowSnapshot> ProbeStackedTextShadows(
        object? labelSettings,
        List<RuntimeScenePropertyNoticeSnapshot> notices)
    {
        if (labelSettings is null)
        {
            return [];
        }

        var count = ProbeDoubleMember(labelSettings, "StackedShadowCount", "properties.text.LabelSettings.StackedShadowCount", notices);
        if (!count.HasValue || count.Value <= 0)
        {
            return [];
        }

        var shadows = new List<RuntimeSceneStackedTextShadowSnapshot>();
        var cappedCount = Math.Min((int)Math.Floor(count.Value), MaxStackedShadowDiagnostics);
        for (var index = 0; index < cappedCount; index++)
        {
            var color = ProbeColorMethod(labelSettings, "GetStackedShadowColor", index, $"properties.text.LabelSettings.StackedShadows[{index}].Color", notices);
            var offset = ProbeVector2Method(labelSettings, "GetStackedShadowOffset", index, $"properties.text.LabelSettings.StackedShadows[{index}].Offset", notices);
            var outlineSize = ProbeDoubleMethod(labelSettings, "GetStackedShadowOutlineSize", index, $"properties.text.LabelSettings.StackedShadows[{index}].OutlineSize", notices);
            if (color is null && offset is null && outlineSize is null)
            {
                continue;
            }

            shadows.Add(new RuntimeSceneStackedTextShadowSnapshot(
                Index: index,
                Color: color,
                Offset: offset,
                OutlineSize: outlineSize,
                Source: "label-settings"));
        }

        return shadows;
    }

    private static RuntimeSceneColorSnapshot? ProbeColorMethod(
        object target,
        string methodName,
        int index,
        string noticeField,
        List<RuntimeScenePropertyNoticeSnapshot> notices)
        => ProbeIndexedMethod(target, methodName, index, noticeField, notices) is { } color ? TryReadColor(color) : null;

    private static RuntimeSceneVector2Snapshot? ProbeVector2Method(
        object target,
        string methodName,
        int index,
        string noticeField,
        List<RuntimeScenePropertyNoticeSnapshot> notices)
        => ProbeIndexedMethod(target, methodName, index, noticeField, notices) is { } vector ? TryReadVector2(vector) : null;

    private static double? ProbeDoubleMethod(
        object? target,
        string methodName,
        int index,
        string noticeField,
        List<RuntimeScenePropertyNoticeSnapshot> notices)
        => target is not null && ProbeIndexedMethod(target, methodName, index, noticeField, notices) is { } value ? ToDouble(value) : null;

    private static Type? FindType(string fullName, object? anchor)
    {
        if (anchor is null)
        {
            return null;
        }

        var assembly = anchor.GetType().Assembly;
        var assemblyName = assembly.GetName().Name;
        if (assemblyName is null || !assemblyName.Contains("Godot", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return assembly.GetType(fullName, throwOnError: false, ignoreCase: false);
    }

    private static object? InvokeBestEffort(object target, string methodName, params object?[] required)
    {
        var method = target.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => string.Equals(method.Name, methodName, StringComparison.Ordinal) && !method.ContainsGenericParameters)
            .OrderBy(method => method.GetParameters().Length)
            .FirstOrDefault(method => method.GetParameters().Length >= required.Length);
        if (method is null)
        {
            return null;
        }

        try
        {
            return method.Invoke(target, ConvertArguments(method, required));
        }
        catch
        {
            return null;
        }
    }

    private static void SetMemberBestEffort(object target, string memberName, object? value)
    {
        if (value is null)
        {
            return;
        }

        var property = FindProperty(target.GetType(), memberName);
        if (property is not null && property.SetMethod is not null)
        {
            var converted = ConvertArgument(value, property.PropertyType);
            if (converted is not null)
            {
                try
                {
                    property.SetValue(target, converted);
                    return;
                }
                catch
                {
                    // Best-effort metric setup only.
                }
            }
        }

        var field = FindField(target.GetType(), memberName);
        if (field is not null)
        {
            var converted = ConvertArgument(value, field.FieldType);
            if (converted is not null)
            {
                try
                {
                    field.SetValue(target, converted);
                }
                catch
                {
                    // Best-effort metric setup only.
                }
            }
        }
    }

    private static object?[] ConvertArguments(MethodInfo method, object?[] required)
    {
        var parameters = method.GetParameters();
        var arguments = new object?[parameters.Length];
        for (var index = 0; index < parameters.Length; index++)
        {
            if (index < required.Length)
            {
                arguments[index] = ConvertArgument(required[index], parameters[index].ParameterType);
            }
            else if (parameters[index].HasDefaultValue)
            {
                arguments[index] = parameters[index].DefaultValue;
            }
            else if (parameters[index].ParameterType == typeof(string))
            {
                arguments[index] = string.Empty;
            }
            else if (parameters[index].ParameterType.IsValueType)
            {
                arguments[index] = Activator.CreateInstance(parameters[index].ParameterType);
            }
            else
            {
                arguments[index] = null;
            }
        }

        return arguments;
    }

    private static object? ConvertArgument(object? value, Type targetType)
    {
        if (value is null)
        {
            return targetType.IsValueType && Nullable.GetUnderlyingType(targetType) is null
                ? Activator.CreateInstance(targetType)
                : null;
        }

        var nonNullableTarget = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (nonNullableTarget.IsInstanceOfType(value))
        {
            return value;
        }

        if (nonNullableTarget.IsEnum)
        {
            if (value is string raw)
            {
                var normalized = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (normalized.Length > 0)
                {
                    long flags = 0;
                    foreach (var token in normalized)
                    {
                        if (Enum.TryParse(nonNullableTarget, NormalizeEnumToken(token), ignoreCase: true, out var parsed))
                        {
                            flags |= Convert.ToInt64(parsed, System.Globalization.CultureInfo.InvariantCulture);
                        }
                    }
                    if (flags != 0)
                    {
                        return Enum.ToObject(nonNullableTarget, flags);
                    }
                }
            }

            if (ToInt(value) is { } enumIntValue)
            {
                return Enum.ToObject(nonNullableTarget, enumIntValue);
            }
        }

        if (nonNullableTarget == typeof(string))
        {
            return value.ToString();
        }
        if (nonNullableTarget == typeof(int) && ToInt(value) is { } convertedIntValue)
        {
            return convertedIntValue;
        }
        if (nonNullableTarget == typeof(long) && ToInt(value) is { } convertedLongValue)
        {
            return (long)convertedLongValue;
        }
        if (nonNullableTarget == typeof(float) && ToDouble(value) is { } floatValue)
        {
            return (float)floatValue;
        }
        if (nonNullableTarget == typeof(double) && ToDouble(value) is { } doubleValue)
        {
            return doubleValue;
        }
        if (nonNullableTarget == typeof(bool) && value is bool boolValue)
        {
            return boolValue;
        }

        return value;
    }

    private static string NormalizeEnumToken(string value)
        => value.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);

    private static object? ProbeIndexedMethod(
        object target,
        string methodName,
        int index,
        string noticeField,
        List<RuntimeScenePropertyNoticeSnapshot> notices)
    {
        var method = target.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method => string.Equals(method.Name, methodName, StringComparison.Ordinal) && method.GetParameters().Length == 1);
        if (method is null)
        {
            return null;
        }

        var parameter = method.GetParameters()[0];
        var argument = ConvertNumericArgument(index, parameter.ParameterType);
        if (argument is null)
        {
            return null;
        }

        try
        {
            return method.Invoke(target, [argument]);
        }
        catch (Exception ex)
        {
            notices.Add(Inaccessible(noticeField, ex));
            return null;
        }
    }

    private static object? ConvertNumericArgument(int value, Type parameterType)
    {
        if (parameterType == typeof(int))
        {
            return value;
        }

        if (parameterType == typeof(long))
        {
            return (long)value;
        }

        if (parameterType == typeof(uint))
        {
            return (uint)value;
        }

        if (parameterType == typeof(ulong))
        {
            return (ulong)value;
        }

        return null;
    }

    private static RuntimeSceneResourceRefSnapshot? ProbeThemeResourceRef(
        object target,
        string key,
        string themeType,
        string noticeField,
        List<RuntimeScenePropertyNoticeSnapshot> notices)
    {
        var resource = ProbeThemeValue(target, "HasThemeFont", "GetThemeFont", key, themeType, noticeField, notices);
        if (resource is null)
        {
            return null;
        }

        return new RuntimeSceneResourceRefSnapshot(
            Field: $"theme:{key}",
            ResourcePath: ProbeMember(resource, "ResourcePath", $"{noticeField}.ResourcePath", notices)?.ToString() ?? string.Empty,
            ResourceType: resource.GetType().FullName ?? resource.GetType().Name,
            ResourceName: ProbeMember(resource, "ResourceName", $"{noticeField}.ResourceName", notices)?.ToString() ?? string.Empty);
    }

    private static RuntimeResourceCandidate? ProbeThemeResourceCandidate(
        object target,
        string key,
        string themeType,
        string noticeField,
        List<RuntimeScenePropertyNoticeSnapshot> notices)
    {
        var resource = ProbeThemeValue(target, "HasThemeFont", "GetThemeFont", key, themeType, noticeField, notices);
        if (resource is null)
        {
            return null;
        }

        return new RuntimeResourceCandidate(
            resource,
            ResourceRef(resource, $"theme:{key}", noticeField, notices));
    }

    private static RuntimeSceneColorSnapshot? ProbeThemeColor(
        object target,
        string key,
        string themeType,
        string noticeField,
        List<RuntimeScenePropertyNoticeSnapshot> notices)
        => ProbeThemeValue(target, "HasThemeColor", "GetThemeColor", key, themeType, noticeField, notices) is { } color
            ? TryReadColor(color)
            : null;

    private static double? ProbeThemeDouble(
        object target,
        string key,
        string themeType,
        string noticeField,
        List<RuntimeScenePropertyNoticeSnapshot> notices)
        => ProbeThemeValue(target, "HasThemeConstant", "GetThemeConstant", key, themeType, noticeField, notices) is { } value
            ? ToDouble(value)
            : null;

    private static double? ProbeThemeFontSize(
        object target,
        string key,
        string themeType,
        string noticeField,
        List<RuntimeScenePropertyNoticeSnapshot> notices)
        => ProbeThemeValue(target, "HasThemeFontSize", "GetThemeFontSize", key, themeType, noticeField, notices) is { } value
            ? ToDouble(value)
            : null;

    private static object? ProbeThemeValue(
        object target,
        string hasMethodName,
        string getMethodName,
        string key,
        string themeType,
        string noticeField,
        List<RuntimeScenePropertyNoticeSnapshot> notices)
    {
        foreach (var candidateThemeType in new string?[] { null, themeType })
        {
            var hasValue = InvokeThemeMethod(target, hasMethodName, key, candidateThemeType, $"{noticeField}.{hasMethodName}", notices);
            if (!Equals(hasValue, true))
            {
                continue;
            }

            return InvokeThemeMethod(target, getMethodName, key, candidateThemeType, $"{noticeField}.{getMethodName}", notices);
        }

        return null;
    }

    private static object? InvokeThemeMethod(
        object target,
        string methodName,
        string key,
        string? themeType,
        string noticeField,
        List<RuntimeScenePropertyNoticeSnapshot> notices)
    {
        var methods = ResolveThemeMethods(target.GetType(), methodName);

        foreach (var method in methods)
        {
            var parameters = method.GetParameters();
            if (parameters.Length is < 1 or > 2)
            {
                continue;
            }

            if (parameters.Length == 1 && themeType is not null)
            {
                continue;
            }

            var keyArgument = ConvertStringNameArgument(key, parameters[0].ParameterType);
            if (keyArgument is null)
            {
                continue;
            }

            object?[] arguments;
            if (parameters.Length == 1)
            {
                arguments = [keyArgument];
            }
            else
            {
                var themeTypeArgument = ConvertStringNameArgument(themeType ?? string.Empty, parameters[1].ParameterType);
                if (themeTypeArgument is null)
                {
                    continue;
                }

                arguments = [keyArgument, themeTypeArgument];
            }

            try
            {
                return method.Invoke(target, arguments);
            }
            catch (Exception ex)
            {
                notices.Add(Inaccessible(noticeField, ex));
                return null;
            }
        }

        return null;
    }

    private static object? ConvertStringNameArgument(string value, Type parameterType)
    {
        if (parameterType == typeof(string))
        {
            return value;
        }

        if (string.Equals(parameterType.FullName, "Godot.StringName", StringComparison.Ordinal)
            || string.Equals(parameterType.Name, "StringName", StringComparison.Ordinal))
        {
            try
            {
                return Activator.CreateInstance(parameterType, value);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static RuntimeSceneVector2Snapshot? ProbeVector2Member(
        object? target,
        string memberName,
        string noticeField,
        List<RuntimeScenePropertyNoticeSnapshot> notices)
    {
        if (target is null)
        {
            return null;
        }

        var vector = ProbeMember(target, memberName, noticeField, notices);
        return vector is null ? null : TryReadVector2(vector);
    }

    private static RuntimeSceneColorSnapshot? TryReadColor(object value)
    {
        var r = ReadDoubleMember(value, "R") ?? ReadDoubleMember(value, "r");
        var g = ReadDoubleMember(value, "G") ?? ReadDoubleMember(value, "g");
        var b = ReadDoubleMember(value, "B") ?? ReadDoubleMember(value, "b");
        var a = ReadDoubleMember(value, "A") ?? ReadDoubleMember(value, "a") ?? 1.0d;
        if (r is null || g is null || b is null)
        {
            return null;
        }

        return new RuntimeSceneColorSnapshot(r.Value, g.Value, b.Value, a, ToHtml(r.Value, g.Value, b.Value, a));
    }

    private static RuntimeSceneVector2Snapshot? TryReadVector2(object value)
    {
        var x = ReadDoubleMember(value, "X") ?? ReadDoubleMember(value, "x");
        var y = ReadDoubleMember(value, "Y") ?? ReadDoubleMember(value, "y");
        return x.HasValue && y.HasValue ? new RuntimeSceneVector2Snapshot(x.Value, y.Value) : null;
    }

    private static double? ReadDoubleMember(object target, string memberName)
    {
        var value = FindProperty(target.GetType(), memberName)?.GetValue(target)
            ?? FindField(target.GetType(), memberName)?.GetValue(target);
        return ToDouble(value);
    }

    private static double? ToDouble(object? value)
        => value switch
        {
            byte number => number,
            short number => number,
            int number => number,
            long number => number,
            uint number => number,
            ulong number => number,
            float number => number,
            double number => number,
            decimal number => (double)number,
            _ => null,
        };

    private static int? ToInt(object? value)
        => value switch
        {
            byte number => number,
            short number => number,
            int number => number,
            long number when number is >= int.MinValue and <= int.MaxValue => (int)number,
            uint number when number <= int.MaxValue => (int)number,
            ulong number when number <= int.MaxValue => (int)number,
            float number when float.IsFinite(number) => (int)Math.Round(number),
            double number when double.IsFinite(number) => (int)Math.Round(number),
            decimal number when number is >= int.MinValue and <= int.MaxValue => (int)Math.Round(number),
            _ => null,
        };

    private static string ToHtml(double r, double g, double b, double a)
    {
        static int Channel(double value)
            => Math.Clamp((int)Math.Round(value <= 1.0d ? value * 255.0d : value), 0, 255);

        return $"#{Channel(r):X2}{Channel(g):X2}{Channel(b):X2}{Channel(a):X2}";
    }

    private static IReadOnlyList<RuntimeSceneRichTextSpanSnapshot> ExtractRichTextColorSpans(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return [];
        }

        var spans = new List<RuntimeSceneRichTextSpanSnapshot>();
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
            rawText,
            @"\[(?:color=)?(?<color>#[0-9a-fA-F]{6,8}|[A-Za-z]+)\](?<text>.*?)\[/color\]|\[(?<tag>red|green|blue|yellow|gold|orange|pink|grey|gray|white|black)\](?<tagText>.*?)\[/\k<tag>\]",
            System.Text.RegularExpressions.RegexOptions.Singleline))
        {
            var color = match.Groups["color"].Success ? match.Groups["color"].Value : match.Groups["tag"].Value;
            var text = match.Groups["text"].Success ? match.Groups["text"].Value : match.Groups["tagText"].Value;
            spans.Add(new RuntimeSceneRichTextSpanSnapshot(
                Tag: color,
                Text: NormalizeText(text) ?? string.Empty,
                Color: TryParseHtmlColor(color)));
        }

        return spans;
    }

    private static RuntimeSceneColorSnapshot? TryParseHtmlColor(string value)
    {
        if (!value.StartsWith('#') || value.Length is not (7 or 9))
        {
            return null;
        }

        try
        {
            var r = Convert.ToInt32(value.Substring(1, 2), 16) / 255.0d;
            var g = Convert.ToInt32(value.Substring(3, 2), 16) / 255.0d;
            var b = Convert.ToInt32(value.Substring(5, 2), 16) / 255.0d;
            var a = value.Length == 9 ? Convert.ToInt32(value.Substring(7, 2), 16) / 255.0d : 1.0d;
            return new RuntimeSceneColorSnapshot(r, g, b, a, ToHtml(r, g, b, a));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static object? ProbeParameterlessMethod(object target, string methodName, string noticeField, List<RuntimeScenePropertyNoticeSnapshot> notices)
    {
        var method = FindParameterlessMethod(target.GetType(), methodName);
        if (method is null)
        {
            return null;
        }

        try
        {
            return method.Invoke(target, null);
        }
        catch (Exception ex)
        {
            notices.Add(Inaccessible(noticeField, ex));
            return null;
        }
    }

    private static PropertyInfo? FindProperty(Type type, string propertyName)
        => CacheReflection
            ? PropertyCache.GetOrAdd((type, propertyName), static key => FindPropertyUncached(key.Item1, key.Item2))
            : FindPropertyUncached(type, propertyName);

    private static PropertyInfo? FindPropertyUncached(Type type, string propertyName)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        for (var current = type; current is not null; current = current.BaseType)
        {
            var property = current.GetProperty(propertyName, flags);
            if (property is not null)
            {
                return property;
            }
        }

        return null;
    }

    private static FieldInfo? FindField(Type type, string fieldName)
        => CacheReflection
            ? FieldCache.GetOrAdd((type, fieldName), static key => FindFieldUncached(key.Item1, key.Item2))
            : FindFieldUncached(type, fieldName);

    private static FieldInfo? FindFieldUncached(Type type, string fieldName)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        for (var current = type; current is not null; current = current.BaseType)
        {
            var field = current.GetField(fieldName, flags);
            if (field is not null)
            {
                return field;
            }
        }

        return null;
    }

    private static MethodInfo? FindParameterlessMethod(Type type, string methodName)
        => CacheReflection
            ? ParameterlessMethodCache.GetOrAdd((type, methodName), static key => FindParameterlessMethodUncached(key.Item1, key.Item2))
            : FindParameterlessMethodUncached(type, methodName);

    private static MethodInfo? FindParameterlessMethodUncached(Type type, string methodName)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        for (var current = type; current is not null; current = current.BaseType)
        {
            var method = current.GetMethod(methodName, flags, Type.DefaultBinder, Type.EmptyTypes, null);
            if (method is not null)
            {
                return method;
            }
        }

        return null;
    }

    // The name-matching + parameter-length-ordered method set an InvokeThemeMethod call chooses from. Deterministic
    // per (Type, methodName); the actual argument binding + Invoke stays per-call (it depends on runtime args).
    private static MethodInfo[] ResolveThemeMethods(Type type, string methodName)
        => CacheReflection
            ? ThemeMethodsByName.GetOrAdd((type, methodName), static key => ResolveThemeMethodsUncached(key.Item1, key.Item2))
            : ResolveThemeMethodsUncached(type, methodName);

    private static MethodInfo[] ResolveThemeMethodsUncached(Type type, string methodName)
        => type
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => string.Equals(method.Name, methodName, StringComparison.Ordinal))
            .OrderBy(method => method.GetParameters().Length)
            .ToArray();

    private static RuntimeScenePropertyNoticeSnapshot Inaccessible(string field, Exception ex)
    {
        var root = ex is TargetInvocationException { InnerException: not null } ? ex.InnerException : ex;
        return new RuntimeScenePropertyNoticeSnapshot("inaccessible", field, root.Message);
    }
}
