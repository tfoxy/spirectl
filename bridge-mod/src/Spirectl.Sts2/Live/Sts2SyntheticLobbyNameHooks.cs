using System.Reflection;
using HarmonyLib;
using Spirectl.Sts2.Core.Logging;

namespace Spirectl.Sts2.Live;

// Harmony install for the SetClientName / synthetic-seat display-name override.
//
// The patched method is PlatformUtil.GetPlayerNameRaw — NOT the BBCode-escaping GetPlayerName wrapper that
// delegates to it. Sts2SyntheticLobbyNameTarget carries the full reasoning; the short version:
//   * the host's lobby NAMEPLATE is a plain-text label, so it reads the RAW lookup (once, when the nameplate
//     becomes ready) — a hook on GetPlayerName never reached that call site and a client that joined with a name
//     kept showing a stale name from a PREVIOUS session;
//   * GetPlayerName DELEGATES to GetPlayerNameRaw, so patching the raw method gives BBCode call sites the override
//     WITH .EscapeBbcodeTags() still applied on top. Patching BOTH would double-apply this postfix, overwrite the
//     escaped string with the raw override and strip the escaping — a '[' in an untrusted display name could then
//     corrupt the game's BBCode parser. So: raw INSTEAD OF, never in addition to.
internal static class Sts2SyntheticLobbyNameHooks
{
    private static readonly object Sync = new();
    private static bool _installed;

    public static void Install(ILogStream logStream)
    {
        lock (Sync)
        {
            if (_installed)
            {
                return;
            }

            Sts2MonoModNativeDependencies.EnsureLoaded(logStream);

            var methods = Sts2SyntheticLobbyNameTarget.SelectPatchTargets(
                typeof(MegaCrit.Sts2.Core.Platform.PlatformUtil));
            var postfix = typeof(Sts2SyntheticLobbyNameHooks).GetMethod(
                nameof(ResolveSyntheticNamePostfix),
                BindingFlags.NonPublic | BindingFlags.Static);
            if (postfix is null || methods.Length == 0)
            {
                logStream.Write(
                    BridgeLogLevel.Warn,
                    "bridge.lobby.synthetic-names",
                    "Unable to install synthetic lobby name hook; PlatformUtil.GetPlayerNameRaw was not found.");
                return;
            }

            var harmony = new Harmony("spirectl.synthetic-lobby-names");
            foreach (var method in methods)
            {
                try
                {
                    harmony.Patch(method, postfix: new HarmonyMethod(postfix));
                }
                catch (Exception ex)
                {
                    logStream.Write(
                        BridgeLogLevel.Warn,
                        "bridge.lobby.synthetic-names",
                        $"Skipping synthetic lobby name hook because Harmony patching failed: {ex.GetType().Name}: {ex.Message}");
                    return;
                }
            }

            _installed = true;
            logStream.Write(
                BridgeLogLevel.Info,
                "bridge.lobby.synthetic-names",
                $"Installed synthetic lobby name hook for {methods.Length} PlatformUtil.GetPlayerNameRaw overload(s).");
        }
    }

    private static void ResolveSyntheticNamePostfix(object[] __args, ref string __result)
    {
        if (__args.Length < 2 || __args[1] is not ulong playerId)
        {
            return;
        }

        var syntheticName = Sts2HostLocalSeatRegistry.ResolveSyntheticName(playerId);
        if (!string.IsNullOrWhiteSpace(syntheticName))
        {
            __result = syntheticName;
        }
    }
}
