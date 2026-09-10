using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using Spirectl.DotnetTools.Models;

namespace Spirectl.DotnetTools.Inspection;

internal enum MetadataCatalogLoadMode
{
    DeclarationsOnly,
    WithReferences,
}

internal sealed record SelectedAssemblyPath(string Path, string Source)
{
    public string CanonicalPath { get; } = MetadataCatalogCache.CanonicalizePath(Path);
}

internal sealed partial class MetadataCatalog
{
    private readonly IReadOnlyList<InspectionSearchRoot> _searchRoots;
    private readonly IReadOnlyList<TypeSymbol> _types;
    private readonly IReadOnlyList<MethodSymbol> _methods;
    private readonly IReadOnlyList<ReferenceEdge> _references;

    private MetadataCatalog(
        IReadOnlyList<InspectionSearchRoot> searchRoots,
        IReadOnlyList<TypeSymbol> types,
        IReadOnlyList<MethodSymbol> methods,
        IReadOnlyList<ReferenceEdge> references)
    {
        _searchRoots = searchRoots;
        _types = types;
        _methods = methods;
        _references = references;
    }

    public static MetadataCatalog Load(
        string assembliesDir,
        string? modsDir,
        MetadataCatalogLoadMode mode,
        bool includeDependencies = false,
        IReadOnlyList<SelectedAssemblyPath>? selectedAssemblies = null)
    {
        return BuildUncached(assembliesDir, modsDir, mode, includeDependencies, selectedAssemblies);
    }

    internal static MetadataCatalog BuildUncached(
        string assembliesDir,
        string? modsDir,
        MetadataCatalogLoadMode mode,
        bool includeDependencies = false,
        IReadOnlyList<SelectedAssemblyPath>? selectedAssemblies = null)
    {
        var searchRoots = MetadataCatalogCache.BuildSearchRoots(assembliesDir, modsDir, includeMods: true);
        var assemblyPaths = selectedAssemblies is { Count: > 0 }
            ? selectedAssemblies
            : SelectAssemblyPaths(searchRoots, includeDependencies);

        if (mode == MetadataCatalogLoadMode.DeclarationsOnly)
        {
            return FromSnapshots(BuildDeclarationSnapshots(searchRoots, assemblyPaths));
        }

        var snapshot = BuildSnapshot(searchRoots, LoadAssemblyIndexes(assemblyPaths), mode);
        return FromSnapshot(snapshot);
    }

    internal static IReadOnlyList<MetadataCatalogSnapshot> BuildDeclarationSnapshots(
        IReadOnlyList<InspectionSearchRoot> searchRoots,
        IReadOnlyList<SelectedAssemblyPath> selectedAssemblies)
    {
        return selectedAssemblies
            .Select(selectedAssembly => BuildSnapshot(
                searchRoots,
                [AssemblyIndex.LoadMetadata(selectedAssembly.CanonicalPath, selectedAssembly.Source)],
                MetadataCatalogLoadMode.DeclarationsOnly))
            .ToArray();
    }

    internal static MetadataCatalogSnapshot BuildDeclarationSnapshot(
        IReadOnlyList<InspectionSearchRoot> searchRoots,
        IReadOnlyList<SelectedAssemblyPath> selectedAssemblies)
    {
        return BuildSnapshot(searchRoots, LoadAssemblyIndexes(selectedAssemblies), MetadataCatalogLoadMode.DeclarationsOnly);
    }

    private static IReadOnlyList<SelectedAssemblyPath> SelectAssemblyPaths(
        IReadOnlyList<InspectionSearchRoot> searchRoots,
        bool includeDependencies)
    {
        var selectedAssemblies = new List<SelectedAssemblyPath>();
        foreach (var root in searchRoots)
        {
            foreach (var assemblyPath in ManagedAssemblySelector.SelectAssemblyFiles(root, includeDependencies))
            {
                selectedAssemblies.Add(new SelectedAssemblyPath(assemblyPath, root.Source));
            }
        }

        return selectedAssemblies;
    }

    private static IReadOnlyList<AssemblyIndex> LoadAssemblyIndexes(IReadOnlyList<SelectedAssemblyPath> selectedAssemblies)
    {
        return selectedAssemblies
            .Select(selectedAssembly => AssemblyIndex.LoadMetadata(selectedAssembly.CanonicalPath, selectedAssembly.Source))
            .ToArray();
    }

    private static MetadataCatalogSnapshot BuildSnapshot(
        IReadOnlyList<InspectionSearchRoot> searchRoots,
        IReadOnlyList<AssemblyIndex> assemblies,
        MetadataCatalogLoadMode mode)
    {
        var lookup = BuildLookup(assemblies);
        var references = new List<ReferenceEdge>();

        switch (mode)
        {
            case MetadataCatalogLoadMode.DeclarationsOnly:
                break;
            case MetadataCatalogLoadMode.WithReferences:
                foreach (var assembly in assemblies)
                {
                    AddDeclarationReferences(assembly, lookup, references);
                }

                foreach (var assembly in assemblies)
                {
                    ScanMethodBodies(assembly, lookup, references);
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown metadata catalog load mode.");
        }

        return ExportSnapshot(searchRoots, assemblies, lookup, references);
    }

    private static MetadataCatalogSnapshot ExportSnapshot(
        IReadOnlyList<InspectionSearchRoot> searchRoots,
        IReadOnlyList<AssemblyIndex> assemblies,
        SymbolLookup lookup,
        IEnumerable<ReferenceEdge> references)
    {
        FinalizeBuilders(assemblies, lookup);

        return new MetadataCatalogSnapshot(
            searchRoots,
            assemblies
                .SelectMany(assembly => assembly.Types.Values)
                .Select(builder => builder.ToTypeSymbol())
                .OrderBy(symbol => symbol.FullName, StringComparer.Ordinal)
                .ToArray(),
            assemblies
                .SelectMany(assembly => assembly.Methods.Values)
                .Select(builder => builder.ToMethodSymbol())
                .OrderBy(symbol => symbol.FullName, StringComparer.Ordinal)
                .ThenBy(symbol => symbol.Signature, StringComparer.Ordinal)
                .ToArray(),
            OrderReferences(references));
    }

    private static IReadOnlyList<ReferenceEdge> OrderReferences(IEnumerable<ReferenceEdge> references)
    {
        return references
            .Distinct()
            .OrderBy(reference => reference.ContainerFullName, StringComparer.Ordinal)
            .ThenBy(reference => reference.ReferenceKind, StringComparer.Ordinal)
            .ThenBy(reference => reference.Via, StringComparer.Ordinal)
            .ThenBy(reference => reference.TargetFullName, StringComparer.Ordinal)
            .ToArray();
    }

    internal static MetadataCatalog FromSnapshots(IReadOnlyList<MetadataCatalogSnapshot> snapshots)
    {
        var searchRoots = snapshots
            .SelectMany(snapshot => snapshot.SearchRoots)
            .Distinct()
            .ToArray();
        var types = snapshots
            .SelectMany(snapshot => snapshot.Types)
            .OrderBy(symbol => symbol.FullName, StringComparer.Ordinal)
            .ToArray();
        var typeLookup = types
            .GroupBy(type => type.FullName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var nestedTypesByParent = types
            .Select(type => new { Type = type, DeclaringTypeFullName = FindDeclaringTypeFullName(type.FullName, typeLookup) })
            .Where(item => item.DeclaringTypeFullName is not null)
            .GroupBy(item => item.DeclaringTypeFullName!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(item => item.Type.FullName, StringComparer.Ordinal)
                    .Select(item => new NestedTypeSymbol(
                        item.Type.Id,
                        item.Type.DisplayName,
                        item.Type.FullName,
                        item.Type.TypeKind,
                        item.Type.Visibility))
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);

        MethodSymbol FinalizeMethod(MethodSymbol method)
        {
            var declaringTypeId = ResolveTypeId(typeLookup, method.DeclaringType)
                ?? $"type:{method.Assembly}:{method.DeclaringType}";

            return method with
            {
                DeclaringTypeId = declaringTypeId,
                Calls = DistinctSummaryReferences(method.Calls).ToArray(),
                FieldReads = DistinctSummaryReferences(method.FieldReads).ToArray(),
                FieldWrites = DistinctSummaryReferences(method.FieldWrites).ToArray(),
                TypeRefs = DistinctSummaryReferences(method.TypeRefs).ToArray(),
            };
        }

        var finalizedTypes = types
            .Select(type =>
            {
                var baseTypeId = ResolveTypeId(typeLookup, type.BaseType);
                var interfaceIds = type.Interfaces
                    .Select(interfaceName => ResolveTypeId(typeLookup, interfaceName))
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Cast<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToArray();
                var nestedTypes = nestedTypesByParent.TryGetValue(type.FullName, out var nestedTypesForType)
                    ? nestedTypesForType
                    : [];
                var relationships = BuildRelationships(type, baseTypeId, interfaceIds);

                return type with
                {
                    BaseTypeId = baseTypeId,
                    InterfaceIds = interfaceIds,
                    Methods = type.Methods.Select(FinalizeMethod).ToArray(),
                    NestedTypes = nestedTypes,
                    Relationships = relationships,
                };
            })
            .ToArray();

        var finalizedMethods = snapshots
            .SelectMany(snapshot => snapshot.Methods)
            .Select(FinalizeMethod)
            .OrderBy(symbol => symbol.FullName, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.Signature, StringComparer.Ordinal)
            .ToArray();

        return new MetadataCatalog(
            searchRoots,
            finalizedTypes,
            finalizedMethods,
            OrderReferences(snapshots.SelectMany(snapshot => snapshot.References)));
    }

    internal MetadataCatalogSnapshot ExportSnapshot()
    {
        return new MetadataCatalogSnapshot(_searchRoots, _types, _methods, _references);
    }

    internal static MetadataCatalog FromSnapshot(MetadataCatalogSnapshot snapshot)
    {
        return FromSnapshots([snapshot]);
    }

    public InspectionResponse Locate(string subject, string query, int limit)
    {
        var trimmedQuery = query.Trim();
        var limitValue = Math.Max(1, limit);
        var matches = subject switch
        {
            "type" => LocateMatches(_types.Select(MatchCandidate.FromType), trimmedQuery),
            "method" => LocateMatches(_methods.Select(MatchCandidate.FromMethod), trimmedQuery),
            "symbol" => LocateMatches(
                _types.Select(MatchCandidate.FromType).Concat(_methods.Select(MatchCandidate.FromMethod)),
                trimmedQuery),
            _ => throw new ToolCommandException(2, "usage_error", $"unknown subject '{subject}'"),
        };

        var ordered = matches
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Source, StringComparer.OrdinalIgnoreCase)
            .ThenBy(match => match.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new InspectionResponse(
            Command: "locate",
            Status: ordered.Count == 0 ? "no-match" : "ok",
            Subject: subject,
            Query: query,
            SearchRoots: _searchRoots,
            Notes:
            [
                "Locate ranks exact, prefix, then substring matches across names and namespaces.",
            ],
            MatchCount: ordered.Count,
            Truncated: ordered.Count > limitValue,
            Matches: ordered.Take(limitValue).ToArray());
    }

    public InspectionResponse Describe(string subject, string query)
    {
        return subject switch
        {
            "type" => BuildTypeDescription(query),
            "method" => BuildMethodDescription(query),
            _ => throw new ToolCommandException(2, "usage_error", $"unknown subject '{subject}'"),
        };
    }

    public InspectionResponse Decompile(string subject, string query, bool full)
    {
        return subject switch
        {
            "type" => BuildTypeDecompile(query, full),
            "method" => BuildMethodDecompile(query, full),
            _ => throw new ToolCommandException(2, "usage_error", $"unknown subject '{subject}'"),
        };
    }

    public InspectionResponse Refs(string subject, string query, int limit)
    {
        return subject switch
        {
            "type" => BuildExactRefsResponse("type", ResolveType(query).Id, query, limit),
            "method" => BuildExactRefsResponse("method", ResolveMethod(query).Id, query, limit),
            "symbol" => BuildSymbolRefsResponse(query, limit),
            _ => throw new ToolCommandException(2, "usage_error", $"unknown subject '{subject}'"),
        };
    }

    public InspectionResponse Derived(string subject, string query, int limit)
    {
        if (subject != "type")
        {
            throw new ToolCommandException(2, "usage_error", $"unknown subject '{subject}'");
        }

        var target = ResolveType(query);
        var derivedTypes = _types
            .SelectMany(type => type.Relationships)
            .Where(relationship => relationship.TargetTypeId == target.Id)
            .Select(relationship => relationship.ToInspectionDerivedType())
            .OrderBy(derived => derived.RelationKind, StringComparer.Ordinal)
            .ThenBy(derived => derived.FullName, StringComparer.Ordinal)
            .ToArray();
        var limitValue = Math.Max(1, limit);

        return new InspectionResponse(
            Command: "derived",
            Status: derivedTypes.Length == 0 ? "no-match" : "ok",
            Subject: subject,
            Query: query,
            SearchRoots: _searchRoots,
            Notes:
            [
                "Derived returns direct base-class, interface-implementation, and interface-inheritance relationships from indexed assemblies only.",
            ],
            Id: target.Id,
            Kind: "type",
            DisplayName: target.DisplayName,
            FullName: target.FullName,
            Namespace: target.Namespace,
            Assembly: target.Assembly,
            AssemblyPath: target.AssemblyPath,
            Source: target.Source,
            DerivedCount: derivedTypes.Length,
            Truncated: derivedTypes.Length > limitValue,
            DerivedTypes: derivedTypes.Take(limitValue).ToArray());
    }

    internal IReadOnlyList<InspectionSearchRoot> SearchRoots => _searchRoots;

    internal IReadOnlyList<HookTypeRecord> ExportHookTypes()
    {
        return _types
            .Select(type => new HookTypeRecord(
                type.Id,
                type.DisplayName,
                type.FullName,
                type.Namespace,
                type.Assembly,
                type.AssemblyPath,
                type.Source,
                type.TypeKind,
                type.Visibility,
                type.BaseTypeId,
                type.InterfaceIds))
            .ToArray();
    }

    internal IReadOnlyList<HookMethodRecord> ExportHookMethods()
    {
        return _methods
            .Select(method => new HookMethodRecord(
                method.Id,
                method.DisplayName,
                method.FullName,
                method.Namespace,
                method.Assembly,
                method.AssemblyPath,
                method.Source,
                method.Signature,
                method.LookupSignature,
                method.DeclaringType,
                method.DeclaringTypeId,
                method.Visibility,
                method.ReturnType,
                method.Parameters,
                method.IsStatic,
                method.IsAbstract,
                method.IsVirtual))
            .ToArray();
    }

    internal int CountInboundReferences(string targetId)
    {
        return _references.Count(reference => string.Equals(reference.TargetId, targetId, StringComparison.OrdinalIgnoreCase));
    }

    private InspectionResponse BuildTypeDescription(string query)
    {
        var type = ResolveType(query);
        return new InspectionResponse(
            Command: "describe",
            Status: "ok",
            Subject: "type",
            Query: query,
            SearchRoots: _searchRoots,
            Notes:
            [
                "Describe returns declared members only; follow baseTypeId and interfaceIds separately when you need inherited API surface.",
            ],
            Id: type.Id,
            Kind: "type",
            DisplayName: type.DisplayName,
            FullName: type.FullName,
            Namespace: type.Namespace,
            Assembly: type.Assembly,
            AssemblyPath: type.AssemblyPath,
            Source: type.Source,
            TypeKind: type.TypeKind,
            Visibility: type.Visibility,
            BaseType: type.BaseType,
            BaseTypeId: type.BaseTypeId,
            Interfaces: type.Interfaces,
            InterfaceIds: type.InterfaceIds,
            Fields: type.Fields.Select(field => field.ToInspectionField()).ToArray(),
            Properties: type.Properties.Select(property => property.ToInspectionProperty()).ToArray(),
            Events: type.Events.Select(eventSymbol => eventSymbol.ToInspectionEvent()).ToArray(),
            Methods: type.Methods.Select(method => method.ToInspectionMethod()).ToArray(),
            NestedTypes: type.NestedTypes.Select(nestedType => nestedType.ToInspectionNestedType()).ToArray(),
            MemberCounts: new InspectionMemberCounts(
                type.Fields.Count,
                type.Properties.Count,
                type.Events.Count,
                type.Methods.Count,
                type.NestedTypes.Count));
    }

    private InspectionResponse BuildMethodDescription(string query)
    {
        var method = ResolveMethod(query);
        return new InspectionResponse(
            Command: "describe",
            Status: "ok",
            Subject: "method",
            Query: query,
            SearchRoots: _searchRoots,
            Notes:
            [
                "Describe resolves one exact method; use locate first when a name is overloaded or repeated across assemblies.",
            ],
            Id: method.Id,
            Kind: "method",
            DisplayName: method.DisplayName,
            FullName: method.FullName,
            Namespace: method.Namespace,
            Assembly: method.Assembly,
            AssemblyPath: method.AssemblyPath,
            Source: method.Source,
            Signature: method.Signature,
            DeclaringType: method.DeclaringType,
            DeclaringTypeId: method.DeclaringTypeId,
            Visibility: method.Visibility,
            ReturnType: method.ReturnType,
            Parameters: method.Parameters,
            IsStatic: method.IsStatic,
            IsAbstract: method.IsAbstract,
            IsVirtual: method.IsVirtual);
    }

    private InspectionResponse BuildTypeDecompile(string query, bool full)
    {
        var type = ResolveType(query);
        return new InspectionResponse(
            Command: "decompile",
            Status: "ok",
            Subject: "type",
            Query: query,
            SearchRoots: _searchRoots,
            Notes: full
                ? [
                    "Full decompile uses embedded ILSpy output for this exact type match.",
                    "memberSummaries remain metadata-derived so callers keep the existing compact navigation data.",
                ]
                : [
                    "Metadata-only decompile groups members and adds body summaries from the static reference graph; it is not a full source reconstruction.",
                    "Method summaries and refs only cover indexed assemblies and available IL metadata; source line numbers are not available.",
                ],
            Id: type.Id,
            Kind: "type",
            DisplayName: type.DisplayName,
            FullName: type.FullName,
            Backend: full ? "ilspy" : "metadata",
            Namespace: type.Namespace,
            Assembly: type.Assembly,
            AssemblyPath: type.AssemblyPath,
            Source: type.Source,
            MemberSummaries: type.Methods.Select(method => method.ToInspectionMemberSummary()).ToArray(),
            Language: "csharp",
            Text: full
                ? IlSpyDecompiler.Decompile(type.AssemblyPath, type.MetadataToken)
                : RenderTypeText(type));
    }

    private InspectionResponse BuildMethodDecompile(string query, bool full)
    {
        var method = ResolveMethod(query);
        var declaringType = ResolveType(method.DeclaringTypeId);

        return new InspectionResponse(
            Command: "decompile",
            Status: "ok",
            Subject: "method",
            Query: query,
            SearchRoots: _searchRoots,
            Notes: full
                ? [
                    "Full decompile uses embedded ILSpy output for this exact method match.",
                    "memberSummaries remain metadata-derived so callers keep the existing compact navigation data.",
                ]
                : [
                    "Metadata-only decompile keeps the real declaring type header and adds a summary derived from IL metadata when a body is available.",
                ],
            Id: method.Id,
            Kind: "method",
            DisplayName: method.DisplayName,
            FullName: method.FullName,
            Backend: full ? "ilspy" : "metadata",
            Namespace: method.Namespace,
            Assembly: method.Assembly,
            AssemblyPath: method.AssemblyPath,
            Source: method.Source,
            Signature: method.Signature,
            MemberSummaries: [method.ToInspectionMemberSummary()],
            Language: "csharp",
            Text: full
                ? IlSpyDecompiler.Decompile(method.AssemblyPath, method.MetadataToken)
                : RenderMethodText(declaringType, method));
    }

    private InspectionResponse BuildExactRefsResponse(string subject, string targetId, string query, int limit)
    {
        var references = _references
            .Where(reference => string.Equals(reference.TargetId, targetId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(reference => reference.ReferenceKind, StringComparer.Ordinal)
            .ThenBy(reference => reference.ContainerFullName, StringComparer.Ordinal)
            .ThenBy(reference => reference.Via, StringComparer.Ordinal)
            .ToArray();
        var limitValue = Math.Max(1, limit);

        return new InspectionResponse(
            Command: "refs",
            Status: references.Length == 0 ? "no-match" : "ok",
            Subject: subject,
            Query: query,
            SearchRoots: _searchRoots,
            Notes:
            [
                "Refs returns references from indexed assemblies only and does not include source line numbers.",
                "Type and method refs require exact resolution; use locate first when the query might be ambiguous.",
            ],
            ReferenceCount: references.Length,
            Truncated: references.Length > limitValue,
            References: references
                .Take(limitValue)
                .Select(reference => reference.ToInspectionReference())
                .ToArray());
    }

    private InspectionResponse BuildSymbolRefsResponse(string query, int limit)
    {
        var limitValue = Math.Max(1, limit);
        var matches = _references
            .Select(reference => (Reference: reference, Match: ScoreReference(reference, query)))
            .Where(item => item.Match is not null)
            .Select(item => new { item.Reference, item.Match!.Value.MatchedText, item.Match.Value.Score })
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Reference.ContainerFullName, StringComparer.Ordinal)
            .ThenBy(item => item.Reference.TargetFullName, StringComparer.Ordinal)
            .ToArray();

        return new InspectionResponse(
            Command: "refs",
            Status: matches.Length == 0 ? "no-match" : "ok",
            Subject: "symbol",
            Query: query,
            SearchRoots: _searchRoots,
            Notes:
            [
                "Symbol refs are name-based fallback only; precision is always reported as 'name'.",
                "Results can include indexed symbols plus unresolved external metadata names referenced from indexed assemblies.",
            ],
            ReferenceCount: matches.Length,
            Truncated: matches.Length > limitValue,
            References: matches
                .Take(limitValue)
                .Select(item => item.Reference.ToInspectionReference("name", item.MatchedText))
                .ToArray());
    }

    private TypeSymbol ResolveType(string query)
    {
        var matches = _types
            .Where(type =>
                type.Id.Equals(query, StringComparison.OrdinalIgnoreCase) ||
                type.FullName.Equals(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new ToolCommandException(3, "not_found", $"No exact type match was found for '{query}'."),
            _ => throw new ToolCommandException(3, "ambiguous_query", $"Query '{query}' matched more than one type. Use the exact locate id instead."),
        };
    }

    private MethodSymbol ResolveMethod(string query)
    {
        var matches = _methods
            .Where(method =>
                method.Id.Equals(query, StringComparison.OrdinalIgnoreCase) ||
                method.LookupSignature.Equals(query, StringComparison.OrdinalIgnoreCase) ||
                method.DisplayName.Equals(query, StringComparison.OrdinalIgnoreCase) ||
                method.FullName.Equals(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new ToolCommandException(3, "not_found", $"No exact method match was found for '{query}'."),
            _ => throw new ToolCommandException(3, "ambiguous_query", $"Query '{query}' matched more than one method. Use the exact locate id or declaring type signature."),
        };
    }

    private static SymbolLookup BuildLookup(IEnumerable<AssemblyIndex> assemblies)
    {
        var lookup = new SymbolLookup();

        foreach (var type in assemblies.SelectMany(assembly => assembly.Types.Values))
        {
            lookup.AddType(type);
            foreach (var field in type.Fields)
            {
                lookup.AddField(field);
            }

            foreach (var method in type.Methods)
            {
                lookup.AddMethod(method);
            }
        }

        return lookup;
    }

    private static void FinalizeBuilders(IEnumerable<AssemblyIndex> assemblies, SymbolLookup lookup)
    {
        var nestedTypesByParent = assemblies
            .SelectMany(assembly => assembly.Types.Values)
            .Where(builder => !string.IsNullOrEmpty(builder.DeclaringTypeFullName))
            .GroupBy(builder => builder.DeclaringTypeFullName!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(builder => builder.FullName, StringComparer.Ordinal)
                    .Select(builder => new NestedTypeSymbol(
                        builder.Id,
                        builder.DisplayName,
                        builder.FullName,
                        builder.TypeKind,
                        builder.Visibility))
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);

        foreach (var type in assemblies.SelectMany(assembly => assembly.Types.Values))
        {
            type.BaseTypeId = lookup.ResolveTypeId(type.BaseType);
            type.InterfaceIds = type.Interfaces
                .Select(lookup.ResolveTypeId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
            type.NestedTypes = nestedTypesByParent.TryGetValue(type.FullName, out var nestedTypes)
                ? nestedTypes
                : [];
            type.Relationships = BuildRelationships(type, lookup);
        }

        foreach (var method in assemblies.SelectMany(assembly => assembly.Methods.Values))
        {
            method.DeclaringTypeId = lookup.ResolveTypeId(method.DeclaringType) ?? $"type:{method.Assembly}:{method.DeclaringType}";
            method.Calls = DistinctSummaryReferences(method.Calls).ToList();
            method.FieldReads = DistinctSummaryReferences(method.FieldReads).ToList();
            method.FieldWrites = DistinctSummaryReferences(method.FieldWrites).ToList();
            method.TypeRefs = DistinctSummaryReferences(method.TypeRefs).ToList();
        }
    }

    private static IReadOnlyList<TypeRelationship> BuildRelationships(TypeSymbolBuilder type, SymbolLookup lookup)
    {
        var relationships = new List<TypeRelationship>();

        if (!string.IsNullOrWhiteSpace(type.BaseType))
        {
            var baseTypeId = lookup.ResolveTypeId(type.BaseType);
            if (baseTypeId is not null)
            {
                relationships.Add(new TypeRelationship(
                    baseTypeId,
                    type.Id,
                    type.DisplayName,
                    type.FullName,
                    type.Namespace,
                    type.Assembly,
                    type.AssemblyPath,
                    type.Source,
                    "extends"));
            }
        }

        foreach (var interfaceName in type.Interfaces)
        {
            var interfaceId = lookup.ResolveTypeId(interfaceName);
            if (interfaceId is null)
            {
                continue;
            }

            relationships.Add(new TypeRelationship(
                interfaceId,
                type.Id,
                type.DisplayName,
                type.FullName,
                type.Namespace,
                type.Assembly,
                type.AssemblyPath,
                type.Source,
                string.Equals(type.TypeKind, "interface", StringComparison.Ordinal) ? "interface-inherits" : "implements"));
        }

        return relationships
            .Distinct()
            .OrderBy(relationship => relationship.RelationKind, StringComparer.Ordinal)
            .ThenBy(relationship => relationship.FullName, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<TypeRelationship> BuildRelationships(
        TypeSymbol type,
        string? baseTypeId,
        IReadOnlyList<string> interfaceIds)
    {
        var relationships = new List<TypeRelationship>();

        if (!string.IsNullOrWhiteSpace(baseTypeId))
        {
            relationships.Add(new TypeRelationship(
                baseTypeId,
                type.Id,
                type.DisplayName,
                type.FullName,
                type.Namespace,
                type.Assembly,
                type.AssemblyPath,
                type.Source,
                "extends"));
        }

        foreach (var interfaceId in interfaceIds)
        {
            relationships.Add(new TypeRelationship(
                interfaceId,
                type.Id,
                type.DisplayName,
                type.FullName,
                type.Namespace,
                type.Assembly,
                type.AssemblyPath,
                type.Source,
                string.Equals(type.TypeKind, "interface", StringComparison.Ordinal) ? "interface-inherits" : "implements"));
        }

        return relationships
            .Distinct()
            .OrderBy(relationship => relationship.RelationKind, StringComparer.Ordinal)
            .ThenBy(relationship => relationship.FullName, StringComparer.Ordinal)
            .ToArray();
    }

    private static string? ResolveTypeId(IReadOnlyDictionary<string, TypeSymbol> typeLookup, string? fullName)
    {
        return !string.IsNullOrWhiteSpace(fullName) && typeLookup.TryGetValue(fullName, out var type)
            ? type.Id
            : null;
    }

    private static string? FindDeclaringTypeFullName(
        string fullName,
        IReadOnlyDictionary<string, TypeSymbol> typeLookup)
    {
        var separator = fullName.LastIndexOf('.');
        while (separator > 0)
        {
            var candidate = fullName[..separator];
            if (typeLookup.ContainsKey(candidate))
            {
                return candidate;
            }

            separator = fullName.LastIndexOf('.', separator - 1);
        }

        return null;
    }


    internal static IEnumerable<string> EnumerateAssemblyFiles(InspectionSearchRoot root)
    {
        if (!Directory.Exists(root.Path))
        {
            return [];
        }

        var searchOption = root.Source == "game"
            ? SearchOption.TopDirectoryOnly
            : SearchOption.AllDirectories;

        return Directory
            .EnumerateFiles(root.Path, "*.dll", searchOption)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<InspectionMatch> LocateMatches(IEnumerable<MatchCandidate> candidates, string query)
    {
        var normalizedQuery = query.Trim();
        if (string.IsNullOrEmpty(normalizedQuery))
        {
            return [];
        }

        var matches = new List<InspectionMatch>();
        foreach (var candidate in candidates)
        {
            var scored = Score(candidate, normalizedQuery);
            if (scored is null)
            {
                continue;
            }

            matches.Add(new InspectionMatch(
                Id: candidate.Id,
                Kind: candidate.Kind,
                DisplayName: candidate.DisplayName,
                FullName: candidate.FullName,
                Namespace: candidate.Namespace,
                Assembly: candidate.Assembly,
                AssemblyPath: candidate.AssemblyPath,
                Source: candidate.Source,
                MatchKind: scored.Value.MatchKind,
                Score: scored.Value.Score,
                Signature: candidate.Signature));
        }

        return matches;
    }

    private static (string MatchKind, int Score)? Score(MatchCandidate candidate, string query)
    {
        foreach (var current in candidate.ExactCandidates)
        {
            if (current.Equals(query, StringComparison.OrdinalIgnoreCase))
            {
                return ("exact", current.Contains("::", StringComparison.Ordinal) || current.Contains('.', StringComparison.Ordinal) ? 300 : 280);
            }
        }

        foreach (var current in candidate.PrefixCandidates)
        {
            if (current.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            {
                return ("prefix", 220);
            }
        }

        foreach (var current in candidate.SubstringCandidates)
        {
            if (current.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                return ("substring", 120);
            }
        }

        return null;
    }

    private static (string MatchedText, int Score)? ScoreReference(ReferenceEdge reference, string query)
    {
        var trimmedQuery = query.Trim();
        if (string.IsNullOrEmpty(trimmedQuery))
        {
            return null;
        }

        foreach (var candidate in new[] { reference.TargetDisplayName, reference.TargetFullName, reference.TargetSignature })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (candidate.Equals(trimmedQuery, StringComparison.OrdinalIgnoreCase))
            {
                return (candidate, candidate.Contains("::", StringComparison.Ordinal) || candidate.Contains('.', StringComparison.Ordinal) ? 300 : 280);
            }
        }

        foreach (var candidate in new[] { reference.TargetDisplayName, reference.TargetFullName, reference.TargetSignature })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (candidate.StartsWith(trimmedQuery, StringComparison.OrdinalIgnoreCase))
            {
                return (candidate, 220);
            }
        }

        foreach (var candidate in new[] { reference.TargetDisplayName, reference.TargetFullName, reference.TargetSignature })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (candidate.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase))
            {
                return (candidate, 120);
            }
        }

        return null;
    }

    private static ILOpCode ReadOpCode(ref BlobReader reader)
    {
        var first = reader.ReadByte();
        if (first != 0xFE)
        {
            return (ILOpCode)first;
        }

        return (ILOpCode)((first << 8) | reader.ReadByte());
    }

    private static void SkipOperand(ref BlobReader reader, OperandType operandType)
    {
        switch (operandType)
        {
            case OperandType.InlineNone:
                return;
            case OperandType.ShortInlineI:
            case OperandType.ShortInlineVar:
            case OperandType.ShortInlineBrTarget:
                reader.ReadByte();
                return;
            case OperandType.InlineVar:
                reader.ReadUInt16();
                return;
            case OperandType.InlineI:
            case OperandType.InlineBrTarget:
            case OperandType.InlineField:
            case OperandType.InlineMethod:
            case OperandType.InlineSig:
            case OperandType.InlineString:
            case OperandType.InlineTok:
            case OperandType.InlineType:
            case OperandType.ShortInlineR:
                reader.ReadInt32();
                return;
            case OperandType.InlineI8:
            case OperandType.InlineR:
                reader.ReadInt64();
                return;
            case OperandType.InlineSwitch:
                var count = reader.ReadInt32();
                for (var index = 0; index < count; index += 1)
                {
                    reader.ReadInt32();
                }
                return;
            default:
                return;
        }
    }

    private static ReferenceTarget? ResolveTypeTarget(string? typeName, SymbolLookup lookup)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return null;
        }

        var resolved = lookup.ResolveType(typeName);
        if (resolved is not null)
        {
            return ReferenceTarget.FromType(resolved);
        }

        return new ReferenceTarget(
            null,
            "type",
            ExtractDisplayName(typeName),
            typeName,
            null);
    }

    private static ReferenceTarget? ResolveTypeTokenTarget(MetadataReader reader, EntityHandle handle, SymbolLookup lookup)
    {
        var fullName = MetadataNameFormatter.FormatEntityName(reader, handle);
        return ResolveTypeTarget(fullName, lookup);
    }

    private static ReferenceTarget? ResolveMethodTokenTarget(MetadataReader reader, EntityHandle handle, SymbolLookup lookup)
    {
        return handle.Kind switch
        {
            HandleKind.MethodDefinition => ResolveMethodDefinitionTarget(reader, (MethodDefinitionHandle)handle, lookup),
            HandleKind.MemberReference => ResolveMemberReferenceMethodTarget(reader, (MemberReferenceHandle)handle, lookup),
            HandleKind.MethodSpecification => ResolveMethodSpecificationTarget(reader, (MethodSpecificationHandle)handle, lookup),
            _ => null,
        };
    }

    private static ReferenceTarget? ResolveMethodDefinitionTarget(
        MetadataReader reader,
        MethodDefinitionHandle handle,
        SymbolLookup lookup)
    {
        var methodDefinition = reader.GetMethodDefinition(handle);
        var declaringType = MetadataNameFormatter.FormatTypeDefinition(reader, methodDefinition.GetDeclaringType());
        var methodName = reader.GetString(methodDefinition.Name);
        var provider = new SignatureTypeNameProvider();
        var decodedSignature = methodDefinition.DecodeSignature(provider, null);
        var parameterTypes = decodedSignature.ParameterTypes.ToArray();
        var lookupSignature = $"{declaringType}::{methodName}({string.Join(",", parameterTypes)})";
        var signature = $"{decodedSignature.ReturnType} {methodName}({string.Join(", ", parameterTypes)})";
        var resolved = lookup.ResolveMethod(lookupSignature);

        if (resolved is not null)
        {
            return ReferenceTarget.FromMethod(resolved);
        }

        return new ReferenceTarget(
            null,
            "method",
            methodName,
            $"{declaringType}::{methodName}",
            signature,
            IsSpecialName: MetadataCatalog.ShouldSkipMethodName(methodName) || methodDefinition.Attributes.HasFlag(MethodAttributes.SpecialName));
    }

    private static ReferenceTarget? ResolveMemberReferenceMethodTarget(
        MetadataReader reader,
        MemberReferenceHandle handle,
        SymbolLookup lookup)
    {
        var memberReference = reader.GetMemberReference(handle);
        if (memberReference.GetKind() != MemberReferenceKind.Method)
        {
            return null;
        }

        var parentName = MetadataNameFormatter.FormatEntityName(reader, memberReference.Parent);
        var methodName = reader.GetString(memberReference.Name);
        var provider = new SignatureTypeNameProvider();
        var decodedSignature = memberReference.DecodeMethodSignature(provider, null);
        var parameterTypes = decodedSignature.ParameterTypes.ToArray();
        var lookupSignature = $"{parentName}::{methodName}({string.Join(",", parameterTypes)})";
        var signature = $"{decodedSignature.ReturnType} {methodName}({string.Join(", ", parameterTypes)})";
        var resolved = lookup.ResolveMethod(lookupSignature);

        if (resolved is not null)
        {
            return ReferenceTarget.FromMethod(resolved);
        }

        return new ReferenceTarget(
            null,
            "method",
            methodName,
            $"{parentName}::{methodName}",
            signature,
            IsSpecialName: ShouldSkipMethodName(methodName));
    }

    private static ReferenceTarget? ResolveMethodSpecificationTarget(
        MetadataReader reader,
        MethodSpecificationHandle handle,
        SymbolLookup lookup)
    {
        var specification = reader.GetMethodSpecification(handle);
        return ResolveMethodTokenTarget(reader, specification.Method, lookup);
    }

    private static ReferenceTarget? ResolveFieldTokenTarget(MetadataReader reader, EntityHandle handle, SymbolLookup lookup)
    {
        return handle.Kind switch
        {
            HandleKind.FieldDefinition => ResolveFieldDefinitionTarget(reader, (FieldDefinitionHandle)handle, lookup),
            HandleKind.MemberReference => ResolveMemberReferenceFieldTarget(reader, (MemberReferenceHandle)handle, lookup),
            _ => null,
        };
    }

    private static ReferenceTarget? ResolveFieldDefinitionTarget(
        MetadataReader reader,
        FieldDefinitionHandle handle,
        SymbolLookup lookup)
    {
        var fieldDefinition = reader.GetFieldDefinition(handle);
        var declaringType = MetadataNameFormatter.FindDeclaringType(reader, handle);
        var name = reader.GetString(fieldDefinition.Name);
        var fullName = $"{declaringType}::{name}";
        var resolved = lookup.ResolveField(fullName);
        if (resolved is not null)
        {
            return ReferenceTarget.FromField(resolved);
        }

        return new ReferenceTarget(
            null,
            "field",
            name,
            fullName,
            fieldDefinition.DecodeSignature(new SignatureTypeNameProvider(), null));
    }

    private static ReferenceTarget? ResolveMemberReferenceFieldTarget(
        MetadataReader reader,
        MemberReferenceHandle handle,
        SymbolLookup lookup)
    {
        var memberReference = reader.GetMemberReference(handle);
        if (memberReference.GetKind() != MemberReferenceKind.Field)
        {
            return null;
        }

        var parentName = MetadataNameFormatter.FormatEntityName(reader, memberReference.Parent);
        var name = reader.GetString(memberReference.Name);
        var fullName = $"{parentName}::{name}";
        var resolved = lookup.ResolveField(fullName);
        if (resolved is not null)
        {
            return ReferenceTarget.FromField(resolved);
        }

        return new ReferenceTarget(
            null,
            "field",
            name,
            fullName,
            memberReference.DecodeFieldSignature(new SignatureTypeNameProvider(), null));
    }

    private static string RenderTypeText(TypeSymbol type)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrEmpty(type.Namespace))
        {
            builder.AppendLine($"namespace {type.Namespace};");
            builder.AppendLine();
        }

        builder.AppendLine(RenderTypeHeader(type));
        builder.AppendLine("{");

        AppendFieldSection(builder, type.Fields);
        AppendPropertySection(builder, type.Properties);
        AppendEventSection(builder, type.Events);
        AppendMethodSection(builder, type.Methods);
        AppendNestedTypeSection(builder, type.NestedTypes);

        builder.AppendLine("}");
        return builder.ToString().TrimEnd();
    }

    private static string RenderMethodText(TypeSymbol declaringType, MethodSymbol method)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrEmpty(declaringType.Namespace))
        {
            builder.AppendLine($"namespace {declaringType.Namespace};");
            builder.AppendLine();
        }

        builder.AppendLine(RenderTypeHeader(declaringType));
        builder.AppendLine("{");
        builder.AppendLine("    // Methods");
        AppendRenderedMethod(builder, method);
        builder.AppendLine("}");
        return builder.ToString().TrimEnd();
    }

    private static void AppendFieldSection(StringBuilder builder, IReadOnlyList<FieldSymbol> fields)
    {
        if (fields.Count == 0)
        {
            return;
        }

        builder.AppendLine("    // Fields");
        foreach (var field in fields)
        {
            builder.AppendLine($"    {field.Visibility} {(field.IsStatic ? "static " : string.Empty)}{field.Type} {field.Name};");
        }

        builder.AppendLine();
    }

    private static void AppendPropertySection(StringBuilder builder, IReadOnlyList<PropertySymbol> properties)
    {
        if (properties.Count == 0)
        {
            return;
        }

        builder.AppendLine("    // Properties");
        foreach (var property in properties)
        {
            var accessors = new List<string>();
            if (property.HasGetter)
            {
                accessors.Add(RenderAccessor(property.GetterVisibility, "get;"));
            }

            if (property.HasSetter)
            {
                accessors.Add(RenderAccessor(property.SetterVisibility, "set;"));
            }

            builder.AppendLine($"    {property.Visibility} {property.Type} {property.Name} {{ {string.Join(" ", accessors)} }}");
        }

        builder.AppendLine();
    }

    private static void AppendEventSection(StringBuilder builder, IReadOnlyList<EventSymbol> events)
    {
        if (events.Count == 0)
        {
            return;
        }

        builder.AppendLine("    // Events");
        foreach (var eventSymbol in events)
        {
            builder.AppendLine($"    {eventSymbol.Visibility} event {eventSymbol.Type} {eventSymbol.Name};");
        }

        builder.AppendLine();
    }

    private static void AppendMethodSection(StringBuilder builder, IReadOnlyList<MethodSymbol> methods)
    {
        if (methods.Count == 0)
        {
            return;
        }

        builder.AppendLine("    // Methods");
        foreach (var method in methods)
        {
            AppendRenderedMethod(builder, method);
        }

        builder.AppendLine();
    }

    private static void AppendRenderedMethod(StringBuilder builder, MethodSymbol method)
    {
        if (method.IsAbstract)
        {
            builder.AppendLine($"    {RenderMethodHeader(method)};");
            return;
        }

        builder.AppendLine($"    {RenderMethodHeader(method)}");
        builder.AppendLine("    {");
        AppendSummaryComment(builder, "Calls", method.Calls);
        AppendSummaryComment(builder, "Field reads", method.FieldReads);
        AppendSummaryComment(builder, "Field writes", method.FieldWrites);
        AppendSummaryComment(builder, "Type refs", method.TypeRefs);
        builder.AppendLine("        throw new global::System.NotImplementedException(\"Method body is unavailable in metadata-only mode.\");");
        builder.AppendLine("    }");
    }

    private static void AppendSummaryComment(StringBuilder builder, string label, IReadOnlyList<SummaryReference> references)
    {
        if (references.Count == 0)
        {
            return;
        }

        builder.AppendLine($"        // {label}: {string.Join(", ", references.Select(reference => reference.DisplayName))}");
    }

    private static void AppendNestedTypeSection(StringBuilder builder, IReadOnlyList<NestedTypeSymbol> nestedTypes)
    {
        if (nestedTypes.Count == 0)
        {
            return;
        }

        builder.AppendLine("    // Nested Types");
        foreach (var nestedType in nestedTypes)
        {
            builder.AppendLine($"    {nestedType.Visibility} {nestedType.TypeKind} {nestedType.Name};");
        }
    }

    private static string RenderTypeHeader(TypeSymbol type)
    {
        var builder = new StringBuilder();
        builder.Append(type.Visibility);
        builder.Append(' ');
        builder.Append(type.TypeKind);
        builder.Append(' ');
        builder.Append(type.DisplayName);

        var inheritance = new List<string>();
        if (!string.IsNullOrEmpty(type.BaseType) && type.BaseType != "System.Object" && type.BaseType != "System.ValueType")
        {
            inheritance.Add(type.BaseType);
        }

        inheritance.AddRange(type.Interfaces);
        if (inheritance.Count > 0)
        {
            builder.Append(" : ");
            builder.Append(string.Join(", ", inheritance));
        }

        return builder.ToString();
    }

    private static string RenderMethodHeader(MethodSymbol method)
    {
        var modifiers = new List<string> { method.Visibility };
        if (method.IsStatic)
        {
            modifiers.Add("static");
        }
        if (method.IsAbstract)
        {
            modifiers.Add("abstract");
        }
        else if (method.IsVirtual)
        {
            modifiers.Add("virtual");
        }

        var parameters = string.Join(", ", method.Parameters.Select(parameter => $"{parameter.Type} {parameter.Name}"));
        return $"{string.Join(" ", modifiers)} {method.ReturnType} {method.DisplayName}({parameters})";
    }

    private static string RenderAccessor(string? visibility, string accessor)
    {
        return string.IsNullOrWhiteSpace(visibility) || visibility == "public"
            ? accessor
            : $"{visibility} {accessor}";
    }

    private static string ExtractDisplayName(string fullName)
    {
        var separator = Math.Max(fullName.LastIndexOf('.'), fullName.LastIndexOf("::", StringComparison.Ordinal));
        return separator >= 0
            ? fullName[(separator + (fullName[separator] == ':' ? 2 : 1))..]
            : fullName;
    }

    private static string DetermineTypeKind(TypeDefinition typeDefinition, MetadataReader reader)
    {
        if (typeDefinition.Attributes.HasFlag(TypeAttributes.Interface))
        {
            return "interface";
        }

        var baseTypeName = typeDefinition.BaseType.IsNil
            ? null
            : MetadataNameFormatter.FormatEntityName(reader, typeDefinition.BaseType);

        if (baseTypeName == "System.Enum")
        {
            return "enum";
        }

        if (baseTypeName == "System.ValueType")
        {
            return "struct";
        }

        return "class";
    }

    private static string FormatTypeVisibility(TypeAttributes attributes)
    {
        return (attributes & TypeAttributes.VisibilityMask) switch
        {
            TypeAttributes.NotPublic => "internal",
            TypeAttributes.Public => "public",
            TypeAttributes.NestedPublic => "public",
            TypeAttributes.NestedPrivate => "private",
            TypeAttributes.NestedFamily => "protected",
            TypeAttributes.NestedAssembly => "internal",
            TypeAttributes.NestedFamORAssem => "protected internal",
            _ => "internal",
        };
    }

    private static string FormatFieldVisibility(FieldAttributes attributes)
    {
        return (attributes & FieldAttributes.FieldAccessMask) switch
        {
            FieldAttributes.Public => "public",
            FieldAttributes.Private => "private",
            FieldAttributes.Family => "protected",
            FieldAttributes.Assembly => "internal",
            FieldAttributes.FamORAssem => "protected internal",
            _ => "private",
        };
    }

    private static string FormatMethodVisibility(MethodAttributes attributes)
    {
        return (attributes & MethodAttributes.MemberAccessMask) switch
        {
            MethodAttributes.Public => "public",
            MethodAttributes.Private => "private",
            MethodAttributes.Family => "protected",
            MethodAttributes.Assembly => "internal",
            MethodAttributes.FamORAssem => "protected internal",
            _ => "private",
        };
    }

    private static string ChooseAccessorVisibility(params string?[] visibilities)
    {
        var ordered = new[] { "public", "protected internal", "protected", "internal", "private" };
        foreach (var visibility in ordered)
        {
            if (visibilities.Contains(visibility, StringComparer.Ordinal))
            {
                return visibility;
            }
        }

        return "private";
    }

    private static bool HasCompilerGeneratedAttribute(MetadataReader reader, CustomAttributeHandleCollection attributes)
    {
        foreach (var handle in attributes)
        {
            var attribute = reader.GetCustomAttribute(handle);
            var fullName = attribute.Constructor.Kind switch
            {
                HandleKind.MemberReference => MetadataNameFormatter.FormatAttributeType(reader, reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent),
                HandleKind.MethodDefinition => MetadataNameFormatter.FormatTypeDefinition(reader, reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType()),
                _ => null,
            };

            if (string.Equals(fullName, "System.Runtime.CompilerServices.CompilerGeneratedAttribute", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ShouldSkipMethodName(string name)
    {
        return name is ".ctor" or ".cctor"
            || name.StartsWith("get_", StringComparison.Ordinal)
            || name.StartsWith("set_", StringComparison.Ordinal)
            || name.StartsWith("add_", StringComparison.Ordinal)
            || name.StartsWith("remove_", StringComparison.Ordinal);
    }

}

internal sealed record HookTypeRecord(
    string Id,
    string DisplayName,
    string FullName,
    string Namespace,
    string Assembly,
    string AssemblyPath,
    string Source,
    string TypeKind,
    string Visibility,
    string? BaseTypeId,
    IReadOnlyList<string> InterfaceIds);

internal sealed record HookMethodRecord(
    string Id,
    string DisplayName,
    string FullName,
    string Namespace,
    string Assembly,
    string AssemblyPath,
    string Source,
    string Signature,
    string LookupSignature,
    string DeclaringType,
    string DeclaringTypeId,
    string Visibility,
    string ReturnType,
    IReadOnlyList<InspectionParameterInfo> Parameters,
    bool IsStatic,
    bool IsAbstract,
    bool IsVirtual);

internal static class MetadataNameFormatter
{
    public static string FormatEntityName(MetadataReader reader, EntityHandle handle)
    {
        return handle.Kind switch
        {
            HandleKind.TypeDefinition => FormatTypeDefinition(reader, (TypeDefinitionHandle)handle),
            HandleKind.TypeReference => FormatTypeReference(reader, (TypeReferenceHandle)handle),
            HandleKind.TypeSpecification => reader.GetTypeSpecification((TypeSpecificationHandle)handle)
                .DecodeSignature(new SignatureTypeNameProvider(), null),
            HandleKind.MemberReference => FormatMemberReferenceParent(reader, (MemberReferenceHandle)handle),
            HandleKind.MethodDefinition => FormatTypeDefinition(reader, reader.GetMethodDefinition((MethodDefinitionHandle)handle).GetDeclaringType()),
            HandleKind.FieldDefinition => FindDeclaringType(reader, (FieldDefinitionHandle)handle),
            _ => handle.Kind.ToString(),
        };
    }

    public static string FormatTypeDefinition(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var type = reader.GetTypeDefinition(handle);
        var typeName = reader.GetString(type.Name);
        if (type.GetDeclaringType().IsNil)
        {
            var namespaceName = reader.GetString(type.Namespace);
            return string.IsNullOrEmpty(namespaceName) ? typeName : $"{namespaceName}.{typeName}";
        }

        return $"{FormatTypeDefinition(reader, type.GetDeclaringType())}.{typeName}";
    }

    public static string FormatTypeReference(MetadataReader reader, TypeReferenceHandle handle)
    {
        var type = reader.GetTypeReference(handle);
        var namespaceName = reader.GetString(type.Namespace);
        var typeName = reader.GetString(type.Name);

        if (type.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            return $"{FormatTypeReference(reader, (TypeReferenceHandle)type.ResolutionScope)}.{typeName}";
        }

        return string.IsNullOrEmpty(namespaceName) ? typeName : $"{namespaceName}.{typeName}";
    }

    public static string FormatMemberReferenceParent(MetadataReader reader, MemberReferenceHandle handle)
    {
        var memberReference = reader.GetMemberReference(handle);
        return FormatEntityName(reader, memberReference.Parent);
    }

    public static string FormatAttributeType(MetadataReader reader, EntityHandle handle)
    {
        return handle.Kind switch
        {
            HandleKind.TypeReference => FormatTypeReference(reader, (TypeReferenceHandle)handle),
            HandleKind.TypeDefinition => FormatTypeDefinition(reader, (TypeDefinitionHandle)handle),
            _ => FormatEntityName(reader, handle),
        };
    }

    public static string FindDeclaringType(MetadataReader reader, FieldDefinitionHandle handle)
    {
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            if (type.GetFields().Any(current => current == handle))
            {
                return FormatTypeDefinition(reader, typeHandle);
            }
        }

        return "<unknown>";
    }

    public static string FindDeclaringType(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var type = reader.GetTypeDefinition(handle);
        return type.GetDeclaringType().IsNil ? string.Empty : FormatTypeDefinition(reader, type.GetDeclaringType());
    }

    public static string ExtractNamespace(string fullName)
    {
        var separator = fullName.LastIndexOf('.');
        return separator >= 0 ? fullName[..separator] : string.Empty;
    }
}

internal static class OperandTypes
{
    public static readonly IReadOnlyDictionary<ushort, OperandType> ByCode = BuildOperandTypeMap();
    public static readonly IReadOnlyDictionary<ushort, string> Names = BuildNameMap();

    private static IReadOnlyDictionary<ushort, OperandType> BuildOperandTypeMap()
    {
        return typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode))
            .Select(field => (OpCode)field.GetValue(null)!)
            .ToDictionary(
                opcode => unchecked((ushort)opcode.Value),
                opcode => opcode.OperandType);
    }

    private static IReadOnlyDictionary<ushort, string> BuildNameMap()
    {
        return typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode))
            .ToDictionary(
                field => unchecked((ushort)((OpCode)field.GetValue(null)!).Value),
                field => field.Name.ToLowerInvariant());
    }
}
