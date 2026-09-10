using System.Text;

namespace Spirectl.DotnetTools.Inspection;

internal sealed class GodotBinaryResourceCatalog
{
    private const uint FormatVersion = 6;
    private const uint FormatFlagNamedSceneIds = 1;
    private const uint FormatFlagUids = 2;
    private const uint FormatFlagRealTIsDouble = 4;
    private const uint FormatFlagHasScriptClass = 8;
    private const int ReservedFields = 11;

    private const uint VariantNil = 1;
    private const uint VariantBool = 2;
    private const uint VariantInt = 3;
    private const uint VariantFloat = 4;
    private const uint VariantString = 5;
    private const uint VariantVector2 = 10;
    private const uint VariantRect2 = 11;
    private const uint VariantVector3 = 12;
    private const uint VariantPlane = 13;
    private const uint VariantQuaternion = 14;
    private const uint VariantAabb = 15;
    private const uint VariantBasis = 16;
    private const uint VariantTransform3D = 17;
    private const uint VariantTransform2D = 18;
    private const uint VariantColor = 20;
    private const uint VariantNodePath = 22;
    private const uint VariantRid = 23;
    private const uint VariantObject = 24;
    private const uint VariantDictionary = 26;
    private const uint VariantArray = 30;
    private const uint VariantPackedByteArray = 31;
    private const uint VariantPackedInt32Array = 32;
    private const uint VariantPackedFloat32Array = 33;
    private const uint VariantPackedStringArray = 34;
    private const uint VariantPackedVector3Array = 35;
    private const uint VariantPackedColorArray = 36;
    private const uint VariantPackedVector2Array = 37;
    private const uint VariantInt64 = 40;
    private const uint VariantDouble = 41;
    private const uint VariantStringName = 44;
    private const uint VariantVector2I = 45;
    private const uint VariantRect2I = 46;
    private const uint VariantVector3I = 47;
    private const uint VariantPackedInt64Array = 48;
    private const uint VariantPackedFloat64Array = 49;
    private const uint VariantVector4 = 50;
    private const uint VariantVector4I = 51;
    private const uint VariantProjection = 52;
    private const uint VariantPackedVector4Array = 53;

    private const uint ObjectEmpty = 0;
    private const uint ObjectExternalResource = 1;
    private const uint ObjectInternalResource = 2;
    private const uint ObjectExternalResourceIndex = 3;

    private const int NameIndexBits = 18;
    private const int NameMask = (1 << NameIndexBits) - 1;
    private const int TypeInstantiated = 0x7FFFFFFF;
    private const int FlagIdIsPath = 1 << 30;
    private const int FlagInstanceIsPlaceholder = 1 << 30;
    private const int FlagPropNameMask = FlagInstanceIsPlaceholder - 1;

    public ParseResult Parse(string documentPath, byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var reader = new EndianReader(stream);
            var notes = new List<string>();

            var magic = Encoding.ASCII.GetString(reader.ReadBytesExactly(4));
            if (magic == "RSCC")
            {
                return new ParseResult(null, [$"Skipped binary resource '{documentPath}' because compressed Godot binary resources are unsupported."]);
            }

            if (magic != "RSRC")
            {
                return new ParseResult(null, [$"Skipped binary resource '{documentPath}' because it did not start with a Godot binary resource header."]);
            }

            reader.BigEndian = reader.ReadUInt32() != 0;
            _ = reader.ReadUInt32();
            _ = reader.ReadUInt32();
            _ = reader.ReadUInt32();
            var versionFormat = reader.ReadUInt32();
            if (versionFormat > FormatVersion)
            {
                return new ParseResult(null, [$"Skipped binary resource '{documentPath}' because resource format version {versionFormat} is unsupported."]);
            }

            _ = ReadUnicodeString(reader);
            _ = reader.ReadUInt64();
            var flags = reader.ReadUInt32();
            var usingNamedSceneIds = (flags & FormatFlagNamedSceneIds) != 0;
            var usingUids = (flags & FormatFlagUids) != 0;
            var realIsDouble = (flags & FormatFlagRealTIsDouble) != 0;
            var uid = usingUids ? reader.ReadUInt64() : 0UL;
            if (!usingUids)
            {
                _ = reader.ReadUInt64();
            }

            if ((flags & FormatFlagHasScriptClass) != 0)
            {
                _ = ReadUnicodeString(reader);
            }

            for (var i = 0; i < ReservedFields; i++)
            {
                _ = reader.ReadUInt32();
            }

            var strings = new List<string>();
            var stringTableSize = reader.ReadUInt32();
            for (var i = 0; i < stringTableSize; i++)
            {
                strings.Add(ReadUnicodeString(reader));
            }

            var externalResources = new List<ExternalResource>();
            var externalResourceCount = reader.ReadUInt32();
            for (var i = 0; i < externalResourceCount; i++)
            {
                var type = ReadUnicodeString(reader);
                var path = NormalizeReferencedPath(documentPath, ReadUnicodeString(reader));
                if (usingUids)
                {
                    _ = reader.ReadUInt64();
                }

                externalResources.Add(new ExternalResource(type, path));
            }

            var internalResources = new List<InternalResource>();
            var internalResourceCount = reader.ReadUInt32();
            for (var i = 0; i < internalResourceCount; i++)
            {
                internalResources.Add(new InternalResource(ReadUnicodeString(reader), reader.ReadUInt64()));
            }

            var parsedResources = new List<ParsedResourceRecord>(internalResources.Count);
            var internalResourcesByPath = new Dictionary<string, ParsedResourceRecord>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < internalResources.Count; i++)
            {
                var isMain = i == internalResources.Count - 1;
                var resolvedPath = ResolveInternalResourcePath(documentPath, internalResources[i].Path, isMain);
                reader.Position = internalResources[i].Offset;

                var resourceType = ReadUnicodeString(reader);
                var propertyCount = reader.ReadInt32();
                var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var propertyIndex = 0; propertyIndex < propertyCount; propertyIndex++)
                {
                    var propertyName = ReadStringTableString(reader, strings);
                    var propertyValue = ReadVariant(
                        reader,
                        strings,
                        externalResources,
                        internalResources,
                        documentPath,
                        realIsDouble,
                        versionFormat,
                        usingNamedSceneIds);
                    properties[propertyName] = propertyValue;
                }

                var record = new ParsedResourceRecord(
                    resolvedPath,
                    Path.GetFileNameWithoutExtension(resolvedPath),
                    resourceType,
                    properties,
                    isMain);
                parsedResources.Add(record);
                if (!isMain)
                {
                    internalResourcesByPath[resolvedPath] = record;
                }
            }

            foreach (var resource in parsedResources)
            {
                foreach (var property in resource.Properties.Values)
                {
                    EnrichInternalReferences(property, internalResourcesByPath);
                }
            }

            var mainResource = parsedResources.LastOrDefault(resource => resource.IsMain);
            if (mainResource is null)
            {
                return new ParseResult(null, [$"Skipped binary resource '{documentPath}' because it did not contain a main resource record."]);
            }

            if (string.Equals(mainResource.Type, "PackedScene", StringComparison.OrdinalIgnoreCase))
            {
                if (!mainResource.Properties.TryGetValue("_bundled", out var bundledValue)
                    || bundledValue is not Dictionary<string, object?> bundled)
                {
                    return new ParseResult(null, [$"Skipped binary scene '{documentPath}' because its PackedScene _bundled metadata was unavailable."]);
                }

                var scene = BuildSceneDocument(documentPath, uid, bundled, internalResourcesByPath, notes);
                return new ParseResult(scene, notes);
            }

            var resourceDocument = BuildResourceDocument(documentPath, uid, mainResource.Type, mainResource.Properties);
            return new ParseResult(resourceDocument, notes);
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or ArgumentOutOfRangeException)
        {
            return new ParseResult(null, [$"Skipped binary resource '{documentPath}' because it was malformed or incomplete."]);
        }
    }

    private static ParsedSceneDocument BuildSceneDocument(
        string documentPath,
        ulong uid,
        Dictionary<string, object?> bundled,
        IReadOnlyDictionary<string, ParsedResourceRecord> internalResourcesByPath,
        List<string> notes)
    {
        if (!TryGetStringList(bundled, "names", out var names)
            || !TryGetObjectList(bundled, "variants", out var variants)
            || !TryGetIntList(bundled, "nodes", out var serializedNodes)
            || !TryGetInt(bundled, "node_count", out var nodeCount))
        {
            throw new ToolCommandException(3, "not_found", $"Scene '{documentPath}' did not contain supported PackedScene metadata.");
        }

        var serializedNodePaths = TryGetStringList(bundled, "node_paths", out var bundledNodePaths)
            ? bundledNodePaths
            : [];
        var nodes = new List<ParsedSceneNode>(nodeCount);
        var builtNodeMap = new Dictionary<int, ParsedSceneNode>();
        var nodeIds = TryGetIntList(bundled, "node_ids", out var explicitNodeIds) ? explicitNodeIds : null;
        var cursor = 0;
        string? rootNodePath = null;

        for (var nodeIndex = 0; nodeIndex < nodeCount; nodeIndex++)
        {
            var parent = serializedNodes[cursor++];
            _ = serializedNodes[cursor++];
            var nodeTypeIndex = serializedNodes[cursor++];
            var nameAndIndex = serializedNodes[cursor++];
            var instanceIndex = serializedNodes[cursor++];
            var propertyCount = serializedNodes[cursor++];

            var propertyNamesAndValues = new List<(string Name, object? Value)>(propertyCount);
            for (var propertyIndex = 0; propertyIndex < propertyCount; propertyIndex++)
            {
                var propertyNameIndex = serializedNodes[cursor++] & FlagPropNameMask;
                var propertyValueIndex = serializedNodes[cursor++];
                propertyNamesAndValues.Add((names[propertyNameIndex], variants[propertyValueIndex]));
            }

            var groupCount = serializedNodes[cursor++];
            cursor += groupCount;

            var nodeName = names[nameAndIndex & NameMask];
            if (nodeIndex == 0)
            {
                rootNodePath = $"/{nodeName}";
            }

            var parentNodePath = ResolveParentNodePath(parent, builtNodeMap, serializedNodePaths, rootNodePath);
            var nodePath = parentNodePath is null ? $"/{nodeName}" : $"{parentNodePath}/{nodeName}";
            var nodeType = nodeTypeIndex == TypeInstantiated ? null : names[nodeTypeIndex];
            var scriptPath = ResolveAttachedScriptPath(propertyNamesAndValues);
            var resourceRefs = propertyNamesAndValues
                .Select(property => BuildResourceReference(property.Name, property.Value))
                .Where(reference => reference is not null)
                .Cast<ParsedResourceRef>()
                .ToArray();
            var nodeRefs = propertyNamesAndValues
                .Select(property => BuildNodeReference(nodePath, property.Name, property.Value))
                .Where(reference => reference is not null)
                .Cast<ParsedNodeRef>()
                .ToArray();

            var instanceScenePath = ResolveInstanceScenePath(instanceIndex, variants);
            var idValue = nodeIds is not null && nodeIndex < nodeIds.Count && nodeIds[nodeIndex] > 0
                ? nodeIds[nodeIndex].ToString()
                : nodePath;
            var node = new ParsedSceneNode(
                Id: $"node:binary:{documentPath}:{idValue}",
                Name: nodeName,
                NodeType: nodeType,
                NodePath: nodePath,
                ParentNodePath: parentNodePath,
                Depth: Math.Max(0, nodePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Length - 1),
                InstanceScenePath: instanceScenePath,
                AttachedScriptPath: scriptPath,
                ResourceRefs: resourceRefs,
                NodeRefs: nodeRefs);

            nodes.Add(node);
            builtNodeMap[nodeIndex] = node;
        }

        var childCounts = nodes
            .Where(node => node.ParentNodePath is not null)
            .GroupBy(node => node.ParentNodePath!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        var finalizedNodes = nodes
            .Select(node => node with
            {
                ChildCount = childCounts.GetValueOrDefault(node.NodePath, 0),
                NodeRefs = node.NodeRefs.Select(reference =>
                {
                    var targetNode = nodes.FirstOrDefault(nodeCandidate =>
                        string.Equals(nodeCandidate.NodePath, reference.TargetNodePath, StringComparison.OrdinalIgnoreCase));
                    return reference with
                    {
                        TargetNodeId = targetNode?.Id,
                        Resolved = targetNode is not null,
                    };
                }).ToArray(),
            })
            .ToArray();

        return new ParsedSceneDocument(
            documentPath,
            uid is 0 or ulong.MaxValue ? null : uid.ToString(),
            finalizedNodes,
            internalResourcesByPath);
    }

    private static ParsedResourceDocument BuildResourceDocument(
        string documentPath,
        ulong uid,
        string resourceType,
        IReadOnlyDictionary<string, object?> properties)
    {
        return new ParsedResourceDocument(
            documentPath,
            uid is 0 or ulong.MaxValue ? null : uid.ToString(),
            resourceType,
            ResolveAttachedScriptPath(properties.Select(property => (property.Key, property.Value))));
    }

    private static object? ReadVariant(
        EndianReader reader,
        IReadOnlyList<string> strings,
        IReadOnlyList<ExternalResource> externalResources,
        IReadOnlyList<InternalResource> internalResources,
        string documentPath,
        bool realIsDouble,
        uint versionFormat,
        bool usingNamedSceneIds)
    {
        var variantType = reader.ReadUInt32();
        return variantType switch
        {
            VariantNil => null,
            VariantBool => reader.ReadUInt32() != 0,
            VariantInt => reader.ReadInt32(),
            VariantInt64 => reader.ReadInt64(),
            VariantFloat => realIsDouble ? reader.ReadDouble() : reader.ReadSingle(),
            VariantDouble => reader.ReadDouble(),
            VariantString => ReadUnicodeString(reader),
            VariantStringName => ReadUnicodeString(reader),
            VariantVector2 => ReadFixedScalars(reader, realIsDouble, 2),
            VariantVector2I => ReadFixedIntegers(reader, 2),
            VariantRect2 => ReadFixedScalars(reader, realIsDouble, 4),
            VariantRect2I => ReadFixedIntegers(reader, 4),
            VariantVector3 => ReadFixedScalars(reader, realIsDouble, 3),
            VariantVector3I => ReadFixedIntegers(reader, 3),
            VariantVector4 => ReadFixedScalars(reader, realIsDouble, 4),
            VariantVector4I => ReadFixedIntegers(reader, 4),
            VariantPlane => ReadFixedScalars(reader, realIsDouble, 4),
            VariantQuaternion => ReadFixedScalars(reader, realIsDouble, 4),
            VariantAabb => ReadFixedScalars(reader, realIsDouble, 6),
            VariantBasis => ReadFixedScalars(reader, realIsDouble, 9),
            VariantTransform2D => ReadFixedScalars(reader, realIsDouble, 6),
            VariantTransform3D => ReadFixedScalars(reader, realIsDouble, 12),
            VariantProjection => ReadFixedScalars(reader, realIsDouble, 16),
            VariantColor => new[] { reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle() },
            VariantNodePath => ReadNodePath(reader, strings, versionFormat),
            VariantRid => reader.ReadUInt32(),
            VariantObject => ReadObjectReference(reader, externalResources, internalResources, documentPath, usingNamedSceneIds),
            VariantDictionary => ReadDictionary(reader, strings, externalResources, internalResources, documentPath, realIsDouble, versionFormat, usingNamedSceneIds),
            VariantArray => ReadArray(reader, strings, externalResources, internalResources, documentPath, realIsDouble, versionFormat, usingNamedSceneIds),
            VariantPackedByteArray => ReadPackedByteArray(reader),
            VariantPackedInt32Array => ReadPackedInt32Array(reader),
            VariantPackedInt64Array => ReadPackedInt64Array(reader),
            VariantPackedFloat32Array => ReadPackedFloatArray(reader),
            VariantPackedFloat64Array => ReadPackedDoubleArray(reader),
            VariantPackedStringArray => ReadPackedStringArray(reader),
            VariantPackedVector2Array => ReadPackedFixedScalarArray(reader, realIsDouble, 2),
            VariantPackedVector3Array => ReadPackedFixedScalarArray(reader, realIsDouble, 3),
            VariantPackedVector4Array => ReadPackedFixedScalarArray(reader, realIsDouble, 4),
            VariantPackedColorArray => ReadPackedColorArray(reader),
            _ => throw new ToolCommandException(3, "not_found", $"Encountered unsupported Godot binary variant type {variantType}."),
        };
    }

    private static Dictionary<string, object?> ReadDictionary(
        EndianReader reader,
        IReadOnlyList<string> strings,
        IReadOnlyList<ExternalResource> externalResources,
        IReadOnlyList<InternalResource> internalResources,
        string documentPath,
        bool realIsDouble,
        uint versionFormat,
        bool usingNamedSceneIds)
    {
        var length = reader.ReadUInt32() & 0x7FFFFFFF;
        var dictionary = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < length; i++)
        {
            var key = ReadVariant(reader, strings, externalResources, internalResources, documentPath, realIsDouble, versionFormat, usingNamedSceneIds);
            var value = ReadVariant(reader, strings, externalResources, internalResources, documentPath, realIsDouble, versionFormat, usingNamedSceneIds);
            dictionary[key?.ToString() ?? string.Empty] = value;
        }

        return dictionary;
    }

    private static List<object?> ReadArray(
        EndianReader reader,
        IReadOnlyList<string> strings,
        IReadOnlyList<ExternalResource> externalResources,
        IReadOnlyList<InternalResource> internalResources,
        string documentPath,
        bool realIsDouble,
        uint versionFormat,
        bool usingNamedSceneIds)
    {
        var length = reader.ReadUInt32() & 0x7FFFFFFF;
        var values = new List<object?>((int)length);
        for (var i = 0; i < length; i++)
        {
            values.Add(ReadVariant(reader, strings, externalResources, internalResources, documentPath, realIsDouble, versionFormat, usingNamedSceneIds));
        }

        return values;
    }

    private static object? ReadObjectReference(
        EndianReader reader,
        IReadOnlyList<ExternalResource> externalResources,
        IReadOnlyList<InternalResource> internalResources,
        string documentPath,
        bool usingNamedSceneIds)
    {
        var objectType = reader.ReadUInt32();
        return objectType switch
        {
            ObjectEmpty => null,
            ObjectExternalResource => BuildLegacyExternalReference(reader, documentPath),
            ObjectExternalResourceIndex => BuildExternalReference(reader.ReadInt32(), externalResources),
            ObjectInternalResource => BuildInternalReference(reader.ReadUInt32(), internalResources, documentPath, usingNamedSceneIds),
            _ => null,
        };
    }

    private static BinaryResourceReference BuildLegacyExternalReference(EndianReader reader, string documentPath)
    {
        var resourceType = ReadUnicodeString(reader);
        var resourcePath = NormalizeReferencedPath(documentPath, ReadUnicodeString(reader));
        return new BinaryResourceReference(resourcePath, resourceType, "external", null);
    }

    private static BinaryResourceReference? BuildExternalReference(int index, IReadOnlyList<ExternalResource> externalResources)
    {
        if (index < 0 || index >= externalResources.Count)
        {
            return null;
        }

        var resource = externalResources[index];
        return new BinaryResourceReference(resource.Path, resource.Type, "external", null);
    }

    private static BinaryResourceReference BuildInternalReference(
        uint index,
        IReadOnlyList<InternalResource> internalResources,
        string documentPath,
        bool usingNamedSceneIds)
    {
        var path = usingNamedSceneIds && index < internalResources.Count
            ? ResolveInternalResourcePath(documentPath, internalResources[(int)index].Path, isMain: false)
            : $"{documentPath}::{index}";
        var localId = internalResources.Count > index && internalResources[(int)index].Path.StartsWith("local://", StringComparison.OrdinalIgnoreCase)
            ? internalResources[(int)index].Path["local://".Length..]
            : null;
        return new BinaryResourceReference(path, null, "internal", localId);
    }

    private static string ReadNodePath(EndianReader reader, IReadOnlyList<string> strings, uint versionFormat)
    {
        var nameCount = reader.ReadUInt16();
        var subnameCountRaw = reader.ReadUInt16();
        var absolute = (subnameCountRaw & 0x8000) != 0;
        var subnameCount = subnameCountRaw & 0x7FFF;
        if (versionFormat < 3)
        {
            subnameCount += 1;
        }

        var names = new List<string>(nameCount);
        for (var i = 0; i < nameCount; i++)
        {
            names.Add(ReadStringTableString(reader, strings));
        }

        var subnames = new List<string>((int)subnameCount);
        for (var i = 0; i < subnameCount; i++)
        {
            subnames.Add(ReadStringTableString(reader, strings));
        }

        var basePath = string.Join('/', names);
        if (absolute)
        {
            basePath = $"/{basePath}";
        }

        return subnames.Count == 0 ? basePath : $"{basePath}:{string.Join(':', subnames)}";
    }

    private static List<byte> ReadPackedByteArray(EndianReader reader)
    {
        var length = (int)reader.ReadUInt32();
        var bytes = reader.ReadBytesExactly(length).ToList();
        AdvancePadding(reader, (uint)length);
        return bytes;
    }

    private static List<int> ReadPackedInt32Array(EndianReader reader)
    {
        var length = (int)reader.ReadUInt32();
        var values = new List<int>(length);
        for (var i = 0; i < length; i++)
        {
            values.Add(reader.ReadInt32());
        }

        return values;
    }

    private static List<long> ReadPackedInt64Array(EndianReader reader)
    {
        var length = (int)reader.ReadUInt32();
        var values = new List<long>(length);
        for (var i = 0; i < length; i++)
        {
            values.Add(reader.ReadInt64());
        }

        return values;
    }

    private static List<float> ReadPackedFloatArray(EndianReader reader)
    {
        var length = (int)reader.ReadUInt32();
        var values = new List<float>(length);
        for (var i = 0; i < length; i++)
        {
            values.Add(reader.ReadSingle());
        }

        return values;
    }

    private static List<double> ReadPackedDoubleArray(EndianReader reader)
    {
        var length = (int)reader.ReadUInt32();
        var values = new List<double>(length);
        for (var i = 0; i < length; i++)
        {
            values.Add(reader.ReadDouble());
        }

        return values;
    }

    private static List<string> ReadPackedStringArray(EndianReader reader)
    {
        var length = (int)reader.ReadUInt32();
        var values = new List<string>(length);
        for (var i = 0; i < length; i++)
        {
            values.Add(ReadUnicodeString(reader));
        }

        return values;
    }

    private static List<object?> ReadPackedFixedScalarArray(EndianReader reader, bool realIsDouble, int tupleLength)
    {
        var length = (int)reader.ReadUInt32();
        var values = new List<object?>(length);
        for (var i = 0; i < length; i++)
        {
            values.Add(ReadFixedScalars(reader, realIsDouble, tupleLength));
        }

        return values;
    }

    private static List<object?> ReadPackedColorArray(EndianReader reader)
    {
        var length = (int)reader.ReadUInt32();
        var values = new List<object?>(length);
        for (var i = 0; i < length; i++)
        {
            values.Add(new[] { reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle() });
        }

        return values;
    }

    private static double[] ReadFixedScalars(EndianReader reader, bool realIsDouble, int count)
    {
        var values = new double[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = realIsDouble ? reader.ReadDouble() : reader.ReadSingle();
        }

        return values;
    }

    private static int[] ReadFixedIntegers(EndianReader reader, int count)
    {
        var values = new int[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = reader.ReadInt32();
        }

        return values;
    }

    private static string ReadStringTableString(EndianReader reader, IReadOnlyList<string> strings)
    {
        var id = reader.ReadUInt32();
        if ((id & 0x80000000) != 0)
        {
            var length = (int)(id & 0x7FFFFFFF);
            return DecodeUtf8(reader.ReadBytesExactly(length));
        }

        return strings[(int)id];
    }

    private static string ReadUnicodeString(EndianReader reader)
    {
        var length = (int)reader.ReadUInt32();
        return DecodeUtf8(reader.ReadBytesExactly(length));
    }

    private static string DecodeUtf8(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        if (bytes[^1] == 0)
        {
            return Encoding.UTF8.GetString(bytes, 0, bytes.Length - 1);
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static void AdvancePadding(EndianReader reader, uint length)
    {
        var extra = 4 - (length % 4);
        if (extra < 4)
        {
            reader.Position += extra;
        }
    }

    private static string ResolveInternalResourcePath(string documentPath, string rawPath, bool isMain)
    {
        if (isMain)
        {
            return documentPath;
        }

        if (rawPath.StartsWith("local://", StringComparison.OrdinalIgnoreCase))
        {
            return $"{documentPath}::{rawPath["local://".Length..]}";
        }

        return NormalizeReferencedPath(documentPath, rawPath);
    }

    private static void EnrichInternalReferences(object? value, IReadOnlyDictionary<string, ParsedResourceRecord> internalResourcesByPath)
    {
        switch (value)
        {
            case BinaryResourceReference reference when string.Equals(reference.ReferenceKind, "internal", StringComparison.OrdinalIgnoreCase)
                && reference.ResourceType is null
                && internalResourcesByPath.TryGetValue(reference.Path, out var resource):
                reference.ResourceType = resource.Type;
                break;
            case Dictionary<string, object?> dictionary:
                foreach (var nestedValue in dictionary.Values)
                {
                    EnrichInternalReferences(nestedValue, internalResourcesByPath);
                }
                break;
            case IEnumerable<object?> values:
                foreach (var nestedValue in values)
                {
                    EnrichInternalReferences(nestedValue, internalResourcesByPath);
                }
                break;
        }
    }

    private static string? ResolveAttachedScriptPath(IEnumerable<(string Name, object? Value)> properties)
    {
        var scriptReference = properties
            .FirstOrDefault(property => string.Equals(property.Name, "script", StringComparison.OrdinalIgnoreCase))
            .Value as BinaryResourceReference;
        return scriptReference?.Path;
    }

    private static ParsedResourceRef? BuildResourceReference(string propertyName, object? value)
    {
        if (value is not BinaryResourceReference reference)
        {
            return null;
        }

        var targetKind = string.Equals(reference.ResourceType, "Script", StringComparison.OrdinalIgnoreCase)
            ? "script"
            : string.Equals(reference.ReferenceKind, "internal", StringComparison.OrdinalIgnoreCase)
                ? "subresource"
                : "external";

        return new ParsedResourceRef(
            propertyName,
            targetKind,
            reference.LocalId ?? reference.Path,
            reference.ResourceType,
            reference.Path.StartsWith("res://", StringComparison.OrdinalIgnoreCase) ? reference.Path : null);
    }

    private static ParsedNodeRef? BuildNodeReference(string currentNodePath, string propertyName, object? value)
    {
        if (value is not string rawPath)
        {
            return null;
        }

        if (!rawPath.StartsWith("/") && !rawPath.StartsWith("."))
        {
            return null;
        }

        var targetNodePath = ResolveNodeTargetPath(currentNodePath, rawPath);
        return new ParsedNodeRef(propertyName, rawPath, null, targetNodePath, targetNodePath is not null);
    }

    private static string? ResolveInstanceScenePath(int instanceIndex, IReadOnlyList<object?> variants)
    {
        if (instanceIndex < 0)
        {
            return null;
        }

        var variantIndex = instanceIndex & ~FlagInstanceIsPlaceholder;
        if (variantIndex < 0 || variantIndex >= variants.Count)
        {
            return null;
        }

        return variants[variantIndex] is BinaryResourceReference reference
            && string.Equals(reference.ResourceType, "PackedScene", StringComparison.OrdinalIgnoreCase)
            ? reference.Path
            : variants[variantIndex] as string;
    }

    private static string? ResolveParentNodePath(
        int parent,
        IReadOnlyDictionary<int, ParsedSceneNode> builtNodes,
        IReadOnlyList<string> serializedNodePaths,
        string? rootNodePath)
    {
        if (parent < 0)
        {
            return null;
        }

        if ((parent & FlagIdIsPath) != 0)
        {
            var pathIndex = parent & ~FlagIdIsPath;
            if (pathIndex < 0 || pathIndex >= serializedNodePaths.Count)
            {
                return null;
            }

            return NormalizeSerializedNodePath(rootNodePath, serializedNodePaths[pathIndex]);
        }

        return builtNodes.TryGetValue(parent, out var parentNode) ? parentNode.NodePath : null;
    }

    private static string? NormalizeSerializedNodePath(string? rootNodePath, string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return null;
        }

        if (rawPath == ".")
        {
            return rootNodePath;
        }

        if (rawPath.StartsWith("/", StringComparison.Ordinal))
        {
            return rawPath;
        }

        if (rootNodePath is null)
        {
            return null;
        }

        return $"{rootNodePath}/{rawPath.TrimStart('.')}".Replace("//", "/");
    }

    private static bool TryGetStringList(IReadOnlyDictionary<string, object?> dictionary, string key, out List<string> values)
    {
        values = [];
        if (!dictionary.TryGetValue(key, out var value))
        {
            return false;
        }

        if (value is List<string> strings)
        {
            values = strings;
            return true;
        }

        if (value is List<object?> objects)
        {
            values = objects.Select(item => item?.ToString() ?? string.Empty).ToList();
            return true;
        }

        return false;
    }

    private static bool TryGetObjectList(IReadOnlyDictionary<string, object?> dictionary, string key, out List<object?> values)
    {
        values = [];
        if (!dictionary.TryGetValue(key, out var value))
        {
            return false;
        }

        if (value is List<object?> objects)
        {
            values = objects;
            return true;
        }

        return false;
    }

    private static bool TryGetIntList(IReadOnlyDictionary<string, object?> dictionary, string key, out List<int> values)
    {
        values = [];
        if (!dictionary.TryGetValue(key, out var value))
        {
            return false;
        }

        switch (value)
        {
            case List<int> ints:
                values = ints;
                return true;
            case IEnumerable<long> longs:
                values = longs.Select(value => (int)value).ToList();
                return true;
            case IEnumerable<object?> objects:
                values = objects.Select(item => Convert.ToInt32(item)).ToList();
                return true;
            default:
                return false;
        }
    }

    private static bool TryGetInt(IReadOnlyDictionary<string, object?> dictionary, string key, out int value)
    {
        value = 0;
        if (!dictionary.TryGetValue(key, out var rawValue) || rawValue is null)
        {
            return false;
        }

        value = Convert.ToInt32(rawValue);
        return true;
    }

    private static string NormalizeReferencedPath(string documentPath, string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return string.Empty;
        }

        if (rawPath.Contains("://", StringComparison.Ordinal))
        {
            return rawPath.Replace('\\', '/');
        }

        var currentPath = documentPath["res://".Length..];
        var baseDirectory = Path.GetDirectoryName(currentPath)?.Replace('\\', '/') ?? string.Empty;
        var combined = Path.GetFullPath(Path.Combine("/", baseDirectory, rawPath))
            .Replace('\\', '/')
            .TrimStart('/');
        return $"res://{combined}";
    }

    private static string? ResolveNodeTargetPath(string currentNodePath, string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return null;
        }

        if (rawPath.StartsWith("/", StringComparison.Ordinal))
        {
            return rawPath;
        }

        var currentSegments = currentNodePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        foreach (var segment in rawPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (currentSegments.Count > 1)
                {
                    currentSegments.RemoveAt(currentSegments.Count - 1);
                }

                continue;
            }

            currentSegments.Add(segment);
        }

        return currentSegments.Count == 0
            ? null
            : $"/{string.Join('/', currentSegments)}";
    }

    internal sealed record ParseResult(
        ParsedDocument? Document,
        IReadOnlyList<string> Notes);

    internal abstract record ParsedDocument(string Path, string? Uid);

    internal sealed record ParsedSceneDocument(
        string Path,
        string? Uid,
        IReadOnlyList<ParsedSceneNode> Nodes,
        IReadOnlyDictionary<string, ParsedResourceRecord> InternalResourcesByPath)
        : ParsedDocument(Path, Uid);

    internal sealed record ParsedResourceDocument(
        string Path,
        string? Uid,
        string ResourceType,
        string? AttachedScriptPath)
        : ParsedDocument(Path, Uid);

    internal sealed record ParsedSceneNode(
        string Id,
        string Name,
        string? NodeType,
        string NodePath,
        string? ParentNodePath,
        int Depth,
        string? InstanceScenePath,
        string? AttachedScriptPath,
        IReadOnlyList<ParsedResourceRef> ResourceRefs,
        IReadOnlyList<ParsedNodeRef> NodeRefs)
    {
        public int ChildCount { get; init; }
    }

    internal sealed record ParsedResourceRef(
        string Property,
        string TargetKind,
        string ResourceId,
        string? ResourceType,
        string? ResourcePath);

    internal sealed record ParsedNodeRef(
        string Property,
        string RawPath,
        string? TargetNodeId,
        string? TargetNodePath,
        bool Resolved);

    internal sealed record ParsedResourceRecord(
        string Path,
        string DisplayName,
        string Type,
        IReadOnlyDictionary<string, object?> Properties,
        bool IsMain);

    private sealed record ExternalResource(string Type, string Path);

    private sealed record InternalResource(string Path, ulong Offset);

    private sealed class BinaryResourceReference(string path, string? resourceType, string referenceKind, string? localId)
    {
        public string Path { get; } = path;

        public string? ResourceType { get; set; } = resourceType;

        public string ReferenceKind { get; } = referenceKind;

        public string? LocalId { get; } = localId;
    }

    private sealed class EndianReader(Stream stream)
    {
        public bool BigEndian { get; set; }

        public ulong Position
        {
            get => (ulong)stream.Position;
            set => stream.Position = checked((long)value);
        }

        public byte[] ReadBytesExactly(int count)
        {
            var buffer = new byte[count];
            var totalRead = 0;
            while (totalRead < count)
            {
                var bytesRead = stream.Read(buffer, totalRead, count - totalRead);
                if (bytesRead == 0)
                {
                    throw new EndOfStreamException($"Expected {count} bytes but only read {totalRead}.");
                }

                totalRead += bytesRead;
            }

            return buffer;
        }

        public ushort ReadUInt16() => BitConverter.ToUInt16(ReadForEndian(sizeof(ushort)));

        public uint ReadUInt32() => BitConverter.ToUInt32(ReadForEndian(sizeof(uint)));

        public ulong ReadUInt64() => BitConverter.ToUInt64(ReadForEndian(sizeof(ulong)));

        public int ReadInt32() => BitConverter.ToInt32(ReadForEndian(sizeof(int)));

        public long ReadInt64() => BitConverter.ToInt64(ReadForEndian(sizeof(long)));

        public float ReadSingle() => BitConverter.ToSingle(ReadForEndian(sizeof(float)));

        public double ReadDouble() => BitConverter.ToDouble(ReadForEndian(sizeof(double)));

        private byte[] ReadForEndian(int size)
        {
            var bytes = ReadBytesExactly(size);
            if (BigEndian)
            {
                Array.Reverse(bytes);
            }

            return bytes;
        }
    }
}
