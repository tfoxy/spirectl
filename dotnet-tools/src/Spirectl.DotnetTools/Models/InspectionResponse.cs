namespace Spirectl.DotnetTools.Models;

public sealed record InspectionResponse(
    string Command,
    string Status,
    string Subject,
    string Query,
    IReadOnlyList<InspectionSearchRoot> SearchRoots,
    IReadOnlyList<string> Notes,
    int? MatchCount = null,
    int? TotalCount = null,
    int? ReturnedCount = null,
    int? Limit = null,
    int? Offset = null,
    bool? Truncated = null,
    int? NextOffset = null,
    InspectionHookFacets? Facets = null,
    IReadOnlyList<InspectionMatch>? Matches = null,
    string? Id = null,
    string? Kind = null,
    string? DisplayName = null,
    string? FullName = null,
    string? Backend = null,
    string? Namespace = null,
    string? Assembly = null,
    string? AssemblyPath = null,
    string? Source = null,
    string? ResourcePath = null,
    string? ResourceType = null,
    string? Signature = null,
    string? TypeKind = null,
    string? Visibility = null,
    string? BaseType = null,
    string? BaseTypeId = null,
    IReadOnlyList<string>? Interfaces = null,
    IReadOnlyList<string>? InterfaceIds = null,
    IReadOnlyList<InspectionFieldInfo>? Fields = null,
    IReadOnlyList<InspectionPropertyInfo>? Properties = null,
    IReadOnlyList<InspectionEventInfo>? Events = null,
    IReadOnlyList<InspectionMethodInfo>? Methods = null,
    IReadOnlyList<InspectionNestedTypeInfo>? NestedTypes = null,
    InspectionMemberCounts? MemberCounts = null,
    string? DeclaringType = null,
    string? DeclaringTypeId = null,
    string? ReturnType = null,
    IReadOnlyList<InspectionParameterInfo>? Parameters = null,
    bool? IsStatic = null,
    bool? IsOverride = null,
    bool? IsAbstract = null,
    bool? IsVirtual = null,
    string? BaseMethodId = null,
    string? BaseMethod = null,
    IReadOnlyList<string>? ImplementedInterfaceMethodIds = null,
    IReadOnlyList<string>? HookForms = null,
    IReadOnlyList<string>? Reasons = null,
    string? ScriptPath = null,
    IReadOnlyList<string>? ScenePaths = null,
    IReadOnlyList<string>? SuggestedNextCommands = null,
    int? ReferenceCount = null,
    IReadOnlyList<InspectionReferenceInfo>? References = null,
    int? DerivedCount = null,
    IReadOnlyList<InspectionDerivedTypeInfo>? DerivedTypes = null,
    IReadOnlyList<InspectionMemberSummary>? MemberSummaries = null,
    string? SceneId = null,
    string? ScenePath = null,
    string? SceneUid = null,
    string? StorageKind = null,
    string? ContainerPath = null,
    string? NodeId = null,
    string? NodeName = null,
    string? NodeType = null,
    string? NodePath = null,
    string? ParentNodeId = null,
    string? ParentNodePath = null,
    int? Depth = null,
    int? ChildCount = null,
    string? InstanceSceneId = null,
    string? InstanceScenePath = null,
    string? AttachedScriptPath = null,
    string? AttachedScriptType = null,
    string? AttachedScriptTypeId = null,
    IReadOnlyList<InspectionSceneNodeInfo>? Nodes = null,
    IReadOnlyList<InspectionSceneChildInfo>? Children = null,
    IReadOnlyList<InspectionSceneResourceRefInfo>? ResourceRefs = null,
    IReadOnlyList<InspectionSceneNodeRefInfo>? NodeRefs = null,
    IReadOnlyDictionary<string, string>? RenderProperties = null,
    string? Language = null,
    string? Text = null);

public sealed record InspectionHookFacets(
    IReadOnlyList<InspectionFacetEntry> Sources,
    IReadOnlyList<InspectionFacetEntry> Assemblies,
    IReadOnlyList<InspectionFacetEntry> HookForms);

public sealed record InspectionFacetEntry(string Value, int Count);

public sealed record InspectionSearchRoot(string Source, string Path);

public sealed record InspectionFieldInfo(
    string Id,
    string Name,
    string Type,
    string Visibility,
    bool IsStatic);

public sealed record InspectionPropertyInfo(
    string Id,
    string Name,
    string Type,
    string Visibility,
    bool HasGetter,
    bool HasSetter,
    string? GetterVisibility = null,
    string? SetterVisibility = null);

public sealed record InspectionEventInfo(
    string Id,
    string Name,
    string Type,
    string Visibility,
    bool HasAdder,
    bool HasRemover,
    string? AdderVisibility = null,
    string? RemoverVisibility = null);

public sealed record InspectionMethodInfo(
    string Id,
    string Name,
    string Visibility,
    string ReturnType,
    string Signature,
    IReadOnlyList<InspectionParameterInfo> Parameters,
    bool IsStatic,
    bool IsAbstract,
    bool IsVirtual);

public sealed record InspectionParameterInfo(string Name, string Type, string? Modifier = null);

public sealed record InspectionNestedTypeInfo(
    string Id,
    string Name,
    string FullName,
    string TypeKind,
    string Visibility);

public sealed record InspectionMemberCounts(
    int Fields,
    int Properties,
    int Events,
    int Methods,
    int NestedTypes);

public sealed record InspectionReferenceInfo(
    string ContainerId,
    string ContainerKind,
    string ContainerDisplayName,
    string ContainerFullName,
    string? ContainerSignature,
    string? TargetId,
    string TargetKind,
    string TargetDisplayName,
    string TargetFullName,
    string? TargetSignature,
    string ReferenceKind,
    string Via,
    string Precision,
    string? MatchedText,
    string Assembly,
    string Source);

public sealed record InspectionDerivedTypeInfo(
    string Id,
    string DisplayName,
    string FullName,
    string Namespace,
    string Assembly,
    string AssemblyPath,
    string Source,
    string RelationKind);

public sealed record InspectionMemberSummary(
    string MethodId,
    string Name,
    string Signature,
    IReadOnlyList<InspectionSummaryReference> Calls,
    IReadOnlyList<InspectionSummaryReference> FieldReads,
    IReadOnlyList<InspectionSummaryReference> FieldWrites,
    IReadOnlyList<InspectionSummaryReference> TypeRefs);

public sealed record InspectionSummaryReference(
    string? Id,
    string Kind,
    string DisplayName,
    string FullName,
    string? Signature,
    string Via,
    string Precision);

public sealed record InspectionSceneNodeInfo(
    string Id,
    string Name,
    string? NodeType,
    string NodePath,
    string? ParentNodeId,
    string? ParentNodePath,
    int Depth,
    int ChildCount,
    string Source,
    string? StorageKind = null,
    string? ContainerPath = null,
    string? InstanceSceneId = null,
    string? InstanceScenePath = null,
    string? AttachedScriptPath = null,
    string? AttachedScriptType = null,
    string? AttachedScriptTypeId = null,
    IReadOnlyDictionary<string, string>? RenderProperties = null);

public sealed record InspectionSceneChildInfo(
    string Id,
    string Name,
    string? NodeType,
    string NodePath,
    string Source,
    string? StorageKind = null,
    string? ContainerPath = null,
    string? AttachedScriptPath = null,
    string? AttachedScriptType = null,
    string? AttachedScriptTypeId = null);

public sealed record InspectionSceneResourceRefInfo(
    string Property,
    string TargetKind,
    string ResourceId,
    string? ResourceType,
    string? ResourcePath,
    string? StorageKind = null,
    string? ContainerPath = null,
    string? AttachedScriptPath = null,
    string? AttachedScriptType = null,
    string? AttachedScriptTypeId = null);

public sealed record InspectionSceneNodeRefInfo(
    string Property,
    string RawPath,
    string? TargetNodeId,
    string? TargetNodePath,
    bool Resolved);
