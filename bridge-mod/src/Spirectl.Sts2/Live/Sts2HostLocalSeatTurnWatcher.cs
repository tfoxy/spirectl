using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Live.GameApi;

namespace Spirectl.Sts2.Live;

/// <summary>
/// Completes the game's end-of-player-turn barrier on behalf of synthetic host-local
/// seats. After every player is ready to end turn, each CLIENT enqueues its own
/// <c>ReadyToBeginEnemyTurnAction</c> (the host only enqueues one for
/// <c>LocalContext.GetMe</c>), and <c>CombatManager.SetReadyToBeginEnemyTurn</c> starts
/// the enemy turn only once the game's readiness set covers every player in the combat
/// (<see cref="GameApiCombat.PlayersReadyToBeginEnemyTurn"/> — which member holds that
/// set differs per game build). A synthetic seat has no network peer, so its action never
/// arrives and the combat stalls with every seat check-marked. The host IS the seat's
/// authority, so this watcher calls <c>SetReadyToBeginEnemyTurn</c> for each missing
/// synthetic seat — exactly what the seat's own
/// <c>ReadyToBeginEnemyTurnAction.ExecuteAction</c> would do — once the host's own
/// readiness has landed in the set. Host games only: in singleplayer
/// (incl. multi-seat fixtures) the game bypasses the barrier, and re-completing it here
/// would run the enemy-turn switch twice. Runs on the main-thread dispatcher tick like
/// <see cref="Sts2HostLocalSeatSyncWatcher"/>.
/// </summary>
internal static class Sts2HostLocalSeatTurnWatcher
{
    private static readonly object Sync = new();
    private static bool _installed;
    private static bool _tickArmed;
    private static long _demandGeneration = -1;
    private static IDisposable? _tickLease;
    private static bool _faultLogged;
    private static ILogStream? _logStream;

    public static void Install(ILogStream logStream)
    {
        lock (Sync)
        {
            if (_installed)
            {
                return;
            }

            _logStream = logStream;
            Sts2HostLocalSeatRegistry.SyntheticSeatDemandChanged += OnSyntheticSeatDemandChanged;
            _installed = true;
            var demand = Sts2HostLocalSeatRegistry.DescribeSyntheticSeatDemand();
            ApplySyntheticSeatDemandLocked(demand.Active, demand.Generation);
            logStream.Write(
                BridgeLogLevel.Info,
                "bridge.host-local-seat-turn",
                "Installed demand-gated host-local seat turn watcher.");
        }
    }

    private static void OnSyntheticSeatDemandChanged(bool active, long generation)
    {
        IDisposable? release = null;
        lock (Sync)
        {
            release = ApplySyntheticSeatDemandLocked(active, generation);
        }
        release?.Dispose();
    }

    private static IDisposable? ApplySyntheticSeatDemandLocked(bool active, long generation)
    {
        if (generation < _demandGeneration)
        {
            return null;
        }

        _demandGeneration = generation;
        if (active == _tickArmed)
        {
            return null;
        }

        _tickArmed = active;
        if (active)
        {
            Sts2MainThreadDispatcher.MainThreadTick += OnMainThreadTick;
            _tickLease = Sts2MainThreadDispatcher.AcquireMainThreadTickLease();
            return null;
        }

        Sts2MainThreadDispatcher.MainThreadTick -= OnMainThreadTick;
        var release = _tickLease;
        _tickLease = null;
        return release;
    }

    private static void OnMainThreadTick()
    {
        try
        {
            AcknowledgePendingEnemyTurnReadiness();
            _faultLogged = false;
        }
        catch (Exception ex)
        {
            if (!_faultLogged)
            {
                _faultLogged = true;
                _logStream?.Write(
                    BridgeLogLevel.Warn,
                    "bridge.host-local-seat-turn",
                    $"Host-local seat turn watcher tick failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static void AcknowledgePendingEnemyTurnReadiness()
    {
        var combatManager = CombatManager.Instance;
        if (combatManager is null || !combatManager.IsInProgress)
        {
            return;
        }

        var netService = RunManager.Instance?.NetService;
        if (netService is null || netService.Type != NetGameType.Host)
        {
            return;
        }

        var state = combatManager.DebugOnlyGetState();
        if (state is null || state.CurrentSide != CombatSide.Player)
        {
            return;
        }

        // The ready set is only ever populated between the host's own
        // ReadyToBeginEnemyTurnAction (end-turn phase two reached via the real flow) and
        // the side switch; StartTurn clears it on the next player turn. An empty set — and
        // an absent one, outside a live combat — means nothing is pending; never seed it
        // ourselves. Where the set lives differs per game build, so it is read through the
        // API lane, which pins both the member names and the startup check on them.
        if (GameApiCombat.PlayersReadyToBeginEnemyTurn(combatManager) is not { Count: > 0 } readyPlayers)
        {
            return;
        }

        foreach (var player in state.Players.ToArray())
        {
            if (readyPlayers.Contains(player)
                || !Sts2HostLocalSeatRegistry.IsSyntheticHostLocalSeat(player.NetId))
            {
                continue;
            }

            // What the seat's own ReadyToBeginEnemyTurnAction.ExecuteAction would do on a
            // real client; the call that completes the set runs the enemy-turn switch.
            combatManager.SetReadyToBeginEnemyTurn(player);
            _logStream?.Write(
                BridgeLogLevel.Info,
                "bridge.host-local-seat-turn",
                $"Auto-acknowledged enemy-turn readiness for synthetic host-local seat p:{player.NetId}.");
        }
    }
}
