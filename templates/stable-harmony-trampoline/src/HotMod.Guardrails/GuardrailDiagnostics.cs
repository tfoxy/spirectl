using Microsoft.CodeAnalysis;

namespace HotMod.Guardrails;

internal static class GuardrailDiagnostics
{
    public static readonly DiagnosticDescriptor ShellReferencesLogic = new(
        "HRG001",
        "Shell patch references reloadable logic",
        "Shell patch symbol '{0}' references reloadable logic type '{1}'",
        "HotReload",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor LogicAppliesHarmonyPatch = new(
        "HRG002",
        "Reloadable logic applies Harmony patches directly",
        "Reloadable logic must not apply Harmony patches directly: '{0}'",
        "HotReload",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor EventSubscriptionWithoutDispose = new(
        "HRG003",
        "Reloadable logic subscribes to an event without an obvious dispose path",
        "Type '{0}' subscribes to an event and does not implement IDisposable",
        "HotReload",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor BackgroundTaskWithoutCancellation = new(
        "HRG004",
        "Reloadable logic starts background work without cancellation ownership",
        "Background work call '{0}' does not pass a cancellation token",
        "HotReload",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MutableStaticLogicField = new(
        "HRG005",
        "Reloadable logic stores mutable static state of reloadable types",
        "Mutable static field '{0}' stores reloadable logic type '{1}'",
        "HotReload",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ContractReferencesImplementation = new(
        "HRG006",
        "Contract assembly references shell or logic implementation",
        "Contract assembly references implementation assembly '{0}'",
        "HotReload",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        customTags: [WellKnownDiagnosticTags.CompilationEnd]);

    public static readonly DiagnosticDescriptor InvalidHotPatchDeclaration = new(
        "HRG010",
        "HotPatch declaration is incomplete",
        "HotPatch declaration on '{0}' must specify TargetType, TargetMethod, and Dispatch",
        "HotReload",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor[] AnalyzerDescriptors =
    [
        ShellReferencesLogic,
        LogicAppliesHarmonyPatch,
        EventSubscriptionWithoutDispose,
        BackgroundTaskWithoutCancellation,
        MutableStaticLogicField,
        ContractReferencesImplementation
    ];
}
