using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace HotMod.Guardrails.Tests;

internal static class AnalyzerTestHost
{
    public static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(
        DiagnosticAnalyzer analyzer,
        string assemblyName,
        params string[] sources)
    {
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp13);
        var syntaxTrees = sources
            .Select((source, index) => CSharpSyntaxTree.ParseText(source, parseOptions, $"Source{index}.cs"))
            .ToArray();
        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Task).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(IDisposable).Assembly.Location)
        };
        var compilation = CSharpCompilation.Create(
            assemblyName,
            syntaxTrees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var diagnostics = await compilation
            .WithAnalyzers(ImmutableArray.Create(analyzer))
            .GetAnalyzerDiagnosticsAsync();
        return diagnostics.OrderBy(diagnostic => diagnostic.Id).ToImmutableArray();
    }

    public static (Compilation Compilation, ImmutableArray<Diagnostic> Diagnostics, string GeneratedText) RunGenerator(
        IIncrementalGenerator generator,
        string assemblyName,
        params string[] sources)
    {
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp13);
        var syntaxTrees = sources
            .Select((source, index) => CSharpSyntaxTree.ParseText(source, parseOptions, $"Source{index}.cs"))
            .ToArray();
        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Dictionary<string, string>).Assembly.Location)
        };
        var compilation = CSharpCompilation.Create(
            assemblyName,
            syntaxTrees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var updatedCompilation, out var diagnostics);
        var generatedText = string.Join(
            "\n--- generated ---\n",
            driver.GetRunResult().GeneratedTrees.Select(tree => tree.GetText().ToString()));
        return (updatedCompilation, diagnostics, generatedText);
    }
}
