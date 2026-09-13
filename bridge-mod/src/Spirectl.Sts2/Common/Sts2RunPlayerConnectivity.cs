using System.Collections;
using Spirectl.Sts2.Live;

namespace Spirectl.Sts2;

// Connection-state policy shared by state projections. A readable host peer set is authoritative.
// Run snapshots fail open when that observation is unavailable, while lobby admission remains strict.

internal static class Sts2RunPlayerConnectivity
{
    // Resolve the connected-netId set once per snapshot. Returns null when connectedness is UNKNOWN, which callers
    // must read as "assume everyone is connected".
    internal static IReadOnlySet<ulong>? ResolveConnectedNetIds(object? netService, ulong? localNetId)
    {
        var connected = ReadPeerNetIds(netService);
        if (connected is null)
        {
            return null;
        }

        if (localNetId.HasValue)
        {
            connected.Add(localNetId.Value);
        }

        return connected;
    }

    // Per-player query against the once-per-snapshot set. Unknown set / unknown netId / host-local synthetic seat
    // all mean "connected" (see the safety contract above).
    internal static bool IsConnected(IReadOnlySet<ulong>? connectedNetIds, ulong? netId)
    {
        if (connectedNetIds is null || !netId.HasValue)
        {
            return true;
        }

        return connectedNetIds.Contains(netId.Value)
               || Sts2HostLocalSeatRegistry.IsHostLocalSeat(netId.Value);
    }

    // Lobby admission is strict: a remote player is ready only after host transport observation.
    internal static bool IsHostObservedLobbyPlayerConnected(IReadOnlySet<ulong>? connectedNetIds, ulong? netId)
        => connectedNetIds is not null && netId.HasValue && connectedNetIds.Contains(netId.Value);

    private static HashSet<ulong>? ReadPeerNetIds(object? netService)
    {
        if (netService is null)
        {
            return null;
        }

        // Primary: the host game service's peer registry (NetClientData.peerId is the netId).
        var fromConnectedPeers = ReadNetIds(
            Sts2LiveIntrospection.GetMemberValue(netService, "ConnectedPeers"),
            netIdMemberName: "peerId");
        if (fromConnectedPeers is not null)
        {
            return fromConnectedPeers;
        }

        // Fallback: the transport host's raw peer ids (already ulong, so no member hop).
        var netHost = Sts2LiveIntrospection.GetMemberValue(netService, "NetHost");
        return ReadNetIds(
            Sts2LiveIntrospection.GetMemberValue(netHost, "ConnectedPeerIds"),
            netIdMemberName: null);
    }

    // Returns null when the collection is not readable at all (member absent / not enumerable / enumeration threw),
    // which is what promotes the whole snapshot back to "connectedness unknown".
    private static HashSet<ulong>? ReadNetIds(object? collection, string? netIdMemberName)
    {
        if (collection is not IEnumerable items)
        {
            return null;
        }

        var netIds = new HashSet<ulong>();
        try
        {
            foreach (var item in items)
            {
                var raw = netIdMemberName is null
                    ? item
                    : Sts2LiveIntrospection.GetMemberValue(item, netIdMemberName);
                if (ToNetId(raw) is { } netId)
                {
                    netIds.Add(netId);
                }
            }
        }
        catch
        {
            return null;
        }

        return netIds;
    }

    private static ulong? ToNetId(object? value)
        => value switch
        {
            ulong typed => typed,
            uint typed => typed,
            long typed when typed >= 0 => (ulong)typed,
            int typed when typed >= 0 => (ulong)typed,
            string text when ulong.TryParse(text, out var parsed) => parsed,
            _ => null,
        };
}
