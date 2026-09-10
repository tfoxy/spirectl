using Spirectl.DotnetTools.Models;
using System.Text.RegularExpressions;

namespace Spirectl.DotnetTools.Inspection;

internal sealed class AssetCatalogService
{
    private static readonly Regex HeaderTokenPattern = new(
        @"([A-Za-z0-9_]+)=(""[^""]*""|[^\s\]]+)",
        RegexOptions.Compiled);

    public AssetCatalogResponse Search(InspectionCommandRequest request)
    {
        var normalizedQuery = Normalize(request.Query);
        var catalog = BuildCatalog(request);
        var matches = catalog.Matches
            .Where(match => MatchesQuery(match.LogicalPath, normalizedQuery) || MatchesQuery(match.SourcePath, normalizedQuery))
            .ToArray();

        return new AssetCatalogResponse(
            Command: "asset-search",
            Status: matches.Length == 0 ? "no-match" : "ok",
            Query: request.Query,
            SearchRoots: catalog.SearchRoots,
            Notes: catalog.Notes,
            MatchCount: matches.Length,
            Matches: matches);
    }

    public AssetCatalogResponse Index(InspectionCommandRequest request)
    {
        var catalog = BuildCatalog(request);
        return new AssetCatalogResponse(
            Command: "asset-index",
            Status: catalog.Matches.Count == 0 ? "no-match" : "ok",
            Query: string.Empty,
            SearchRoots: catalog.SearchRoots,
            Notes: catalog.Notes,
            MatchCount: catalog.Matches.Count,
            Matches: catalog.Matches);
    }

    private static AssetCatalogBuildResult BuildCatalog(InspectionCommandRequest request)
    {
        var resourcesDir = request.ResourcesDir ?? throw new ToolCommandException(
            2,
            "usage_error",
            "missing required --resources-dir option");
        var searchRoots = BuildSearchRoots(resourcesDir, request.IncludeMods ? request.ModsDir : null);
        var packedCatalog = new GodotPackedResourceCatalog();
        var matches = new List<AssetCatalogMatch>();
        var notes = new List<string>();
        var seen = new Dictionary<string, AssetCatalogMatch>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in searchRoots)
        {
            var excludedRoot = ResolveExcludedRoot(root.Path, request.ExcludeDir);
            foreach (var filePath in Directory.EnumerateFiles(root.Path, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal))
            {
                if (IsExcludedPath(filePath, excludedRoot))
                {
                    continue;
                }

                if (string.Equals(Path.GetExtension(filePath), ".pck", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var assetKind = AssetKindFromPath(filePath);
                if (assetKind is null)
                {
                    continue;
                }

                var relativePath = Path.GetRelativePath(root.Path, filePath).Replace('\\', '/');
                AddOrSkip(
                    seen,
                    matches,
                    notes,
                    new AssetCatalogMatch(
                        LogicalPath: $"res://{relativePath}",
                        SourcePath: relativePath,
                        SourceRoot: root.Source,
                        AssetKind: assetKind,
                        StorageKind: "filesystem-file",
                        LoadPath: filePath,
                        ReadableOffline: IsOfflineReadable(assetKind),
                        ResourceType: DetectResourceType(filePath, () => File.ReadAllBytes(filePath), assetKind)));
            }

            foreach (var packPath in Directory.EnumerateFiles(root.Path, "*.pck", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal))
            {
                if (IsExcludedPath(packPath, excludedRoot))
                {
                    continue;
                }

                var pack = packedCatalog.Load(packPath);
                notes.AddRange(pack.Notes);
                foreach (var entry in pack.Entries)
                {
                    var assetKind = AssetKindFromPath(entry.LogicalPath);
                    if (assetKind is null)
                    {
                        continue;
                    }

                    var sourcePath = TrimResourcePrefix(entry.LogicalPath);
                    AddOrSkip(
                        seen,
                        matches,
                        notes,
                        new AssetCatalogMatch(
                            LogicalPath: entry.LogicalPath,
                            SourcePath: sourcePath,
                            SourceRoot: root.Source,
                            AssetKind: assetKind,
                            StorageKind: "packed-entry",
                            LoadPath: entry.LogicalPath,
                            ReadableOffline: IsOfflineReadable(assetKind),
                            ContainerPath: entry.PackPath,
                            ResourceType: DetectResourceType(
                                entry.LogicalPath,
                                () => packedCatalog.ReadEntryBytes(entry),
                                assetKind)));
                }
            }
        }

        return new AssetCatalogBuildResult(
            searchRoots,
            [.. notes.Distinct(StringComparer.Ordinal)],
            [.. matches
                .OrderBy(match => match.SourceRoot, StringComparer.Ordinal)
                .ThenBy(match => match.SourcePath, StringComparer.Ordinal)]);
    }

    public AssetReadResponse ReadPacked(InspectionCommandRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ContainerPath))
        {
            throw new ToolCommandException(2, "usage_error", "asset-read requires <container-path> <logical-path>");
        }

        var catalog = new GodotPackedResourceCatalog().Load(request.ContainerPath);
        var entry = catalog.Entries.FirstOrDefault(current =>
            string.Equals(current.LogicalPath, NormalizeLogicalPath(request.Query), StringComparison.OrdinalIgnoreCase))
            ?? throw new ToolCommandException(
                3,
                "not_found",
                $"Packed entry '{request.Query}' was not found in '{request.ContainerPath}'.");

        var bytes = new GodotPackedResourceCatalog().ReadEntryBytes(entry);
        return new AssetReadResponse(
            Command: "asset-read",
            Status: "ok",
            LogicalPath: entry.LogicalPath,
            StorageKind: "packed-entry",
            ContainerPath: entry.PackPath,
            ContentsBase64: Convert.ToBase64String(bytes),
            Notes: catalog.Notes);
    }

    private static IReadOnlyList<InspectionSearchRoot> BuildSearchRoots(string resourcesDir, string? modsDir)
    {
        var roots = new List<InspectionSearchRoot>
        {
            new("resources", resourcesDir),
        };
        if (!string.IsNullOrWhiteSpace(modsDir))
        {
            roots.Add(new InspectionSearchRoot("mods", modsDir));
        }
        return roots;
    }

    private sealed record AssetCatalogBuildResult(
        IReadOnlyList<InspectionSearchRoot> SearchRoots,
        IReadOnlyList<string> Notes,
        IReadOnlyList<AssetCatalogMatch> Matches);

    private static void AddOrSkip(
        IDictionary<string, AssetCatalogMatch> seen,
        ICollection<AssetCatalogMatch> matches,
        ICollection<string> notes,
        AssetCatalogMatch match)
    {
        var key = $"{match.SourceRoot}:{match.SourcePath}";
        if (!seen.TryGetValue(key, out var existing))
        {
            seen[key] = match;
            matches.Add(match);
            return;
        }

        if (StoragePriority(match.StorageKind) > StoragePriority(existing.StorageKind))
        {
            matches.Remove(existing);
            matches.Add(match);
            seen[key] = match;
            notes.Add($"Skipped duplicate asset '{existing.LogicalPath}' from '{existing.StorageKind}' in favor of a filesystem file.");
            return;
        }

        notes.Add($"Skipped duplicate asset '{match.LogicalPath}' from '{match.StorageKind}' because a higher-priority source already exists.");
    }

    private static int StoragePriority(string storageKind)
    {
        return storageKind switch
        {
            "filesystem-file" => 2,
            "packed-entry" => 1,
            _ => 0,
        };
    }

    private static bool MatchesQuery(string path, string normalizedQuery)
    {
        var normalizedPath = Normalize(path);
        if (normalizedPath.Contains(normalizedQuery, StringComparison.Ordinal))
        {
            return true;
        }

        var stem = Normalize(Path.GetFileNameWithoutExtension(path));
        return stem.Contains(normalizedQuery, StringComparison.Ordinal);
    }

    private static string Normalize(string value)
    {
        return value.Replace('\\', '/').Trim().ToLowerInvariant();
    }

    private static string NormalizeLogicalPath(string path)
    {
        if (path.StartsWith("res://", StringComparison.OrdinalIgnoreCase))
        {
            return path.Replace('\\', '/');
        }

        return $"res://{path.TrimStart('/').Replace('\\', '/')}";
    }

    private static string TrimResourcePrefix(string logicalPath)
    {
        return logicalPath.StartsWith("res://", StringComparison.OrdinalIgnoreCase)
            ? logicalPath["res://".Length..]
            : logicalPath.TrimStart('/');
    }

    private static string? AssetKindFromPath(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" or ".webp" or ".avif" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tga" or ".tif" or ".tiff" or ".qoi" or ".ico" or ".pnm" or ".hdr" or ".ff" => "image",
            ".tscn" or ".escn" or ".scn" => "scene",
            ".tres" or ".res" => "resource",
            ".ttf" or ".otf" or ".woff" or ".woff2" => "font",
            _ => null,
        };
    }

    private static bool IsOfflineReadable(string assetKind)
    {
        return assetKind is "image" or "scene" or "resource" or "font";
    }

    private static string? ResolveExcludedRoot(string searchRoot, string? excludeDir)
    {
        if (string.IsNullOrWhiteSpace(excludeDir))
        {
            return null;
        }

        var fullSearchRoot = Path.GetFullPath(searchRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullExcludedRoot = Path.GetFullPath(excludeDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return fullExcludedRoot.StartsWith(fullSearchRoot, StringComparison.OrdinalIgnoreCase)
            ? fullExcludedRoot
            : null;
    }

    private static bool IsExcludedPath(string path, string? excludedRoot)
    {
        if (string.IsNullOrWhiteSpace(excludedRoot))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return fullPath.StartsWith(excludedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string? DetectResourceType(string path, Func<byte[]>? readBytes, string assetKind)
    {
        if (string.Equals(assetKind, "scene", StringComparison.Ordinal))
        {
            return "PackedScene";
        }

        if (string.Equals(assetKind, "font", StringComparison.Ordinal))
        {
            return "FontFile";
        }

        if (!string.Equals(assetKind, "resource", StringComparison.Ordinal))
        {
            return null;
        }

        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".tres" => TryReadTextResourceType(readBytes),
            ".res" => TryReadBinaryResourceType(readBytes, path),
            _ => null,
        };
    }

    private static string? TryReadTextResourceType(Func<byte[]>? readBytes)
    {
        if (readBytes is null)
        {
            return null;
        }

        try
        {
            var firstLine = ReadUtf8FirstLine(readBytes());
            if (string.IsNullOrWhiteSpace(firstLine))
            {
                return null;
            }

            foreach (Match match in HeaderTokenPattern.Matches(firstLine))
            {
                if (!string.Equals(match.Groups[1].Value, "type", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return match.Groups[2].Value.Trim('"');
            }
        }
        catch
        {
            // Asset search stays best-effort for additive metadata.
        }

        return null;
    }

    private static string? TryReadBinaryResourceType(Func<byte[]>? readBytes, string path)
    {
        if (readBytes is null)
        {
            return null;
        }

        try
        {
            var parsed = new GodotBinaryResourceCatalog().Parse(NormalizeLogicalPath(path), readBytes());
            return parsed.Document switch
            {
                GodotBinaryResourceCatalog.ParsedSceneDocument => "PackedScene",
                GodotBinaryResourceCatalog.ParsedResourceDocument resource => resource.ResourceType,
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    private static string ReadUtf8FirstLine(byte[] bytes)
    {
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        var lineEnd = text.IndexOfAny(['\r', '\n']);
        return lineEnd >= 0 ? text[..lineEnd] : text;
    }
}
