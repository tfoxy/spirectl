using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;

namespace Spirectl.DotnetTools.Inspection;

internal static class IlSpyDecompiler
{
    public static string Decompile(string assemblyPath, int metadataToken)
    {
        var decompiler = new CSharpDecompiler(
            assemblyPath,
            new UniversalAssemblyResolver(
                assemblyPath,
                throwOnError: false,
                targetFramework: null,
                runtimePack: null,
                PEStreamOptions.Default,
                MetadataReaderOptions.Default),
            new DecompilerSettings
            {
                AlwaysUseBraces = true,
            });

        return decompiler.DecompileAsString([MetadataTokens.EntityHandle(metadataToken)]);
    }
}
