namespace Spirectl.Sts2;

internal static class Sts2HostLocalSeatRegistry
{
    private static readonly object Sync = new();
    private static HashSet<ulong> _hostLocalSeatNetIds = [];
    private static readonly Dictionary<ulong, string> SyntheticNamesByNetId = new();
    // Display-name overrides for REAL networked clients (e.g. a couch-coop headless ENet peer joining as
    // netId 1002). Unlike synthetic seats, these netIds are genuine remote players, so they must NOT be added
    // to _hostLocalSeatNetIds (that flag drives action ownership / remote-orchestration everywhere). This map
    // only feeds name resolution: the PlatformUtil.GetPlayerNameRaw hook + Sts2LobbyNameResolver both consult
    // ResolveSyntheticName, which now returns an override when no synthetic seat owns the netId. Not pruned by
    // ReplaceHostLocalSeats (those are fixture-only host-local seats; real client names persist until cleared).
    private static readonly Dictionary<ulong, string> ClientNameOverridesByNetId = new();

    public static void ReplaceHostLocalSeats(IEnumerable<ulong> netIds)
    {
        lock (Sync)
        {
            _hostLocalSeatNetIds = netIds.ToHashSet();
            foreach (var netId in SyntheticNamesByNetId.Keys.Where(netId => !_hostLocalSeatNetIds.Contains(netId)).ToArray())
            {
                SyntheticNamesByNetId.Remove(netId);
            }
        }
    }

    public static void RegisterSyntheticSeat(ulong netId, string displayName)
    {
        var name = NormalizeName(displayName)
            ?? throw new ArgumentException("Synthetic lobby seats require a non-empty display name.", nameof(displayName));
        lock (Sync)
        {
            _hostLocalSeatNetIds.Add(netId);
            SyntheticNamesByNetId[netId] = name;
        }
    }

    public static void UnregisterSyntheticSeat(ulong netId)
    {
        lock (Sync)
        {
            SyntheticNamesByNetId.Remove(netId);
            _hostLocalSeatNetIds.Remove(netId);
        }
    }

    public static bool IsHostLocalSeat(ulong netId)
    {
        lock (Sync)
        {
            return _hostLocalSeatNetIds.Contains(netId);
        }
    }

    public static bool IsSyntheticHostLocalSeat(ulong netId)
    {
        lock (Sync)
        {
            return SyntheticNamesByNetId.ContainsKey(netId);
        }
    }

    // Register/clear a display-name override for a real networked client (no host-local-seat side effects).
    public static void RegisterClientName(ulong netId, string displayName)
    {
        var name = NormalizeName(displayName)
            ?? throw new ArgumentException("Client name overrides require a non-empty display name.", nameof(displayName));
        lock (Sync)
        {
            ClientNameOverridesByNetId[netId] = name;
        }
    }

    public static void UnregisterClientName(ulong netId)
    {
        lock (Sync)
        {
            ClientNameOverridesByNetId.Remove(netId);
        }
    }

    // The single name-resolution entry point used by the GetPlayerNameRaw hook and Sts2LobbyNameResolver.
    // A synthetic host-local seat name wins; otherwise a real-client override applies; otherwise null
    // (callers fall through to the platform lookup, which returns the raw netId for headless peers).
    public static string? ResolveSyntheticName(ulong netId)
    {
        lock (Sync)
        {
            return SyntheticNamesByNetId.TryGetValue(netId, out var synthetic)
                ? synthetic
                : ClientNameOverridesByNetId.GetValueOrDefault(netId);
        }
    }

    public static ulong? FindSyntheticSeatByName(string displayName, IReadOnlySet<ulong>? activeNetIds = null)
    {
        var name = NormalizeName(displayName);
        if (name is null)
        {
            return null;
        }

        lock (Sync)
        {
            foreach (var (netId, candidate) in SyntheticNamesByNetId)
            {
                if ((activeNetIds is null || activeNetIds.Contains(netId))
                    && string.Equals(candidate, name, StringComparison.Ordinal))
                {
                    return netId;
                }
            }
        }

        return null;
    }

    private static string? NormalizeName(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
