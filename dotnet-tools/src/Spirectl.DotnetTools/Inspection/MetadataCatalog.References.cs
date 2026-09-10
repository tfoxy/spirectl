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
    private static void AddDeclarationReferences(
        AssemblyIndex assembly,
        SymbolLookup lookup,
        List<ReferenceEdge> references)
    {
        foreach (var type in assembly.Types.Values)
        {
            var typeContainer = ContainerDescriptor.ForType(type);
            AddTypeReference(references, typeContainer, ResolveTypeTarget(type.BaseType, lookup), "type-use", "base-type");

            foreach (var interfaceName in type.Interfaces)
            {
                AddTypeReference(references, typeContainer, ResolveTypeTarget(interfaceName, lookup), "type-use", "interface");
            }

            foreach (var field in type.Fields.Where(field => !field.IsHidden))
            {
                AddTypeReference(references, typeContainer, ResolveTypeTarget(field.Type, lookup), "type-use", "field-type");
            }

            foreach (var property in type.Properties)
            {
                AddTypeReference(references, typeContainer, ResolveTypeTarget(property.Type, lookup), "type-use", "property-type");
            }

            foreach (var eventSymbol in type.Events)
            {
                AddTypeReference(references, typeContainer, ResolveTypeTarget(eventSymbol.Type, lookup), "type-use", "event-type");
            }

            foreach (var method in type.Methods)
            {
                var methodContainer = ContainerDescriptor.ForMethod(method);
                AddTypeReference(references, methodContainer, ResolveTypeTarget(method.ReturnType, lookup), "type-use", "return-type");
                AddSummaryReference(method.TypeRefs, ResolveTypeTarget(method.ReturnType, lookup), "return-type");

                foreach (var parameter in method.Parameters)
                {
                    var target = ResolveTypeTarget(parameter.Type, lookup);
                    AddTypeReference(references, methodContainer, target, "type-use", "parameter-type");
                    AddSummaryReference(method.TypeRefs, target, "parameter-type");
                }
            }
        }
    }

    private static void ScanMethodBodies(AssemblyIndex assembly, SymbolLookup lookup, List<ReferenceEdge> references)
    {
        using var stream = File.OpenRead(assembly.AssemblyPath);
        using var peReader = new PEReader(stream);
        if (!peReader.HasMetadata)
        {
            return;
        }

        var reader = peReader.GetMetadataReader();

        foreach (var method in assembly.Methods.Values)
        {
            var methodDefinition = reader.GetMethodDefinition(method.Handle);
            if (methodDefinition.RelativeVirtualAddress == 0)
            {
                continue;
            }

            var body = peReader.GetMethodBody(methodDefinition.RelativeVirtualAddress);
            var ilReader = body.GetILReader();
            var container = ContainerDescriptor.ForMethod(method);

            while (ilReader.RemainingBytes > 0)
            {
                var opcode = ReadOpCode(ref ilReader);
                var operandType = OperandTypes.ByCode.TryGetValue((ushort)opcode, out var value)
                    ? value
                    : OperandType.InlineNone;
                var opcodeName = OperandTypes.Names.TryGetValue((ushort)opcode, out var name)
                    ? name
                    : opcode.ToString().ToLowerInvariant();

                switch (operandType)
                {
                    case OperandType.InlineMethod:
                        {
                            var target = ResolveMethodTokenTarget(reader, MetadataTokens.EntityHandle(ilReader.ReadInt32()), lookup);
                            if (target is null)
                            {
                                continue;
                            }

                            if (string.Equals(opcodeName, "newobj", StringComparison.OrdinalIgnoreCase))
                            {
                                var typeTarget = target.ToDeclaringTypeTarget(lookup);
                                AddTypeReference(references, container, typeTarget, "type-use", "newobj");
                                AddSummaryReference(method.TypeRefs, typeTarget, "newobj");
                                continue;
                            }

                            if (target.IsSpecialName)
                            {
                                continue;
                            }

                            references.Add(new ReferenceEdge(
                                container.Id,
                                container.Kind,
                                container.DisplayName,
                                container.FullName,
                                container.Signature,
                                target.Id,
                                target.Kind,
                                target.DisplayName,
                                target.FullName,
                                target.Signature,
                                "method-call",
                                opcodeName,
                                "semantic",
                                container.Assembly,
                                container.Source));
                            AddSummaryReference(method.Calls, target, opcodeName);
                            continue;
                        }
                    case OperandType.InlineField:
                        {
                            var target = ResolveFieldTokenTarget(reader, MetadataTokens.EntityHandle(ilReader.ReadInt32()), lookup);
                            if (target is null || target.IsHidden)
                            {
                                continue;
                            }

                            var referenceKind = opcodeName.StartsWith("st", StringComparison.Ordinal) ? "field-write" : "field-read";
                            references.Add(new ReferenceEdge(
                                container.Id,
                                container.Kind,
                                container.DisplayName,
                                container.FullName,
                                container.Signature,
                                target.Id,
                                target.Kind,
                                target.DisplayName,
                                target.FullName,
                                target.Signature,
                                referenceKind,
                                opcodeName,
                                "semantic",
                                container.Assembly,
                                container.Source));
                            AddSummaryReference(referenceKind == "field-write" ? method.FieldWrites : method.FieldReads, target, opcodeName);
                            continue;
                        }
                    case OperandType.InlineType:
                        {
                            var target = ResolveTypeTokenTarget(reader, MetadataTokens.EntityHandle(ilReader.ReadInt32()), lookup);
                            AddTypeReference(references, container, target, "type-use", opcodeName);
                            AddSummaryReference(method.TypeRefs, target, opcodeName);
                            continue;
                        }
                    case OperandType.InlineTok:
                        {
                            var handle = MetadataTokens.EntityHandle(ilReader.ReadInt32());
                            switch (handle.Kind)
                            {
                                case HandleKind.TypeDefinition:
                                case HandleKind.TypeReference:
                                case HandleKind.TypeSpecification:
                                    var typeTarget = ResolveTypeTokenTarget(reader, handle, lookup);
                                    AddTypeReference(references, container, typeTarget, "type-use", opcodeName);
                                    AddSummaryReference(method.TypeRefs, typeTarget, opcodeName);
                                    break;
                                case HandleKind.FieldDefinition:
                                case HandleKind.MemberReference:
                                    var fieldTarget = ResolveFieldTokenTarget(reader, handle, lookup);
                                    if (fieldTarget is not null && !fieldTarget.IsHidden)
                                    {
                                        references.Add(new ReferenceEdge(
                                            container.Id,
                                            container.Kind,
                                            container.DisplayName,
                                            container.FullName,
                                            container.Signature,
                                            fieldTarget.Id,
                                            fieldTarget.Kind,
                                            fieldTarget.DisplayName,
                                            fieldTarget.FullName,
                                            fieldTarget.Signature,
                                            "field-read",
                                            opcodeName,
                                            "semantic",
                                            container.Assembly,
                                            container.Source));
                                        AddSummaryReference(method.FieldReads, fieldTarget, opcodeName);
                                    }
                                    break;
                                case HandleKind.MethodDefinition:
                                case HandleKind.MethodSpecification:
                                    var methodTarget = ResolveMethodTokenTarget(reader, handle, lookup);
                                    if (methodTarget is not null && !methodTarget.IsSpecialName)
                                    {
                                        references.Add(new ReferenceEdge(
                                            container.Id,
                                            container.Kind,
                                            container.DisplayName,
                                            container.FullName,
                                            container.Signature,
                                            methodTarget.Id,
                                            methodTarget.Kind,
                                            methodTarget.DisplayName,
                                            methodTarget.FullName,
                                            methodTarget.Signature,
                                            "method-call",
                                            opcodeName,
                                            "semantic",
                                            container.Assembly,
                                            container.Source));
                                        AddSummaryReference(method.Calls, methodTarget, opcodeName);
                                    }
                                    break;
                                default:
                                    break;
                            }

                            continue;
                        }
                }

                SkipOperand(ref ilReader, operandType);
            }
        }
    }

    private static void AddTypeReference(
        List<ReferenceEdge> references,
        ContainerDescriptor container,
        ReferenceTarget? target,
        string referenceKind,
        string via)
    {
        if (target is null)
        {
            return;
        }

        references.Add(new ReferenceEdge(
            container.Id,
            container.Kind,
            container.DisplayName,
            container.FullName,
            container.Signature,
            target.Id,
            target.Kind,
            target.DisplayName,
            target.FullName,
            target.Signature,
            referenceKind,
            via,
            "semantic",
            container.Assembly,
            container.Source));
    }

    private static void AddSummaryReference(List<SummaryReference> summaries, ReferenceTarget? target, string via)
    {
        if (target is null)
        {
            return;
        }

        summaries.Add(new SummaryReference(
            target.Id,
            target.Kind,
            target.DisplayName,
            target.FullName,
            target.Signature,
            via,
            "semantic"));
    }

    private static IReadOnlyList<SummaryReference> DistinctSummaryReferences(IEnumerable<SummaryReference> summaries)
    {
        return summaries
            .Distinct()
            .OrderBy(summary => summary.Kind, StringComparer.Ordinal)
            .ThenBy(summary => summary.FullName, StringComparer.Ordinal)
            .ThenBy(summary => summary.Via, StringComparer.Ordinal)
            .ToArray();
    }
}
