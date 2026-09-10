using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spirectl.DotnetTools.Models;

namespace Spirectl.DotnetTools.Inspection;

internal sealed class MetadataCatalogCache
{
    public const int SchemaVersion = 0;

    private static readonly JsonSerializerOptions KeyJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions EntryJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly int _schemaVersion;
    private readonly HelperBuildIdentity _helperBuildIdentity;

    public MetadataCatalogCache()
        : this(SchemaVersion, HelperBuildIdentity.Current())
    {
    }

    internal MetadataCatalogCache(int schemaVersion, HelperBuildIdentity helperBuildIdentity)
    {
        _schemaVersion = schemaVersion;
        _helperBuildIdentity = helperBuildIdentity;
    }

    public MetadataCatalogCacheIdentity BuildIdentity(
        string assembliesDir,
        string? modsDir,
        bool includeMods,
        MetadataCatalogLoadMode loadMode,
        bool includeDependencies = false)
    {
        var searchRoots = BuildSearchRoots(assembliesDir, modsDir, includeMods)
            .Select(root => new CanonicalSearchRoot(root.Source, CanonicalizePath(root.Path)))
            .ToArray();
        var fingerprints = BuildAssemblyFingerprints(searchRoots, includeDependencies).ToArray();

        return new MetadataCatalogCacheIdentity(
            _schemaVersion,
            _helperBuildIdentity,
            loadMode,
            includeMods,
            includeDependencies,
            searchRoots,
            fingerprints);
    }

    public string BuildDeclarationsKey(MetadataCatalogCacheIdentity identity)
    {
        return BuildCacheKey("declarations", identity);
    }

    public string BuildReferencesKey(MetadataCatalogCacheIdentity identity)
    {
        return BuildCacheKey("references", identity);
    }

    public MetadataCatalog TryLoadOrBuild(
        string cacheRoot,
        string assembliesDir,
        string? modsDir,
        bool includeMods,
        bool includeDependencies,
        MetadataCatalogLoadMode loadMode,
        MetadataCatalogCacheMode cacheMode,
        IReadOnlyList<SelectedAssemblyPath>? selectedAssemblies = null)
    {
        if (cacheMode == MetadataCatalogCacheMode.Disabled)
        {
            return MetadataCatalog.BuildUncached(
                assembliesDir,
                includeMods ? modsDir : null,
                loadMode,
                includeDependencies,
                selectedAssemblies);
        }

        var identity = BuildIdentity(assembliesDir, modsDir, includeMods, loadMode, includeDependencies);
        if (loadMode == MetadataCatalogLoadMode.DeclarationsOnly)
        {
            return TryLoadOrBuildDeclarations(
                cacheRoot,
                assembliesDir,
                modsDir,
                includeMods,
                includeDependencies,
                cacheMode,
                identity,
                selectedAssemblies);
        }

        if (selectedAssemblies is { Count: > 0 })
        {
            return MetadataCatalog.BuildUncached(
                assembliesDir,
                includeMods ? modsDir : null,
                loadMode,
                includeDependencies,
                selectedAssemblies);
        }

        var key = loadMode == MetadataCatalogLoadMode.WithReferences
            ? BuildReferencesKey(identity)
            : BuildDeclarationsKey(identity);
        var entryKind = loadMode == MetadataCatalogLoadMode.WithReferences ? "references" : "declarations";
        var entryPath = BuildReferenceEntryPath(cacheRoot, key);

        if (cacheMode != MetadataCatalogCacheMode.Refresh
            && TryReadEntry(entryPath, identity, out var cachedCatalog))
        {
            return cachedCatalog;
        }

        if (cacheMode == MetadataCatalogCacheMode.RequireHit)
        {
            throw new ToolCommandException(
                3,
                "cache_miss",
                $"No valid {entryKind} metadata catalog cache entry exists for the requested assembly roots.");
        }

        var catalog = MetadataCatalog.BuildUncached(assembliesDir, includeMods ? modsDir : null, loadMode, includeDependencies);
        TryWriteEntry(entryPath, new MetadataCatalogCacheEntry(identity, catalog.ExportSnapshot()));
        return catalog;
    }

    private MetadataCatalog TryLoadOrBuildDeclarations(
        string cacheRoot,
        string assembliesDir,
        string? modsDir,
        bool includeMods,
        bool includeDependencies,
        MetadataCatalogCacheMode cacheMode,
        MetadataCatalogCacheIdentity identity,
        IReadOnlyList<SelectedAssemblyPath>? selectedAssemblies)
    {
        var manifestKey = BuildDeclarationsKey(identity);
        var manifestPath = BuildDeclarationsManifestPath(cacheRoot, manifestKey);
        DeclarationCatalogManifest? cachedManifest = null;
        var selectedAssemblyKeys = BuildSelectedAssemblyKeys(identity, selectedAssemblies);

        if (cacheMode != MetadataCatalogCacheMode.Refresh
            && TryReadDeclarationsManifest(manifestPath, identity, out cachedManifest)
            && cachedManifest is not null
            && TryReadDeclarationEntries(cacheRoot, cachedManifest, selectedAssemblyKeys, out var cachedCatalog))
        {
            return cachedCatalog;
        }

        if (cacheMode == MetadataCatalogCacheMode.RequireHit)
        {
            throw new ToolCommandException(
                3,
                "cache_miss",
                "No valid declarations metadata catalog cache entry exists for the requested assembly roots.");
        }

        var searchRoots = BuildSearchRoots(assembliesDir, includeMods ? modsDir : null, includeMods)
            .Select(root => new InspectionSearchRoot(root.Source, CanonicalizePath(root.Path)))
            .ToArray();
        var existingEntriesByPath = cachedManifest?.Assemblies
            .ToDictionary(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, DeclarationAssemblyManifestEntry>(StringComparer.OrdinalIgnoreCase);

        var snapshots = new List<MetadataCatalog.MetadataCatalogSnapshot>();
        var manifestAssemblies = new List<DeclarationAssemblyManifestEntry>();

        foreach (var assemblyFingerprint in identity.Assemblies)
        {
            var selectedAssembly = new SelectedAssemblyPath(assemblyFingerprint.Path, assemblyFingerprint.Source);
            var fileInfo = new FileInfo(selectedAssembly.CanonicalPath);
            var cachedMvid = existingEntriesByPath.TryGetValue(selectedAssembly.CanonicalPath, out var existingEntry)
                && existingEntry.SizeBytes == fileInfo.Length
                && existingEntry.LastWriteTimeUtcTicks == fileInfo.LastWriteTimeUtc.Ticks
                    ? existingEntry.Mvid
                    : TryReadMvid(selectedAssembly.CanonicalPath);
            var assemblyName = Path.GetFileNameWithoutExtension(selectedAssembly.CanonicalPath);
            var entryKey = BuildDeclarationAssemblyKey(new DeclarationAssemblyKeyPayload(
                _schemaVersion,
                _helperBuildIdentity,
                selectedAssembly.Source,
                selectedAssembly.CanonicalPath,
                assemblyName,
                fileInfo.Length,
                fileInfo.LastWriteTimeUtc.Ticks,
                cachedMvid));
            var entryPath = BuildDeclarationAssemblyEntryPath(cacheRoot, entryKey);
            var manifestEntry = new DeclarationAssemblyManifestEntry(
                selectedAssembly.Source,
                selectedAssembly.CanonicalPath,
                assemblyName,
                fileInfo.Length,
                fileInfo.LastWriteTimeUtc.Ticks,
                cachedMvid,
                entryKey);

            if (selectedAssemblyKeys is null || selectedAssemblyKeys.Contains(AssemblySelectionKey(manifestEntry)))
            {
                if (cacheMode != MetadataCatalogCacheMode.Refresh
                    && TryReadDeclarationAssemblyEntry(entryPath, manifestEntry, out var cachedSnapshot))
                {
                    snapshots.Add(cachedSnapshot);
                }
                else
                {
                    var snapshot = MetadataCatalog.BuildDeclarationSnapshot(searchRoots, [selectedAssembly]);
                    TryWriteJson(entryPath, new DeclarationAssemblyCacheEntry(manifestEntry, snapshot));
                    snapshots.Add(snapshot);
                }
            }

            manifestAssemblies.Add(manifestEntry);
        }

        var manifest = new DeclarationCatalogManifest(identity, manifestAssemblies);
        TryWriteJson(manifestPath, manifest);
        return MetadataCatalog.FromSnapshots(snapshots);
    }

    internal static IReadOnlyList<InspectionSearchRoot> BuildSearchRoots(
        string assembliesDir,
        string? modsDir,
        bool includeMods)
    {
        var searchRoots = new List<InspectionSearchRoot>
        {
            new("game", assembliesDir),
        };

        if (includeMods && !string.IsNullOrWhiteSpace(modsDir))
        {
            searchRoots.Add(new InspectionSearchRoot("mod", modsDir));
        }

        return searchRoots;
    }

    internal static AssemblyFingerprint BuildAssemblyFingerprint(string source, string assemblyPath)
    {
        var fileInfo = new FileInfo(assemblyPath);
        return new AssemblyFingerprint(
            source,
            CanonicalizePath(assemblyPath),
            fileInfo.Length,
            fileInfo.LastWriteTimeUtc.Ticks,
            Mvid: null);
    }

    internal static string? TryReadMvid(string assemblyPath)
    {
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(stream);
            if (!peReader.HasMetadata)
            {
                return null;
            }

            var reader = peReader.GetMetadataReader();
            var moduleDefinition = reader.GetModuleDefinition();
            var mvid = reader.GetGuid(moduleDefinition.Mvid);
            return mvid == Guid.Empty ? null : mvid.ToString("D");
        }
        catch (BadImageFormatException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    internal static string CanonicalizePath(string path)
    {
        return Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string BuildCacheKey(string entryKind, MetadataCatalogCacheIdentity identity)
    {
        var payload = new MetadataCatalogCacheKeyPayload(entryKind, identity);
        var json = JsonSerializer.Serialize(payload, KeyJsonOptions);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string BuildDeclarationAssemblyKey(DeclarationAssemblyKeyPayload payload)
    {
        var json = JsonSerializer.Serialize(payload, KeyJsonOptions);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string BuildDeclarationsManifestPath(string cacheRoot, string key)
    {
        return Path.Combine(CanonicalizePath(cacheRoot), "metadata-catalog", "declarations", $"manifest-{key}.json");
    }

    private static string BuildDeclarationAssemblyEntryPath(string cacheRoot, string key)
    {
        return Path.Combine(CanonicalizePath(cacheRoot), "metadata-catalog", "declarations", "assemblies", $"{key}.json");
    }

    private static string BuildReferenceEntryPath(string cacheRoot, string key)
    {
        return Path.Combine(CanonicalizePath(cacheRoot), "metadata-catalog", "references", $"{key}.json");
    }

    private static bool TryReadEntry(
        string entryPath,
        MetadataCatalogCacheIdentity expectedIdentity,
        out MetadataCatalog catalog)
    {
        catalog = null!;

        try
        {
            if (!File.Exists(entryPath))
            {
                return false;
            }

            using var stream = File.OpenRead(entryPath);
            var entry = JsonSerializer.Deserialize<MetadataCatalogCacheEntry>(stream, EntryJsonOptions);
            if (entry?.Identity is null
                || entry.Snapshot is null
                || !IdentityMatches(entry.Identity, expectedIdentity))
            {
                return false;
            }

            catalog = MetadataCatalog.FromSnapshot(entry.Snapshot);
            return true;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidOperationException
            or NullReferenceException
            or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryReadDeclarationsManifest(
        string manifestPath,
        MetadataCatalogCacheIdentity expectedIdentity,
        out DeclarationCatalogManifest? manifest)
    {
        manifest = null;

        try
        {
            if (!File.Exists(manifestPath))
            {
                return false;
            }

            using var stream = File.OpenRead(manifestPath);
            var candidate = JsonSerializer.Deserialize<DeclarationCatalogManifest>(stream, EntryJsonOptions);
            if (candidate?.Identity is null
                || candidate.Assemblies is null
                || !IdentityMatches(candidate.Identity, expectedIdentity))
            {
                return false;
            }

            manifest = candidate;
            return true;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidOperationException
            or NullReferenceException
            or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryReadDeclarationEntries(
        string cacheRoot,
        DeclarationCatalogManifest manifest,
        IReadOnlySet<string>? selectedAssemblyKeys,
        out MetadataCatalog catalog)
    {
        catalog = null!;
        var snapshots = new List<MetadataCatalog.MetadataCatalogSnapshot>();

        foreach (var assembly in manifest.Assemblies)
        {
            if (selectedAssemblyKeys is not null && !selectedAssemblyKeys.Contains(AssemblySelectionKey(assembly)))
            {
                continue;
            }

            var entryPath = BuildDeclarationAssemblyEntryPath(cacheRoot, assembly.EntryKey);
            if (!TryReadDeclarationAssemblyEntry(entryPath, assembly, out var snapshot))
            {
                return false;
            }

            snapshots.Add(snapshot);
        }

        if (selectedAssemblyKeys is not null && snapshots.Count != selectedAssemblyKeys.Count)
        {
            return false;
        }

        catalog = MetadataCatalog.FromSnapshots(snapshots);
        return true;
    }

    private static IReadOnlySet<string>? BuildSelectedAssemblyKeys(
        MetadataCatalogCacheIdentity identity,
        IReadOnlyList<SelectedAssemblyPath>? selectedAssemblies)
    {
        if (selectedAssemblies is not { Count: > 0 })
        {
            return null;
        }

        var identityAssemblyKeys = identity.Assemblies
            .Select(AssemblySelectionKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedAssemblyKeys = selectedAssemblies
            .Select(AssemblySelectionKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return selectedAssemblyKeys.IsSubsetOf(identityAssemblyKeys)
            ? selectedAssemblyKeys
            : null;
    }

    private static string AssemblySelectionKey(SelectedAssemblyPath selectedAssembly)
    {
        return $"{selectedAssembly.Source}\n{selectedAssembly.CanonicalPath}";
    }

    private static string AssemblySelectionKey(AssemblyFingerprint assembly)
    {
        return $"{assembly.Source}\n{assembly.Path}";
    }

    private static string AssemblySelectionKey(DeclarationAssemblyManifestEntry assembly)
    {
        return $"{assembly.Source}\n{assembly.Path}";
    }

    private static bool TryReadDeclarationAssemblyEntry(
        string entryPath,
        DeclarationAssemblyManifestEntry expectedAssembly,
        out MetadataCatalog.MetadataCatalogSnapshot snapshot)
    {
        snapshot = null!;

        try
        {
            if (!File.Exists(entryPath))
            {
                return false;
            }

            using var stream = File.OpenRead(entryPath);
            var entry = JsonSerializer.Deserialize<DeclarationAssemblyCacheEntry>(stream, EntryJsonOptions);
            if (entry?.Assembly is null
                || entry.Snapshot is null
                || !DeclarationAssemblyMatches(entry.Assembly, expectedAssembly))
            {
                return false;
            }

            snapshot = entry.Snapshot;
            return true;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidOperationException
            or NullReferenceException
            or NotSupportedException)
        {
            return false;
        }
    }

    private static void TryWriteEntry(string entryPath, MetadataCatalogCacheEntry entry)
    {
        TryWriteJson(entryPath, entry);
    }

    private static void TryWriteJson<T>(string entryPath, T entry)
    {
        try
        {
            var directory = Path.GetDirectoryName(entryPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            Directory.CreateDirectory(directory);
            var tempPath = Path.Combine(directory, $".{Path.GetFileName(entryPath)}.{Guid.NewGuid():N}.tmp");

            try
            {
                using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, entry, EntryJsonOptions);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(tempPath, entryPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or JsonException
            or NotSupportedException)
        {
        }
    }

    private static bool IdentityMatches(MetadataCatalogCacheIdentity actual, MetadataCatalogCacheIdentity expected)
    {
        return string.Equals(
            JsonSerializer.Serialize(actual, KeyJsonOptions),
            JsonSerializer.Serialize(expected, KeyJsonOptions),
            StringComparison.Ordinal);
    }

    private static bool DeclarationAssemblyMatches(
        DeclarationAssemblyManifestEntry actual,
        DeclarationAssemblyManifestEntry expected)
    {
        return string.Equals(
            JsonSerializer.Serialize(actual, KeyJsonOptions),
            JsonSerializer.Serialize(expected, KeyJsonOptions),
            StringComparison.Ordinal);
    }

    private static IEnumerable<AssemblyFingerprint> BuildAssemblyFingerprints(
        IEnumerable<CanonicalSearchRoot> searchRoots,
        bool includeDependencies)
    {
        foreach (var root in searchRoots)
        {
            var inspectionRoot = new InspectionSearchRoot(root.Source, root.Path);
            foreach (var assemblyPath in ManagedAssemblySelector.SelectAssemblyFiles(inspectionRoot, includeDependencies))
            {
                yield return BuildAssemblyFingerprint(root.Source, assemblyPath);
            }
        }
    }
}

internal sealed record MetadataCatalogCacheIdentity(
    int SchemaVersion,
    HelperBuildIdentity HelperBuildIdentity,
    MetadataCatalogLoadMode LoadMode,
    bool IncludeMods,
    bool IncludeDependencies,
    IReadOnlyList<CanonicalSearchRoot> SearchRoots,
    IReadOnlyList<AssemblyFingerprint> Assemblies);

internal sealed record HelperBuildIdentity(
    string AssemblyName,
    string? AssemblyVersion,
    string ModuleVersionId,
    string? Location)
{
    public static HelperBuildIdentity Current()
    {
        var assembly = typeof(MetadataCatalogCache).Assembly;
        var module = assembly.ManifestModule;
        var location = string.IsNullOrWhiteSpace(assembly.Location)
            ? null
            : MetadataCatalogCache.CanonicalizePath(assembly.Location);

        return new HelperBuildIdentity(
            assembly.GetName().Name ?? "Spirectl.DotnetTools",
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? assembly.GetName().Version?.ToString(),
            module.ModuleVersionId.ToString("D"),
            location);
    }
}

internal sealed record CanonicalSearchRoot(string Source, string Path);

internal sealed record AssemblyFingerprint(
    string Source,
    string Path,
    long SizeBytes,
    long LastWriteTimeUtcTicks,
    string? Mvid);

internal sealed record MetadataCatalogCacheKeyPayload(
    string EntryKind,
    MetadataCatalogCacheIdentity Identity);

internal sealed record MetadataCatalogCacheEntry(
    MetadataCatalogCacheIdentity Identity,
    MetadataCatalog.MetadataCatalogSnapshot Snapshot);

internal sealed record DeclarationCatalogManifest(
    MetadataCatalogCacheIdentity Identity,
    IReadOnlyList<DeclarationAssemblyManifestEntry> Assemblies);

internal sealed record DeclarationAssemblyManifestEntry(
    string Source,
    string Path,
    string AssemblyName,
    long SizeBytes,
    long LastWriteTimeUtcTicks,
    string? Mvid,
    string EntryKey);

internal sealed record DeclarationAssemblyKeyPayload(
    int SchemaVersion,
    HelperBuildIdentity HelperBuildIdentity,
    string Source,
    string Path,
    string AssemblyName,
    long SizeBytes,
    long LastWriteTimeUtcTicks,
    string? Mvid);

internal sealed record DeclarationAssemblyCacheEntry(
    DeclarationAssemblyManifestEntry Assembly,
    MetadataCatalog.MetadataCatalogSnapshot Snapshot);
