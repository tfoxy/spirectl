using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Spirectl.DotnetTools.Inspection;

internal sealed class DecompileExportService
{
    public object Execute(InspectionCommandRequest request)
    {
        var assembliesDir = request.AssembliesDir ?? throw new ToolCommandException(
            2,
            "usage_error",
            "missing required --assemblies-dir option");
        var outputDir = request.Query;
        if (string.IsNullOrWhiteSpace(outputDir))
        {
            throw new ToolCommandException(2, "usage_error", "command 'decompile-export' requires <output-dir>");
        }

        PrepareOutputDirectory(outputDir);

        var summaries = new List<object>();
        var typeCount = 0;
        foreach (var assemblyPath in EnumerateAssemblies(assembliesDir))
        {
            var assemblyName = Path.GetFileNameWithoutExtension(assemblyPath);
            var assemblyOutputDir = Path.Combine(outputDir, assemblyName);
            Directory.CreateDirectory(assemblyOutputDir);

            var exportedTypes = ExportAssemblyTypes(assemblyPath, assemblyOutputDir);
            typeCount += exportedTypes;
            summaries.Add(new
            {
                assembly = assemblyName,
                assemblyPath,
                outputDir = assemblyOutputDir,
                typeCount = exportedTypes,
            });
        }

        return new
        {
            command = "decompile-export",
            outputDir,
            assemblyCount = summaries.Count,
            typeCount,
            assemblies = summaries,
        };
    }

    private static void PrepareOutputDirectory(string outputDir)
    {
        if (Directory.Exists(outputDir))
        {
            Directory.Delete(outputDir, recursive: true);
        }

        Directory.CreateDirectory(outputDir);
    }

    private static IReadOnlyList<string> EnumerateAssemblies(string assembliesDir)
    {
        if (!Directory.Exists(assembliesDir))
        {
            throw new ToolCommandException(
                3,
                "assemblies_dir_not_found",
                $"assemblies directory '{assembliesDir}' was not found");
        }

        return Directory
            .EnumerateFiles(assembliesDir)
            .Where(path =>
                path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int ExportAssemblyTypes(string assemblyPath, string outputDir)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        var metadataReader = peReader.GetMetadataReader();
        var typeCount = 0;

        foreach (var handle in metadataReader.TypeDefinitions)
        {
            var definition = metadataReader.GetTypeDefinition(handle);
            var name = metadataReader.GetString(definition.Name);
            if (string.Equals(name, "<Module>", StringComparison.Ordinal))
            {
                continue;
            }

            var namespaceName = metadataReader.GetString(definition.Namespace);
            var fileName = SanitizeFileName(string.IsNullOrWhiteSpace(namespaceName)
                ? name
                : $"{namespaceName}.{name}");
            var outputPath = Path.Combine(outputDir, $"{fileName}.cs");
            var contents = IlSpyDecompiler.Decompile(
                assemblyPath,
                MetadataTokens.GetToken(handle));
            File.WriteAllText(outputPath, contents);
            typeCount += 1;
        }

        return typeCount;
    }

    private static string SanitizeFileName(string fullName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = fullName
            .Select(ch => invalid.Contains(ch) || ch is '<' or '>' or ':' ? '_' : ch)
            .ToArray();
        return new string(chars);
    }
}
