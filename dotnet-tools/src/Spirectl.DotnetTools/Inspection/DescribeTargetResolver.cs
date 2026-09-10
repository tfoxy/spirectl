using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Spirectl.DotnetTools.Models;

namespace Spirectl.DotnetTools.Inspection;

internal static class DescribeTargetResolver
{
    public static IReadOnlyList<SelectedAssemblyPath>? ResolveSelectedAssemblies(
        string subject,
        string query,
        string assembliesDir,
        string? modsDir,
        bool includeDependencies)
    {
        var selectedAssemblies = EnumerateSelectedAssemblies(assembliesDir, modsDir, includeDependencies);

        if (TryParseStableIdAssembly(subject, query, out var stableAssemblyName))
        {
            var stableMatches = selectedAssemblies
                .Where(assembly => string.Equals(assembly.MetadataAssemblyName, stableAssemblyName, StringComparison.OrdinalIgnoreCase))
                .Select(assembly => assembly.ToSelectedAssemblyPath())
                .ToArray();

            return stableMatches.Length == 1 ? stableMatches : null;
        }

        if (!string.Equals(subject, "method", StringComparison.Ordinal) || !IsShortExactMethodQuery(query))
        {
            return null;
        }

        var methodAssemblyMatches = selectedAssemblies
            .Where(assembly => AssemblyContainsMethodDisplayName(assembly.Path, query))
            .Select(assembly => assembly.ToSelectedAssemblyPath())
            .ToArray();

        return methodAssemblyMatches.Length switch
        {
            0 => throw new ToolCommandException(3, "not_found", $"No exact method match was found for '{query}'."),
            1 => methodAssemblyMatches,
            _ => throw new ToolCommandException(3, "ambiguous_query", $"Query '{query}' matched more than one method. Use the exact locate id or declaring type signature."),
        };
    }

    internal static bool TryParseStableIdAssembly(string subject, string query, out string assemblyName)
    {
        assemblyName = string.Empty;
        var prefix = subject switch
        {
            "type" => "type:",
            "method" => "method:",
            _ => null,
        };

        if (prefix is null || !query.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var assemblyStart = prefix.Length;
        var assemblyEnd = query.IndexOf(':', assemblyStart);
        if (assemblyEnd <= assemblyStart)
        {
            return false;
        }

        assemblyName = query[assemblyStart..assemblyEnd];
        return !string.IsNullOrWhiteSpace(assemblyName);
    }

    internal static IReadOnlyList<SelectedAssemblyPath> FindAssemblyPathsByMetadataName(
        string metadataAssemblyName,
        string assembliesDir,
        string? modsDir,
        bool includeDependencies)
    {
        return EnumerateSelectedAssemblies(assembliesDir, modsDir, includeDependencies)
            .Where(assembly => string.Equals(assembly.MetadataAssemblyName, metadataAssemblyName, StringComparison.OrdinalIgnoreCase))
            .Select(assembly => assembly.ToSelectedAssemblyPath())
            .ToArray();
    }

    internal static IReadOnlyList<SelectedAssemblyPath> FindMethodCandidateAssemblyPaths(
        string methodDisplayName,
        string assembliesDir,
        string? modsDir,
        bool includeDependencies)
    {
        return EnumerateSelectedAssemblies(assembliesDir, modsDir, includeDependencies)
            .Where(assembly => AssemblyContainsMethodDisplayName(assembly.Path, methodDisplayName))
            .Select(assembly => assembly.ToSelectedAssemblyPath())
            .ToArray();
    }

    private static bool IsShortExactMethodQuery(string query)
    {
        return !string.IsNullOrWhiteSpace(query)
            && !query.StartsWith("method:", StringComparison.OrdinalIgnoreCase)
            && !query.Contains("::", StringComparison.Ordinal)
            && !query.Contains('(');
    }

    private static IReadOnlyList<SelectedAssemblyMetadata> EnumerateSelectedAssemblies(
        string assembliesDir,
        string? modsDir,
        bool includeDependencies)
    {
        var roots = MetadataCatalogCache.BuildSearchRoots(assembliesDir, modsDir, includeMods: true);
        var selected = new List<SelectedAssemblyMetadata>();

        foreach (var root in roots)
        {
            foreach (var assemblyPath in ManagedAssemblySelector.SelectAssemblyFiles(root, includeDependencies))
            {
                selected.Add(new SelectedAssemblyMetadata(
                    MetadataCatalogCache.CanonicalizePath(assemblyPath),
                    root.Source,
                    ReadMetadataAssemblyName(assemblyPath)));
            }
        }

        return selected
            .Where(assembly => !string.IsNullOrWhiteSpace(assembly.MetadataAssemblyName))
            .OrderBy(assembly => assembly.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? ReadMetadataAssemblyName(string assemblyPath)
    {
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(stream);
            if (!peReader.HasMetadata)
            {
                return null;
            }

            var reader = peReader.GetMetadataReader();
            if (!reader.IsAssembly)
            {
                return null;
            }

            return reader.GetString(reader.GetAssemblyDefinition().Name);
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool AssemblyContainsMethodDisplayName(string assemblyPath, string methodDisplayName)
    {
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(stream);
            if (!peReader.HasMetadata)
            {
                return false;
            }

            var reader = peReader.GetMetadataReader();
            foreach (var handle in reader.MethodDefinitions)
            {
                var methodDefinition = reader.GetMethodDefinition(handle);
                var methodName = reader.GetString(methodDefinition.Name);
                if (methodDefinition.Attributes.HasFlag(MethodAttributes.SpecialName)
                    || ShouldSkipMethodName(methodName))
                {
                    continue;
                }

                if (string.Equals(methodName, methodDisplayName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool ShouldSkipMethodName(string name)
    {
        return name is ".ctor" or ".cctor"
            || name.StartsWith("get_", StringComparison.Ordinal)
            || name.StartsWith("set_", StringComparison.Ordinal)
            || name.StartsWith("add_", StringComparison.Ordinal)
            || name.StartsWith("remove_", StringComparison.Ordinal);
    }

    private sealed record SelectedAssemblyMetadata(
        string Path,
        string Source,
        string? MetadataAssemblyName)
    {
        public SelectedAssemblyPath ToSelectedAssemblyPath()
        {
            return new SelectedAssemblyPath(Path, Source);
        }
    }
}
