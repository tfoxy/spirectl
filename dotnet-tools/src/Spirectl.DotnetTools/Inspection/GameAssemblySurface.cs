using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;

namespace Spirectl.DotnetTools.Inspection;

/// <summary>
/// One side of a reference verification run: the candidate game assemblies in a single
/// <c>data_sts2_*</c> directory, resolved the way a loader would resolve them.
/// </summary>
/// <remarks>
/// Type keys are only ever resolved against the configured game assemblies, so a binding that
/// silently moved into a framework assembly still reports as missing. Base types and interfaces,
/// on the other hand, resolve against anything reachable: the same directory first, then the
/// running runtime directory, following type-forwarder facades. That is what lets a member that
/// is declared on a base class or an interface — rather than on the type the compiler named —
/// still resolve.
/// </remarks>
internal sealed class GameAssemblySurface(string assembliesDir, IReadOnlyList<string> gameAssemblyNames)
    : IDisposable
{
    private const int MaxForwarderDepth = 8;
    private const int MaxInheritanceLevels = 512;
    private const string UnreadableType = "?";

    private static readonly string RuntimeDirectory = RuntimeEnvironment.GetRuntimeDirectory();

    private readonly Dictionary<string, LoadedAssembly?> _assemblies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ResolvedType?> _types = new(StringComparer.Ordinal);

    /// <summary>Resolve a referenced game type, falling back to the other game assemblies.</summary>
    public ResolvedType? ResolveGameType(string preferredAssembly, string typeFullName)
    {
        var cacheKey = $"{preferredAssembly}|{typeFullName}";
        if (_types.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var resolved = ResolveInAssemblyNamed(preferredAssembly, typeFullName);
        if (resolved is null)
        {
            foreach (var name in gameAssemblyNames)
            {
                if (string.Equals(name, preferredAssembly, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                resolved = ResolveInAssemblyNamed(name, typeFullName);
                if (resolved is not null)
                {
                    break;
                }
            }
        }

        _types[cacheKey] = resolved;
        return resolved;
    }

    /// <summary>
    /// Render every member named <paramref name="memberName"/> that is visible on the referenced
    /// type, walking its base chain and every interface it carries.
    /// </summary>
    /// <returns>
    /// <c>null</c> when the owner type itself is missing, an empty list when the type survives but
    /// carries no member of that name, otherwise one rendered signature per matching member.
    /// </returns>
    public IReadOnlyList<string>? DescribeMembers(string preferredAssembly, string typeFullName, string memberName)
    {
        var type = ResolveGameType(preferredAssembly, typeFullName);
        if (type is null)
        {
            return null;
        }

        var rendered = new List<string>();
        foreach (var level in CollectInheritanceLevels(type.Value))
        {
            rendered.AddRange(DescribeDeclaredMembers(level, memberName));
        }

        return [.. rendered.Distinct(StringComparer.Ordinal)];
    }

    public void Dispose()
    {
        foreach (var assembly in _assemblies.Values)
        {
            assembly?.Dispose();
        }

        _assemblies.Clear();
    }

    private LoadedAssembly? Load(string simpleName)
    {
        if (_assemblies.TryGetValue(simpleName, out var cached))
        {
            return cached;
        }

        LoadedAssembly? loaded = null;
        var path = ResolveAssemblyPath(simpleName);
        if (path is not null)
        {
            try
            {
                loaded = LoadedAssembly.Open(path);
            }
            catch (Exception error) when (error is IOException or BadImageFormatException or UnauthorizedAccessException)
            {
                loaded = null;
            }
        }

        _assemblies[simpleName] = loaded;
        return loaded;
    }

    private string? ResolveAssemblyPath(string simpleName)
    {
        var candidate = Path.Combine(assembliesDir, simpleName + ".dll");
        if (File.Exists(candidate))
        {
            return candidate;
        }

        var runtimeCandidate = Path.Combine(RuntimeDirectory, simpleName + ".dll");
        return File.Exists(runtimeCandidate) ? runtimeCandidate : null;
    }

    private ResolvedType? ResolveInAssemblyNamed(string simpleName, string typeFullName)
    {
        var assembly = Load(simpleName);
        return assembly is null ? null : ResolveInAssembly(assembly, typeFullName, 0);
    }

    private ResolvedType? ResolveInAssembly(LoadedAssembly assembly, string typeFullName, int depth)
    {
        if (assembly.Types.TryGetValue(typeFullName, out var handle))
        {
            return new ResolvedType(assembly, handle);
        }

        if (depth < MaxForwarderDepth && assembly.Forwarders.TryGetValue(typeFullName, out var target))
        {
            var forwarded = Load(target);
            if (forwarded is not null)
            {
                return ResolveInAssembly(forwarded, typeFullName, depth + 1);
            }
        }

        return null;
    }

    private IReadOnlyList<ResolvedType> CollectInheritanceLevels(ResolvedType type)
    {
        var levels = new List<ResolvedType>();
        var visited = new HashSet<ResolvedType>();
        var pending = new Queue<ResolvedType>();
        pending.Enqueue(type);

        while (pending.Count > 0 && levels.Count < MaxInheritanceLevels)
        {
            var current = pending.Dequeue();
            if (!visited.Add(current))
            {
                continue;
            }

            levels.Add(current);

            var baseType = ResolveBaseType(current);
            if (baseType is not null)
            {
                pending.Enqueue(baseType.Value);
            }

            foreach (var contract in ResolveInterfaces(current))
            {
                pending.Enqueue(contract);
            }
        }

        return levels;
    }

    private ResolvedType? ResolveBaseType(ResolvedType type)
    {
        var definition = type.Assembly.Reader.GetTypeDefinition(type.Handle);
        return ResolveTypeEntity(type.Assembly, definition.BaseType);
    }

    private IEnumerable<ResolvedType> ResolveInterfaces(ResolvedType type)
    {
        var definition = type.Assembly.Reader.GetTypeDefinition(type.Handle);
        foreach (var handle in definition.GetInterfaceImplementations())
        {
            var implementation = type.Assembly.Reader.GetInterfaceImplementation(handle);
            var resolved = ResolveTypeEntity(type.Assembly, implementation.Interface);
            if (resolved is not null)
            {
                yield return resolved.Value;
            }
        }
    }

    internal ResolvedType? ResolveTypeEntity(LoadedAssembly assembly, EntityHandle handle)
    {
        if (handle.IsNil)
        {
            return null;
        }

        return handle.Kind switch
        {
            HandleKind.TypeDefinition => new ResolvedType(assembly, (TypeDefinitionHandle)handle),
            HandleKind.TypeReference => ResolveTypeReference(assembly, (TypeReferenceHandle)handle),
            HandleKind.TypeSpecification => assembly.Reader
                .GetTypeSpecification((TypeSpecificationHandle)handle)
                .DecodeSignature(new ResolvingTypeProvider(this, assembly), null),
            _ => null,
        };
    }

    private ResolvedType? ResolveTypeReference(LoadedAssembly assembly, TypeReferenceHandle handle)
    {
        var reader = assembly.Reader;
        var reference = reader.GetTypeReference(handle);
        var name = reader.GetString(reference.Name);

        if (reference.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            var declaring = ResolveTypeReference(assembly, (TypeReferenceHandle)reference.ResolutionScope);
            return declaring is null ? null : FindNestedType(declaring.Value, name);
        }

        var namespaceName = reference.Namespace.IsNil ? string.Empty : reader.GetString(reference.Namespace);
        var fullName = namespaceName.Length == 0 ? name : $"{namespaceName}.{name}";

        if (reference.ResolutionScope.Kind == HandleKind.AssemblyReference)
        {
            var assemblyReference = reader.GetAssemblyReference((AssemblyReferenceHandle)reference.ResolutionScope);
            var target = Load(reader.GetString(assemblyReference.Name));
            return target is null ? null : ResolveInAssembly(target, fullName, 0);
        }

        return ResolveInAssembly(assembly, fullName, 0);
    }

    private static ResolvedType? FindNestedType(ResolvedType declaringType, string name)
    {
        var reader = declaringType.Assembly.Reader;
        foreach (var handle in reader.GetTypeDefinition(declaringType.Handle).GetNestedTypes())
        {
            if (string.Equals(reader.GetString(reader.GetTypeDefinition(handle).Name), name, StringComparison.Ordinal))
            {
                return new ResolvedType(declaringType.Assembly, handle);
            }
        }

        return null;
    }

    private static IEnumerable<string> DescribeDeclaredMembers(ResolvedType type, string memberName)
    {
        var reader = type.Assembly.Reader;
        var definition = reader.GetTypeDefinition(type.Handle);
        var provider = new SimpleTypeNameProvider();

        foreach (var handle in definition.GetFields())
        {
            var field = reader.GetFieldDefinition(handle);
            if (!string.Equals(reader.GetString(field.Name), memberName, StringComparison.Ordinal))
            {
                continue;
            }

            string fieldType;
            try
            {
                fieldType = field.DecodeSignature(provider, null);
            }
            catch (BadImageFormatException)
            {
                fieldType = UnreadableType;
            }

            yield return $"field {fieldType} {memberName}";
        }

        foreach (var handle in definition.GetMethods())
        {
            var method = reader.GetMethodDefinition(handle);
            if (!string.Equals(reader.GetString(method.Name), memberName, StringComparison.Ordinal))
            {
                continue;
            }

            var isConstructor = memberName is ".ctor" or ".cctor";
            var isStatic = (method.Attributes & MethodAttributes.Static) != 0;
            string rendered;
            try
            {
                var signature = method.DecodeSignature(provider, null);
                var parameters = string.Join(",", signature.ParameterTypes);
                rendered = isConstructor
                    ? $"ctor({parameters})"
                    : $"{signature.ReturnType} {memberName}({parameters}){(isStatic ? " static" : string.Empty)}";
            }
            catch (BadImageFormatException)
            {
                rendered = isConstructor
                    ? $"ctor({UnreadableType})"
                    : $"{UnreadableType} {memberName}({UnreadableType})";
            }

            yield return rendered;
        }

        foreach (var handle in definition.GetProperties())
        {
            var property = reader.GetPropertyDefinition(handle);
            if (!string.Equals(reader.GetString(property.Name), memberName, StringComparison.Ordinal))
            {
                continue;
            }

            string propertyType;
            try
            {
                propertyType = property.DecodeSignature(provider, null).ReturnType;
            }
            catch (BadImageFormatException)
            {
                propertyType = UnreadableType;
            }

            yield return $"prop {propertyType} {memberName}";
        }

        foreach (var handle in definition.GetEvents())
        {
            var eventDefinition = reader.GetEventDefinition(handle);
            if (string.Equals(reader.GetString(eventDefinition.Name), memberName, StringComparison.Ordinal))
            {
                yield return $"event {memberName}";
            }
        }

        foreach (var handle in definition.GetNestedTypes())
        {
            if (string.Equals(reader.GetString(reader.GetTypeDefinition(handle).Name), memberName, StringComparison.Ordinal))
            {
                yield return $"nested {memberName}";
            }
        }
    }

    /// <summary>A type definition plus the assembly whose metadata reader owns its handle.</summary>
    internal readonly record struct ResolvedType(LoadedAssembly Assembly, TypeDefinitionHandle Handle);

    /// <summary>One memory-mapped assembly with a lazily built type-definition and forwarder index.</summary>
    internal sealed class LoadedAssembly : IDisposable
    {
        private readonly FileStream _stream;
        private readonly PEReader _peReader;

        private LoadedAssembly(
            FileStream stream,
            PEReader peReader,
            MetadataReader reader,
            IReadOnlyDictionary<string, TypeDefinitionHandle> types,
            IReadOnlyDictionary<string, string> forwarders)
        {
            _stream = stream;
            _peReader = peReader;
            Reader = reader;
            Types = types;
            Forwarders = forwarders;
        }

        public MetadataReader Reader { get; }

        public IReadOnlyDictionary<string, TypeDefinitionHandle> Types { get; }

        public IReadOnlyDictionary<string, string> Forwarders { get; }

        public static LoadedAssembly Open(string path)
        {
            var stream = File.OpenRead(path);
            PEReader? peReader = null;
            try
            {
                peReader = new PEReader(stream);
                if (!peReader.HasMetadata)
                {
                    throw new BadImageFormatException($"'{path}' does not contain managed metadata.");
                }

                var reader = peReader.GetMetadataReader();
                return new LoadedAssembly(stream, peReader, reader, BuildTypeIndex(reader), BuildForwarderIndex(reader));
            }
            catch
            {
                peReader?.Dispose();
                stream.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            _peReader.Dispose();
            _stream.Dispose();
        }

        private static IReadOnlyDictionary<string, TypeDefinitionHandle> BuildTypeIndex(MetadataReader reader)
        {
            var index = new Dictionary<string, TypeDefinitionHandle>(StringComparer.Ordinal);
            foreach (var handle in reader.TypeDefinitions)
            {
                index.TryAdd(BuildTypeDefinitionName(reader, handle), handle);
            }

            return index;
        }

        private static IReadOnlyDictionary<string, string> BuildForwarderIndex(MetadataReader reader)
        {
            var index = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var handle in reader.ExportedTypes)
            {
                var exported = reader.GetExportedType(handle);
                if (!exported.IsForwarder || exported.Implementation.Kind != HandleKind.AssemblyReference)
                {
                    continue;
                }

                var name = reader.GetString(exported.Name);
                var namespaceName = exported.Namespace.IsNil ? string.Empty : reader.GetString(exported.Namespace);
                var fullName = namespaceName.Length == 0 ? name : $"{namespaceName}.{name}";
                var target = reader.GetAssemblyReference((AssemblyReferenceHandle)exported.Implementation);
                index.TryAdd(fullName, reader.GetString(target.Name));
            }

            return index;
        }

        private static string BuildTypeDefinitionName(MetadataReader reader, TypeDefinitionHandle handle)
        {
            var definition = reader.GetTypeDefinition(handle);
            var name = reader.GetString(definition.Name);
            var declaring = definition.GetDeclaringType();
            if (!declaring.IsNil)
            {
                return $"{BuildTypeDefinitionName(reader, declaring)}+{name}";
            }

            var namespaceName = reader.GetString(definition.Namespace);
            return namespaceName.Length == 0 ? name : $"{namespaceName}.{name}";
        }
    }

    /// <summary>
    /// Resolves a type signature down to one type definition, dropping generic arguments the same
    /// way reflection does when it reports the constructed type's declaring definition.
    /// </summary>
    private sealed class ResolvingTypeProvider(GameAssemblySurface surface, LoadedAssembly assembly)
        : ISignatureTypeProvider<ResolvedType?, object?>
    {
        public ResolvedType? GetArrayType(ResolvedType? elementType, ArrayShape shape) => null;

        public ResolvedType? GetByReferenceType(ResolvedType? elementType) => null;

        public ResolvedType? GetFunctionPointerType(MethodSignature<ResolvedType?> signature) => null;

        public ResolvedType? GetGenericInstantiation(ResolvedType? genericType, ImmutableArray<ResolvedType?> typeArguments) => genericType;

        public ResolvedType? GetGenericMethodParameter(object? genericContext, int index) => null;

        public ResolvedType? GetGenericTypeParameter(object? genericContext, int index) => null;

        public ResolvedType? GetModifiedType(ResolvedType? modifier, ResolvedType? unmodifiedType, bool isRequired) => unmodifiedType;

        public ResolvedType? GetPinnedType(ResolvedType? elementType) => elementType;

        public ResolvedType? GetPointerType(ResolvedType? elementType) => null;

        public ResolvedType? GetPrimitiveType(PrimitiveTypeCode typeCode) => null;

        public ResolvedType? GetSZArrayType(ResolvedType? elementType) => null;

        public ResolvedType? GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            return new ResolvedType(assembly, handle);
        }

        public ResolvedType? GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            return surface.ResolveTypeEntity(assembly, handle);
        }

        public ResolvedType? GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
        {
            return reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
        }
    }
}
