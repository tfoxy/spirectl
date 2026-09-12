using System.Collections;
using Spirectl.Sts2.Live;

namespace Spirectl.Sts2;

// Run-player connectedness for the state snapshot: is each run player's ENet peer currently connected?
//
// The lobby already answers this from the in-run lobby's roster of ids — GameApiNames.LobbyPlayerIds, which
// names the member per game build (see Sts2StateProvider.ResolvePlayerConnected, mirroring
// NRemoteLobbyPlayer._isConnected). Once the run starts that lobby is gone, so the run path reads the HOST net
// service's live peer registry instead:
//
//     INetHostGameService.ConnectedPeers -> IReadOnlyList<NetClientData>   (NetClientData.peerId == netId)
//
// NetHostGameService keeps that list exact — a peer is appended in OnPeerConnected and removed in
// OnPeerDisconnected — so it is the authoritative "who is on the wire right now" set. If it is unreachable (older
// build, a CLIENT net service, a reshaped member) we fall back to the transport's own NetHost.ConnectedPeerIds
// (ENetHost/SteamHost expose IEnumerable<ulong>).
//
// SAFETY CONTRACT — never produce a FALSE "disconnected". A missing net service, a missing/unreadable peer
// collection, or a player with no resolvable netId all yield "connected". Only an explicitly readable peer set that
// demonstrably lacks a player's netId marks that player disconnected. The set is resolved ONCE per snapshot
// (ResolveConnectedNetIds) and then queried per player (IsConnected) because ResolveRunPlayers is a hot path.
//
// Two netIds are unioned into the set because they legitimately never appear in ConnectedPeers:
//   * the LOCAL player (INetGameService.NetId) — the host is not its own peer, but it is always connected;
//   * synthetic host-local seats (Sts2HostLocalSeatRegistry) — fixture seats with no ENet peer at all.
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
