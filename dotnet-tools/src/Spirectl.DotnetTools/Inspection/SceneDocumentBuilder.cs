using System.Text.RegularExpressions;
using Spirectl.DotnetTools.Models;
using static Spirectl.DotnetTools.Inspection.SceneDocumentPaths;

namespace Spirectl.DotnetTools.Inspection;

internal static class SceneDocumentBuilder
{
    private static readonly Regex ExtResourcePattern = new(
        @"ExtResource\(""([^""]+)""\)",
        RegexOptions.Compiled);

    private static readonly Regex SubResourcePattern = new(
        @"SubResource\(""([^""]+)""\)",
        RegexOptions.Compiled);

    private static readonly Regex NodePathPattern = new(
        @"NodePath\(""([^""]*)""\)",
        RegexOptions.Compiled);

    internal static SceneDocument BuildSceneDocument(
        string source,
        string documentPath,
        string? uid,
        IReadOnlyDictionary<string, ExternalResourceEntry> extResources,
        IReadOnlyDictionary<string, SubResourceEntry> subResources,
        IReadOnlyList<PendingNodeEntry> pendingNodes,
        GodotScriptCatalog scriptCatalog,
        string storageKind = "text-file",
        string? containerPath = null,
        IReadOnlyList<string>? notes = null)
    {
        if (pendingNodes.Count == 0)
        {
            throw new ToolCommandException(3, "not_found", $"Scene '{documentPath}' did not contain any nodes.");
        }

        var rootNode = pendingNodes.First();
        var builtNodes = new List<SceneNodeEntry>(pendingNodes.Count);
        var nodesByPath = new Dictionary<string, SceneNodeEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var pendingNode in pendingNodes)
        {
            var parentNodePath = pendingNode.ParentRawPath is null
                ? null
                : NormalizeParentPath(rootNode.Name, pendingNode.ParentRawPath);
            var nodePath = parentNodePath is null
                ? $"/{pendingNode.Name}"
                : $"{parentNodePath}/{pendingNode.Name}";
            var scriptPath = ResolveAttachedScriptPath(documentPath, pendingNode.Properties, extResources);
            var scriptType = scriptCatalog.ResolveByScriptPath(scriptPath)
                ?? scriptCatalog.ResolveByGlobalClass(pendingNode.NodeType);

            var resourceRefs = pendingNode.Properties
                .Select(property => BuildResourceReference(documentPath, property, extResources, subResources))
                .Where(reference => reference is not null)
                .Cast<ResourceReferenceEntry>()
                .ToArray();

            var nodeRefs = pendingNode.Properties
                .Select(property => BuildNodeReference(nodePath, property))
                .Where(reference => reference is not null)
                .Cast<NodeReferenceEntry>()
                .ToArray();
            var renderProperties = BuildRenderProperties(pendingNode.Properties);

            var instanceScenePath = pendingNode.InstanceResourceId is not null
                && extResources.TryGetValue(pendingNode.InstanceResourceId, out var instanceResource)
                && string.Equals(instanceResource.Type, "PackedScene", StringComparison.OrdinalIgnoreCase)
                ? instanceResource.Path
                : null;

            var node = new SceneNodeEntry(
                id: $"node:{source}:{documentPath}:{nodePath}",
                name: pendingNode.Name,
                nodeType: pendingNode.NodeType,
                nodePath: nodePath,
                parentNodeId: null,
                parentNodePath: parentNodePath,
                depth: Math.Max(0, nodePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Length - 1),
                childCount: 0,
                source: source,
                storageKind: storageKind,
                containerPath: containerPath,
                instanceSceneId: null,
                instanceScenePath: instanceScenePath,
                attachedScriptPath: scriptPath ?? scriptType?.ScriptPath,
                attachedScriptType: scriptType?.FullName,
                attachedScriptTypeId: scriptType?.Id,
                resourceRefs: resourceRefs,
                nodeRefs: nodeRefs,
                renderProperties: renderProperties,
                notes: notes ?? []);

            builtNodes.Add(node);
            nodesByPath[nodePath] = node;
        }

        foreach (var node in builtNodes)
        {
            if (node.ParentNodePath is not null && nodesByPath.TryGetValue(node.ParentNodePath, out var parent))
            {
                node.ParentNodeId = parent.Id;
            }

            node.NodeRefs = node.NodeRefs
                .Select(reference =>
                {
                    if (reference.TargetNodePath is not null
                        && nodesByPath.TryGetValue(reference.TargetNodePath, out var target))
                    {
                        return reference with
                        {
                            TargetNodeId = target.Id,
                            Resolved = true,
                        };
                    }

                    return reference with
                    {
                        TargetNodeId = null,
                        Resolved = false,
                    };
                })
                .ToArray();
        }

        foreach (var group in builtNodes.Where(node => node.ParentNodeId is not null).GroupBy(node => node.ParentNodeId))
        {
            if (group.Key is not null && builtNodes.FirstOrDefault(node => node.Id == group.Key) is { } parent)
            {
                parent.ChildCount = group.Count();
            }
        }

        return new SceneDocument(
            id: $"scene:{source}:{documentPath}",
            displayName: Path.GetFileNameWithoutExtension(documentPath),
            path: documentPath,
            uid: uid,
            source: source,
            storageKind: storageKind,
            containerPath: containerPath,
            nodes: builtNodes,
            nodesByPath: nodesByPath,
            notes: notes ?? []);
    }

    internal static ResourceDocument BuildResourceDocument(
        string source,
        string documentPath,
        string? uid,
        string resourceType,
        IReadOnlyDictionary<string, ExternalResourceEntry> extResources,
        IReadOnlyList<PropertyEntry> rootProperties,
        GodotScriptCatalog scriptCatalog,
        string storageKind = "text-file",
        string? containerPath = null,
        IReadOnlyList<string>? notes = null)
    {
        var scriptPath = ResolveAttachedScriptPath(documentPath, rootProperties, extResources);
        var scriptType = scriptCatalog.ResolveByScriptPath(scriptPath)
            ?? scriptCatalog.ResolveByGlobalClass(resourceType);

        return new ResourceDocument(
            id: $"resource:{source}:{documentPath}",
            displayName: Path.GetFileNameWithoutExtension(documentPath),
            path: documentPath,
            uid: uid,
            source: source,
            storageKind: storageKind,
            containerPath: containerPath,
            resourceType: resourceType,
            attachedScriptPath: scriptPath ?? scriptType?.ScriptPath,
            attachedScriptType: scriptType?.FullName,
            attachedScriptTypeId: scriptType?.Id,
            notes: notes ?? []);
    }

    internal static SceneDocument BuildBinarySceneDocument(
        string source,
        GodotBinaryResourceCatalog.ParsedSceneDocument scene,
        GodotScriptCatalog scriptCatalog,
        string storageKind,
        string? containerPath,
        IReadOnlyList<string> notes)
    {
        var builtNodes = new List<SceneNodeEntry>(scene.Nodes.Count);
        var nodesByPath = new Dictionary<string, SceneNodeEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var parsedNode in scene.Nodes)
        {
            var scriptType = scriptCatalog.ResolveByScriptPath(parsedNode.AttachedScriptPath)
                ?? scriptCatalog.ResolveByGlobalClass(parsedNode.NodeType);
            var resourceRefs = parsedNode.ResourceRefs
                .Select(reference => new ResourceReferenceEntry(
                    reference.Property,
                    reference.TargetKind,
                    reference.ResourceId,
                    reference.ResourceType,
                    reference.ResourcePath))
                .ToArray();
            var nodeRefs = parsedNode.NodeRefs
                .Select(reference => new NodeReferenceEntry(
                    reference.Property,
                    reference.RawPath,
                    reference.TargetNodeId,
                    reference.TargetNodePath,
                    reference.Resolved))
                .ToArray();

            var node = new SceneNodeEntry(
                id: parsedNode.Id.Replace("node:binary:", $"node:{source}:", StringComparison.Ordinal),
                name: parsedNode.Name,
                nodeType: parsedNode.NodeType,
                nodePath: parsedNode.NodePath,
                parentNodeId: null,
                parentNodePath: parsedNode.ParentNodePath,
                depth: parsedNode.Depth,
                childCount: parsedNode.ChildCount,
                source: source,
                storageKind: storageKind,
                containerPath: containerPath,
                instanceSceneId: null,
                instanceScenePath: parsedNode.InstanceScenePath,
                attachedScriptPath: parsedNode.AttachedScriptPath ?? scriptType?.ScriptPath,
                attachedScriptType: scriptType?.FullName,
                attachedScriptTypeId: scriptType?.Id,
                resourceRefs: resourceRefs,
                nodeRefs: nodeRefs,
                renderProperties: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                notes: notes);
            builtNodes.Add(node);
            nodesByPath[node.NodePath] = node;
        }

        foreach (var node in builtNodes)
        {
            if (node.ParentNodePath is not null && nodesByPath.TryGetValue(node.ParentNodePath, out var parent))
            {
                node.ParentNodeId = parent.Id;
            }

            node.NodeRefs = node.NodeRefs
                .Select(reference =>
                {
                    if (reference.TargetNodePath is not null
                        && nodesByPath.TryGetValue(reference.TargetNodePath, out var target))
                    {
                        return reference with
                        {
                            TargetNodeId = target.Id,
                            Resolved = true,
                        };
                    }

                    return reference with
                    {
                        TargetNodeId = null,
                        Resolved = false,
                    };
                })
                .ToArray();
        }

        return new SceneDocument(
            id: $"scene:{source}:{scene.Path}",
            displayName: Path.GetFileNameWithoutExtension(scene.Path),
            path: scene.Path,
            uid: scene.Uid,
            source: source,
            storageKind: storageKind,
            containerPath: containerPath,
            nodes: builtNodes,
            nodesByPath: nodesByPath,
            notes: notes);
    }

    internal static ResourceDocument BuildBinaryResourceDocument(
        string source,
        GodotBinaryResourceCatalog.ParsedResourceDocument resource,
        GodotScriptCatalog scriptCatalog,
        string storageKind,
        string? containerPath,
        IReadOnlyList<string> notes)
    {
        var scriptType = scriptCatalog.ResolveByScriptPath(resource.AttachedScriptPath)
            ?? scriptCatalog.ResolveByGlobalClass(resource.ResourceType);
        return new ResourceDocument(
            id: $"resource:{source}:{resource.Path}",
            displayName: Path.GetFileNameWithoutExtension(resource.Path),
            path: resource.Path,
            uid: resource.Uid,
            source: source,
            storageKind: storageKind,
            containerPath: containerPath,
            resourceType: resource.ResourceType,
            attachedScriptPath: resource.AttachedScriptPath ?? scriptType?.ScriptPath,
            attachedScriptType: scriptType?.FullName,
            attachedScriptTypeId: scriptType?.Id,
            notes: notes);
    }

    private static ResourceReferenceEntry? BuildResourceReference(
        string documentPath,
        PropertyEntry property,
        IReadOnlyDictionary<string, ExternalResourceEntry> extResources,
        IReadOnlyDictionary<string, SubResourceEntry> subResources)
    {
        var extId = ParseExtResourceId(property.Value);
        if (extId is not null && extResources.TryGetValue(extId, out var extResource))
        {
            var targetKind = string.Equals(extResource.Type, "Script", StringComparison.OrdinalIgnoreCase)
                ? "script"
                : "external";
            return new ResourceReferenceEntry(
                property.Name,
                targetKind,
                extId,
                extResource.Type,
                NormalizeReferencedPath(documentPath, extResource.Path));
        }

        var subId = ParseSubResourceId(property.Value);
        if (subId is not null && subResources.TryGetValue(subId, out var subResource))
        {
            return new ResourceReferenceEntry(
                property.Name,
                "subresource",
                subId,
                subResource.Type,
                null);
        }

        return null;
    }

    private static NodeReferenceEntry? BuildNodeReference(string currentNodePath, PropertyEntry property)
    {
        var rawPath = ParseNodePath(property.Value);
        if (rawPath is null)
        {
            return null;
        }

        var targetNodePath = ResolveNodeTargetPath(currentNodePath, rawPath);
        return new NodeReferenceEntry(
            Property: property.Name,
            RawPath: rawPath,
            TargetNodeId: targetNodePath is null ? null : $"node-ref:{targetNodePath}",
            TargetNodePath: targetNodePath,
            Resolved: targetNodePath is not null);
    }

    private static IReadOnlyDictionary<string, string> BuildRenderProperties(IEnumerable<PropertyEntry> properties)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in properties)
        {
            if (IsRenderProperty(property.Name))
            {
                result[property.Name] = property.Value;
            }
        }

        return result;
    }

    private static bool IsRenderProperty(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var normalized = name.Trim();
        return normalized is "visible"
            or "position"
            or "scale"
            or "rotation"
            or "rotation_degrees"
            or "size"
            or "pivot_offset"
            or "z_index"
            or "texture"
            or "text"
            or "modulate"
            or "self_modulate"
            or "clip_contents"
            or "offset_left"
            or "offset_top"
            or "offset_right"
            or "offset_bottom"
            or "anchor_left"
            or "anchor_top"
            or "anchor_right"
            or "anchor_bottom";
    }

    private static string? ResolveAttachedScriptPath(
        string documentPath,
        IEnumerable<PropertyEntry> properties,
        IReadOnlyDictionary<string, ExternalResourceEntry> extResources)
    {
        var scriptProperty = properties.FirstOrDefault(property =>
            string.Equals(property.Name, "script", StringComparison.OrdinalIgnoreCase));
        if (scriptProperty is null)
        {
            return null;
        }

        var extId = ParseExtResourceId(scriptProperty.Value);
        if (extId is not null && extResources.TryGetValue(extId, out var extResource))
        {
            return NormalizeReferencedPath(documentPath, extResource.Path);
        }

        return ParseQuotedPath(scriptProperty.Value) is { } path
            ? NormalizeReferencedPath(documentPath, path)
            : null;
    }

    internal static string? ParseExtResourceId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = ExtResourcePattern.Match(value);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? ParseSubResourceId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = SubResourcePattern.Match(value);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? ParseNodePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = NodePathPattern.Match(value);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? ParseQuotedPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"'
            ? Unquote(trimmed)
            : null;
    }

}
