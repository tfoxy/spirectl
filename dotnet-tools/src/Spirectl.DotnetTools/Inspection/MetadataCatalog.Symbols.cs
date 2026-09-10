using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using Spirectl.DotnetTools.Models;


namespace Spirectl.DotnetTools.Inspection;

internal sealed partial class MetadataCatalog
{
    private sealed class AssemblyIndex
    {
        private AssemblyIndex(
            string assemblyName,
            string assemblyPath,
            string source,
            IReadOnlyDictionary<TypeDefinitionHandle, TypeSymbolBuilder> types,
            IReadOnlyDictionary<MethodDefinitionHandle, MethodSymbolBuilder> methods)
        {
            AssemblyName = assemblyName;
            AssemblyPath = assemblyPath;
            Source = source;
            Types = types;
            Methods = methods;
        }

        public string AssemblyName { get; }

        public string AssemblyPath { get; }

        public string Source { get; }

        public IReadOnlyDictionary<TypeDefinitionHandle, TypeSymbolBuilder> Types { get; }

        public IReadOnlyDictionary<MethodDefinitionHandle, MethodSymbolBuilder> Methods { get; }

        public static AssemblyIndex LoadMetadata(string assemblyPath, string source)
        {
            using var stream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(stream);
            if (!peReader.HasMetadata)
            {
                return new AssemblyIndex(
                    Path.GetFileNameWithoutExtension(assemblyPath),
                    assemblyPath,
                    source,
                    new Dictionary<TypeDefinitionHandle, TypeSymbolBuilder>(),
                    new Dictionary<MethodDefinitionHandle, MethodSymbolBuilder>());
            }

            var reader = peReader.GetMetadataReader();
            if (!reader.IsAssembly)
            {
                return new AssemblyIndex(
                    Path.GetFileNameWithoutExtension(assemblyPath),
                    assemblyPath,
                    source,
                    new Dictionary<TypeDefinitionHandle, TypeSymbolBuilder>(),
                    new Dictionary<MethodDefinitionHandle, MethodSymbolBuilder>());
            }

            var assemblyDefinition = reader.GetAssemblyDefinition();
            var assemblyName = reader.GetString(assemblyDefinition.Name);
            var provider = new SignatureTypeNameProvider();
            var types = new Dictionary<TypeDefinitionHandle, TypeSymbolBuilder>();
            var methods = new Dictionary<MethodDefinitionHandle, MethodSymbolBuilder>();

            foreach (var typeHandle in reader.TypeDefinitions)
            {
                var typeDefinition = reader.GetTypeDefinition(typeHandle);
                var typeName = reader.GetString(typeDefinition.Name);
                if (typeName == "<Module>")
                {
                    continue;
                }

                var fullName = MetadataNameFormatter.FormatTypeDefinition(reader, typeHandle);
                var namespaceName = MetadataNameFormatter.ExtractNamespace(fullName);
                var visibility = FormatTypeVisibility(typeDefinition.Attributes);
                var typeKind = DetermineTypeKind(typeDefinition, reader);
                var baseType = typeDefinition.BaseType.IsNil
                    ? null
                    : MetadataNameFormatter.FormatEntityName(reader, typeDefinition.BaseType);
                var interfaces = typeDefinition
                    .GetInterfaceImplementations()
                    .Select(handle => reader.GetInterfaceImplementation(handle))
                    .Select(implementation => MetadataNameFormatter.FormatEntityName(reader, implementation.Interface))
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray();
                var declaringTypeFullName = typeDefinition.Attributes.HasFlag(TypeAttributes.NestedPublic)
                    || typeDefinition.Attributes.HasFlag(TypeAttributes.NestedPrivate)
                    || typeDefinition.Attributes.HasFlag(TypeAttributes.NestedFamily)
                    || typeDefinition.Attributes.HasFlag(TypeAttributes.NestedAssembly)
                    || typeDefinition.Attributes.HasFlag(TypeAttributes.NestedFamORAssem)
                    ? MetadataNameFormatter.FindDeclaringType(reader, typeHandle)
                    : null;

                var fieldBuilders = typeDefinition
                    .GetFields()
                    .Select(handle =>
                    {
                        var field = reader.GetFieldDefinition(handle);
                        var fieldName = reader.GetString(field.Name);
                        var compilerGenerated = HasCompilerGeneratedAttribute(reader, field.GetCustomAttributes());
                        return new FieldSymbolBuilder(
                            Handle: handle,
                            Id: $"field:{assemblyName}:{fullName}::{fieldName}",
                            Name: fieldName,
                            FullName: $"{fullName}::{fieldName}",
                            Type: field.DecodeSignature(provider, null),
                            Visibility: FormatFieldVisibility(field.Attributes),
                            IsStatic: field.Attributes.HasFlag(FieldAttributes.Static),
                            IsHidden: compilerGenerated || fieldName.Contains("k__BackingField", StringComparison.Ordinal));
                    })
                    .OrderBy(field => field.Name, StringComparer.Ordinal)
                    .ToArray();

                var propertyBuilders = typeDefinition
                    .GetProperties()
                    .Select(handle =>
                    {
                        var property = reader.GetPropertyDefinition(handle);
                        var accessors = property.GetAccessors();
                        var getterVisibility = accessors.Getter.IsNil ? null : FormatMethodVisibility(reader.GetMethodDefinition(accessors.Getter).Attributes);
                        var setterVisibility = accessors.Setter.IsNil ? null : FormatMethodVisibility(reader.GetMethodDefinition(accessors.Setter).Attributes);
                        var signature = property.DecodeSignature(provider, null);
                        var propertyName = reader.GetString(property.Name);
                        return new PropertySymbol(
                            Id: $"property:{assemblyName}:{fullName}::{propertyName}",
                            Name: propertyName,
                            Type: signature.ReturnType,
                            Visibility: ChooseAccessorVisibility(getterVisibility, setterVisibility),
                            HasGetter: !accessors.Getter.IsNil,
                            HasSetter: !accessors.Setter.IsNil,
                            GetterVisibility: getterVisibility,
                            SetterVisibility: setterVisibility);
                    })
                    .OrderBy(property => property.Name, StringComparer.Ordinal)
                    .ToArray();

                var eventBuilders = typeDefinition
                    .GetEvents()
                    .Select(handle =>
                    {
                        var eventDefinition = reader.GetEventDefinition(handle);
                        var accessors = eventDefinition.GetAccessors();
                        var adderVisibility = accessors.Adder.IsNil ? null : FormatMethodVisibility(reader.GetMethodDefinition(accessors.Adder).Attributes);
                        var removerVisibility = accessors.Remover.IsNil ? null : FormatMethodVisibility(reader.GetMethodDefinition(accessors.Remover).Attributes);
                        var eventName = reader.GetString(eventDefinition.Name);
                        return new EventSymbol(
                            Id: $"event:{assemblyName}:{fullName}::{eventName}",
                            Name: eventName,
                            Type: MetadataNameFormatter.FormatEntityName(reader, eventDefinition.Type),
                            Visibility: ChooseAccessorVisibility(adderVisibility, removerVisibility),
                            HasAdder: !accessors.Adder.IsNil,
                            HasRemover: !accessors.Remover.IsNil,
                            AdderVisibility: adderVisibility,
                            RemoverVisibility: removerVisibility);
                    })
                    .OrderBy(eventSymbol => eventSymbol.Name, StringComparer.Ordinal)
                    .ToArray();

                var methodBuilders = typeDefinition
                    .GetMethods()
                    .Select(handle =>
                    {
                        var method = reader.GetMethodDefinition(handle);
                        var methodName = reader.GetString(method.Name);
                        if (method.Attributes.HasFlag(MethodAttributes.SpecialName) || ShouldSkipMethodName(methodName))
                        {
                            return null;
                        }

                        var decodedSignature = method.DecodeSignature(provider, null);
                        var parameterNames = method
                            .GetParameters()
                            .Select(parameterHandle => reader.GetParameter(parameterHandle))
                            .Where(parameter => parameter.SequenceNumber > 0)
                            .Select(parameter => reader.GetString(parameter.Name))
                            .ToArray();
                        var parameters = decodedSignature.ParameterTypes
                            .Select((type, index) => new InspectionParameterInfo(
                                parameterNames.ElementAtOrDefault(index) ?? $"arg{index}",
                                type))
                            .ToArray();
                        var parameterTypeList = string.Join(", ", parameters.Select(parameter => parameter.Type));
                        var lookupSignature = $"{fullName}::{methodName}({string.Join(",", parameters.Select(parameter => parameter.Type))})";
                        var signature = $"{decodedSignature.ReturnType} {methodName}({parameterTypeList})";

                        return new MethodSymbolBuilder(
                            handle: handle,
                            id: $"method:{assemblyName}:{lookupSignature}",
                            displayName: methodName,
                            fullName: $"{fullName}::{methodName}",
                            @namespace: namespaceName,
                            assembly: assemblyName,
                            assemblyPath: assemblyPath,
                            source: source,
                            signature: signature,
                            lookupSignature: lookupSignature,
                            declaringType: fullName,
                            visibility: FormatMethodVisibility(method.Attributes),
                            parameters: parameters,
                            returnType: decodedSignature.ReturnType,
                            isStatic: method.Attributes.HasFlag(MethodAttributes.Static),
                            isAbstract: method.Attributes.HasFlag(MethodAttributes.Abstract),
                            isVirtual: method.Attributes.HasFlag(MethodAttributes.Virtual));
                    })
                    .Where(method => method is not null)
                    .Cast<MethodSymbolBuilder>()
                    .OrderBy(method => method.DisplayName, StringComparer.Ordinal)
                    .ThenBy(method => method.Signature, StringComparer.Ordinal)
                    .ToArray();

                foreach (var methodBuilder in methodBuilders)
                {
                    methods[methodBuilder.Handle] = methodBuilder;
                }

                types[typeHandle] = new TypeSymbolBuilder(
                    handle: typeHandle,
                    id: $"type:{assemblyName}:{fullName}",
                    displayName: typeName,
                    fullName: fullName,
                    @namespace: namespaceName,
                    assembly: assemblyName,
                    assemblyPath: assemblyPath,
                    source: source,
                    typeKind: typeKind,
                    visibility: visibility,
                    baseType: baseType,
                    interfaces: interfaces,
                    declaringTypeFullName: declaringTypeFullName,
                    fields: fieldBuilders,
                    properties: propertyBuilders,
                    events: eventBuilders,
                    methods: methodBuilders);
            }

            return new AssemblyIndex(assemblyName, assemblyPath, source, types, methods);
        }
    }

    private sealed class SymbolLookup
    {
        private readonly Dictionary<string, TypeSymbolBuilder> _typeIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TypeSymbolBuilder> _typesByFullName = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, MethodSymbolBuilder> _methodIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, MethodSymbolBuilder> _methodsByLookupSignature = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, FieldSymbolBuilder> _fieldsByFullName = new(StringComparer.OrdinalIgnoreCase);

        public void AddType(TypeSymbolBuilder type)
        {
            _typeIds[type.Id] = type;
            if (!_typesByFullName.ContainsKey(type.FullName))
            {
                _typesByFullName[type.FullName] = type;
            }
        }

        public void AddMethod(MethodSymbolBuilder method)
        {
            _methodIds[method.Id] = method;
            if (!_methodsByLookupSignature.ContainsKey(method.LookupSignature))
            {
                _methodsByLookupSignature[method.LookupSignature] = method;
            }
        }

        public void AddField(FieldSymbolBuilder field)
        {
            if (!_fieldsByFullName.ContainsKey(field.FullName))
            {
                _fieldsByFullName[field.FullName] = field;
            }
        }

        public TypeSymbolBuilder? ResolveType(string fullName)
        {
            return _typesByFullName.TryGetValue(fullName, out var type) ? type : null;
        }

        public string? ResolveTypeId(string? fullName)
        {
            return string.IsNullOrWhiteSpace(fullName) ? null : ResolveType(fullName)?.Id;
        }

        public MethodSymbolBuilder? ResolveMethod(string lookupSignature)
        {
            return _methodsByLookupSignature.TryGetValue(lookupSignature, out var method) ? method : null;
        }

        public FieldSymbolBuilder? ResolveField(string fullName)
        {
            return _fieldsByFullName.TryGetValue(fullName, out var field) ? field : null;
        }
    }

    private sealed record ContainerDescriptor(
        string Id,
        string Kind,
        string DisplayName,
        string FullName,
        string? Signature,
        string Assembly,
        string Source)
    {
        public static ContainerDescriptor ForType(TypeSymbolBuilder type)
        {
            return new ContainerDescriptor(type.Id, "type", type.DisplayName, type.FullName, null, type.Assembly, type.Source);
        }

        public static ContainerDescriptor ForMethod(MethodSymbolBuilder method)
        {
            return new ContainerDescriptor(method.Id, "method", method.DisplayName, method.FullName, method.Signature, method.Assembly, method.Source);
        }
    }

    private sealed record ReferenceTarget(
        string? Id,
        string Kind,
        string DisplayName,
        string FullName,
        string? Signature,
        bool IsHidden = false,
        bool IsSpecialName = false)
    {
        public static ReferenceTarget FromType(TypeSymbolBuilder type)
        {
            return new ReferenceTarget(type.Id, "type", type.DisplayName, type.FullName, null);
        }

        public static ReferenceTarget FromMethod(MethodSymbolBuilder method)
        {
            return new ReferenceTarget(method.Id, "method", method.DisplayName, method.FullName, method.Signature);
        }

        public static ReferenceTarget FromField(FieldSymbolBuilder field)
        {
            return new ReferenceTarget(field.Id, "field", field.Name, field.FullName, field.Type, field.IsHidden);
        }

        public ReferenceTarget ToDeclaringTypeTarget(SymbolLookup lookup)
        {
            if (Kind != "method")
            {
                return this;
            }

            var declaringTypeName = FullName.Contains("::", StringComparison.Ordinal)
                ? FullName[..FullName.IndexOf("::", StringComparison.Ordinal)]
                : FullName;
            return ResolveTypeTarget(declaringTypeName, lookup)
                ?? new ReferenceTarget(null, "type", ExtractDisplayName(declaringTypeName), declaringTypeName, null);
        }
    }

    internal sealed record MetadataCatalogSnapshot(
        IReadOnlyList<InspectionSearchRoot> SearchRoots,
        IReadOnlyList<TypeSymbol> Types,
        IReadOnlyList<MethodSymbol> Methods,
        IReadOnlyList<ReferenceEdge> References);

    internal sealed record ReferenceEdge(
        string ContainerId,
        string ContainerKind,
        string ContainerDisplayName,
        string ContainerFullName,
        string? ContainerSignature,
        string? TargetId,
        string TargetKind,
        string TargetDisplayName,
        string TargetFullName,
        string? TargetSignature,
        string ReferenceKind,
        string Via,
        string Precision,
        string Assembly,
        string Source)
    {
        public InspectionReferenceInfo ToInspectionReference(string? precisionOverride = null, string? matchedText = null)
        {
            return new InspectionReferenceInfo(
                ContainerId,
                ContainerKind,
                ContainerDisplayName,
                ContainerFullName,
                ContainerSignature,
                TargetId,
                TargetKind,
                TargetDisplayName,
                TargetFullName,
                TargetSignature,
                ReferenceKind,
                Via,
                precisionOverride ?? Precision,
                matchedText,
                Assembly,
                Source);
        }
    }

    internal sealed record SummaryReference(
        string? Id,
        string Kind,
        string DisplayName,
        string FullName,
        string? Signature,
        string Via,
        string Precision)
    {
        public InspectionSummaryReference ToInspectionSummaryReference()
        {
            return new InspectionSummaryReference(Id, Kind, DisplayName, FullName, Signature, Via, Precision);
        }
    }

    internal sealed record TypeRelationship(
        string TargetTypeId,
        string Id,
        string DisplayName,
        string FullName,
        string Namespace,
        string Assembly,
        string AssemblyPath,
        string Source,
        string RelationKind)
    {
        public InspectionDerivedTypeInfo ToInspectionDerivedType()
        {
            return new InspectionDerivedTypeInfo(Id, DisplayName, FullName, Namespace, Assembly, AssemblyPath, Source, RelationKind);
        }
    }

    internal sealed record NestedTypeSymbol(
        string Id,
        string Name,
        string FullName,
        string TypeKind,
        string Visibility)
    {
        public InspectionNestedTypeInfo ToInspectionNestedType()
        {
            return new InspectionNestedTypeInfo(Id, Name, FullName, TypeKind, Visibility);
        }
    }

    internal sealed record FieldSymbol(
        string Id,
        string Name,
        string FullName,
        string Type,
        string Visibility,
        bool IsStatic)
    {
        public InspectionFieldInfo ToInspectionField()
        {
            return new InspectionFieldInfo(Id, Name, Type, Visibility, IsStatic);
        }
    }

    internal sealed record PropertySymbol(
        string Id,
        string Name,
        string Type,
        string Visibility,
        bool HasGetter,
        bool HasSetter,
        string? GetterVisibility,
        string? SetterVisibility)
    {
        public InspectionPropertyInfo ToInspectionProperty()
        {
            return new InspectionPropertyInfo(Id, Name, Type, Visibility, HasGetter, HasSetter, GetterVisibility, SetterVisibility);
        }
    }

    internal sealed record EventSymbol(
        string Id,
        string Name,
        string Type,
        string Visibility,
        bool HasAdder,
        bool HasRemover,
        string? AdderVisibility,
        string? RemoverVisibility)
    {
        public InspectionEventInfo ToInspectionEvent()
        {
            return new InspectionEventInfo(Id, Name, Type, Visibility, HasAdder, HasRemover, AdderVisibility, RemoverVisibility);
        }
    }

    internal sealed record TypeSymbol(
        string Id,
        string DisplayName,
        string FullName,
        string Namespace,
        string Assembly,
        string AssemblyPath,
        string Source,
        int MetadataToken,
        string TypeKind,
        string Visibility,
        string? BaseType,
        string? BaseTypeId,
        IReadOnlyList<string> Interfaces,
        IReadOnlyList<string> InterfaceIds,
        IReadOnlyList<FieldSymbol> Fields,
        IReadOnlyList<PropertySymbol> Properties,
        IReadOnlyList<EventSymbol> Events,
        IReadOnlyList<MethodSymbol> Methods,
        IReadOnlyList<NestedTypeSymbol> NestedTypes,
        IReadOnlyList<TypeRelationship> Relationships);

    internal sealed record MethodSymbol(
        string Id,
        string DisplayName,
        string FullName,
        string Namespace,
        string Assembly,
        string AssemblyPath,
        string Source,
        int MetadataToken,
        string Signature,
        string LookupSignature,
        string DeclaringType,
        string DeclaringTypeId,
        string Visibility,
        string ReturnType,
        IReadOnlyList<InspectionParameterInfo> Parameters,
        bool IsStatic,
        bool IsAbstract,
        bool IsVirtual,
        IReadOnlyList<SummaryReference> Calls,
        IReadOnlyList<SummaryReference> FieldReads,
        IReadOnlyList<SummaryReference> FieldWrites,
        IReadOnlyList<SummaryReference> TypeRefs)
    {
        public InspectionMethodInfo ToInspectionMethod()
        {
            return new InspectionMethodInfo(
                Id,
                DisplayName,
                Visibility,
                ReturnType,
                Signature,
                Parameters,
                IsStatic,
                IsAbstract,
                IsVirtual);
        }

        public InspectionMemberSummary ToInspectionMemberSummary()
        {
            return new InspectionMemberSummary(
                Id,
                DisplayName,
                Signature,
                Calls.Select(call => call.ToInspectionSummaryReference()).ToArray(),
                FieldReads.Select(fieldRead => fieldRead.ToInspectionSummaryReference()).ToArray(),
                FieldWrites.Select(fieldWrite => fieldWrite.ToInspectionSummaryReference()).ToArray(),
                TypeRefs.Select(typeRef => typeRef.ToInspectionSummaryReference()).ToArray());
        }
    }

    private sealed class TypeSymbolBuilder
    {
        public TypeSymbolBuilder(
            TypeDefinitionHandle handle,
            string id,
            string displayName,
            string fullName,
            string @namespace,
            string assembly,
            string assemblyPath,
            string source,
            string typeKind,
            string visibility,
            string? baseType,
            IReadOnlyList<string> interfaces,
            string? declaringTypeFullName,
            IReadOnlyList<FieldSymbolBuilder> fields,
            IReadOnlyList<PropertySymbol> properties,
            IReadOnlyList<EventSymbol> events,
            IReadOnlyList<MethodSymbolBuilder> methods)
        {
            Handle = handle;
            Id = id;
            DisplayName = displayName;
            FullName = fullName;
            Namespace = @namespace;
            Assembly = assembly;
            AssemblyPath = assemblyPath;
            Source = source;
            TypeKind = typeKind;
            Visibility = visibility;
            BaseType = baseType;
            Interfaces = interfaces;
            DeclaringTypeFullName = declaringTypeFullName;
            Fields = fields;
            Properties = properties;
            Events = events;
            Methods = methods;
        }

        public TypeDefinitionHandle Handle { get; }
        public string Id { get; }
        public string DisplayName { get; }
        public string FullName { get; }
        public string Namespace { get; }
        public string Assembly { get; }
        public string AssemblyPath { get; }
        public string Source { get; }
        public string TypeKind { get; }
        public string Visibility { get; }
        public string? BaseType { get; }
        public IReadOnlyList<string> Interfaces { get; }
        public string? DeclaringTypeFullName { get; }
        public IReadOnlyList<FieldSymbolBuilder> Fields { get; }
        public IReadOnlyList<PropertySymbol> Properties { get; }
        public IReadOnlyList<EventSymbol> Events { get; }
        public IReadOnlyList<MethodSymbolBuilder> Methods { get; }
        public string? BaseTypeId { get; set; }
        public IReadOnlyList<string> InterfaceIds { get; set; } = [];
        public IReadOnlyList<NestedTypeSymbol> NestedTypes { get; set; } = [];
        public IReadOnlyList<TypeRelationship> Relationships { get; set; } = [];

        public TypeSymbol ToTypeSymbol()
        {
            return new TypeSymbol(
                Id,
                DisplayName,
                FullName,
                Namespace,
                Assembly,
                AssemblyPath,
                Source,
                MetadataTokens.GetToken(Handle),
                TypeKind,
                Visibility,
                BaseType,
                BaseTypeId,
                Interfaces,
                InterfaceIds,
                Fields
                    .Where(field => !field.IsHidden)
                    .Select(field => field.ToFieldSymbol())
                    .ToArray(),
                Properties,
                Events,
                Methods.Select(method => method.ToMethodSymbol()).ToArray(),
                NestedTypes,
                Relationships);
        }
    }

    private sealed record FieldSymbolBuilder(
        FieldDefinitionHandle Handle,
        string Id,
        string Name,
        string FullName,
        string Type,
        string Visibility,
        bool IsStatic,
        bool IsHidden)
    {
        public FieldSymbol ToFieldSymbol()
        {
            return new FieldSymbol(Id, Name, FullName, Type, Visibility, IsStatic);
        }
    }

    private sealed class MethodSymbolBuilder
    {
        public MethodSymbolBuilder(
            MethodDefinitionHandle handle,
            string id,
            string displayName,
            string fullName,
            string @namespace,
            string assembly,
            string assemblyPath,
            string source,
            string signature,
            string lookupSignature,
            string declaringType,
            string visibility,
            IReadOnlyList<InspectionParameterInfo> parameters,
            string returnType,
            bool isStatic,
            bool isAbstract,
            bool isVirtual)
        {
            Handle = handle;
            Id = id;
            DisplayName = displayName;
            FullName = fullName;
            Namespace = @namespace;
            Assembly = assembly;
            AssemblyPath = assemblyPath;
            Source = source;
            Signature = signature;
            LookupSignature = lookupSignature;
            DeclaringType = declaringType;
            Visibility = visibility;
            Parameters = parameters;
            ReturnType = returnType;
            IsStatic = isStatic;
            IsAbstract = isAbstract;
            IsVirtual = isVirtual;
        }

        public MethodDefinitionHandle Handle { get; }
        public string Id { get; }
        public string DisplayName { get; }
        public string FullName { get; }
        public string Namespace { get; }
        public string Assembly { get; }
        public string AssemblyPath { get; }
        public string Source { get; }
        public string Signature { get; }
        public string LookupSignature { get; }
        public string DeclaringType { get; }
        public string Visibility { get; }
        public IReadOnlyList<InspectionParameterInfo> Parameters { get; }
        public string ReturnType { get; }
        public bool IsStatic { get; }
        public bool IsAbstract { get; }
        public bool IsVirtual { get; }
        public string DeclaringTypeId { get; set; } = string.Empty;
        public List<SummaryReference> Calls { get; set; } = [];
        public List<SummaryReference> FieldReads { get; set; } = [];
        public List<SummaryReference> FieldWrites { get; set; } = [];
        public List<SummaryReference> TypeRefs { get; set; } = [];

        public MethodSymbol ToMethodSymbol()
        {
            return new MethodSymbol(
                Id,
                DisplayName,
                FullName,
                Namespace,
                Assembly,
                AssemblyPath,
                Source,
                MetadataTokens.GetToken(Handle),
                Signature,
                LookupSignature,
                DeclaringType,
                DeclaringTypeId,
                Visibility,
                ReturnType,
                Parameters,
                IsStatic,
                IsAbstract,
                IsVirtual,
                Calls,
                FieldReads,
                FieldWrites,
                TypeRefs);
        }
    }

    private sealed record MatchCandidate(
        string Id,
        string Kind,
        string DisplayName,
        string FullName,
        string Namespace,
        string Assembly,
        string AssemblyPath,
        string Source,
        string? Signature,
        IReadOnlyList<string> ExactCandidates,
        IReadOnlyList<string> PrefixCandidates,
        IReadOnlyList<string> SubstringCandidates)
    {
        public static MatchCandidate FromType(TypeSymbol type)
        {
            return new MatchCandidate(
                type.Id,
                "type",
                type.DisplayName,
                type.FullName,
                type.Namespace,
                type.Assembly,
                type.AssemblyPath,
                type.Source,
                null,
                [type.DisplayName, type.FullName],
                [type.DisplayName, type.FullName, type.Namespace],
                [type.DisplayName, type.FullName, type.Namespace]);
        }

        public static MatchCandidate FromMethod(MethodSymbol method)
        {
            return new MatchCandidate(
                method.Id,
                "method",
                method.DisplayName,
                method.FullName,
                method.Namespace,
                method.Assembly,
                method.AssemblyPath,
                method.Source,
                method.Signature,
                [method.DisplayName, method.FullName, method.LookupSignature],
                [method.DisplayName, method.FullName, method.LookupSignature, method.DeclaringType, method.Namespace],
                [method.DisplayName, method.FullName, method.LookupSignature, method.DeclaringType, method.Namespace]);
        }
    }
}
