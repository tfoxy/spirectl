using Spirectl.Sts2.Core.Perspective;
using Spirectl.Sts2.Core.Protocol;

namespace Spirectl.Sts2.Core.State;

public static class OwnershipMetadata
{
    public static string? ResolvePerspective(string? playerId, PlayerScope scope = PlayerScope.Local)
    {
        if (string.IsNullOrWhiteSpace(playerId))
        {
            return null;
        }

        return scope == PlayerScope.Omniscient
            ? $"omniscient:{playerId}"
            : $"local:{playerId}";
    }

    public static RemoteClientOrchestrationCapabilitySnapshot LocalOnlyDegraded(bool provisional = false)
        => new(
            "local-only-degraded",
            RemoteClientOrchestrationStateSnapshot.LocalOnlyDegraded,
            "This local bridge can execute local-player actions only; independent remote clients require explicitly configured client bridges.",
            provisional);

    public static RemoteClientOrchestrationCapabilitySnapshot HostLocalSeat(bool provisional = false)
        => new(
            "host-local-seat",
            RemoteClientOrchestrationStateSnapshot.HostLocalSeat,
            "The current host bridge owns this local player seat and can execute its semantic actions.",
            provisional);
}
