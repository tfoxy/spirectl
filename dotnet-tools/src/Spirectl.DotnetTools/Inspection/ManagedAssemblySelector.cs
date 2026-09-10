using Spirectl.DotnetTools.Models;

namespace Spirectl.DotnetTools.Inspection;

internal static class ManagedAssemblySelector
{
    public static IReadOnlyList<string> SelectAssemblyFiles(
        InspectionSearchRoot root,
        bool includeDependencies)
    {
        return MetadataCatalog.EnumerateAssemblyFiles(root)
            .Where(path => ShouldInclude(root, path, includeDependencies))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool ShouldInclude(
        InspectionSearchRoot root,
        string assemblyPath,
        bool includeDependencies)
    {
        if (includeDependencies || root.Source != "game")
        {
            return true;
        }

        var fileName = Path.GetFileName(assemblyPath);
        return !IsDefaultExcludedDependency(fileName);
    }

    private static bool IsDefaultExcludedDependency(string fileName)
    {
        return fileName.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("GodotSharp.dll", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("0Harmony.dll", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("MonoMod.", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("SmartFormat", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("Sentry.dll", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("Steamworks.NET.dll", StringComparison.OrdinalIgnoreCase);
    }
}
