using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;
using Spirectl.Sts2.Core.Logging;

namespace Spirectl.Sts2.Live;

// Captures every NON-host host-local seat's generated RewardsSet so the browser can see + commit its
// rewards. RewardsSet.Offer early-returns at `if (!LocalContext.IsMe(Player)) return;` for non-local
// players, so the host never builds NRewardsScreen buttons for them — but GenerateWithoutOffering()
// runs first (and is called per seat by the fixture loader / any per-seat offering), fully populating
// Rewards with no screen and no LocalContext. We postfix it, and for a host-local seat that is NOT the
// local "me", stash the live Rewards in Sts2RewardCaptureRegistry (which registers the surfaced overlay).
// The host's own seat keeps its native screen path (we skip netId == localNetId). Real remote peers are
// not host-local seats, so they're skipped too. Mirrors Sts2ChooseACardOverlayHooks.
internal static class Sts2RewardsCaptureHooks
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

            var target = typeof(RewardsSet).GetMethod(
                nameof(RewardsSet.GenerateWithoutOffering),
                BindingFlags.Public | BindingFlags.Instance,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);
            var postfix = typeof(Sts2RewardsCaptureHooks).GetMethod(
                nameof(RegisterGeneratedRewardsPostfix),
                BindingFlags.NonPublic | BindingFlags.Static);
            if (target is null || postfix is null)
            {
                logStream.Write(
                    BridgeLogLevel.Warn,
                    "bridge.state.rewards",
                    "Unable to install reward-capture hook; RewardsSet.GenerateWithoutOffering was not found.");
                return;
            }

            try
            {
                new Harmony("spirectl.state.reward-capture")
                    .Patch(target, postfix: new HarmonyMethod(postfix));
                _installed = true;
                logStream.Write(BridgeLogLevel.Info, "bridge.state.rewards", "Installed reward-capture hook.");
            }
            catch (Exception ex)
            {
                logStream.Write(
                    BridgeLogLevel.Warn,
                    "bridge.state.rewards",
                    $"Reward-capture hook was not installed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static void RegisterGeneratedRewardsPostfix(RewardsSet __instance, Task<List<Reward>> __result)
    {
        var netId = __instance.Player.NetId;
        var localNetId = ToUInt64(Sts2LiveIntrospection.GetMemberValue(RunManager.Instance?.NetService, "NetId"));

        // Only non-host host-local (couch) seats: the host's own seat keeps its native NRewardsScreen,
        // and true remote peers resolve rewards through their own client.
        if (!Sts2HostLocalSeatRegistry.IsHostLocalSeat(netId)
            || (localNetId.HasValue && netId == localNetId.Value))
        {
            return;
        }

        var playerId = $"p:{netId}";
        _ = __result.ContinueWith(
            task =>
            {
                if (task.Status == TaskStatus.RanToCompletion && task.Result is { Count: > 0 } rewards)
                {
                    Sts2RewardCaptureRegistry.Capture(playerId, netId, rewards);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static ulong? ToUInt64(object? value)
        => value switch
        {
            null => null,
            ulong typed => typed,
            uint typed => typed,
            int typed when typed >= 0 => (ulong)typed,
            long typed when typed >= 0 => (ulong)typed,
            _ when ulong.TryParse(value.ToString(), out var parsed) => parsed,
            _ => null,
        };
}
