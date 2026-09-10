using Spirectl.DotnetTools.Models;
using static Spirectl.DotnetTools.Inspection.SceneDocumentPaths;

namespace Spirectl.DotnetTools.Inspection;

internal sealed class SceneInspectionIndex
{
    private readonly IReadOnlyList<InspectionSearchRoot> _searchRoots;
    private readonly IReadOnlyList<SceneDocument> _scenes;
    private readonly IReadOnlyDictionary<string, SceneDocument> _scenesByPath;
    private readonly IReadOnlyList<ResourceDocument> _resources;
    private readonly IReadOnlyDictionary<string, ResourceDocument> _resourcesByPath;
    private readonly GodotScriptCatalog _scriptCatalog;
    private readonly int _textResourceCount;
    private readonly int _binaryResourceCount;
    private readonly int _packedEntryCount;
    private readonly IReadOnlyList<string> _catalogNotes;

    internal SceneInspectionIndex(
        IReadOnlyList<InspectionSearchRoot> searchRoots,
        IReadOnlyList<SceneDocument> scenes,
        IReadOnlyList<ResourceDocument> resources,
        GodotScriptCatalog scriptCatalog,
        int textResourceCount,
        int binaryResourceCount,
        int packedEntryCount,
        IReadOnlyList<string> catalogNotes)
    {
        _searchRoots = searchRoots;
        _scenes = scenes;
        _resources = resources;
        _scenesByPath = scenes.ToDictionary(scene => scene.Path, StringComparer.OrdinalIgnoreCase);
        _resourcesByPath = resources.ToDictionary(resource => resource.Path, StringComparer.OrdinalIgnoreCase);
        _scriptCatalog = scriptCatalog;
        _textResourceCount = textResourceCount;
        _binaryResourceCount = binaryResourceCount;
        _packedEntryCount = packedEntryCount;
        _catalogNotes = catalogNotes;
    }

    public InspectionResponse Search(string query, int limit)
    {
        var normalizedQuery = query.Trim();
        var limitValue = Math.Max(1, limit);
        var candidates = BuildSearchCandidates()
            .Select(candidate => new { Candidate = candidate, Score = ScoreCandidate(candidate, normalizedQuery) })
            .Where(item => item.Score is not null)
            .Select(item => new { item.Candidate, Score = item.Score!.Value })
            .OrderByDescending(item => item.Score.Score)
            .ThenBy(item => item.Candidate.Match.Source, StringComparer.Ordinal)
            .ThenBy(item => item.Candidate.Match.FullName, StringComparer.Ordinal)
            .ToArray();

        return new InspectionResponse(
            Command: "scene-search",
            Status: candidates.Length == 0 ? "no-match" : "ok",
            Subject: "scene",
            Query: query,
            SearchRoots: _searchRoots,
            Notes: BuildNotes(
                candidates.Any(item => item.Candidate.HasUnresolvedScript),
                candidates.Select(item => item.Candidate.Notes).SelectMany(notes => notes)),
            MatchCount: candidates.Length,
            Truncated: candidates.Length > limitValue,
            Matches: candidates
                .Take(limitValue)
                .Select(item => item.Candidate.Match with
                {
                    MatchKind = item.Score.MatchKind,
                    Score = item.Score.Score,
                })
                .ToArray());
    }

    public InspectionResponse Tree(string sceneQuery)
    {
        var scene = ResolveScene(sceneQuery);
        return new InspectionResponse(
            Command: "scene-tree",
            Status: "ok",
            Subject: "scene",
            Query: sceneQuery,
            SearchRoots: _searchRoots,
            Notes: BuildNotes(scene.HasUnresolvedScript, scene.Notes),
            Id: scene.Id,
            Kind: "scene",
            DisplayName: scene.DisplayName,
            FullName: scene.Path,
            Source: scene.Source,
            SceneId: scene.Id,
            ScenePath: scene.Path,
            SceneUid: scene.Uid,
            StorageKind: scene.StorageKind,
            ContainerPath: scene.ContainerPath,
            Nodes: scene.Nodes.Select(node => node.ToInspectionNodeInfo()).ToArray());
    }

    public InspectionResponse Node(string sceneQuery, string nodePathQuery)
    {
        var scene = ResolveScene(sceneQuery);
        var normalizedNodePath = NormalizeNodePath(nodePathQuery);
        if (!scene.NodesByPath.TryGetValue(normalizedNodePath, out var node))
        {
            throw new ToolCommandException(
                3,
                "not_found",
                $"No exact node match was found for '{nodePathQuery}' in scene '{sceneQuery}'.");
        }

        var children = scene.Nodes
            .Where(current => string.Equals(current.ParentNodeId, node.Id, StringComparison.OrdinalIgnoreCase))
            .Select(child => child.ToInspectionChildInfo())
            .ToArray();

        return new InspectionResponse(
            Command: "scene-node",
            Status: "ok",
            Subject: "node",
            Query: sceneQuery,
            SearchRoots: _searchRoots,
            Notes: BuildNotes(scene.HasUnresolvedScript || node.HasUnresolvedScript, scene.Notes.Concat(node.Notes)),
            Id: node.Id,
            Kind: "node",
            DisplayName: node.Name,
            FullName: $"{scene.Path}::{node.NodePath}",
            Source: node.Source,
            SceneId: scene.Id,
            ScenePath: scene.Path,
            SceneUid: scene.Uid,
            StorageKind: node.StorageKind,
            ContainerPath: node.ContainerPath,
            NodeId: node.Id,
            NodeName: node.Name,
            NodeType: node.NodeType,
            NodePath: node.NodePath,
            ParentNodeId: node.ParentNodeId,
            ParentNodePath: node.ParentNodePath,
            Depth: node.Depth,
            ChildCount: node.ChildCount,
            InstanceSceneId: node.InstanceSceneId,
            InstanceScenePath: node.InstanceScenePath,
            AttachedScriptPath: node.AttachedScriptPath,
            AttachedScriptType: node.AttachedScriptType,
            AttachedScriptTypeId: node.AttachedScriptTypeId,
            Children: children,
            ResourceRefs: node.ResourceRefs
                .Select(reference => reference.ToInspection(_resourcesByPath, _scriptCatalog))
                .ToArray(),
            NodeRefs: node.NodeRefs.Select(reference => reference.ToInspection()).ToArray(),
            RenderProperties: node.RenderProperties.Count == 0 ? null : node.RenderProperties);
    }

    private IEnumerable<SearchCandidate> BuildSearchCandidates()
    {
        foreach (var scene in _scenes)
        {
            yield return new SearchCandidate(
                scene.ToSearchMatch(),
                ExactTexts:
                [
                    scene.Id,
                    scene.Path,
                    TrimResourcePrefix(scene.Path),
                    scene.Uid ?? string.Empty,
                    scene.DisplayName,
                ],
                PrefixTexts:
                [
                    scene.DisplayName,
                    scene.Path,
                    TrimResourcePrefix(scene.Path),
                ],
                SubstringTexts:
                [
                    scene.DisplayName,
                    scene.Path,
                    TrimResourcePrefix(scene.Path),
                ],
                HasUnresolvedScript: scene.HasUnresolvedScript,
                Notes: scene.Notes);

            foreach (var node in scene.Nodes)
            {
                yield return new SearchCandidate(
                    node.ToSearchMatch(scene.Path),
                    ExactTexts:
                    [
                        node.Id,
                        node.Name,
                        node.NodePath,
                        $"{scene.Path}::{node.NodePath}",
                        node.AttachedScriptType ?? string.Empty,
                    ],
                    PrefixTexts:
                    [
                        node.Name,
                        node.NodePath,
                        node.AttachedScriptType ?? string.Empty,
                        scene.Path,
                    ],
                    SubstringTexts:
                    [
                        node.Name,
                        node.NodePath,
                        $"{scene.Path}::{node.NodePath}",
                        node.AttachedScriptType ?? string.Empty,
                        scene.Path,
                    ],
                    HasUnresolvedScript: node.HasUnresolvedScript,
                    Notes: scene.Notes.Concat(node.Notes).ToArray());
            }
        }

        foreach (var resource in _resources)
        {
            yield return new SearchCandidate(
                resource.ToSearchMatch(),
                ExactTexts:
                [
                    resource.Id,
                    resource.Path,
                    TrimResourcePrefix(resource.Path),
                    resource.Uid ?? string.Empty,
                    resource.ResourceType,
                    resource.DisplayName,
                    resource.AttachedScriptType ?? string.Empty,
                ],
                PrefixTexts:
                [
                    resource.DisplayName,
                    resource.ResourceType,
                    resource.Path,
                    TrimResourcePrefix(resource.Path),
                    resource.AttachedScriptType ?? string.Empty,
                ],
                SubstringTexts:
                [
                    resource.DisplayName,
                    resource.ResourceType,
                    resource.Path,
                    TrimResourcePrefix(resource.Path),
                    resource.AttachedScriptType ?? string.Empty,
                ],
                HasUnresolvedScript: resource.HasUnresolvedScript,
                Notes: resource.Notes);
        }
    }

    private SceneDocument ResolveScene(string query)
    {
        var normalizedQuery = query.Trim();
        var matches = _scenes
            .Where(scene =>
                string.Equals(scene.Id, normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(scene.Path, normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(TrimResourcePrefix(scene.Path), normalizedQuery.TrimStart('/'), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(scene.Uid, normalizedQuery, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new ToolCommandException(3, "not_found", $"No exact scene match was found for '{query}'."),
            _ => throw new ToolCommandException(3, "ambiguous_query", $"Query '{query}' matched more than one scene. Use the exact scene id or full res:// path."),
        };
    }

    private IReadOnlyList<string> BuildNotes(bool hasUnresolvedScript, IEnumerable<string>? additionalNotes = null)
    {
        var notes = new List<string>
        {
            "Scene inspection indexes supported Godot text (.tscn, .escn, .tres), binary (.scn, .res), and unencrypted packed (.pck) scene/resource assets.",
            "Binary scene parsing is limited to PackedScene _bundled metadata plus script, node-path, and resource-reference fields needed for supported scene inspection surfaces.",
        };

        if (_textResourceCount == 0 && _binaryResourceCount == 0 && _packedEntryCount == 0)
        {
            notes.Add("No supported scene/resource files were indexed from the configured search roots.");
        }

        if (!_scriptCatalog.Enabled)
        {
            notes.Add("Managed script enrichment is disabled because no assemblies directory was provided.");
        }
        else
        {
            notes.Add("Managed script enrichment is best-effort and depends on Godot.ScriptPathAttribute or Godot.GlobalClassAttribute metadata.");
        }

        if (_packedEntryCount == 0)
        {
            notes.Add("No supported .pck entries were indexed from the configured search roots.");
        }

        if (hasUnresolvedScript)
        {
            notes.Add("Some attached scripts could not be resolved to managed types; raw script paths are returned when available.");
        }

        if (additionalNotes is not null)
        {
            notes.AddRange(additionalNotes.Where(note => !string.IsNullOrWhiteSpace(note)));
        }

        notes.AddRange(_catalogNotes);
        return notes.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static (string MatchKind, int Score)? ScoreCandidate(SearchCandidate candidate, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        foreach (var exactText in candidate.ExactTexts.Where(text => !string.IsNullOrWhiteSpace(text)))
        {
            if (string.Equals(exactText, query, StringComparison.OrdinalIgnoreCase))
            {
                return ("exact", 300);
            }
        }

        foreach (var prefixText in candidate.PrefixTexts.Where(text => !string.IsNullOrWhiteSpace(text)))
        {
            if (prefixText.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            {
                return ("prefix", 200);
            }
        }

        foreach (var text in candidate.SubstringTexts.Where(text => !string.IsNullOrWhiteSpace(text)))
        {
            if (text.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                return ("substring", 100);
            }
        }

        return null;
    }

    private sealed record SearchCandidate(
        InspectionMatch Match,
        IReadOnlyList<string> ExactTexts,
        IReadOnlyList<string> PrefixTexts,
        IReadOnlyList<string> SubstringTexts,
        bool HasUnresolvedScript,
        IReadOnlyList<string> Notes);

}
