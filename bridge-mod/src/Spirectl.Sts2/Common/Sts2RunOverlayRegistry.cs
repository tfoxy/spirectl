using Spirectl.Sts2.Core.State;
using Spirectl.Sts2.Live;

#pragma warning disable IDE0130
namespace Spirectl.Sts2;
#pragma warning restore IDE0130

internal static class Sts2RunOverlayRegistry
{
    private static readonly Lock Sync = new();
    private static readonly Dictionary<string, OverlayEntry> OverlaysById = [];
    private static long _nextSequence;

    public static bool IsObservableLocalOwner(ulong playerNetId, ulong? localNetId)
        => (localNetId.HasValue && playerNetId == localNetId.Value)
           || Sts2HostLocalSeatRegistry.IsHostLocalSeat(playerNetId);

    public static void Register(StateRunOverlaySnapshot overlay)
    {
        lock (Sync)
        {
            OverlaysById[overlay.Id] = new OverlayEntry(_nextSequence++, overlay);
        }

        // An overlay appearing changes what the player can see and act on, so a watching mirror should not have
        // to wait out an idle interval to notice. Accelerator only — see Sts2SemanticStateRevision.
        Sts2SemanticStateRevision.Bump();
    }

    public static void Unregister(string overlayId)
    {
        bool removed;
        lock (Sync)
        {
            removed = OverlaysById.Remove(overlayId);
        }

        if (removed)
        {
            Sts2SemanticStateRevision.Bump();
        }
    }

    public static IReadOnlyList<StateRunOverlaySnapshot> GetForPlayer(string playerId)
    {
        lock (Sync)
        {
            return [.. OverlaysById.Values
                .Where(entry => entry.Overlay.Id.StartsWith($"overlay:{playerId}:", StringComparison.Ordinal))
                .OrderBy(entry => entry.Sequence)
                .Select(entry => entry.Overlay)];
        }
    }

    public static void ClearForTest()
    {
        lock (Sync)
        {
            OverlaysById.Clear();
            _nextSequence = 0;
        }
    }

    private sealed record OverlayEntry(long Sequence, StateRunOverlaySnapshot Overlay);
}
