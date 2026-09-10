using System.Reflection;
using System.Runtime.Loader;

namespace HotMod.Shell.Runtime;

public sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _shadowDirectory;

    public PluginLoadContext(string mainAssemblyPath)
        : base($"HotMod.Logic:{Path.GetFileNameWithoutExtension(mainAssemblyPath)}", isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
        _shadowDirectory = Path.GetDirectoryName(mainAssemblyPath) ?? AppContext.BaseDirectory;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (ShouldUseDefaultContext(assemblyName))
        {
            return FindDefaultAssembly(assemblyName);
        }

        var resolvedPath = _resolver.ResolveAssemblyToPath(assemblyName);
        if (resolvedPath is not null)
        {
            return LoadFromAssemblyPath(resolvedPath);
        }

        var localPath = Path.Combine(_shadowDirectory, $"{assemblyName.Name}.dll");
        return File.Exists(localPath) ? LoadFromAssemblyPath(localPath) : null;
    }

    private static bool ShouldUseDefaultContext(AssemblyName assemblyName)
    {
        var name = assemblyName.Name ?? string.Empty;
        return name == "HotMod.Contracts"
            || name.StartsWith("System.", StringComparison.Ordinal)
            || name.StartsWith("Microsoft.", StringComparison.Ordinal)
            || FindDefaultAssembly(assemblyName) is not null;
    }

    private static Assembly? FindDefaultAssembly(AssemblyName assemblyName)
    {
        return Default.Assemblies.FirstOrDefault(assembly =>
            AssemblyName.ReferenceMatchesDefinition(assembly.GetName(), assemblyName));
    }
}
