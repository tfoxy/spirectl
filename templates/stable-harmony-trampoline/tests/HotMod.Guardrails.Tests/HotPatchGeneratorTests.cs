using HotMod.Guardrails;
using Xunit;

namespace HotMod.Guardrails.Tests;

public sealed class HotPatchGeneratorTests
{
    [Fact]
    public void GeneratorEmitsPatchThatDelegatesThroughShellRuntime()
    {
        var (_, diagnostics, generatedText) = AnalyzerTestHost.RunGenerator(
            new HotPatchGenerator(),
            "HotMod.Shell",
            """
            using HotMod.Shell.Guardrails;
            namespace HotMod.Shell.HarmonyPatches;

            [HotPatch(
                TargetType = "Combat.NCombat",
                TargetMethod = "StartTurn",
                Hook = HotPatchHook.Postfix,
                Dispatch = "combat.turn")]
            internal sealed partial class GeneratedCombatTurnPatch;
            """);

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "HRG010");
        Assert.Contains("AccessTools.Method(\"Combat.NCombat:StartTurn\")", generatedText);
        Assert.Contains("HotRuntime.Current.DispatchHook(\"combat.turn\"", generatedText);
        Assert.Contains("public static void DispatchForTest", generatedText);
    }

    [Fact]
    public void GeneratorReportsIncompletePatchDeclaration()
    {
        var (_, diagnostics, _) = AnalyzerTestHost.RunGenerator(
            new HotPatchGenerator(),
            "HotMod.Shell",
            """
            using HotMod.Shell.Guardrails;
            namespace HotMod.Shell.HarmonyPatches;

            [HotPatch(TargetType = "Combat.NCombat")]
            internal sealed partial class GeneratedCombatTurnPatch;
            """);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "HRG010");
    }

    [Fact]
    public void GeneratorDoesNotInjectMarkerAttributeOutsideShellAssemblies()
    {
        var (_, _, generatedText) = AnalyzerTestHost.RunGenerator(
            new HotPatchGenerator(),
            "HotMod.Logic",
            """
            namespace HotMod.Logic;
            public sealed class Logic;
            """);

        Assert.DoesNotContain("HotPatchAttribute", generatedText);
    }
}
