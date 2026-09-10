using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Spirectl.DotnetTools.Inspection;

internal sealed class GodotScriptCatalog
{
    private readonly Dictionary<string, ManagedScriptType> _byScriptPath;
    private readonly Dictionary<string, ManagedScriptType> _byGlobalClassName;
    private readonly Dictionary<string, ManagedScriptType> _byTypeId;

    private GodotScriptCatalog(
        bool enabled,
        Dictionary<string, ManagedScriptType> byScriptPath,
        Dictionary<string, ManagedScriptType> byGlobalClassName,
        Dictionary<string, ManagedScriptType> byTypeId)
    {
        Enabled = enabled;
        _byScriptPath = byScriptPath;
        _byGlobalClassName = byGlobalClassName;
        _byTypeId = byTypeId;
    }

    public bool Enabled { get; }

    public static GodotScriptCatalog Load(string? assembliesDir, string? modsDir)
    {
        if (string.IsNullOrWhiteSpace(assembliesDir))
        {
            return new GodotScriptCatalog(
                enabled: false,
                byScriptPath: new Dictionary<string, ManagedScriptType>(StringComparer.OrdinalIgnoreCase),
                byGlobalClassName: new Dictionary<string, ManagedScriptType>(StringComparer.OrdinalIgnoreCase),
                byTypeId: new Dictionary<string, ManagedScriptType>(StringComparer.OrdinalIgnoreCase));
        }

        var byScriptPath = new Dictionary<string, ManagedScriptType>(StringComparer.OrdinalIgnoreCase);
        var byGlobalClassName = new Dictionary<string, ManagedScriptType>(StringComparer.OrdinalIgnoreCase);
        var byTypeId = new Dictionary<string, ManagedScriptType>(StringComparer.OrdinalIgnoreCase);

        foreach (var assemblyPath in EnumerateAssemblyFiles(assembliesDir, recursive: false))
        {
            IndexAssembly(assemblyPath, byScriptPath, byGlobalClassName, byTypeId);
        }

        if (!string.IsNullOrWhiteSpace(modsDir))
        {
            foreach (var assemblyPath in EnumerateAssemblyFiles(modsDir, recursive: true))
            {
                IndexAssembly(assemblyPath, byScriptPath, byGlobalClassName, byTypeId);
            }
        }

        return new GodotScriptCatalog(
            enabled: true,
            byScriptPath: byScriptPath,
            byGlobalClassName: byGlobalClassName,
            byTypeId: byTypeId);
    }

    public ManagedScriptType? ResolveByScriptPath(string? scriptPath)
    {
        if (string.IsNullOrWhiteSpace(scriptPath))
        {
            return null;
        }

        _byScriptPath.TryGetValue(NormalizeResourcePath(scriptPath), out var type);
        return type;
    }

    public ManagedScriptType? ResolveByGlobalClass(string? className)
    {
        if (string.IsNullOrWhiteSpace(className))
        {
            return null;
        }

        _byGlobalClassName.TryGetValue(className, out var type);
        return type;
    }

    public ManagedScriptType? ResolveByTypeId(string? typeId)
    {
        if (string.IsNullOrWhiteSpace(typeId))
        {
            return null;
        }

        _byTypeId.TryGetValue(typeId, out var type);
        return type;
    }

    private static IEnumerable<string> EnumerateAssemblyFiles(string root, bool recursive)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(root, "*.dll", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void IndexAssembly(
        string assemblyPath,
        Dictionary<string, ManagedScriptType> byScriptPath,
        Dictionary<string, ManagedScriptType> byGlobalClassName,
        Dictionary<string, ManagedScriptType> byTypeId)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        if (!peReader.HasMetadata)
        {
            return;
        }

        var reader = peReader.GetMetadataReader();
        if (!reader.IsAssembly)
        {
            return;
        }

        var assemblyDefinition = reader.GetAssemblyDefinition();
        var assemblyName = reader.GetString(assemblyDefinition.Name);

        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var typeDefinition = reader.GetTypeDefinition(typeHandle);
            var typeName = reader.GetString(typeDefinition.Name);
            if (typeName == "<Module>")
            {
                continue;
            }

            var fullName = MetadataNameFormatter.FormatTypeDefinition(reader, typeHandle);
            var scriptPaths = new List<string>();
            var isGlobalClass = false;

            foreach (var attributeHandle in typeDefinition.GetCustomAttributes())
            {
                var attribute = reader.GetCustomAttribute(attributeHandle);
                var attributeType = ResolveAttributeTypeFullName(reader, attribute);
                if (string.Equals(attributeType, "Godot.ScriptPathAttribute", StringComparison.Ordinal))
                {
                    var path = TryReadSingleStringArgument(reader, attribute);
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        scriptPaths.Add(NormalizeResourcePath(path));
                    }
                }

                if (string.Equals(attributeType, "Godot.GlobalClassAttribute", StringComparison.Ordinal))
                {
                    isGlobalClass = true;
                }
            }

            if (scriptPaths.Count == 0 && !isGlobalClass)
            {
                continue;
            }

            var scriptType = new ManagedScriptType(
                Id: $"type:{assemblyName}:{fullName}",
                FullName: fullName,
                DisplayName: typeName,
                ScriptPath: scriptPaths.FirstOrDefault(),
                IsGlobalClass: isGlobalClass);

            byTypeId[scriptType.Id] = scriptType;

            foreach (var scriptPath in scriptPaths)
            {
                if (!byScriptPath.ContainsKey(scriptPath))
                {
                    byScriptPath[scriptPath] = scriptType;
                }
            }

            if (isGlobalClass && !byGlobalClassName.ContainsKey(typeName))
            {
                byGlobalClassName[typeName] = scriptType;
            }
        }
    }

    private static string? ResolveAttributeTypeFullName(MetadataReader reader, CustomAttribute attribute)
    {
        return attribute.Constructor.Kind switch
        {
            HandleKind.MemberReference => MetadataNameFormatter.FormatAttributeType(
                reader,
                reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent),
            HandleKind.MethodDefinition => MetadataNameFormatter.FormatTypeDefinition(
                reader,
                reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType()),
            _ => null,
        };
    }

    private static string? TryReadSingleStringArgument(MetadataReader reader, CustomAttribute attribute)
    {
        try
        {
            var blobReader = reader.GetBlobReader(attribute.Value);
            if (blobReader.ReadUInt16() != 1)
            {
                return null;
            }

            return blobReader.ReadSerializedString();
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeResourcePath(string path)
    {
        return path.Replace('\\', '/');
    }
}

internal sealed record ManagedScriptType(
    string Id,
    string FullName,
    string DisplayName,
    string? ScriptPath,
    bool IsGlobalClass);
