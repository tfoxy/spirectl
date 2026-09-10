using HarmonyLib;
using HotMod.Shell.Runtime;

namespace HotMod.Shell.HarmonyPatches;

#if HOTMOD_STS2
[HarmonyPatch]
#endif
public static class ExampleCombatTurnPatch
{
#if HOTMOD_STS2
    private static System.Reflection.MethodBase? TargetMethod()
    {
        return AccessTools.Method("Combat.NCombat:StartTurn");
    }

    private static void Postfix()
    {
        DispatchForTest();
    }
#endif

    public static void DispatchForTest(IReadOnlyDictionary<string, string>? values = null)
    {
        HotRuntime.Current.OnCombatTurn(values ?? new Dictionary<string, string>());
    }
}
