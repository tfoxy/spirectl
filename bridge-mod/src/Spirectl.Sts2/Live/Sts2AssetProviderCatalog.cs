using System.Text.Json;

namespace Spirectl.Sts2.Live;

public sealed class Sts2AssetProviderCatalog
{
    private readonly Dictionary<string, Entry> _entries;

    public Sts2AssetProviderCatalog(Dictionary<string, Entry> entries) => _entries = entries;

    public bool TryResolve(string key, out Entry entry) => _entries.TryGetValue(key, out entry!);

    /// <summary>
    /// Shape used when the assembly ships no provider-query table: no entries, so every
    /// <see cref="TryResolve"/> misses and callers fall back to their own resolution.
    /// </summary>
    private const string EmptyCatalogJson = "{\"entries\":[]}";

    public static Sts2AssetProviderCatalog LoadDefault()
    {
        // The embedded query table is optional: builds that carry none must still boot, so an
        // absent resource reads as an empty catalog rather than a load-time failure.
        using var stream = typeof(Sts2AssetProviderCatalog).Assembly
            .GetManifestResourceStream("Spirectl.Sts2.Data.base-game-asset-provider-queries.json");
        using var document = stream is null
            ? JsonDocument.Parse(EmptyCatalogJson)
            : JsonDocument.Parse(stream);
        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var item in document.RootElement.GetProperty("entries").EnumerateArray())
        {
            var key = item.GetProperty("key").GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(key) || entries.ContainsKey(key))
            {
                throw new InvalidOperationException($"Duplicate/invalid asset provider key '{key}'.");
            }

            entries[key] = new Entry(
                key,
                item.GetProperty("kind").GetString() ?? "resource",
                item.GetProperty("loadPath").GetString() ?? string.Empty);
        }

        return new Sts2AssetProviderCatalog(entries);
    }

    public sealed record Entry(string Key, string Kind, string LoadPath);
}
