using System.Text.RegularExpressions;
using static Spirectl.DotnetTools.Inspection.SceneDocumentBuilder;
using static Spirectl.DotnetTools.Inspection.SceneDocumentPaths;

namespace Spirectl.DotnetTools.Inspection;

internal static class GodotTextResourceParser
{
    private static readonly Regex HeaderTokenPattern = new(
        @"([A-Za-z0-9_]+)=(""[^""]*""|[^\s\]]+)",
        RegexOptions.Compiled);

    internal static object? ParseTextDocument(
        string source,
        string documentPath,
        IEnumerable<string> lines,
        GodotScriptCatalog scriptCatalog,
        string storageKind = "text-file",
        string? containerPath = null)
    {
        var materializedLines = lines.ToArray();
        if (materializedLines.Length == 0)
        {
            return null;
        }

        string? descriptorKind = null;
        Dictionary<string, string>? descriptorAttributes = null;
        var extResources = new Dictionary<string, ExternalResourceEntry>(StringComparer.OrdinalIgnoreCase);
        var subResources = new Dictionary<string, SubResourceEntry>(StringComparer.OrdinalIgnoreCase);
        var pendingNodes = new List<PendingNodeEntry>();
        var rootResourceProperties = new List<PropertyEntry>();
        PendingNodeEntry? currentNode = null;
        var inRootResourceSection = false;

        foreach (var line in materializedLines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith(';'))
            {
                continue;
            }

            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                var (headerKind, attributes) = ParseHeader(trimmed);
                currentNode = null;
                inRootResourceSection = false;

                switch (headerKind)
                {
                    case "gd_scene":
                    case "gd_resource":
                        descriptorKind = headerKind;
                        descriptorAttributes = attributes;
                        break;
                    case "ext_resource":
                        if (attributes.TryGetValue("id", out var extId))
                        {
                            extResources[extId] = new ExternalResourceEntry(
                                Id: extId,
                                Type: attributes.GetValueOrDefault("type") ?? "Resource",
                                Path: NormalizeReferencedPath(documentPath, attributes.GetValueOrDefault("path")),
                                Uid: attributes.GetValueOrDefault("uid"));
                        }
                        break;
                    case "sub_resource":
                        if (attributes.TryGetValue("id", out var subId))
                        {
                            subResources[subId] = new SubResourceEntry(
                                Id: subId,
                                Type: attributes.GetValueOrDefault("type") ?? "Resource");
                        }
                        break;
                    case "node":
                        var pendingNode = new PendingNodeEntry(
                            name: attributes.GetValueOrDefault("name") ?? "UnnamedNode",
                            nodeType: attributes.GetValueOrDefault("type"),
                            parentRawPath: attributes.GetValueOrDefault("parent"),
                            instanceResourceId: ParseExtResourceId(attributes.GetValueOrDefault("instance")));
                        pendingNodes.Add(pendingNode);
                        currentNode = pendingNode;
                        break;
                    case "resource":
                        inRootResourceSection = true;
                        break;
                }

                continue;
            }

            var property = ParseProperty(trimmed);
            if (property is null)
            {
                continue;
            }

            if (currentNode is not null)
            {
                currentNode.Properties.Add(property);
            }
            else if (inRootResourceSection)
            {
                rootResourceProperties.Add(property);
            }
        }

        if (descriptorKind == "gd_scene" && descriptorAttributes is not null)
        {
            return BuildSceneDocument(
                source,
                documentPath,
                descriptorAttributes.GetValueOrDefault("uid"),
                extResources,
                subResources,
                pendingNodes,
                scriptCatalog,
                storageKind,
                containerPath);
        }

        if (descriptorKind == "gd_resource" && descriptorAttributes is not null)
        {
            return BuildResourceDocument(
                source,
                documentPath,
                descriptorAttributes.GetValueOrDefault("uid"),
                descriptorAttributes.GetValueOrDefault("type") ?? "Resource",
                extResources,
                rootResourceProperties,
                scriptCatalog,
                storageKind,
                containerPath);
        }

        return null;
    }

    private static (string HeaderKind, Dictionary<string, string> Attributes) ParseHeader(string headerLine)
    {
        var content = headerLine[1..^1];
        var separatorIndex = content.IndexOf(' ');
        var headerKind = separatorIndex >= 0 ? content[..separatorIndex] : content;
        var attributesText = separatorIndex >= 0 ? content[(separatorIndex + 1)..] : string.Empty;
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in HeaderTokenPattern.Matches(attributesText))
        {
            attributes[match.Groups[1].Value] = Unquote(match.Groups[2].Value);
        }

        return (headerKind, attributes);
    }

    private static PropertyEntry? ParseProperty(string line)
    {
        var separatorIndex = line.IndexOf('=');
        if (separatorIndex <= 0)
        {
            return null;
        }

        var name = line[..separatorIndex].Trim();
        var value = line[(separatorIndex + 1)..].Trim();
        return string.IsNullOrWhiteSpace(name)
            ? null
            : new PropertyEntry(name, value);
    }

}
