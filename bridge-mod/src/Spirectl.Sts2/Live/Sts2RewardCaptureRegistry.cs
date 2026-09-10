using MegaCrit.Sts2.Core.Rewards;

namespace Spirectl.Sts2.Live;

// Holds the LIVE per-seat Reward objects for non-host (browser-only) seats, whose rewards have no
// native NRewardsScreen on the host (RewardsSet.Offer early-returns at the LocalContext gate, so the
// host never builds reward buttons for them). Sts2RewardsCaptureHooks captures each non-host seat's
// generated RewardsSet here; the state provider surfaces them via a registered StateRunOverlaySnapshot
// (so the browser can see + click the rewards), and the per-player commit resolves the live Reward to
// apply by its stable id. Host-local seats only (gated upstream); real remote peers act through their
// own client. Mirrors how Sts2RunOverlayRegistry exposes other per-seat overlays — but holds live refs.
internal static class Sts2RewardCaptureRegistry
{
    private static readonly Lock Sync = new();
    private static readonly Dictionary<string, Seat> SeatsByPlayerId = [];

    private sealed class Seat(ulong netId, List<Reward> rewards)
    {
        public ulong NetId { get; } = netId;

        // Ordered live rewards; the visible index in the stable id (reward:<player-id>:visible:<i>)
        // is the position here, matching Sts2RewardsOverlayInspector's projection order.
        public List<Reward> Rewards { get; } = rewards;
    }

    public static void Capture(string playerId, ulong netId, IReadOnlyList<Reward> rewards)
    {
        lock (Sync)
        {
            SeatsByPlayerId[playerId] = new Seat(netId, [.. rewards]);
        }

        Republish(playerId);
    }

    public static bool TryResolve(string playerId, int visibleIndex, out Reward reward, out ulong netId)
    {
        lock (Sync)
        {
            if (SeatsByPlayerId.TryGetValue(playerId, out var seat)
                && visibleIndex >= 0
                && visibleIndex < seat.Rewards.Count)
            {
                reward = seat.Rewards[visibleIndex];
                netId = seat.NetId;
                return true;
            }
        }

        reward = null!;
        netId = 0;
        return false;
    }

    // Drop a consumed reward and refresh the surfaced overlay (the remaining rewards re-index, exactly
    // like the native rewards screen re-indexes its buttons after a claim). Unregisters when empty.
    public static void Consume(string playerId, Reward reward)
    {
        lock (Sync)
        {
            if (SeatsByPlayerId.TryGetValue(playerId, out var seat))
            {
                seat.Rewards.Remove(reward);
            }
        }

        Republish(playerId);
    }

    public static void ClearAll()
    {
        string[] playerIds;
        lock (Sync)
        {
            playerIds = [.. SeatsByPlayerId.Keys];
            SeatsByPlayerId.Clear();
        }

        foreach (var playerId in playerIds)
        {
            Sts2RunOverlayRegistry.Unregister(Sts2RewardsOverlayInspector.CapturedRewardsOverlayId(playerId));
        }
    }

    private static void Republish(string playerId)
    {
        List<Reward> rewards;
        lock (Sync)
        {
            if (!SeatsByPlayerId.TryGetValue(playerId, out var seat) || seat.Rewards.Count == 0)
            {
                SeatsByPlayerId.Remove(playerId);
                Sts2RunOverlayRegistry.Unregister(Sts2RewardsOverlayInspector.CapturedRewardsOverlayId(playerId));
                return;
            }

            rewards = [.. seat.Rewards];
        }

        if (Sts2RewardsOverlayInspector.BuildCapturedRewardsOverlay(playerId, rewards) is { } overlay)
        {
            Sts2RunOverlayRegistry.Register(overlay);
        }
        else
        {
            Sts2RunOverlayRegistry.Unregister(Sts2RewardsOverlayInspector.CapturedRewardsOverlayId(playerId));
        }
    }
}
