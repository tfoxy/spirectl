using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2.Live.GameApi;

namespace Spirectl.Sts2.Live;

/// <summary>Preserves per-seat end-turn readiness without disrupting the game's own predicate.</summary>
internal static class Sts2EndTurnReadinessHooks
{
    internal static readonly string[] RequiredMembers =
    [
        "MegaCrit.Sts2.Core.Combat.CombatManager.AllPlayersReadyToEndTurn",
        "MegaCrit.Sts2.Core.Combat.CombatManager.IsPlayerReadyToEndTurn",
    ];

    private static readonly System.Threading.Lock Sync = new();
    private static bool _installed;

    public static bool IsInstalled
    {
        get
        {
            lock (Sync)
            {
                return _installed;
            }
        }
    }

    public static void Install(ILogStream logStream)
    {
        lock (Sync)
        {
            if (_installed)
            {
                return;
            }

            Sts2MonoModNativeDependencies.EnsureLoaded(logStream);

            var target = GameApiHooks.AllPlayersReadyToEndTurnTarget();
            var postfix = typeof(Sts2EndTurnReadinessHooks).GetMethod(
                nameof(AllPlayersReadyToEndTurnPostfix),
                BindingFlags.NonPublic | BindingFlags.Static);
            if (target is null || postfix is null)
            {
                logStream.Write(
                    BridgeLogLevel.Warn,
                    "bridge.combat.end-turn-readiness",
                    "Unable to install end-turn readiness hook; CombatManager.AllPlayersReadyToEndTurn was not found.");
                return;
            }

            try
            {
                var harmony = new Harmony("spirectl.end-turn-readiness");
                harmony.Patch(target, postfix: new HarmonyMethod(postfix));
            }
            catch (Exception ex)
            {
                logStream.Write(
                    BridgeLogLevel.Warn,
                    "bridge.combat.end-turn-readiness",
                    $"Skipping end-turn readiness hook because Harmony patching failed: {ex.GetType().Name}: {ex.Message}");
                return;
            }

            _installed = true;
            logStream.Write(
                BridgeLogLevel.Info,
                "bridge.combat.end-turn-readiness",
                "Installed end-turn readiness hook; multi-seat single-machine combats now wait for every seat.");
        }
    }

    private static void AllPlayersReadyToEndTurnPostfix(CombatManager __instance, ref bool __result)
    {
        if (!__result)
        {
            // The strict (real-multiplayer) branch already said no; never loosen it.
            return;
        }

        try
        {
            var result = __result;
            UpdateReadiness(__instance, ref result);
            __result = result;
        }
        catch (Exception exception)
        {
            Sts2CompatibilityReport.Process.Record("hook_runtime_failed", nameof(Sts2EndTurnReadinessHooks),
                $"End-turn readiness unavailable: {exception.GetType().Name}: {exception.Message}");
        }
    }

    // Keep JIT failures for typed game calls inside the caller's exception boundary.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void UpdateReadiness(CombatManager __instance, ref bool __result)
    {
        var state = __instance.DebugOnlyGetState();
        var players = state?.Players;
        if (players is null || players.Count <= 1)
        {
            // Vanilla solo behavior untouched.
            return;
        }

        var allReady = true;
        foreach (var player in players)
        {
            if (!__instance.IsPlayerReadyToEndTurn(player))
            {
                allReady = false;
                break;
            }
        }

        __result = allReady && state!.CurrentSide == CombatSide.Player;

#if !SPIRECTL_PROFILE_EMBEDDED
        // This postfix runs on a POLLED predicate, so bumping unconditionally would defeat idle backoff
        // entirely. Only the transition is news; the steady state is not. Accelerator only, and single-threaded
        // (game main thread), so a plain field is enough.
        if (__result != _lastReadyResult)
        {
            _lastReadyResult = __result;
            Sts2SemanticStateRevision.Bump();
        }
#endif
    }

#if !SPIRECTL_PROFILE_EMBEDDED
    private static bool _lastReadyResult;
#endif
}
