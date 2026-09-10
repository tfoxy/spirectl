using Spirectl.DotnetTools.Models;
using static Spirectl.DotnetTools.Inspection.GodotTextResourceParser;
using static Spirectl.DotnetTools.Inspection.SceneDocumentBuilder;

namespace Spirectl.DotnetTools.Inspection;

internal static class SceneCatalogLoader
{
    internal static SceneInspectionIndex Load(
        string resourcesDir,
        string? modsDir,
        string? assembliesDir)
    {
        var searchRoots = new List<InspectionSearchRoot>
        {
            new("game", resourcesDir),
        };

        if (!string.IsNullOrWhiteSpace(modsDir))
        {
            searchRoots.Add(new InspectionSearchRoot("mod", modsDir));
        }

        var scriptCatalog = GodotScriptCatalog.Load(assembliesDir, modsDir);
        var binaryCatalog = new GodotBinaryResourceCatalog();
        var packedCatalog = new GodotPackedResourceCatalog();
        var scenes = new List<SceneDocument>();
        var resources = new List<ResourceDocument>();
        var catalogNotes = new List<string>();
        var textResourceCount = 0;
        var binaryResourceCount = 0;
        var packedEntryCount = 0;

        foreach (var root in searchRoots)
        {
            foreach (var filePath in EnumerateTextResources(root.Path))
            {
                textResourceCount += 1;
                var parsed = ParseTextDocument(root.Source, CurrentDocumentPath(filePath, root.Path), File.ReadAllLines(filePath), scriptCatalog);
                switch (parsed)
                {
                    case SceneDocument scene:
                        scenes.Add(scene);
                        break;
                    case ResourceDocument resource:
                        resources.Add(resource);
                        break;
                }
            }

            foreach (var filePath in EnumerateBinaryResources(root.Path))
            {
                binaryResourceCount += 1;
                var logicalPath = CurrentDocumentPath(filePath, root.Path);
                var parsed = binaryCatalog.Parse(logicalPath, File.ReadAllBytes(filePath));
                catalogNotes.AddRange(parsed.Notes);

                switch (parsed.Document)
                {
                    case GodotBinaryResourceCatalog.ParsedSceneDocument scene:
                        scenes.Add(BuildBinarySceneDocument(root.Source, scene, scriptCatalog, "binary-file", null, parsed.Notes));
                        break;
                    case GodotBinaryResourceCatalog.ParsedResourceDocument resource:
                        resources.Add(BuildBinaryResourceDocument(root.Source, resource, scriptCatalog, "binary-file", null, parsed.Notes));
                        break;
                }
            }

            foreach (var packPath in EnumeratePackedResources(root.Path))
            {
                var packed = packedCatalog.Load(packPath);
                catalogNotes.AddRange(packed.Notes);

                foreach (var entry in packed.Entries)
                {
                    packedEntryCount += 1;
                    var extension = Path.GetExtension(entry.LogicalPath);
                    var entryBytes = packedCatalog.ReadEntryBytes(entry);

                    if (IsTextResourceExtension(extension))
                    {
                        var parsed = ParseTextDocument(root.Source, entry.LogicalPath, ReadUtf8Lines(entryBytes), scriptCatalog, "packed-entry", packPath);
                        switch (parsed)
                        {
                            case SceneDocument scene:
                                scenes.Add(scene);
                                break;
                            case ResourceDocument resource:
                                resources.Add(resource);
                                break;
                        }

                        continue;
                    }

                    if (IsBinaryResourceExtension(extension))
                    {
                        var parsed = binaryCatalog.Parse(entry.LogicalPath, entryBytes);
                        catalogNotes.AddRange(parsed.Notes);
                        switch (parsed.Document)
                        {
                            case GodotBinaryResourceCatalog.ParsedSceneDocument scene:
                                scenes.Add(BuildBinarySceneDocument(root.Source, scene, scriptCatalog, "packed-entry", packPath, parsed.Notes));
                                break;
                            case GodotBinaryResourceCatalog.ParsedResourceDocument resource:
                                resources.Add(BuildBinaryResourceDocument(root.Source, resource, scriptCatalog, "packed-entry", packPath, parsed.Notes));
                                break;
                        }
                    }
                }
            }
        }

        scenes = CollapseSceneDuplicates(scenes);
        resources = CollapseResourceDuplicates(resources);

        var sceneIdsByPath = scenes.ToDictionary(scene => scene.Path, scene => scene.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var scene in scenes)
        {
            scene.ResolveInstanceSceneIds(sceneIdsByPath);
        }

        return new SceneInspectionIndex(
            searchRoots: searchRoots,
            scenes: scenes.OrderBy(scene => scene.Path, StringComparer.Ordinal).ToArray(),
            resources: resources.OrderBy(resource => resource.Path, StringComparer.Ordinal).ToArray(),
            scriptCatalog: scriptCatalog,
            textResourceCount: textResourceCount,
            binaryResourceCount: binaryResourceCount,
            packedEntryCount: packedEntryCount,
            catalogNotes: catalogNotes.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static List<SceneDocument> CollapseSceneDuplicates(IEnumerable<SceneDocument> scenes)
    {
        var collapsed = new Dictionary<string, SceneDocument>(StringComparer.OrdinalIgnoreCase);
        foreach (var scene in scenes)
        {
            if (!collapsed.TryGetValue(scene.Path, out var existing))
            {
                collapsed[scene.Path] = scene;
                continue;
            }

            var winner = ChoosePreferred(existing, scene);
            var skipped = ReferenceEquals(winner, existing) ? scene : existing;
            winner.Notes.Add($"Skipped duplicate scene '{scene.Path}' from lower-priority storage '{skipped.StorageKind}'{FormatContainerNote(skipped.ContainerPath)}.");
            collapsed[scene.Path] = winner;
        }

        return collapsed.Values.ToList();
    }

    private static List<ResourceDocument> CollapseResourceDuplicates(IEnumerable<ResourceDocument> resources)
    {
        var collapsed = new Dictionary<string, ResourceDocument>(StringComparer.OrdinalIgnoreCase);
        foreach (var resource in resources)
        {
            if (!collapsed.TryGetValue(resource.Path, out var existing))
            {
                collapsed[resource.Path] = resource;
                continue;
            }

            var winner = ChoosePreferred(existing, resource);
            var skipped = ReferenceEquals(winner, existing) ? resource : existing;
            winner.Notes.Add($"Skipped duplicate resource '{resource.Path}' from lower-priority storage '{skipped.StorageKind}'{FormatContainerNote(skipped.ContainerPath)}.");
            collapsed[resource.Path] = winner;
        }

        return collapsed.Values.ToList();
    }

    private static string FormatContainerNote(string? containerPath)
    {
        return string.IsNullOrWhiteSpace(containerPath) ? string.Empty : $" in '{containerPath}'";
    }

    private static T ChoosePreferred<T>(T left, T right)
        where T : IStorageDocument
    {
        var leftPriority = StoragePriority(left.StorageKind);
        var rightPriority = StoragePriority(right.StorageKind);
        return leftPriority <= rightPriority ? left : right;
    }

    private static int StoragePriority(string storageKind) => storageKind switch
    {
        "text-file" => 0,
        "binary-file" => 1,
        "packed-entry" => 2,
        _ => 3,
    };

    private static string CurrentDocumentPath(string filePath, string rootPath)
    {
        var relativePath = Path.GetRelativePath(rootPath, filePath)
            .Replace('\\', '/')
            .TrimStart('/');
        return $"res://{relativePath}";
    }

    private static IEnumerable<string> EnumerateTextResources(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(path => IsTextResourceExtension(Path.GetExtension(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> EnumerateBinaryResources(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(path => IsBinaryResourceExtension(Path.GetExtension(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> EnumeratePackedResources(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(root, "*.pck", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsTextResourceExtension(string extension)
    {
        return extension.Equals(".tscn", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".escn", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".tres", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBinaryResourceExtension(string extension)
    {
        return extension.Equals(".scn", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".res", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> ReadUtf8Lines(byte[] bytes)
    {
        using var reader = new StringReader(System.Text.Encoding.UTF8.GetString(bytes));
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

}
