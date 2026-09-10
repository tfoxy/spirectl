using HotMod.Guardrails;
using Xunit;

namespace HotMod.Guardrails.Tests;

public sealed class HotReloadGuardrailAnalyzerTests
{
    [Fact]
    public async Task CleanTrampolineSetupProducesNoDiagnostics()
    {
        var diagnostics = await AnalyzerTestHost.AnalyzeAsync(
            new HotReloadGuardrailAnalyzer(),
            "HotMod.Shell",
            """
            namespace HotMod.Shell.HarmonyPatches;
            public static class Patch
            {
                public static void DispatchForTest() {}
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task ShellPatchReferenceToLogicTypeReportsHrg001()
    {
        var diagnostics = await AnalyzerTestHost.AnalyzeAsync(
            new HotReloadGuardrailAnalyzer(),
            "HotMod.Shell",
            """
            namespace HotMod.Logic { public sealed class HotLogic {} }
            namespace HotMod.Shell.HarmonyPatches;
            public static class Patch
            {
                private static HotMod.Logic.HotLogic? Current;
            }
            """);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "HRG001");
    }

    [Fact]
    public async Task ReloadableLogicHarmonyPatchReportsHrg002()
    {
        var diagnostics = await AnalyzerTestHost.AnalyzeAsync(
            new HotReloadGuardrailAnalyzer(),
            "HotMod.Logic",
            """
            namespace HarmonyLib { public sealed class HarmonyPatchAttribute : System.Attribute {} }
            namespace HotMod.Logic;
            [HarmonyLib.HarmonyPatch]
            public sealed class BadPatch {}
            """);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "HRG002");
    }

    [Fact]
    public async Task EventSubscriptionWithoutDisposeReportsHrg003()
    {
        var diagnostics = await AnalyzerTestHost.AnalyzeAsync(
            new HotReloadGuardrailAnalyzer(),
            "HotMod.Logic",
            """
            namespace HotMod.Logic;
            public sealed class Source { public event System.Action? Changed; }
            public sealed class Logic
            {
                public void Attach(Source source) { source.Changed += OnChanged; }
                private void OnChanged() {}
            }
            """);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "HRG003");
    }

    [Fact]
    public async Task BackgroundTaskWithoutCancellationReportsHrg004()
    {
        var diagnostics = await AnalyzerTestHost.AnalyzeAsync(
            new HotReloadGuardrailAnalyzer(),
            "HotMod.Logic",
            """
            namespace HotMod.Logic;
            public sealed class Logic
            {
                public void Start() { System.Threading.Tasks.Task.Run(() => {}); }
            }
            """);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "HRG004");
    }

    [Fact]
    public async Task MutableStaticLogicFieldReportsHrg005()
    {
        var diagnostics = await AnalyzerTestHost.AnalyzeAsync(
            new HotReloadGuardrailAnalyzer(),
            "HotMod.Logic",
            """
            namespace HotMod.Logic;
            public sealed class Logic {}
            public sealed class Holder { private static Logic? Current; }
            """);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "HRG005");
    }
}
