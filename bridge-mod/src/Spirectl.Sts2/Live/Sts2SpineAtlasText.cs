using System.Globalization;

namespace Spirectl.Sts2.Live;

internal sealed record SpineAtlasPage(
    string Name,
    int Width,
    int Height,
    string? Format,
    string? Filter,
    string? Repeat,
    bool PremultipliedAlpha,
    double Scale,
    IReadOnlyDictionary<string, string> Fields);

// One parsed region. `Rotate` is in DEGREES (0 or 90 for the legacy boolean form; the modern form can carry
// any of 0/90/180/270), so a consumer never has to know which spelling the file used.
internal sealed record SpineAtlasRegion(
    string Name,
    string Page,
    int PageIndex,
    int X,
    int Y,
    int Width,
    int Height,
    int OffsetX,
    int OffsetY,
    int OriginalWidth,
    int OriginalHeight,
    int Rotate,
    int Index,
    IReadOnlyDictionary<string, string> Fields);

internal sealed record SpineAtlasDocument(
    IReadOnlyList<SpineAtlasPage> Pages,
    IReadOnlyList<SpineAtlasRegion> Regions);

// Parser for the text Spine/libgdx atlas that a SpineAtlasResource carries as `atlas_data`. The format is
// line-based: a bare line names a page (after a blank line) or a region (otherwise), and every following
// `key: value` line belongs to whichever of the two was named last. Both the legacy spelling
// (`xy` / `size` / `orig` / `offset` / `rotate: false`) and the 4.x spelling (`bounds` / `offsets` /
// `rotate: 90`) are accepted and normalized to one shape. Unknown keys are kept verbatim in `Fields` rather
// than dropped, so the probe's dump does not lose information the parser did not model.
internal static class Sts2SpineAtlasText
{
    internal static SpineAtlasDocument Parse(string? text)
    {
        var pages = new List<SpineAtlasPage>();
        var regions = new List<SpineAtlasRegion>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return new SpineAtlasDocument(pages, regions);
        }

        // Page and region are accumulated in mutable holders and only turned into records at the end: a page's
        // index has to be known while its regions are still being read, and a `key: value` line has to be
        // routable to whichever of the two was named most recently.
        var pageBuilders = new List<(string Name, Dictionary<string, string> Fields)>();
        var regionBuilders = new List<(string Name, int PageIndex, Dictionary<string, string> Fields)>();

        Dictionary<string, string>? pageFields = null;
        Dictionary<string, string>? regionFields = null;
        // A blank line ends the current page (matching the reference reader's "page = null on empty line"),
        // so the next bare line names a page rather than another region.
        var expectPage = true;

        foreach (var rawLine in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                expectPage = true;
                regionFields = null;
                pageFields = null;
                continue;
            }

            var separator = line.IndexOf(':');
            if (separator > 0)
            {
                var key = line[..separator].Trim();
                var value = line[(separator + 1)..].Trim();
                if (regionFields is not null)
                {
                    regionFields[key] = value;
                }
                else
                {
                    pageFields?[key] = value;
                }

                continue;
            }

            if (expectPage || pageBuilders.Count == 0)
            {
                pageFields = new Dictionary<string, string>(StringComparer.Ordinal);
                regionFields = null;
                pageBuilders.Add((line, pageFields));
                expectPage = false;
                continue;
            }

            regionFields = new Dictionary<string, string>(StringComparer.Ordinal);
            pageFields = null;
            regionBuilders.Add((line, pageBuilders.Count - 1, regionFields));
        }

        foreach (var (name, fields) in pageBuilders)
        {
            pages.Add(BuildPage(name, fields));
        }

        foreach (var (name, pageIndex, fields) in regionBuilders)
        {
            var pageName = pageIndex >= 0 && pageIndex < pages.Count ? pages[pageIndex].Name : string.Empty;
            regions.Add(BuildRegion(name, pageName, pageIndex, fields));
        }

        return new SpineAtlasDocument(pages, regions);
    }

    private static SpineAtlasPage BuildPage(string name, Dictionary<string, string> fields)
    {
        var (width, height) = ReadPair(fields, "size", 0, 0);
        return new SpineAtlasPage(
            name,
            width,
            height,
            fields.GetValueOrDefault("format"),
            fields.GetValueOrDefault("filter"),
            fields.GetValueOrDefault("repeat"),
            ReadBool(fields, "pma"),
            ReadDouble(fields, "scale", 1d),
            fields);
    }

    private static SpineAtlasRegion BuildRegion(
        string name,
        string page,
        int pageIndex,
        Dictionary<string, string> fields)
    {
        int x;
        int y;
        int width;
        int height;
        if (fields.TryGetValue("bounds", out var bounds))
        {
            var parts = SplitInts(bounds);
            x = At(parts, 0, 0);
            y = At(parts, 1, 0);
            width = At(parts, 2, 0);
            height = At(parts, 3, 0);
        }
        else
        {
            (x, y) = ReadPair(fields, "xy", 0, 0);
            (width, height) = ReadPair(fields, "size", 0, 0);
        }

        int offsetX;
        int offsetY;
        int originalWidth;
        int originalHeight;
        if (fields.TryGetValue("offsets", out var offsets))
        {
            var parts = SplitInts(offsets);
            offsetX = At(parts, 0, 0);
            offsetY = At(parts, 1, 0);
            originalWidth = At(parts, 2, width);
            originalHeight = At(parts, 3, height);
        }
        else
        {
            (offsetX, offsetY) = ReadPair(fields, "offset", 0, 0);
            (originalWidth, originalHeight) = ReadPair(fields, "orig", width, height);
        }

        var rotate = 0;
        if (fields.TryGetValue("rotate", out var rotateText))
        {
            var trimmed = rotateText.Trim();
            rotate = trimmed.Equals("true", StringComparison.OrdinalIgnoreCase)
                ? 90
                : trimmed.Equals("false", StringComparison.OrdinalIgnoreCase)
                    ? 0
                    : int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var degrees)
                        ? degrees
                        : 0;
        }

        // A rotated region stores its packed box with width/height swapped relative to the source image; keep
        // the packed values verbatim (that is what a CSS/canvas crop needs) and let `Rotate` carry the fact.
        return new SpineAtlasRegion(
            name,
            page,
            pageIndex,
            x,
            y,
            width,
            height,
            offsetX,
            offsetY,
            originalWidth,
            originalHeight,
            rotate,
            ReadInt(fields, "index", -1),
            fields);
    }

    private static (int First, int Second) ReadPair(
        Dictionary<string, string> fields,
        string key,
        int defaultFirst,
        int defaultSecond)
    {
        if (!fields.TryGetValue(key, out var value))
        {
            return (defaultFirst, defaultSecond);
        }

        var parts = SplitInts(value);
        return (At(parts, 0, defaultFirst), At(parts, 1, defaultSecond));
    }

    private static int[] SplitInts(string value)
    {
        var chunks = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var parsed = new int[chunks.Length];
        for (var i = 0; i < chunks.Length; i += 1)
        {
            parsed[i] = int.TryParse(chunks[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                ? number
                : 0;
        }

        return parsed;
    }

    private static int At(int[] values, int index, int fallback)
        => index >= 0 && index < values.Length ? values[index] : fallback;

    private static int ReadInt(Dictionary<string, string> fields, string key, int fallback)
        => fields.TryGetValue(key, out var value)
            && int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : fallback;

    private static double ReadDouble(Dictionary<string, string> fields, string key, double fallback)
        => fields.TryGetValue(key, out var value)
            && double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : fallback;

    private static bool ReadBool(Dictionary<string, string> fields, string key)
        => fields.TryGetValue(key, out var value)
            && value.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";
}
