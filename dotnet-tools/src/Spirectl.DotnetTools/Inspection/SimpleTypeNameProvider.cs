using System.Collections.Immutable;
using System.Reflection.Metadata;

namespace Spirectl.DotnetTools.Inspection;

/// <summary>
/// Renders signature types the way <see cref="System.Type.Name"/> does: the bare type name, with
/// no namespace and no generic arguments.
/// </summary>
/// <remarks>
/// Reference verification compares two renderings of the same member across two game builds, so
/// what matters is that a shape change is visible and a namespace is not. Dropping namespaces
/// keeps the reported diff to the part a reader can act on, and dropping generic arguments matches
/// what reflection reports for a constructed generic (<c>List`1</c>). The sibling
/// <see cref="SignatureTypeNameProvider"/> stays fully qualified for symbol lookup, where an exact
/// name is the whole point.
/// </remarks>
internal readonly struct SimpleTypeNameProvider : ISignatureTypeProvider<string, object?>
{
    public string GetArrayType(string elementType, ArrayShape shape)
    {
        return $"{elementType}[{new string(',', Math.Max(0, shape.Rank - 1))}]";
    }

    public string GetByReferenceType(string elementType)
    {
        return $"{elementType}&";
    }

    public string GetFunctionPointerType(MethodSignature<string> signature)
    {
        return "IntPtr";
    }

    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
    {
        return genericType;
    }

    public string GetGenericMethodParameter(object? genericContext, int index)
    {
        return $"!!{index}";
    }

    public string GetGenericTypeParameter(object? genericContext, int index)
    {
        return $"!{index}";
    }

    public string GetModifiedType(string modifierType, string unmodifiedType, bool isRequired)
    {
        return unmodifiedType;
    }

    public string GetPinnedType(string elementType)
    {
        return elementType;
    }

    public string GetPointerType(string elementType)
    {
        return $"{elementType}*";
    }

    public string GetPrimitiveType(PrimitiveTypeCode typeCode)
    {
        return typeCode switch
        {
            PrimitiveTypeCode.Boolean => "Boolean",
            PrimitiveTypeCode.Byte => "Byte",
            PrimitiveTypeCode.Char => "Char",
            PrimitiveTypeCode.Double => "Double",
            PrimitiveTypeCode.Int16 => "Int16",
            PrimitiveTypeCode.Int32 => "Int32",
            PrimitiveTypeCode.Int64 => "Int64",
            PrimitiveTypeCode.IntPtr => "IntPtr",
            PrimitiveTypeCode.Object => "Object",
            PrimitiveTypeCode.SByte => "SByte",
            PrimitiveTypeCode.Single => "Single",
            PrimitiveTypeCode.String => "String",
            PrimitiveTypeCode.TypedReference => "TypedReference",
            PrimitiveTypeCode.UInt16 => "UInt16",
            PrimitiveTypeCode.UInt32 => "UInt32",
            PrimitiveTypeCode.UInt64 => "UInt64",
            PrimitiveTypeCode.UIntPtr => "UIntPtr",
            PrimitiveTypeCode.Void => "Void",
            _ => typeCode.ToString(),
        };
    }

    public string GetSZArrayType(string elementType)
    {
        return $"{elementType}[]";
    }

    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        return reader.GetString(reader.GetTypeDefinition(handle).Name);
    }

    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        return reader.GetString(reader.GetTypeReference(handle).Name);
    }

    public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
    {
        return reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
    }
}
