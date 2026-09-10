using Spirectl.DotnetTools.Models;

namespace Spirectl.DotnetTools.Inspection;

internal interface IStorageDocument
{
    string StorageKind { get; }

    string? ContainerPath { get; }
}

internal sealed record PropertyEntry(string Name, string Value);

internal sealed record ExternalResourceEntry(
    string Id,
    string Type,
    string Path,
    string? Uid);

internal sealed record SubResourceEntry(
    string Id,
    string Type);

internal sealed class PendingNodeEntry
{
    public PendingNodeEntry(
        string name,
        string? nodeType,
        string? parentRawPath,
        string? instanceResourceId)
    {
        Name = name;
        NodeType = nodeType;
        ParentRawPath = parentRawPath;
        InstanceResourceId = instanceResourceId;
        Properties = [];
    }

    public string Name { get; }

    public string? NodeType { get; }

    public string? ParentRawPath { get; }

    public string? InstanceResourceId { get; }

    public List<PropertyEntry> Properties { get; }
}

internal sealed class SceneDocument : IStorageDocument
{
    public SceneDocument(
        string id,
        string displayName,
        string path,
        string? uid,
        string source,
        string storageKind,
        string? containerPath,
        IReadOnlyList<SceneNodeEntry> nodes,
        IReadOnlyDictionary<string, SceneNodeEntry> nodesByPath,
        IReadOnlyList<string> notes)
    {
        Id = id;
        DisplayName = displayName;
        Path = path;
        Uid = uid;
        Source = source;
        StorageKind = storageKind;
        ContainerPath = containerPath;
        Nodes = nodes;
        NodesByPath = nodesByPath;
        Notes = notes.ToList();
    }

    public string Id { get; }

    public string DisplayName { get; }

    public string Path { get; }

    public string? Uid { get; }

    public string Source { get; }

    public string StorageKind { get; }

    public string? ContainerPath { get; }

    public IReadOnlyList<SceneNodeEntry> Nodes { get; }

    public IReadOnlyDictionary<string, SceneNodeEntry> NodesByPath { get; }

    public List<string> Notes { get; }

    public bool HasUnresolvedScript => Nodes.Any(node => node.HasUnresolvedScript);

    public void ResolveInstanceSceneIds(IReadOnlyDictionary<string, string> sceneIdsByPath)
    {
        foreach (var node in Nodes)
        {
            if (node.InstanceScenePath is not null
                && sceneIdsByPath.TryGetValue(node.InstanceScenePath, out var sceneId))
            {
                node.InstanceSceneId = sceneId;
            }
        }
    }

    public InspectionMatch ToSearchMatch()
    {
        return new InspectionMatch(
            Id: Id,
            Kind: "scene",
            DisplayName: DisplayName,
            FullName: Path,
            Namespace: null,
            Assembly: null,
            AssemblyPath: null,
            Source: Source,
            MatchKind: string.Empty,
            Score: 0,
            Signature: null,
            SceneId: Id,
            ScenePath: Path,
            SceneUid: Uid,
            StorageKind: StorageKind,
            ContainerPath: ContainerPath,
            ResourcePath: null,
            ResourceType: null,
            NodePath: null,
            AttachedScriptPath: null,
            AttachedScriptType: null,
            AttachedScriptTypeId: null);
    }
}

internal sealed class ResourceDocument : IStorageDocument
{
    public ResourceDocument(
        string id,
        string displayName,
        string path,
        string? uid,
        string source,
        string storageKind,
        string? containerPath,
        string resourceType,
        string? attachedScriptPath,
        string? attachedScriptType,
        string? attachedScriptTypeId,
        IReadOnlyList<string> notes)
    {
        Id = id;
        DisplayName = displayName;
        Path = path;
        Uid = uid;
        Source = source;
        StorageKind = storageKind;
        ContainerPath = containerPath;
        ResourceType = resourceType;
        AttachedScriptPath = attachedScriptPath;
        AttachedScriptType = attachedScriptType;
        AttachedScriptTypeId = attachedScriptTypeId;
        Notes = notes.ToList();
    }

    public string Id { get; }

    public string DisplayName { get; }

    public string Path { get; }

    public string? Uid { get; }

    public string Source { get; }

    public string StorageKind { get; }

    public string? ContainerPath { get; }

    public string ResourceType { get; }

    public string? AttachedScriptPath { get; }

    public string? AttachedScriptType { get; }

    public string? AttachedScriptTypeId { get; }

    public List<string> Notes { get; }

    public bool HasUnresolvedScript => AttachedScriptPath is not null && AttachedScriptTypeId is null;

    public InspectionMatch ToSearchMatch()
    {
        return new InspectionMatch(
            Id: Id,
            Kind: "resource",
            DisplayName: DisplayName,
            FullName: Path,
            Namespace: null,
            Assembly: null,
            AssemblyPath: null,
            Source: Source,
            MatchKind: string.Empty,
            Score: 0,
            Signature: null,
            SceneId: null,
            ScenePath: null,
            SceneUid: Uid,
            StorageKind: StorageKind,
            ContainerPath: ContainerPath,
            ResourcePath: Path,
            ResourceType: ResourceType,
            NodePath: null,
            AttachedScriptPath: AttachedScriptPath,
            AttachedScriptType: AttachedScriptType,
            AttachedScriptTypeId: AttachedScriptTypeId);
    }
}

internal sealed class SceneNodeEntry
{
    public SceneNodeEntry(
        string id,
        string name,
        string? nodeType,
        string nodePath,
        string? parentNodeId,
        string? parentNodePath,
        int depth,
        int childCount,
        string source,
        string storageKind,
        string? containerPath,
        string? instanceSceneId,
        string? instanceScenePath,
        string? attachedScriptPath,
        string? attachedScriptType,
        string? attachedScriptTypeId,
        IReadOnlyList<ResourceReferenceEntry> resourceRefs,
        IReadOnlyList<NodeReferenceEntry> nodeRefs,
        IReadOnlyDictionary<string, string> renderProperties,
        IReadOnlyList<string> notes)
    {
        Id = id;
        Name = name;
        NodeType = nodeType;
        NodePath = nodePath;
        ParentNodeId = parentNodeId;
        ParentNodePath = parentNodePath;
        Depth = depth;
        ChildCount = childCount;
        Source = source;
        StorageKind = storageKind;
        ContainerPath = containerPath;
        InstanceSceneId = instanceSceneId;
        InstanceScenePath = instanceScenePath;
        AttachedScriptPath = attachedScriptPath;
        AttachedScriptType = attachedScriptType;
        AttachedScriptTypeId = attachedScriptTypeId;
        ResourceRefs = resourceRefs;
        NodeRefs = nodeRefs;
        RenderProperties = renderProperties;
        Notes = notes.ToList();
    }

    public string Id { get; }

    public string Name { get; }

    public string? NodeType { get; }

    public string NodePath { get; }

    public string? ParentNodeId { get; set; }

    public string? ParentNodePath { get; }

    public int Depth { get; }

    public int ChildCount { get; set; }

    public string Source { get; }

    public string StorageKind { get; }

    public string? ContainerPath { get; }

    public string? InstanceSceneId { get; set; }

    public string? InstanceScenePath { get; }

    public string? AttachedScriptPath { get; }

    public string? AttachedScriptType { get; }

    public string? AttachedScriptTypeId { get; }

    public IReadOnlyList<ResourceReferenceEntry> ResourceRefs { get; }

    public IReadOnlyList<NodeReferenceEntry> NodeRefs { get; set; }

    public IReadOnlyDictionary<string, string> RenderProperties { get; }

    public List<string> Notes { get; }

    public bool HasUnresolvedScript => AttachedScriptPath is not null && AttachedScriptTypeId is null;

    public InspectionMatch ToSearchMatch(string scenePath)
    {
        return new InspectionMatch(
            Id: Id,
            Kind: "node",
            DisplayName: Name,
            FullName: $"{scenePath}::{NodePath}",
            Namespace: null,
            Assembly: null,
            AssemblyPath: null,
            Source: Source,
            MatchKind: string.Empty,
            Score: 0,
            Signature: null,
            SceneId: null,
            ScenePath: scenePath,
            SceneUid: null,
            StorageKind: StorageKind,
            ContainerPath: ContainerPath,
            ResourcePath: null,
            ResourceType: NodeType,
            NodePath: NodePath,
            AttachedScriptPath: AttachedScriptPath,
            AttachedScriptType: AttachedScriptType,
            AttachedScriptTypeId: AttachedScriptTypeId);
    }

    public InspectionSceneNodeInfo ToInspectionNodeInfo()
    {
        return new InspectionSceneNodeInfo(
            Id: Id,
            Name: Name,
            NodeType: NodeType,
            NodePath: NodePath,
            ParentNodeId: ParentNodeId,
            ParentNodePath: ParentNodePath,
            Depth: Depth,
            ChildCount: ChildCount,
            Source: Source,
            StorageKind: StorageKind,
            ContainerPath: ContainerPath,
            InstanceSceneId: InstanceSceneId,
            InstanceScenePath: InstanceScenePath,
            AttachedScriptPath: AttachedScriptPath,
            AttachedScriptType: AttachedScriptType,
            AttachedScriptTypeId: AttachedScriptTypeId,
            RenderProperties: RenderProperties.Count == 0 ? null : RenderProperties);
    }

    public InspectionSceneChildInfo ToInspectionChildInfo()
    {
        return new InspectionSceneChildInfo(
            Id: Id,
            Name: Name,
            NodeType: NodeType,
            NodePath: NodePath,
            Source: Source,
            StorageKind: StorageKind,
            ContainerPath: ContainerPath,
            AttachedScriptPath: AttachedScriptPath,
            AttachedScriptType: AttachedScriptType,
            AttachedScriptTypeId: AttachedScriptTypeId);
    }
}

internal sealed record ResourceReferenceEntry(
    string Property,
    string TargetKind,
    string ResourceId,
    string? ResourceType,
    string? ResourcePath)
{
    public InspectionSceneResourceRefInfo ToInspection(
        IReadOnlyDictionary<string, ResourceDocument> resourcesByPath,
        GodotScriptCatalog scriptCatalog)
    {
        var attachedScriptPath = default(string);
        var attachedScriptType = default(string);
        var attachedScriptTypeId = default(string);
        var storageKind = default(string);
        var containerPath = default(string);

        if (ResourcePath is not null && resourcesByPath.TryGetValue(ResourcePath, out var resource))
        {
            attachedScriptPath = resource.AttachedScriptPath;
            attachedScriptType = resource.AttachedScriptType;
            attachedScriptTypeId = resource.AttachedScriptTypeId;
            storageKind = resource.StorageKind;
            containerPath = resource.ContainerPath;
        }
        else if (string.Equals(TargetKind, "script", StringComparison.OrdinalIgnoreCase))
        {
            var scriptType = scriptCatalog.ResolveByScriptPath(ResourcePath);
            attachedScriptPath = scriptType?.ScriptPath ?? ResourcePath;
            attachedScriptType = scriptType?.FullName;
            attachedScriptTypeId = scriptType?.Id;
        }

        return new InspectionSceneResourceRefInfo(
            Property: Property,
            TargetKind: TargetKind,
            ResourceId: ResourceId,
            ResourceType: ResourceType,
            ResourcePath: ResourcePath,
            StorageKind: storageKind,
            ContainerPath: containerPath,
            AttachedScriptPath: attachedScriptPath,
            AttachedScriptType: attachedScriptType,
            AttachedScriptTypeId: attachedScriptTypeId);
    }
}

internal sealed record NodeReferenceEntry(
    string Property,
    string RawPath,
    string? TargetNodeId,
    string? TargetNodePath,
    bool Resolved)
{
    public InspectionSceneNodeRefInfo ToInspection()
    {
        return new InspectionSceneNodeRefInfo(
            Property: Property,
            RawPath: RawPath,
            TargetNodeId: TargetNodeId,
            TargetNodePath: TargetNodePath,
            Resolved: Resolved);
    }
}
