using Spirectl.Sts2.Core.State;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// Run-player connectedness in the state snapshot (StateRunPlayerSnapshot.IsConnected). The lobby already answers
// this from RunLobby.ConnectedPlayerIds; the run path reads the host net service's live peer registry
// (INetHostGameService.ConnectedPeers -> NetClientData.peerId), falling back to NetHost.ConnectedPeerIds.
//
// The load-bearing property is the SAFETY CONTRACT: a snapshot must never claim a false "disconnected". Anything
// unresolvable — no net service, no readable peer collection, no netId — resolves to CONNECTED.
public sealed class Sts2RunPlayerConnectivityTests
{
    [Fact]
    public void RunPlayerSnapshotDefaultsToConnected()
    {
        // Trailing + defaulted so every pre-existing construction site and fixture keeps its old behaviour.
        var player = RunPlayer();

        Assert.True(player.IsConnected);
        Assert.False((player with { IsConnected = false }).IsConnected);
    }

    [Fact]
    public void ConnectedPeersDriveTheSnapshotFlag()
    {
        var netService = new HostNetServiceDouble
        {
            NetId = 1001,
            ConnectedPeers = [new NetClientDataDouble(1002), new NetClientDataDouble(1003)],
        };

        var connected = Sts2RunPlayerConnectivity.ResolveConnectedNetIds(netService, localNetId: 1001);

        Assert.NotNull(connected);
        // The local player is unioned in: the host is never its own peer, but it is always connected.
        ulong[] sorted = [.. connected.OrderBy(netId => netId)];
        Assert.Equal<IEnumerable<ulong>>([1001, 1002, 1003], sorted);
        Assert.True(Sts2RunPlayerConnectivity.IsConnected(connected, 1001));
        Assert.True(Sts2RunPlayerConnectivity.IsConnected(connected, 1002));
        // A peer that dropped off the wire is the ONLY way a player is reported disconnected.
        Assert.False(Sts2RunPlayerConnectivity.IsConnected(connected, 1004));
        Assert.False(RunPlayer(isConnected: Sts2RunPlayerConnectivity.IsConnected(connected, 1004)).IsConnected);
    }

    [Fact]
    public void FallsBackToNetHostConnectedPeerIds()
    {
        // No ConnectedPeers member at all (older/reshaped host service) -> the transport's raw peer ids.
        var netService = new HostNetServiceWithoutConnectedPeersDouble
        {
            NetHost = new NetHostDouble { ConnectedPeerIds = [1002UL, 1003UL] },
        };

        var connected = Sts2RunPlayerConnectivity.ResolveConnectedNetIds(netService, localNetId: 1001);

        Assert.NotNull(connected);
        ulong[] sorted = [.. connected.OrderBy(netId => netId)];
        Assert.Equal<IEnumerable<ulong>>([1001, 1002, 1003], sorted);
    }

    [Fact]
    public void UnresolvableNetServiceLeavesEveryPlayerConnected()
    {
        // 1. no net service at all
        Assert.Null(Sts2RunPlayerConnectivity.ResolveConnectedNetIds(null, localNetId: 1001));
        // 2. a CLIENT net service: neither ConnectedPeers nor NetHost exists there
        Assert.Null(Sts2RunPlayerConnectivity.ResolveConnectedNetIds(new ClientNetServiceDouble(), localNetId: 1001));
        // 3. the members exist but hold nothing enumerable
        Assert.Null(Sts2RunPlayerConnectivity.ResolveConnectedNetIds(
            new HostNetServiceWithoutConnectedPeersDouble { NetHost = null },
            localNetId: 1001));

        // "unknown" must read as connected for every player, including one with no resolvable netId.
        Assert.True(Sts2RunPlayerConnectivity.IsConnected(null, 1002));
        Assert.True(Sts2RunPlayerConnectivity.IsConnected(null, null));
        Assert.True(RunPlayer(isConnected: Sts2RunPlayerConnectivity.IsConnected(null, 1002)).IsConnected);
    }

    [Fact]
    public void PlayerWithoutNetIdStaysConnected()
    {
        var netService = new HostNetServiceDouble { NetId = 1001, ConnectedPeers = [new NetClientDataDouble(1002)] };
        var connected = Sts2RunPlayerConnectivity.ResolveConnectedNetIds(netService, localNetId: 1001);

        Assert.True(Sts2RunPlayerConnectivity.IsConnected(connected, null));
    }

    [Fact]
    public void HostLocalSyntheticSeatStaysConnected()
    {
        // A synthetic host-local seat has no ENet peer by construction, so ConnectedPeers can never list it —
        // reporting it disconnected would be exactly the false negative the contract forbids.
        var netService = new HostNetServiceDouble { NetId = 1001, ConnectedPeers = [new NetClientDataDouble(1002)] };
        var connected = Sts2RunPlayerConnectivity.ResolveConnectedNetIds(netService, localNetId: 1001);

        Assert.False(Sts2RunPlayerConnectivity.IsConnected(connected, 2));
        Sts2HostLocalSeatRegistry.RegisterSyntheticSeat(2, "Couch Seat");
        try
        {
            Assert.True(Sts2RunPlayerConnectivity.IsConnected(connected, 2));
        }
        finally
        {
            Sts2HostLocalSeatRegistry.UnregisterSyntheticSeat(2);
        }
    }

    private static StateRunPlayerSnapshot RunPlayer(bool? isConnected = null)
    {
        var player = new StateRunPlayerSnapshot(
            Id: "p:1002",
            SourceType: "MegaCrit.Sts2.Core.Runs.Player",
            NetId: "1002",
            DisplayName: "Alice",
            CharacterId: "ironclad",
            IsLocal: false,
            IsHost: false,
            IsRemote: true,
            Creature: null,
            Gold: 99,
            Deck: null,
            Relics: [],
            InventoryComplete: true,
            Notices: []);
        return isConnected is null ? player : player with { IsConnected = isConnected.Value };
    }

    // Mirrors MegaCrit.Sts2.Core.Entities.Multiplayer.NetClientData (a struct with a public peerId FIELD, so the
    // reflection path has to read a boxed struct's field rather than a property).
    private struct NetClientDataDouble(ulong netId)
    {
        public ulong peerId = netId;

        public bool readyForBroadcasting = false;
    }

    // Mirrors NetHostGameService: ConnectedPeers + NetId + NetHost.
    private sealed class HostNetServiceDouble
    {
        public ulong NetId { get; init; }

        public IReadOnlyList<NetClientDataDouble> ConnectedPeers { get; init; } = [];
    }

    private sealed class HostNetServiceWithoutConnectedPeersDouble
    {
        public NetHostDouble? NetHost { get; init; }
    }

    // Mirrors MegaCrit.Sts2.Core.Multiplayer.Transport.NetHost.ConnectedPeerIds.
    private sealed class NetHostDouble
    {
        public IEnumerable<ulong> ConnectedPeerIds { get; init; } = [];
    }

    // Mirrors NetClientGameService: no ConnectedPeers, no NetHost — a client cannot know the peer set.
    private sealed class ClientNetServiceDouble
    {
        public ulong NetId { get; init; } = 1002;

        public ulong HostNetId { get; init; } = 1001;
    }
}
