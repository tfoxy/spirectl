using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Embedding;

namespace Spirectl.Sts2.Live;

/// <summary>Publishes connection failures before product-specific dialog handling can hide them.</summary>
internal static class Sts2MultiplayerConnectionHooks
{
    private static readonly object Sync = new();
    private static bool _installed;

    internal static bool IsInstalled { get { lock (Sync) return _installed; } }

    public static void Install(ILogStream logStream)
    {
        lock (Sync)
        {
            if (_installed) return;

            try
            {
                var harmony = new Harmony("spirectl.multiplayer.connection-observation");
                var targets = ResolveTargets();
                harmony.Patch(targets.Connecting, postfix: new HarmonyMethod(typeof(Sts2MultiplayerConnectionHooks).GetMethod(nameof(ClientConstructedPostfix), BindingFlags.NonPublic | BindingFlags.Static)!));
                harmony.Patch(targets.Disconnected, postfix: new HarmonyMethod(typeof(Sts2MultiplayerConnectionHooks).GetMethod(nameof(DisconnectedPostfix), BindingFlags.NonPublic | BindingFlags.Static)!));
                harmony.Patch(targets.NetworkError, prefix: new HarmonyMethod(typeof(Sts2MultiplayerConnectionHooks).GetMethod(nameof(NetworkErrorPrefix), BindingFlags.NonPublic | BindingFlags.Static)!) { priority = Priority.First });
                _installed = true;
                logStream.Write(BridgeLogLevel.Info, "bridge.multiplayer.connection", "Installed native multiplayer connection observation hooks.");
            }
            catch (Exception ex)
            {
                logStream.Write(BridgeLogLevel.Warn, "bridge.multiplayer.connection", $"Skipping multiplayer connection observation hooks: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    internal static MultiplayerConnectionHookTargets ResolveTargets()
    {
        var target = typeof(NetClientGameService).GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault();
        if (target is null) throw new MissingMethodException(typeof(NetClientGameService).FullName, ".ctor");
        var disconnected = AccessTools.Method(typeof(NetClientGameService), nameof(NetClientGameService.OnDisconnectedFromHost));
        if (disconnected is null) throw new MissingMethodException(typeof(NetClientGameService).FullName, nameof(NetClientGameService.OnDisconnectedFromHost));
        var networkError = AccessTools.Method(typeof(NErrorPopup), nameof(NErrorPopup.Create), [typeof(NetErrorInfo)]);
        if (networkError is null) throw new MissingMethodException(typeof(NErrorPopup).FullName, nameof(NErrorPopup.Create));
        return new MultiplayerConnectionHookTargets(target, disconnected, networkError);
    }

    private static void ClientConstructedPostfix()
        => EmbeddableMultiplayerConnectionHub.Shared.Publish(
            MultiplayerConnectionPhase.Connecting,
            error: null,
            DateTimeOffset.UtcNow);

    private static void DisconnectedPostfix()
        => EmbeddableMultiplayerConnectionHub.Shared.Publish(
            MultiplayerConnectionPhase.Disconnected,
            error: null,
            DateTimeOffset.UtcNow);

    private static void NetworkErrorPrefix(NetErrorInfo info)
        => EmbeddableMultiplayerConnectionHub.Shared.Publish(
            MultiplayerConnectionPhase.Failed,
            new MultiplayerConnectionError("native-network-error", info.ToString()),
            DateTimeOffset.UtcNow);
}

internal sealed record MultiplayerConnectionHookTargets(
    ConstructorInfo Connecting,
    MethodInfo Disconnected,
    MethodInfo NetworkError);
