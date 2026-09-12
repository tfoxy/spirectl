using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Live.GameApi;

namespace Spirectl.Sts2.Live;

/// <summary>
/// Makes multi-seat single-machine combats respect per-seat end-turn readiness.
///
/// The game's <c>CombatManager.AllPlayersReadyToEndTurn()</c> computes the per-player
/// ready count but ignores it whenever <c>RunManager.IsSinglePlayerOrFakeMultiplayer</c>
/// (NetGameType.Singleplayer) — it returns <c>true</c> unconditionally, so the FIRST seat
/// that calls <c>SetReadyToEndTurn</c> resolves the whole turn for everyone. That breaks
/// host-local couch-coop seats and multi-player fixtures, where one seat must be able to
/// sit in "ready, waiting on allies".
///
/// This postfix mirrors the game's own real-multiplayer branch when the combat has more
/// than one player: all players must be ready AND the current side must still be Player.
/// Solo combats (one player) keep the vanilla behavior untouched.
/// </summary>
internal static class Sts2EndTurnReadinessHooks
{
    private static readonly object Sync = new();
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

        // This postfix runs on a POLLED predicate, so bumping unconditionally would defeat idle backoff
        // entirely. Only the transition is news; the steady state is not. Accelerator only, and single-threaded
        // (game main thread), so a plain field is enough.
        if (__result != _lastReadyResult)
        {
            _lastReadyResult = __result;
            Sts2SemanticStateRevision.Bump();
        }
    }

    private static bool _lastReadyResult;
}
