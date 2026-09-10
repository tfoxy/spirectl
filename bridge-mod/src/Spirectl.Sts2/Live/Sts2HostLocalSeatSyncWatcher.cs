using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;
using Spirectl.Sts2.Core.Logging;

namespace Spirectl.Sts2.Live;

/// <summary>
/// Completes the game's combat-state sync barrier on behalf of synthetic host-local
/// seats. <c>CombatStateSynchronizer.StartSync</c> waits for a <c>SyncPlayerDataMessage</c>
/// from every id in <c>RunLobby.ConnectedPlayerIds</c>, but a synthetic seat has no
/// network peer, so the barrier never completes and the run hangs on the embark fade
/// (and on every later room entry). The host's copy of the seat's player IS the
/// authoritative state, so this watcher feeds the seat's own serialized player through
/// the synchronizer's message handler — exactly the payload a real client would send.
/// Runs on the main-thread dispatcher tick (not a Harmony patch — Harmony/MonoMod's
/// native helper fails to load in the live game, so runtime patching is unavailable).
/// </summary>
internal static class Sts2HostLocalSeatSyncWatcher
{
    private static readonly object Sync = new();
    private static bool _installed;
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
            Sts2MainThreadDispatcher.MainThreadTick += OnMainThreadTick;
            _installed = true;
            logStream.Write(
                BridgeLogLevel.Info,
                "bridge.host-local-seat-sync",
                "Installed host-local seat sync watcher on the main-thread dispatcher tick.");
        }
    }

    private static void OnMainThreadTick()
    {
        try
        {
            AcknowledgePendingSync();
            _faultLogged = false;
        }
        catch (Exception ex)
        {
            if (!_faultLogged)
            {
                _faultLogged = true;
                _logStream?.Write(
                    BridgeLogLevel.Warn,
                    "bridge.host-local-seat-sync",
                    $"Failed to auto-acknowledge combat sync for synthetic seats: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static void AcknowledgePendingSync()
    {
        var runManager = RunManager.Instance;
        var synchronizer = runManager.CombatStateSynchronizer;
        if (synchronizer is null || synchronizer.IsDisabled)
        {
            return;
        }

        // A pending, uncompleted completion source is the marker that StartSync ran
        // and WaitForSync is (or will be) blocked on missing peer messages.
        if (Sts2LiveIntrospection.GetMemberValue(synchronizer, "_syncCompletionSource") is not TaskCompletionSource pending
            || pending.Task.IsCompleted)
        {
            return;
        }

        var runLobby = runManager.RunLobby;
        var netService = runManager.NetService;
        if (runLobby is null || netService is null || netService.Type != NetGameType.Host)
        {
            return;
        }

        if (Sts2LiveIntrospection.GetMemberValue(synchronizer, "_runState") is not RunState runState
            || Sts2LiveIntrospection.GetMemberValue(synchronizer, "_syncData") is not Dictionary<ulong, SerializablePlayer> syncData)
        {
            return;
        }

        foreach (var playerId in runLobby.ConnectedPlayerIds.ToArray())
        {
            if (syncData.ContainsKey(playerId) || !Sts2HostLocalSeatRegistry.IsSyntheticHostLocalSeat(playerId))
            {
                continue;
            }

            var player = runState.GetPlayer(playerId);
            if (player is null)
            {
                continue;
            }

            var message = default(SyncPlayerDataMessage);
            message.player = player.ToSerializable();
            if (Sts2LiveIntrospection.TryInvokeMethod(synchronizer, "OnSyncPlayerMessageReceived", message, playerId))
            {
                _logStream?.Write(
                    BridgeLogLevel.Info,
                    "bridge.host-local-seat-sync",
                    $"Auto-acknowledged combat sync for synthetic host-local seat p:{playerId}.");
            }
            else
            {
                _logStream?.Write(
                    BridgeLogLevel.Warn,
                    "bridge.host-local-seat-sync",
                    $"Could not deliver the synthetic sync message for p:{playerId}; OnSyncPlayerMessageReceived was not invokable.");
            }
        }
    }
}
