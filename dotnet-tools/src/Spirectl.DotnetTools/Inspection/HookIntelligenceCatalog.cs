using System.Text.RegularExpressions;
using Spirectl.DotnetTools.Models;

namespace Spirectl.DotnetTools.Inspection;

internal sealed class HookIntelligenceCatalog
{
    private readonly IReadOnlyList<InspectionSearchRoot> _searchRoots;
    private readonly IReadOnlyDictionary<string, HookTypeRecord> _typesById;
    private readonly IReadOnlyDictionary<string, HookMethodRecord> _methodsById;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<HookMethodRecord>> _methodsByTypeId;
    private readonly IReadOnlyDictionary<string, int> _inboundReferenceCounts;
    private readonly GodotScriptCatalog _scriptCatalog;
    private readonly SceneScriptIndex _sceneScriptIndex;

    private HookIntelligenceCatalog(
        IReadOnlyList<InspectionSearchRoot> searchRoots,
        IReadOnlyDictionary<string, HookTypeRecord> typesById,
        IReadOnlyDictionary<string, HookMethodRecord> methodsById,
        IReadOnlyDictionary<string, IReadOnlyList<HookMethodRecord>> methodsByTypeId,
        IReadOnlyDictionary<string, int> inboundReferenceCounts,
        GodotScriptCatalog scriptCatalog,
        SceneScriptIndex sceneScriptIndex)
    {
        _searchRoots = searchRoots;
        _typesById = typesById;
        _methodsById = methodsById;
        _methodsByTypeId = methodsByTypeId;
        _inboundReferenceCounts = inboundReferenceCounts;
        _scriptCatalog = scriptCatalog;
        _sceneScriptIndex = sceneScriptIndex;
    }

    public static HookIntelligenceCatalog Load(MetadataCatalog metadataCatalog, string? resourcesDir, string? modsDir)
    {
        var methods = metadataCatalog.ExportHookMethods();
        var types = metadataCatalog.ExportHookTypes();
        var searchRoots = metadataCatalog.SearchRoots.ToArray();
        var assembliesDir = searchRoots
            .FirstOrDefault(root => string.Equals(root.Source, "game", StringComparison.OrdinalIgnoreCase))
            ?.Path;

        return new HookIntelligenceCatalog(
            searchRoots,
            types.ToDictionary(type => type.Id, StringComparer.OrdinalIgnoreCase),
            methods.ToDictionary(method => method.Id, StringComparer.OrdinalIgnoreCase),
            methods
                .GroupBy(method => method.DeclaringTypeId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<HookMethodRecord>)[.. group
                        .OrderBy(method => method.LookupSignature, StringComparer.Ordinal)],
                    StringComparer.OrdinalIgnoreCase),
            methods.ToDictionary(
                method => method.Id,
                method => metadataCatalog.CountInboundReferences(method.Id),
                StringComparer.OrdinalIgnoreCase),
            GodotScriptCatalog.Load(assembliesDir ?? string.Empty, modsDir),
            SceneScriptIndex.Load(resourcesDir));
    }

    public InspectionResponse List(
        string query,
        int limit,
        int offset,
        string? sourceFilter,
        string? assemblyFilter,
        IReadOnlyList<string> formFilters,
        bool hasScript,
        string sort)
    {
        var trimmedQuery = query.Trim();
        var limitValue = Math.Max(1, limit);
        var offsetValue = Math.Max(0, offset);
        var formFilterSet = new HashSet<string>(formFilters, StringComparer.OrdinalIgnoreCase);
        var candidates = _methodsById.Values
            .Select(method =>
            {
                var scored = Score(method, trimmedQuery);
                return (
                    Method: method,
                    MatchKind: scored?.MatchKind ?? (string.IsNullOrWhiteSpace(trimmedQuery) ? "catalog" : "none"),
                    Score: scored?.Score ?? 0);
            })
            .Where(item => item.MatchKind != "none")
            .Select(item => BuildHookMatch(item.Method, item.MatchKind, item.Score))
            .Where(match => sourceFilter is null || match.Source.Equals(sourceFilter, StringComparison.OrdinalIgnoreCase))
            .Where(match => assemblyFilter is null || string.Equals(match.Assembly, assemblyFilter, StringComparison.OrdinalIgnoreCase))
            .Where(match => formFilterSet.Count == 0 || (match.HookForms ?? []).Any(formFilterSet.Contains))
            .Where(match => !hasScript || !string.IsNullOrWhiteSpace(match.ScriptPath))
            .ToArray();

        var matches = SortMatches(candidates, sort)
            .ToArray();

        var notes = new List<string>
        {
            "Hook catalog ranks exact, prefix, then substring matches across method names, signatures, and declaring types when a query is provided.",
            "Empty hooks queries return a browseable static method catalog page.",
            "Hook forms are advisory and metadata-based; confirm exact call sites with hook-info, describe, refs, or decompile before patching.",
        };
        if (_sceneScriptIndex.Enabled)
        {
            notes.Add("Scene path hints come from static text scene scans under the provided resources root.");
        }
        else
        {
            notes.Add("Scene path hints are omitted unless you provide --resources-dir.");
        }

        return new InspectionResponse(
            Command: "hooks",
            Status: matches.Length == 0 ? "no-match" : "ok",
            Subject: "method",
            Query: query,
            SearchRoots: _searchRoots,
            Notes: notes,
            MatchCount: matches.Length,
            TotalCount: matches.Length,
            ReturnedCount: Math.Min(limitValue, Math.Max(0, matches.Length - offsetValue)),
            Limit: limitValue,
            Offset: offsetValue,
            Truncated: offsetValue + limitValue < matches.Length,
            NextOffset: offsetValue + limitValue < matches.Length ? offsetValue + limitValue : null,
            Facets: BuildFacets(matches),
            Matches: matches.Skip(offsetValue).Take(limitValue).ToArray());
    }

    public InspectionResponse Info(string query)
    {
        var method = ResolveExactMethod(query);
        var baseMethod = ResolveBaseMethod(method);
        var interfaceMethods = ResolveImplementedInterfaceMethods(method);
        var scriptType = _scriptCatalog.ResolveByTypeId(method.DeclaringTypeId);
        var scenePaths = scriptType?.ScriptPath is { } scriptPath
            ? _sceneScriptIndex.ResolveScenePaths(scriptPath)
            : [];
        var hookForms = BuildHookForms(method, baseMethod, interfaceMethods.Count);
        var reasons = BuildReasons(method, baseMethod, interfaceMethods.Count, scriptType?.ScriptPath);

        return new InspectionResponse(
            Command: "hook-info",
            Status: "ok",
            Subject: "method",
            Query: query,
            SearchRoots: _searchRoots,
            Notes:
            [
                "Hook signature resolves one exact method id or exact lookup signature and keeps the result metadata-first.",
                "Base-method and interface links are derived from indexed inheritance metadata; source line numbers and runtime patch safety are out of scope.",
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
            IsOverride: baseMethod is not null,
            IsAbstract: method.IsAbstract,
            IsVirtual: method.IsVirtual,
            BaseMethodId: baseMethod?.Id,
            BaseMethod: baseMethod?.Signature,
            ImplementedInterfaceMethodIds: [.. interfaceMethods.Select(current => current.Id)],
            HookForms: hookForms,
            Reasons: reasons,
            ScriptPath: scriptType?.ScriptPath,
            ScenePaths: scenePaths,
            SuggestedNextCommands: BuildSuggestedNextCommands(method, scriptType?.ScriptPath),
            ReferenceCount: _inboundReferenceCounts.GetValueOrDefault(method.Id, 0));
    }

    private static IEnumerable<InspectionMatch> SortMatches(IEnumerable<InspectionMatch> matches, string sort)
    {
        return sort switch
        {
            "name" => matches
                .OrderBy(match => match.FullName, StringComparer.Ordinal)
                .ThenBy(match => match.Signature, StringComparer.Ordinal),
            "reference-count" => matches
                .OrderByDescending(match => match.ReferenceCount ?? 0)
                .ThenBy(match => match.FullName, StringComparer.Ordinal),
            "assembly" => matches
                .OrderBy(match => match.Assembly, StringComparer.Ordinal)
                .ThenBy(match => match.FullName, StringComparer.Ordinal),
            _ => matches
                .OrderByDescending(match => match.Score)
                .ThenBy(match => match.Source, StringComparer.Ordinal)
                .ThenBy(match => match.FullName, StringComparer.Ordinal),
        };
    }

    private static InspectionHookFacets BuildFacets(IReadOnlyList<InspectionMatch> matches)
    {
        return new InspectionHookFacets(
            Sources: BuildFacet(matches.Select(match => match.Source)),
            Assemblies: BuildFacet(matches
                .Select(match => match.Assembly ?? string.Empty)
                .Where(value => !string.IsNullOrWhiteSpace(value))),
            HookForms: BuildFacet(matches.SelectMany(match => match.HookForms ?? [])));
    }

    private static IReadOnlyList<InspectionFacetEntry> BuildFacet(IEnumerable<string> values)
    {
        return values
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Select(group => new InspectionFacetEntry(group.Key, group.Count()))
            .OrderBy(entry => entry.Value, StringComparer.Ordinal)
            .ToArray();
    }

    private HookMethodRecord ResolveExactMethod(string query)
    {
        var matches = _methodsById.Values
            .Where(method =>
                method.Id.Equals(query, StringComparison.OrdinalIgnoreCase)
                || method.LookupSignature.Equals(query, StringComparison.OrdinalIgnoreCase)
                || method.DisplayName.Equals(query, StringComparison.OrdinalIgnoreCase)
                || method.FullName.Equals(query, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new ToolCommandException(3, "not_found", $"No exact method hook info was found for '{query}'."),
            _ => throw new ToolCommandException(3, "ambiguous_query", $"Query '{query}' matched more than one method. Use the exact method id or lookup signature."),
        };
    }

    private InspectionMatch BuildHookMatch(HookMethodRecord method, string matchKind, int score)
    {
        var baseMethod = ResolveBaseMethod(method);
        var interfaceMethods = ResolveImplementedInterfaceMethods(method);
        var scriptType = _scriptCatalog.ResolveByTypeId(method.DeclaringTypeId);
        var scenePaths = scriptType?.ScriptPath is { } scriptPath
            ? _sceneScriptIndex.ResolveScenePaths(scriptPath)
            : [];

        return new InspectionMatch(
            Id: method.Id,
            Kind: "method",
            DisplayName: method.DisplayName,
            FullName: method.FullName,
            Namespace: method.Namespace,
            Assembly: method.Assembly,
            AssemblyPath: method.AssemblyPath,
            Source: method.Source,
            MatchKind: matchKind,
            Score: score + ScoreBonuses(method, baseMethod, interfaceMethods.Count, scriptType?.ScriptPath),
            Signature: method.Signature,
            HookForms: BuildHookForms(method, baseMethod, interfaceMethods.Count),
            Reasons: BuildReasons(method, baseMethod, interfaceMethods.Count, scriptType?.ScriptPath),
            ScriptPath: scriptType?.ScriptPath,
            ScenePaths: scenePaths,
            SuggestedNextCommands: BuildSuggestedNextCommands(method, scriptType?.ScriptPath),
            DeclaringType: method.DeclaringType,
            DeclaringTypeId: method.DeclaringTypeId,
            Visibility: method.Visibility,
            IsStatic: method.IsStatic,
            IsAbstract: method.IsAbstract,
            IsVirtual: method.IsVirtual,
            ReferenceCount: _inboundReferenceCounts.GetValueOrDefault(method.Id, 0));
    }

    private int ScoreBonuses(HookMethodRecord method, HookMethodRecord? baseMethod, int interfaceMethodCount, string? scriptPath)
    {
        var score = Math.Min(30, _inboundReferenceCounts.GetValueOrDefault(method.Id, 0) * 3);
        if (baseMethod is not null)
        {
            score += 20;
        }

        if (interfaceMethodCount > 0)
        {
            score += 18;
        }

        if (!string.IsNullOrWhiteSpace(scriptPath))
        {
            score += 12;
        }

        if (method.IsVirtual)
        {
            score += 8;
        }

        return score;
    }

    private static (string MatchKind, int Score)? Score(HookMethodRecord method, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        foreach (var candidate in ExactCandidates(method))
        {
            if (candidate.Equals(query, StringComparison.OrdinalIgnoreCase))
            {
                return ("exact", candidate.Contains("::", StringComparison.Ordinal) ? 320 : 300);
            }
        }

        foreach (var candidate in PrefixCandidates(method))
        {
            if (candidate.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            {
                return ("prefix", 220);
            }
        }

        foreach (var candidate in PrefixCandidates(method))
        {
            if (candidate.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                return ("substring", 120);
            }
        }

        return null;
    }

    private static IEnumerable<string> ExactCandidates(HookMethodRecord method)
    {
        yield return method.DisplayName;
        yield return method.FullName;
        yield return method.LookupSignature;
        yield return method.Id;
    }

    private static IEnumerable<string> PrefixCandidates(HookMethodRecord method)
    {
        yield return method.DisplayName;
        yield return method.FullName;
        yield return method.LookupSignature;
        yield return method.DeclaringType;
        yield return method.Namespace;
    }

    private HookMethodRecord? ResolveBaseMethod(HookMethodRecord method)
    {
        if (!_typesById.TryGetValue(method.DeclaringTypeId, out var declaringType))
        {
            return null;
        }

        var currentBaseTypeId = declaringType.BaseTypeId;
        while (!string.IsNullOrWhiteSpace(currentBaseTypeId))
        {
            if (_methodsByTypeId.TryGetValue(currentBaseTypeId, out var methods))
            {
                var match = methods.FirstOrDefault(candidate => MethodShapeMatches(candidate, method));
                if (match is not null)
                {
                    return match;
                }
            }

            currentBaseTypeId = _typesById.TryGetValue(currentBaseTypeId, out var baseType)
                ? baseType.BaseTypeId
                : null;
        }

        return null;
    }

    private IReadOnlyList<HookMethodRecord> ResolveImplementedInterfaceMethods(HookMethodRecord method)
    {
        if (!_typesById.TryGetValue(method.DeclaringTypeId, out var declaringType))
        {
            return [];
        }

        var results = new List<HookMethodRecord>();
        foreach (var interfaceId in EnumerateInterfaceClosure(declaringType))
        {
            if (!_methodsByTypeId.TryGetValue(interfaceId, out var methods))
            {
                continue;
            }

            var match = methods.FirstOrDefault(candidate => MethodShapeMatches(candidate, method));
            if (match is not null)
            {
                results.Add(match);
            }
        }

        return [.. results
            .DistinctBy(current => current.Id, StringComparer.OrdinalIgnoreCase)
            .OrderBy(current => current.LookupSignature, StringComparer.Ordinal)];
    }

    private IEnumerable<string> EnumerateInterfaceClosure(HookTypeRecord type)
    {
        var pending = new Queue<string>(type.InterfaceIds);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            if (!seen.Add(current))
            {
                continue;
            }

            yield return current;

            if (_typesById.TryGetValue(current, out var interfaceType))
            {
                foreach (var parentInterfaceId in interfaceType.InterfaceIds)
                {
                    pending.Enqueue(parentInterfaceId);
                }
            }
        }
    }

    private static bool MethodShapeMatches(HookMethodRecord candidate, HookMethodRecord method)
    {
        return candidate.DisplayName.Equals(method.DisplayName, StringComparison.Ordinal)
            && candidate.Parameters.Select(parameter => parameter.Type)
                .SequenceEqual(method.Parameters.Select(parameter => parameter.Type), StringComparer.Ordinal)
            && candidate.ReturnType.Equals(method.ReturnType, StringComparison.Ordinal);
    }

    private IReadOnlyList<string> BuildHookForms(HookMethodRecord method, HookMethodRecord? baseMethod, int interfaceMethodCount)
    {
        var forms = new List<string>();

        if (method.IsAbstract)
        {
            if (interfaceMethodCount > 0)
            {
                forms.Add("managed-interface-contract");
            }

            return forms;
        }

        forms.Add("managed-prefix");
        forms.Add("managed-postfix");

        if (baseMethod is not null || interfaceMethodCount > 0 || method.IsVirtual)
        {
            forms.Add("managed-override");
        }

        return forms;
    }

    private IReadOnlyList<string> BuildReasons(HookMethodRecord method, HookMethodRecord? baseMethod, int interfaceMethodCount, string? scriptPath)
    {
        var reasons = new List<string>();
        if (baseMethod is not null)
        {
            reasons.Add($"Overrides {baseMethod.FullName}.");
        }

        if (interfaceMethodCount > 0)
        {
            reasons.Add($"Implements {interfaceMethodCount} interface hook contract(s).");
        }

        if (!string.IsNullOrWhiteSpace(scriptPath))
        {
            reasons.Add($"Declaring type is bound to Godot script {scriptPath}.");
        }

        var inboundReferences = _inboundReferenceCounts.GetValueOrDefault(method.Id, 0);
        if (inboundReferences > 0)
        {
            reasons.Add($"Referenced by {inboundReferences} indexed method(s).");
        }

        if (reasons.Count == 0)
        {
            reasons.Add("Metadata exposes an exact managed method shape suitable for staged hook follow-up.");
        }

        return reasons;
    }

    private static IReadOnlyList<string> BuildSuggestedNextCommands(HookMethodRecord method, string? scriptPath)
    {
        var commands = new List<string>
        {
            $"sts2 code hook-info {method.Id}",
            $"sts2 code describe method {method.Id}",
            $"sts2 code refs method {method.Id}",
        };

        if (!string.IsNullOrWhiteSpace(scriptPath))
        {
            commands.Add($"sts2 code scene-search {Path.GetFileNameWithoutExtension(scriptPath)}");
        }

        return commands;
    }

    private sealed class SceneScriptIndex
    {
        private static readonly Regex ScriptPathRegex = new(@"res://[^""'\s]+\.cs", RegexOptions.Compiled);
        private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _scenePathsByScriptPath;

        private SceneScriptIndex(bool enabled, IReadOnlyDictionary<string, IReadOnlyList<string>> scenePathsByScriptPath)
        {
            Enabled = enabled;
            _scenePathsByScriptPath = scenePathsByScriptPath;
        }

        public bool Enabled { get; }

        public static SceneScriptIndex Load(string? resourcesDir)
        {
            if (string.IsNullOrWhiteSpace(resourcesDir) || !Directory.Exists(resourcesDir))
            {
                return new SceneScriptIndex(
                    enabled: false,
                    scenePathsByScriptPath: new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase));
            }

            var scenePathsByScriptPath = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var scenePath in Directory.EnumerateFiles(resourcesDir, "*.tscn", SearchOption.AllDirectories))
            {
                var sceneText = File.ReadAllText(scenePath);
                var resourceScenePath = $"res://{Path.GetRelativePath(resourcesDir, scenePath).Replace('\\', '/')}";
                foreach (Match match in ScriptPathRegex.Matches(sceneText))
                {
                    if (!scenePathsByScriptPath.TryGetValue(match.Value, out var scenePaths))
                    {
                        scenePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        scenePathsByScriptPath[match.Value] = scenePaths;
                    }

                    scenePaths.Add(resourceScenePath);
                }
            }

            return new SceneScriptIndex(
                enabled: true,
                scenePathsByScriptPath: scenePathsByScriptPath.ToDictionary(
                    pair => pair.Key,
                    pair => (IReadOnlyList<string>)[.. pair.Value.OrderBy(value => value, StringComparer.Ordinal)],
                    StringComparer.OrdinalIgnoreCase));
        }

        public IReadOnlyList<string> ResolveScenePaths(string scriptPath)
        {
            return _scenePathsByScriptPath.TryGetValue(scriptPath, out var scenePaths)
                ? scenePaths
                : [];
        }
    }
}
