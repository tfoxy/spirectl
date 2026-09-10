using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace HotMod.Guardrails;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HotReloadGuardrailAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        GuardrailDiagnostics.AnalyzerDescriptors.ToImmutableArray();

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(Register);
    }

    private static void Register(CompilationStartAnalysisContext context)
    {
        var role = ProjectRole.FromAssemblyName(context.Compilation.AssemblyName ?? "");

        if (role.Kind == ProjectRoleKind.Shell)
        {
            context.RegisterSymbolAction(action => AnalyzeShellNamedType(action, role), SymbolKind.NamedType);
        }

        if (role.Kind == ProjectRoleKind.Logic)
        {
            context.RegisterSyntaxNodeAction(AnalyzeLogicAttribute, SyntaxKind.Attribute);
            context.RegisterSyntaxNodeAction(AnalyzeLogicInvocation, SyntaxKind.InvocationExpression);
            context.RegisterSyntaxNodeAction(AnalyzeLogicAssignment, SyntaxKind.AddAssignmentExpression);
            context.RegisterSymbolAction(action => AnalyzeLogicField(action, role), SymbolKind.Field);
        }

        if (role.Kind == ProjectRoleKind.Contracts)
        {
            context.RegisterCompilationEndAction(action => AnalyzeContractReferences(action, role));
        }
    }

    private static void AnalyzeShellNamedType(SymbolAnalysisContext context, ProjectRole role)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (!type.ContainingNamespace.ToDisplayString().EndsWith(".HarmonyPatches", StringComparison.Ordinal))
        {
            return;
        }

        foreach (var member in type.GetMembers())
        {
            foreach (var referenced in ReferencedTypes(member))
            {
                if (role.IsLogicSymbol(referenced))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        GuardrailDiagnostics.ShellReferencesLogic,
                        member.Locations.FirstOrDefault(),
                        member.Name,
                        referenced.ToDisplayString()));
                }
            }
        }
    }

    private static void AnalyzeLogicAttribute(SyntaxNodeAnalysisContext context)
    {
        var attribute = (AttributeSyntax)context.Node;
        var symbol = context.SemanticModel.GetSymbolInfo(attribute).Symbol?.ContainingType;
        var display = symbol?.ToDisplayString() ?? "";
        if (display is "HarmonyLib.HarmonyPatch" or "HarmonyLib.HarmonyPatchAttribute")
        {
            context.ReportDiagnostic(Diagnostic.Create(
                GuardrailDiagnostics.LogicAppliesHarmonyPatch,
                attribute.GetLocation(),
                display));
        }
    }

    private static void AnalyzeLogicInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        var symbol = context.SemanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
        var containingType = symbol?.ContainingType.ToDisplayString() ?? "";
        var methodName = symbol?.Name ?? "";

        if (containingType == "HarmonyLib.Harmony" && methodName.StartsWith("Patch", StringComparison.Ordinal))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                GuardrailDiagnostics.LogicAppliesHarmonyPatch,
                invocation.GetLocation(),
                $"{containingType}.{methodName}"));
        }

        if ((containingType == "System.Threading.Tasks.Task" && methodName == "Run")
            || (containingType == "System.Threading.Tasks.TaskFactory" && methodName == "StartNew"))
        {
            var hasCancellationToken = symbol?.Parameters.Any(parameter =>
                parameter.Type.ToDisplayString() == "System.Threading.CancellationToken") == true;
            if (!hasCancellationToken)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    GuardrailDiagnostics.BackgroundTaskWithoutCancellation,
                    invocation.GetLocation(),
                    $"{containingType}.{methodName}"));
            }
        }
    }

    private static void AnalyzeLogicAssignment(SyntaxNodeAnalysisContext context)
    {
        var assignment = (AssignmentExpressionSyntax)context.Node;
        var eventSymbol = context.SemanticModel.GetSymbolInfo(assignment.Left).Symbol as IEventSymbol;
        if (eventSymbol is null)
        {
            return;
        }

        var containingType = assignment.FirstAncestorOrSelf<TypeDeclarationSyntax>();
        if (containingType is null)
        {
            return;
        }

        var containingSymbol = context.SemanticModel.GetDeclaredSymbol(containingType);
        if (containingSymbol is not null && !ImplementsIDisposable(containingSymbol))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                GuardrailDiagnostics.EventSubscriptionWithoutDispose,
                assignment.GetLocation(),
                containingSymbol.ToDisplayString()));
        }
    }

    private static void AnalyzeLogicField(SymbolAnalysisContext context, ProjectRole role)
    {
        var field = (IFieldSymbol)context.Symbol;
        if (!field.IsStatic || field.IsReadOnly || !role.IsLogicSymbol(field.Type))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            GuardrailDiagnostics.MutableStaticLogicField,
            field.Locations.FirstOrDefault(),
            field.Name,
            field.Type.ToDisplayString()));
    }

    private static void AnalyzeContractReferences(CompilationAnalysisContext context, ProjectRole role)
    {
        foreach (var reference in context.Compilation.ReferencedAssemblyNames)
        {
            if (role.IsShellAssembly(reference.Name) || role.IsLogicAssembly(reference.Name))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    GuardrailDiagnostics.ContractReferencesImplementation,
                    Location.None,
                    reference.Name));
            }
        }
    }

    private static IEnumerable<ITypeSymbol> ReferencedTypes(ISymbol member)
    {
        if (member is IFieldSymbol field)
        {
            yield return field.Type;
        }
        else if (member is IPropertySymbol property)
        {
            yield return property.Type;
        }
        else if (member is IMethodSymbol method)
        {
            foreach (var parameter in method.Parameters)
            {
                yield return parameter.Type;
            }

            yield return method.ReturnType;
        }
        else if (member is IEventSymbol eventSymbol && eventSymbol.Type is not null)
        {
            yield return eventSymbol.Type;
        }
    }

    private static bool ImplementsIDisposable(INamedTypeSymbol type)
    {
        return type.AllInterfaces.Any(symbol => symbol.ToDisplayString() == "System.IDisposable");
    }

    private readonly struct ProjectRole
    {
        public ProjectRole(ProjectRoleKind kind, string prefix)
        {
            Kind = kind;
            Prefix = prefix;
        }

        public ProjectRoleKind Kind { get; }

        private string Prefix { get; }

        public static ProjectRole FromAssemblyName(string assemblyName)
        {
            foreach (var suffix in new[] { ".Shell", ".Logic", ".Contracts" })
            {
                if (assemblyName.EndsWith(suffix, StringComparison.Ordinal))
                {
                    var kind = suffix switch
                    {
                        ".Shell" => ProjectRoleKind.Shell,
                        ".Logic" => ProjectRoleKind.Logic,
                        _ => ProjectRoleKind.Contracts
                    };
                    return new ProjectRole(kind, assemblyName.Substring(0, assemblyName.Length - suffix.Length));
                }
            }

            return new ProjectRole(ProjectRoleKind.Unknown, assemblyName);
        }

        public bool IsShellAssembly(string? assemblyName) => assemblyName == $"{Prefix}.Shell";

        public bool IsLogicAssembly(string? assemblyName) => assemblyName == $"{Prefix}.Logic";

        public bool IsLogicSymbol(ITypeSymbol? symbol)
        {
            if (symbol is null)
            {
                return false;
            }

            return IsLogicAssembly(symbol.ContainingAssembly?.Name)
                || symbol.ContainingNamespace.ToDisplayString().StartsWith($"{Prefix}.Logic", StringComparison.Ordinal);
        }
    }

    private enum ProjectRoleKind
    {
        Unknown,
        Shell,
        Logic,
        Contracts
    }
}
