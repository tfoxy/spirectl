using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spirectl.DotnetTools.Inspection;
using Spirectl.DotnetTools.Models;

namespace Spirectl.DotnetTools;

public static class ToolCommandDispatcher
{
    public static ToolCommandResult Dispatch(string[] args)
    {
        if (args.Length < 1)
        {
            return Failure("usage: dotnet-tools <locate|describe|decompile|refs|derived> <subject> <query> | <hooks> [query] | <hook-info> <query> | <verify-references> <assembly[,assembly...]> | <scene-search> <query> | <scene-tree> <scene> | <scene-node> <scene> <node-path> | <asset-search> <query> | <asset-index> | <asset-read> <container-path> <logical-path> | <decompile-export> <output-dir> [--assemblies-dir <path>] [--control-assemblies-dir <path>] [--resources-dir <path>] [--mods-dir <path>] [--exclude-dir <path>] [--include-mods] [--include-dependencies] [--full] [--limit <n>] [--offset <n>] [--source <game|mod>] [--assembly <name>] [--form <name>] [--has-script] [--sort <relevance|name|reference-count|assembly>] [--json]");
        }

        InspectionCommandRequest request;
        try
        {
            request = ParseRequest(args);
        }
        catch (ToolCommandException error)
        {
            return Failure(error.Message, error.Code, error.ExitCode);
        }

        object response;
        try
        {
            response = request.Command switch
            {
                "asset-search" or "asset-index" or "asset-read" => ExecuteAssetCommand(request),
                "decompile-export" => new DecompileExportService().Execute(request),
                "verify-references" => new ReferenceVerificationService().Execute(request),
                _ => new InspectionCommandService().Execute(request),
            };
        }
        catch (ToolCommandException error)
        {
            return Failure(error.Message, error.Code, error.ExitCode);
        }

        if (response is ReferenceVerificationResponse verification)
        {
            return new ToolCommandResult(
                VerificationExitCode(verification.Status),
                request.Json ? SerializeJson(verification) : SerializeVerificationHuman(verification));
        }

        return request.Json || request.Command is "asset-search" or "asset-index" or "asset-read" or "decompile-export"
            ? new ToolCommandResult(0, SerializeJson(response))
            : new ToolCommandResult(0, SerializeHuman((InspectionResponse)response));
    }

    private static InspectionCommandRequest ParseRequest(string[] args)
    {
        var command = args[0];
        if (command is not ("locate" or "describe" or "decompile" or "refs" or "derived" or "hooks" or "hook-info" or "verify-references" or "scene-search" or "scene-tree" or "scene-node" or "asset-search" or "asset-index" or "asset-read" or "decompile-export"))
        {
            throw new ToolCommandException(2, "usage_error", $"unknown command '{command}'");
        }

        string? subject = null;
        string query;
        string? secondaryQuery = null;
        string? containerPath = null;
        int optionsStartIndex;

        switch (command)
        {
            case "locate":
            case "describe":
            case "decompile":
            case "refs":
            case "derived":
                if (args.Length < 3)
                {
                    throw new ToolCommandException(2, "usage_error", $"command '{command}' requires <subject> <query>");
                }

                subject = args[1];
                query = args[2];
                optionsStartIndex = 3;
                break;
            case "scene-search":
            case "scene-tree":
            case "hook-info":
            case "verify-references":
            case "asset-search":
            case "decompile-export":
                if (args.Length < 2)
                {
                    throw new ToolCommandException(2, "usage_error", $"command '{command}' requires <query>");
                }

                query = args[1];
                optionsStartIndex = 2;
                break;
            case "hooks":
                query = args.Length >= 2 && !args[1].StartsWith("--", StringComparison.Ordinal)
                    ? args[1]
                    : string.Empty;
                optionsStartIndex = string.IsNullOrWhiteSpace(query) ? 1 : 2;
                break;
            case "asset-index":
                query = string.Empty;
                optionsStartIndex = 1;
                break;
            case "scene-node":
                if (args.Length < 3)
                {
                    throw new ToolCommandException(2, "usage_error", "command 'scene-node' requires <scene> <node-path>");
                }

                query = args[1];
                secondaryQuery = args[2];
                optionsStartIndex = 3;
                break;
            case "asset-read":
                if (args.Length < 3)
                {
                    throw new ToolCommandException(2, "usage_error", "command 'asset-read' requires <container-path> <logical-path>");
                }

                containerPath = args[1];
                query = args[2];
                optionsStartIndex = 3;
                break;
            default:
                throw new ToolCommandException(2, "usage_error", $"unknown command '{command}'");
        }

        var json = false;
        string? assembliesDir = null;
        string? controlAssembliesDir = null;
        string? resourcesDir = null;
        string? modsDir = null;
        string? excludeDir = null;
        var includeMods = false;
        var includeDependencies = false;
        var full = false;
        var limit = command == "hooks" ? 100 : 20;
        var offset = 0;
        string? sourceFilter = null;
        string? assemblyFilter = null;
        var formFilters = new List<string>();
        var hasScript = false;
        var sort = "relevance";
        string? cacheDir = null;
        var cacheMode = MetadataCatalogCacheMode.Auto;

        for (var index = optionsStartIndex; index < args.Length; index += 1)
        {
            switch (args[index])
            {
                case "--json":
                    json = true;
                    break;
                case "--assemblies-dir":
                    assembliesDir = ReadOptionValue(args, ref index, "--assemblies-dir");
                    break;
                case "--control-assemblies-dir":
                    if (command != "verify-references")
                    {
                        throw new ToolCommandException(2, "usage_error", "--control-assemblies-dir is only supported for verify-references");
                    }

                    controlAssembliesDir = ReadOptionValue(args, ref index, "--control-assemblies-dir");
                    break;
                case "--resources-dir":
                    resourcesDir = ReadOptionValue(args, ref index, "--resources-dir");
                    break;
                case "--mods-dir":
                    modsDir = ReadOptionValue(args, ref index, "--mods-dir");
                    break;
                case "--exclude-dir":
                    excludeDir = ReadOptionValue(args, ref index, "--exclude-dir");
                    break;
                case "--include-mods":
                    includeMods = true;
                    break;
                case "--include-dependencies":
                    includeDependencies = true;
                    break;
                case "--full":
                    if (command != "decompile")
                    {
                        throw new ToolCommandException(2, "usage_error", "--full is only supported for decompile");
                    }

                    full = true;
                    break;
                case "--limit":
                    limit = int.Parse(ReadOptionValue(args, ref index, "--limit"));
                    break;
                case "--offset":
                    offset = int.Parse(ReadOptionValue(args, ref index, "--offset"));
                    break;
                case "--source":
                    sourceFilter = ReadOptionValue(args, ref index, "--source");
                    break;
                case "--assembly":
                    assemblyFilter = ReadOptionValue(args, ref index, "--assembly");
                    break;
                case "--form":
                    formFilters.Add(ReadOptionValue(args, ref index, "--form"));
                    break;
                case "--has-script":
                    hasScript = true;
                    break;
                case "--sort":
                    sort = ReadOptionValue(args, ref index, "--sort");
                    break;
                case "--cache-dir":
                    cacheDir = ReadOptionValue(args, ref index, "--cache-dir");
                    break;
                case "--cache-mode":
                    cacheMode = ParseCacheMode(ReadOptionValue(args, ref index, "--cache-mode"));
                    break;
                default:
                    throw new ToolCommandException(2, "usage_error", $"unknown option '{args[index]}'");
            }
        }

        if (command is "locate" or "describe" or "decompile" or "refs" or "derived" or "hooks" or "hook-info" or "verify-references" or "decompile-export"
            && string.IsNullOrWhiteSpace(assembliesDir))
        {
            throw new ToolCommandException(2, "usage_error", "missing required --assemblies-dir option");
        }

        if (limit < 1)
        {
            throw new ToolCommandException(2, "usage_error", "--limit must be greater than zero");
        }

        if (offset < 0)
        {
            throw new ToolCommandException(2, "usage_error", "--offset must be zero or greater");
        }

        if (sourceFilter is not null && sourceFilter is not ("game" or "mod"))
        {
            throw new ToolCommandException(2, "usage_error", "--source must be 'game' or 'mod'");
        }

        if (sort is not ("relevance" or "name" or "reference-count" or "assembly"))
        {
            throw new ToolCommandException(2, "usage_error", "--sort must be one of relevance, name, reference-count, or assembly");
        }

        foreach (var form in formFilters)
        {
            if (form is not ("managed-prefix" or "managed-postfix" or "managed-override" or "managed-interface-contract"))
            {
                throw new ToolCommandException(2, "usage_error", $"unsupported hook form '{form}'");
            }
        }

        if (command is "scene-search" or "scene-tree" or "scene-node"
            or "asset-search" or "asset-index"
            && string.IsNullOrWhiteSpace(resourcesDir))
        {
            throw new ToolCommandException(2, "usage_error", "missing required --resources-dir option");
        }

        return new InspectionCommandRequest(
            Command: command,
            Subject: subject,
            Query: query,
            SecondaryQuery: secondaryQuery,
            ContainerPath: containerPath,
            AssembliesDir: assembliesDir,
            ControlAssembliesDir: controlAssembliesDir,
            ResourcesDir: resourcesDir,
            ModsDir: modsDir,
            ExcludeDir: excludeDir,
            IncludeMods: includeMods,
            IncludeDependencies: includeDependencies,
            Full: full,
            Limit: limit,
            Offset: offset,
            SourceFilter: sourceFilter,
            AssemblyFilter: assemblyFilter,
            FormFilters: formFilters,
            HasScript: hasScript,
            Sort: sort,
            CacheDir: cacheDir,
            CacheMode: cacheMode,
            Json: json);
    }

    private static MetadataCatalogCacheMode ParseCacheMode(string value)
    {
        return value switch
        {
            "auto" => MetadataCatalogCacheMode.Auto,
            "disabled" => MetadataCatalogCacheMode.Disabled,
            "refresh" => MetadataCatalogCacheMode.Refresh,
            "require-hit" => MetadataCatalogCacheMode.RequireHit,
            _ => throw new ToolCommandException(2, "usage_error", $"invalid --cache-mode '{value}'"),
        };
    }

    private static object ExecuteAssetCommand(InspectionCommandRequest request)
    {
        var service = new AssetCatalogService();
        return request.Command switch
        {
            "asset-search" => service.Search(request),
            "asset-index" => service.Index(request),
            "asset-read" => service.ReadPacked(request),
            _ => throw new ToolCommandException(2, "usage_error", $"unknown command '{request.Command}'"),
        };
    }

    /// <summary>
    /// A clean run exits 0, a candidate build that broke bindings exits 3 (the same code every
    /// other resolution failure uses), and a control build that could not resolve its own bindings
    /// exits 2, because that is an input problem that makes the whole run unreadable.
    /// </summary>
    private static int VerificationExitCode(string status)
    {
        return status switch
        {
            "ok" => 0,
            "control-dirty" => 2,
            _ => 3,
        };
    }

    private static string SerializeVerificationHuman(ReferenceVerificationResponse response)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"command: {response.Command}");
        builder.AppendLine($"status: {response.Status}");
        builder.AppendLine($"consumers: {string.Join(", ", response.Consumers.Select(consumer => consumer.Assembly))}");
        builder.AppendLine($"assembliesDir: {response.AssembliesDir}");
        if (response.ControlAssembliesDir is not null)
        {
            builder.AppendLine($"controlAssembliesDir: {response.ControlAssembliesDir}");
        }
        builder.AppendLine($"gameAssemblies: {string.Join(", ", response.GameAssemblies)}");
        builder.AppendLine($"typeReferenceCount: {response.TypeReferenceCount}");
        builder.AppendLine($"memberReferenceCount: {response.MemberReferenceCount}");
        builder.AppendLine($"breakCount: {response.BreakCount}");

        if (response.Control is { } control)
        {
            builder.AppendLine($"control: {control.Status} ({control.UnresolvedCount} unresolved)");
            AppendMissingTypes(builder, "control: missing types", control.MissingTypes);
            AppendMissingMembers(builder, "control: missing members (owner type missing)", control.MissingMembersWithMissingOwner);
            AppendMissingMembers(builder, "control: missing members", control.MissingMembers);
        }

        AppendMissingTypes(builder, "missing types", response.MissingTypes);
        AppendMissingMembers(builder, "missing members (owner type missing)", response.MissingMembersWithMissingOwner);
        AppendMissingMembers(builder, "missing members", response.MissingMembers);

        builder.AppendLine($"changed signatures: {response.ChangedSignatures.Count}");
        foreach (var change in response.ChangedSignatures)
        {
            builder.AppendLine($"  - {change.Assembly}  {change.Type}::{change.Member}   [{string.Join(" ", change.Consumers)}]");
            builder.AppendLine($"      control:   {change.ControlSignature}");
            builder.AppendLine($"      candidate: {change.CandidateSignature}");
        }

        builder.AppendLine("notes:");
        foreach (var note in response.Notes)
        {
            builder.AppendLine($"  - {note}");
        }

        return builder.ToString();
    }

    private static void AppendMissingTypes(
        StringBuilder builder,
        string label,
        IReadOnlyList<ReferenceMissingType> rows)
    {
        builder.AppendLine($"{label}: {rows.Count}");
        foreach (var row in rows)
        {
            builder.AppendLine($"  - {row.Assembly}  {row.Type}   [{string.Join(" ", row.Consumers)}]");
        }
    }

    private static void AppendMissingMembers(
        StringBuilder builder,
        string label,
        IReadOnlyList<ReferenceMissingMember> rows)
    {
        builder.AppendLine($"{label}: {rows.Count}");
        foreach (var row in rows)
        {
            var shape = row.ParameterCount is { } parameterCount
                ? $"{row.MemberKind}/{parameterCount}"
                : row.MemberKind;
            builder.AppendLine($"  - {row.Assembly}  {row.Type}::{row.Member} ({shape})   [{string.Join(" ", row.Consumers)}]");
        }
    }

    private static ToolCommandResult Failure(string message, string code = "usage_error", int exitCode = 2)
    {
        var payload = new
        {
            error = new
            {
                code,
                message,
            },
        };

        return new ToolCommandResult(exitCode, SerializeJson(payload));
    }

    private static string ReadOptionValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ToolCommandException(2, "usage_error", $"missing value for {option}");
        }

        index += 1;
        return args[index];
    }

    private static string SerializeJson<T>(T payload)
    {
        return JsonSerializer.Serialize(payload, JsonOptions.Default) + Environment.NewLine;
    }

    private static string SerializeHuman(InspectionResponse response)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"command: {response.Command}");
        builder.AppendLine($"status: {response.Status}");
        builder.AppendLine($"subject: {response.Subject}");
        builder.AppendLine($"query: {response.Query}");
        if (response.MatchCount is not null)
        {
            builder.AppendLine($"matchCount: {response.MatchCount}");
        }
        if (response.ReferenceCount is not null)
        {
            builder.AppendLine($"referenceCount: {response.ReferenceCount}");
        }
        if (response.DerivedCount is not null)
        {
            builder.AppendLine($"derivedCount: {response.DerivedCount}");
        }
        if (response.FullName is not null)
        {
            builder.AppendLine($"fullName: {response.FullName}");
        }
        if (response.Backend is not null)
        {
            builder.AppendLine($"backend: {response.Backend}");
        }
        if (response.Signature is not null)
        {
            builder.AppendLine($"signature: {response.Signature}");
        }
        if (response.BaseMethodId is not null)
        {
            builder.AppendLine($"baseMethodId: {response.BaseMethodId}");
        }
        if (response.ScenePath is not null)
        {
            builder.AppendLine($"scenePath: {response.ScenePath}");
        }
        if (response.ScriptPath is not null)
        {
            builder.AppendLine($"scriptPath: {response.ScriptPath}");
        }
        if (response.NodePath is not null)
        {
            builder.AppendLine($"nodePath: {response.NodePath}");
        }
        if (response.ResourcePath is not null)
        {
            builder.AppendLine($"resourcePath: {response.ResourcePath}");
        }
        if (response.Language is not null)
        {
            builder.AppendLine($"language: {response.Language}");
        }
        if (response.Text is not null)
        {
            builder.AppendLine("text:");
            builder.AppendLine(response.Text);
        }
        if (response.Matches is { Count: > 0 })
        {
            builder.AppendLine("matches:");
            foreach (var match in response.Matches)
            {
                builder.AppendLine($"  - {match.Kind}: {match.FullName}");
                if (match.ScriptPath is not null)
                {
                    builder.AppendLine($"    scriptPath: {match.ScriptPath}");
                }
            }
        }
        if (response.HookForms is { Count: > 0 })
        {
            builder.AppendLine($"hookForms: {string.Join(", ", response.HookForms)}");
        }
        if (response.ScenePaths is { Count: > 0 })
        {
            builder.AppendLine($"scenePaths: {string.Join(", ", response.ScenePaths)}");
        }
        if (response.References is { Count: > 0 })
        {
            builder.AppendLine("references:");
            foreach (var reference in response.References)
            {
                builder.AppendLine($"  - {reference.ReferenceKind}: {reference.ContainerFullName} -> {reference.TargetFullName} ({reference.Via}, {reference.Precision})");
            }
        }
        if (response.DerivedTypes is { Count: > 0 })
        {
            builder.AppendLine("derivedTypes:");
            foreach (var derivedType in response.DerivedTypes)
            {
                builder.AppendLine($"  - {derivedType.RelationKind}: {derivedType.FullName}");
            }
        }
        if (response.Fields is { Count: > 0 })
        {
            builder.AppendLine("fields:");
            foreach (var field in response.Fields)
            {
                builder.AppendLine($"  - {field.Type} {field.Name}");
            }
        }
        if (response.Properties is { Count: > 0 })
        {
            builder.AppendLine("properties:");
            foreach (var property in response.Properties)
            {
                builder.AppendLine($"  - {property.Type} {property.Name}");
            }
        }
        if (response.Events is { Count: > 0 })
        {
            builder.AppendLine("events:");
            foreach (var eventInfo in response.Events)
            {
                builder.AppendLine($"  - {eventInfo.Type} {eventInfo.Name}");
            }
        }
        if (response.Methods is { Count: > 0 })
        {
            builder.AppendLine("methods:");
            foreach (var method in response.Methods)
            {
                builder.AppendLine($"  - {method.Signature}");
            }
        }
        if (response.NestedTypes is { Count: > 0 })
        {
            builder.AppendLine("nestedTypes:");
            foreach (var nestedType in response.NestedTypes)
            {
                builder.AppendLine($"  - {nestedType.TypeKind}: {nestedType.FullName}");
            }
        }
        if (response.MemberSummaries is { Count: > 0 })
        {
            builder.AppendLine("memberSummaries:");
            foreach (var summary in response.MemberSummaries)
            {
                builder.AppendLine($"  - {summary.Signature}");
            }
        }
        if (response.Nodes is { Count: > 0 })
        {
            builder.AppendLine("nodes:");
            foreach (var node in response.Nodes)
            {
                builder.AppendLine($"  - {node.NodePath} ({node.NodeType ?? "node"})");
            }
        }
        if (response.Children is { Count: > 0 })
        {
            builder.AppendLine("children:");
            foreach (var child in response.Children)
            {
                builder.AppendLine($"  - {child.NodePath} ({child.NodeType ?? "node"})");
            }
        }
        if (response.ResourceRefs is { Count: > 0 })
        {
            builder.AppendLine("resourceRefs:");
            foreach (var resourceRef in response.ResourceRefs)
            {
                builder.AppendLine($"  - {resourceRef.Property} -> {resourceRef.ResourcePath ?? resourceRef.ResourceId}");
            }
        }
        if (response.NodeRefs is { Count: > 0 })
        {
            builder.AppendLine("nodeRefs:");
            foreach (var nodeRef in response.NodeRefs)
            {
                builder.AppendLine($"  - {nodeRef.Property} -> {nodeRef.TargetNodePath ?? nodeRef.RawPath}");
            }
        }
        if (response.Notes.Count > 0)
        {
            builder.AppendLine("notes:");
            foreach (var note in response.Notes)
            {
                builder.AppendLine($"  - {note}");
            }
        }

        return builder.ToString();
    }
}

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };
}
