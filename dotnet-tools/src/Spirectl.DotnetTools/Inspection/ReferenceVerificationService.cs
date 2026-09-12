using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Spirectl.DotnetTools.Models;

namespace Spirectl.DotnetTools.Inspection;

/// <summary>
/// Answers "do this already-built assembly's game bindings still resolve against that game build?"
/// by reading the consumer's own <c>TypeRef</c>/<c>MemberRef</c> tables and resolving every game
/// binding against a candidate assemblies directory.
/// </summary>
/// <remarks>
/// This deliberately works on shipped binaries rather than on a compile. A cross-version build
/// stops at the first project whose declarations fail, and Roslyn never binds a method body once a
/// declaration diagnostic exists, so a compiler error list is a lower bound on the real delta
/// rather than the delta itself.
/// </remarks>
internal sealed class ReferenceVerificationService
{
    /// <summary>The assembly references whose bindings count as "the game".</summary>
    internal static readonly IReadOnlyList<string> GameAssemblies = ["sts2", "GodotSharp", "0Harmony"];

    public ReferenceVerificationResponse Execute(InspectionCommandRequest request)
    {
        var consumerPaths = ParseConsumerPaths(request.Query);
        var assembliesDir = RequireDirectory(
            request.AssembliesDir ?? throw new ToolCommandException(2, "usage_error", "missing required --assemblies-dir option"),
            "--assemblies-dir");
        var controlAssembliesDir = request.ControlAssembliesDir is null
            ? null
            : RequireDirectory(request.ControlAssembliesDir, "--control-assemblies-dir");

        var bindings = CollectBindings(consumerPaths);

        using var candidate = new GameAssemblySurface(assembliesDir, GameAssemblies);
        using var control = controlAssembliesDir is null
            ? null
            : new GameAssemblySurface(controlAssembliesDir, GameAssemblies);

        var controlReport = control is null ? null : BuildControlReport(bindings, control);
        var missingTypes = new List<ReferenceMissingType>();
        var missingMembersWithMissingOwner = new List<ReferenceMissingMember>();
        var missingMembers = new List<ReferenceMissingMember>();
        var changedSignatures = new List<ReferenceChangedSignature>();

        foreach (var type in bindings.Types)
        {
            if (candidate.ResolveGameType(type.Assembly, type.TypeFullName) is null)
            {
                missingTypes.Add(new ReferenceMissingType(type.Assembly, type.TypeFullName, [.. type.Consumers]));
            }
        }

        foreach (var member in bindings.Members)
        {
            IReadOnlyList<string>? controlMembers = null;
            if (control is not null)
            {
                controlMembers = control.DescribeMembers(member.Assembly, member.TypeFullName, member.MemberName);
                if (controlMembers is not { Count: > 0 })
                {
                    // The control could not resolve this binding either, so the candidate verdict
                    // for it would be a statement about the probe, not about the game build.
                    continue;
                }
            }

            var candidateMembers = candidate.DescribeMembers(member.Assembly, member.TypeFullName, member.MemberName);
            if (candidateMembers is null)
            {
                missingMembersWithMissingOwner.Add(ToMissingMember(member));
                continue;
            }

            if (candidateMembers.Count == 0)
            {
                missingMembers.Add(ToMissingMember(member));
                continue;
            }

            if (controlMembers is null)
            {
                continue;
            }

            var controlSignature = RenderSignatures(controlMembers);
            var candidateSignature = RenderSignatures(candidateMembers);
            if (!string.Equals(controlSignature, candidateSignature, StringComparison.Ordinal))
            {
                changedSignatures.Add(new ReferenceChangedSignature(
                    member.Assembly,
                    member.TypeFullName,
                    member.MemberName,
                    [.. member.Consumers],
                    controlSignature,
                    candidateSignature));
            }
        }

        var breakCount = missingTypes.Count
            + missingMembersWithMissingOwner.Count
            + missingMembers.Count
            + changedSignatures.Count;
        var status = controlReport is { Status: "dirty" }
            ? "control-dirty"
            : breakCount == 0 ? "ok" : "broken";

        return new ReferenceVerificationResponse(
            Command: "verify-references",
            Status: status,
            Notes: BuildNotes(controlReport),
            Consumers:
            [
                .. consumerPaths.Select(path => new ReferenceVerificationConsumer(Path.GetFileName(path), path)),
            ],
            GameAssemblies: GameAssemblies,
            AssembliesDir: assembliesDir,
            ControlAssembliesDir: controlAssembliesDir,
            TypeReferenceCount: bindings.Types.Count,
            MemberReferenceCount: bindings.Members.Count,
            BreakCount: breakCount,
            MissingTypes: missingTypes,
            MissingMembersWithMissingOwner: missingMembersWithMissingOwner,
            MissingMembers: missingMembers,
            ChangedSignatures: changedSignatures,
            Control: controlReport);
    }

    private static ReferenceVerificationControl BuildControlReport(BindingSet bindings, GameAssemblySurface control)
    {
        var missingTypes = new List<ReferenceMissingType>();
        var missingMembersWithMissingOwner = new List<ReferenceMissingMember>();
        var missingMembers = new List<ReferenceMissingMember>();

        foreach (var type in bindings.Types)
        {
            if (control.ResolveGameType(type.Assembly, type.TypeFullName) is null)
            {
                missingTypes.Add(new ReferenceMissingType(type.Assembly, type.TypeFullName, [.. type.Consumers]));
            }
        }

        foreach (var member in bindings.Members)
        {
            var described = control.DescribeMembers(member.Assembly, member.TypeFullName, member.MemberName);
            if (described is null)
            {
                missingMembersWithMissingOwner.Add(ToMissingMember(member));
            }
            else if (described.Count == 0)
            {
                missingMembers.Add(ToMissingMember(member));
            }
        }

        var unresolvedCount = missingTypes.Count + missingMembersWithMissingOwner.Count + missingMembers.Count;
        return new ReferenceVerificationControl(
            unresolvedCount == 0 ? "clean" : "dirty",
            unresolvedCount,
            missingTypes,
            missingMembersWithMissingOwner,
            missingMembers);
    }

    private static IReadOnlyList<string> BuildNotes(ReferenceVerificationControl? control)
    {
        var notes = new List<string>
        {
            "Bindings come from the consumer assemblies' own metadata reference tables, so this reports what the shipped binaries bind to, not what their sources would compile to today.",
        };

        if (control is null)
        {
            notes.Add("Signature-change detection needs a known-good build to compare against; pass --control-assemblies-dir to get it. Without one, only unresolved types and members are reported.");
            return notes;
        }

        notes.Add(control.Status == "clean"
            ? "The control build resolved every binding, so the candidate verdict is about the game build rather than about this probe."
            : "The control build left bindings unresolved, so the candidate verdict is void: fix the control inputs before reading the candidate buckets.");
        return notes;
    }

    private static ReferenceMissingMember ToMissingMember(MemberBinding member)
    {
        return new ReferenceMissingMember(
            member.Assembly,
            member.TypeFullName,
            member.MemberName,
            member.MemberKind,
            member.ParameterCount,
            [.. member.Consumers]);
    }

    private static string RenderSignatures(IReadOnlyList<string> members)
    {
        return string.Join(" | ", members.OrderBy(member => member, StringComparer.Ordinal));
    }

    private static IReadOnlyList<string> ParseConsumerPaths(string query)
    {
        var paths = query
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
        if (paths.Length == 0)
        {
            throw new ToolCommandException(
                2,
                "usage_error",
                "command 'verify-references' requires at least one consumer assembly path");
        }

        foreach (var path in paths)
        {
            if (!File.Exists(path))
            {
                throw new ToolCommandException(3, "not_found", $"Consumer assembly '{path}' was not found.");
            }
        }

        return paths;
    }

    private static string RequireDirectory(string path, string option)
    {
        if (!Directory.Exists(path))
        {
            throw new ToolCommandException(3, "not_found", $"Assemblies directory '{path}' for {option} was not found.");
        }

        return path;
    }

    private static BindingSet CollectBindings(IReadOnlyList<string> consumerPaths)
    {
        var types = new SortedDictionary<string, TypeBinding>(StringComparer.Ordinal);
        var members = new SortedDictionary<string, MemberBinding>(StringComparer.Ordinal);

        foreach (var path in consumerPaths)
        {
            var consumer = Path.GetFileName(path);
            using var stream = File.OpenRead(path);
            using var peReader = new PEReader(stream);
            if (!peReader.HasMetadata)
            {
                throw new ToolCommandException(
                    2,
                    "usage_error",
                    $"Consumer assembly '{path}' does not contain managed metadata.");
            }

            var reader = peReader.GetMetadataReader();
            var gameScopes = new Dictionary<AssemblyReferenceHandle, string>();
            foreach (var handle in reader.AssemblyReferences)
            {
                var name = reader.GetString(reader.GetAssemblyReference(handle).Name);
                if (GameAssemblies.Contains(name, StringComparer.Ordinal))
                {
                    gameScopes[handle] = name;
                }
            }

            if (gameScopes.Count == 0)
            {
                continue;
            }

            foreach (var handle in reader.TypeReferences)
            {
                var assembly = ResolveGameScope(reader, gameScopes, handle);
                if (assembly is null)
                {
                    continue;
                }

                var typeFullName = BuildTypeReferenceName(reader, handle);
                var key = $"{assembly}|{typeFullName}";
                if (!types.TryGetValue(key, out var binding))
                {
                    types[key] = binding = new TypeBinding(assembly, typeFullName, new SortedSet<string>(StringComparer.Ordinal));
                }

                binding.Consumers.Add(consumer);
            }

            var provider = new SignatureTypeNameProvider();
            foreach (var handle in reader.MemberReferences)
            {
                var reference = reader.GetMemberReference(handle);
                if (reference.Parent.Kind != HandleKind.TypeReference)
                {
                    continue;
                }

                var parent = (TypeReferenceHandle)reference.Parent;
                var assembly = ResolveGameScope(reader, gameScopes, parent);
                if (assembly is null)
                {
                    continue;
                }

                var typeFullName = BuildTypeReferenceName(reader, parent);
                var memberName = reader.GetString(reference.Name);
                string kind;
                int? parameterCount = null;
                if (reference.GetKind() == MemberReferenceKind.Method)
                {
                    kind = "method";
                    parameterCount = reference.DecodeMethodSignature(provider, null).ParameterTypes.Length;
                }
                else
                {
                    kind = "field";
                }

                var key = $"{assembly}|{typeFullName}::{memberName}/{parameterCount?.ToString() ?? "-"}/{kind}";
                if (!members.TryGetValue(key, out var binding))
                {
                    members[key] = binding = new MemberBinding(
                        assembly,
                        typeFullName,
                        memberName,
                        kind,
                        parameterCount,
                        new SortedSet<string>(StringComparer.Ordinal));
                }

                binding.Consumers.Add(consumer);
            }
        }

        return new BindingSet([.. types.Values], [.. members.Values]);
    }

    private static string? ResolveGameScope(
        MetadataReader reader,
        IReadOnlyDictionary<AssemblyReferenceHandle, string> gameScopes,
        TypeReferenceHandle handle)
    {
        var scope = reader.GetTypeReference(handle).ResolutionScope;
        return scope.Kind switch
        {
            HandleKind.AssemblyReference => gameScopes.TryGetValue((AssemblyReferenceHandle)scope, out var name) ? name : null,
            HandleKind.TypeReference => ResolveGameScope(reader, gameScopes, (TypeReferenceHandle)scope),
            _ => null,
        };
    }

    private static string BuildTypeReferenceName(MetadataReader reader, TypeReferenceHandle handle)
    {
        var reference = reader.GetTypeReference(handle);
        var name = reader.GetString(reference.Name);
        if (reference.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            return $"{BuildTypeReferenceName(reader, (TypeReferenceHandle)reference.ResolutionScope)}+{name}";
        }

        var namespaceName = reference.Namespace.IsNil ? string.Empty : reader.GetString(reference.Namespace);
        return namespaceName.Length == 0 ? name : $"{namespaceName}.{name}";
    }

    private sealed record BindingSet(IReadOnlyList<TypeBinding> Types, IReadOnlyList<MemberBinding> Members);

    private sealed record TypeBinding(string Assembly, string TypeFullName, SortedSet<string> Consumers);

    private sealed record MemberBinding(
        string Assembly,
        string TypeFullName,
        string MemberName,
        string MemberKind,
        int? ParameterCount,
        SortedSet<string> Consumers);
}
