using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;
using Spirectl.Sts2.Core.Logging;

namespace Spirectl.Sts2.Live;

// Captures completed rewards for host-local seats without a native rewards screen. The host's own
// seat keeps its native screen, and real remote peers keep their client-owned rewards.
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
            var postfix = target is null ? null : ResolvePostfix(target.ReturnType);
            if (target is null || postfix is null)
            {
                logStream.Write(
                    BridgeLogLevel.Warn,
                    "bridge.state.rewards",
                    "Unable to install reward-capture hook; RewardsSet.GenerateWithoutOffering "
                        + $"has a missing or unsupported signature (return type: {target?.ReturnType}).");
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

    internal static MethodInfo? ResolvePostfix(Type returnType)
    {
        var name = returnType == typeof(Task<List<Reward>>)
            ? nameof(RegisterGeneratedRewardsPostfix)
            : returnType == typeof(Task)
                ? nameof(RegisterGeneratedRewardsTaskPostfix)
                : null;
        return name is null ? null : typeof(Sts2RewardsCaptureHooks).GetMethod(
            name, BindingFlags.NonPublic | BindingFlags.Static);
    }

    private static void RegisterGeneratedRewardsPostfix(RewardsSet __instance, Task<List<Reward>> __result)
        => RegisterGeneratedRewards(__instance, __result, () => __result.Result);

    private static void RegisterGeneratedRewardsTaskPostfix(RewardsSet __instance, Task __result)
        => RegisterGeneratedRewards(__instance, __result, () => __instance.Rewards);

    private static void RegisterGeneratedRewards(
        RewardsSet rewardsSet, Task generation, Func<IReadOnlyList<Reward>> resolveRewards)
    {
        var netId = rewardsSet.Player.NetId;
        var localNetId = ToUInt64(Sts2LiveIntrospection.GetMemberValue(RunManager.Instance?.NetService, "NetId"));

        // Only non-host host-local (couch) seats: the host's own seat keeps its native NRewardsScreen,
        // and true remote peers resolve rewards through their own client.
        if (!Sts2HostLocalSeatRegistry.IsHostLocalSeat(netId)
            || (localNetId.HasValue && netId == localNetId.Value))
        {
            return;
        }

        var playerId = $"p:{netId}";
        _ = generation.ContinueWith(
            task =>
            {
                if (task.Status == TaskStatus.RanToCompletion && resolveRewards() is { Count: > 0 } rewards)
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
