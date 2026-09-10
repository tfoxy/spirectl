namespace Spirectl.Sts2.Live;

// R14 — the Godot-free half of a PARKED CARD-FLIGHT RESOLVE: one flight's resolve request, held until the watcher
// has actually tracked the node the hint is about.
//
// THE RACE THIS EXISTS TO CLOSE. Both card-flight producers publish from a `_Ready` hook deferred by one idle
// flush, and the hint can only be built once the watcher's node registry knows the mover — the registry is filled
// exclusively by the capture walk's reconcile, which is timer-clocked AND interval-gated. On a bench whose walk
// runs at ~50Hz the deferred publish usually lands after a reconcile and resolves; on a real host under viewer load
// it usually lands BEFORE one, and the hint is lost for good. A live host measured a 100% loss of shuffle flights
// this way (every deferred resolve found an untracked node) while the same build on a fast walk kept ~83% of them.
//
// The producer cannot simply retry on its own clock: it has no signal for "the registry changed". So a resolve that
// misses the registry is PARKED here, and the watcher DRAINS the park immediately after each reconcile — the one
// moment the answer can have changed — publishing from there. A parked resolve is therefore a one-slot-per-mover
// IOU with a short TTL, never a queue: an entry either resolves within a few reconciles or is dropped.
//
// WHAT MAY AND MAY NOT BE PARKED. Every scalar carried here is drawn ONCE when the flight is created and never
// changes (the two anchors, the arc's control point, the integrator's speed/accel/duration, the spawn scale), so a
// late replay of them is exactly as correct as an immediate one. What must NOT be carried is anything derived from
// the mover node's LIVE pose — its basis/rotation — because the node may have been animated between the park and
// the drain. Those are re-read at drain time by the watcher's tracked-resolve core, which is why the park stores
// only plain numbers and instance ids and holds no node references at all (so it can never keep a freed node alive,
// and so this whole contract is unit-testable without the engine).
internal enum Sts2FlightFamily
{
    /// <summary>The discard→draw shuffle sweep: the mover is the throwaway flight VFX the game just spawned.</summary>
    Shuffle,

    /// <summary>The hand→discard fly: the mover is the REAL card node the watcher has been tracking all along.</summary>
    Discard,
}

// One parked resolve request. `MoverInstanceId` is the key (the node the hint targets and the node whose tracking
// the retry waits on); `ParkedAtMs`/`DeadlineMs` are wall clock (Environment.TickCount64 on the live path, plain
// numbers in tests).
internal readonly record struct Sts2ParkedFlightResolve(
    Sts2FlightFamily Family,
    ulong MoverInstanceId,
    ulong TrailInstanceId,
    IReadOnlyList<ulong> TrailStrokeInstanceIds,
    double StartX,
    double StartY,
    double EndX,
    double EndY,
    double ControlX,
    double ControlY,
    double Speed0,
    double Accel,
    double Duration,
    double Scale0,
    long ParkedAtMs,
    long DeadlineMs)
{
    // Two requests for the SAME mover collapse into one slot. The NEWER payload wins outright — it was drawn from a
    // newer spawn on that instance id, so the older one's curve is describing an animation that no longer exists —
    // but the deadline is never SHORTENED, matching the parked-window rule next door (Sts2PendingFlightWindow):
    // whichever request is willing to wait longer for the node to appear sets how long the slot lives.
    internal static Sts2ParkedFlightResolve Merge(Sts2ParkedFlightResolve existing, Sts2ParkedFlightResolve incoming)
        => incoming with
        {
            DeadlineMs = existing.DeadlineMs >= incoming.DeadlineMs ? existing.DeadlineMs : incoming.DeadlineMs,
        };
}

// The bounded park itself: one slot per mover instance id, swept by TTL.
//
// SIZING. The cap is small on purpose. A parked entry is only useful for the handful of reconciles between a
// flight's spawn and its first tracking, and the worst honest burst is one shuffle (~30 cards, spawned over several
// frames, each entry living ~one reconcile). A park that grew past the cap would mean the drain has stopped
// running, and in that state holding MORE stale geometry helps nobody — so the cap refuses, the caller counts the
// refusal, and that flight simply keeps streaming (the pre-R14 behaviour, fail-open).
//
// The TTL is the other half of the same guarantee: a mover that never gets tracked (a shuffle cancelled the same
// frame, a node freed before the walk reached it) must not sit in a slot. 500ms is generous next to the ~16-128ms
// capture cadence — several reconciles' worth of retries — and short enough that a dropped entry is dropped while
// the animation it describes is still playing rather than long after it ended.
internal sealed class Sts2CardFlightResolveParkStore
{
    internal const int CardFlightResolveParkCap = 64;
    internal const long ResolveParkTtlMs = 500;

    private readonly Dictionary<ulong, Sts2ParkedFlightResolve> _parked = [];

    internal int Count => _parked.Count;

    /// <summary>The TTL deadline a resolve parked at <paramref name="nowMs"/> gets.</summary>
    internal static long DeadlineFrom(long nowMs) => nowMs + ResolveParkTtlMs;

    /// <summary>
    /// Park one resolve request, merging into an existing slot for the same mover. False = REFUSED because the park
    /// is at its cap (a brand-new mover only); the caller counts it and falls back to streaming. Deliberately does
    /// not sweep on refusal: every park is followed by a drain (a flight spawn dirties the tree, so a reconcile is
    /// always pending), and the drain is where expiry belongs.
    /// </summary>
    internal bool TryPark(Sts2ParkedFlightResolve entry)
    {
        if (_parked.TryGetValue(entry.MoverInstanceId, out var existing))
        {
            _parked[entry.MoverInstanceId] = Sts2ParkedFlightResolve.Merge(existing, entry);
            return true;
        }

        if (_parked.Count >= CardFlightResolveParkCap)
        {
            return false;
        }

        _parked[entry.MoverInstanceId] = entry;
        return true;
    }

    /// <summary>
    /// Drop every entry whose TTL has passed and return how many went, optionally collecting them so the caller can
    /// attribute each drop to the right producer family.
    /// </summary>
    internal int Sweep(long nowMs, List<Sts2ParkedFlightResolve>? expired = null)
    {
        if (_parked.Count == 0)
        {
            return 0;
        }

        List<ulong>? ids = null;
        foreach (var (id, entry) in _parked)
        {
            if (entry.DeadlineMs <= nowMs)
            {
                (ids ??= []).Add(id);
                expired?.Add(entry);
            }
        }

        if (ids is null)
        {
            return 0;
        }

        foreach (var id in ids)
        {
            _parked.Remove(id);
        }

        return ids.Count;
    }

    /// <summary>
    /// The entries still worth retrying at <paramref name="nowMs"/>, as a COPY — the caller removes resolved slots
    /// while iterating. Allocation-free while the park is empty, which is every tick outside a flight.
    /// </summary>
    internal IReadOnlyList<Sts2ParkedFlightResolve> SnapshotLive(long nowMs)
    {
        if (_parked.Count == 0)
        {
            return [];
        }

        List<Sts2ParkedFlightResolve>? live = null;
        foreach (var entry in _parked.Values)
        {
            if (entry.DeadlineMs > nowMs)
            {
                (live ??= []).Add(entry);
            }
        }

        return live is null ? [] : live;
    }

    internal bool Remove(ulong moverInstanceId) => _parked.Remove(moverInstanceId);

    internal void Clear() => _parked.Clear();
}
