namespace Spirectl.Sts2.Live;

// R13 — the Godot-free half of a card flight's PARKED streaming-suppression window.
//
// A flight is resolved from a `_Ready` hook, i.e. while the nodes it wants to freeze are still being added to the
// tree: the watcher has not tracked them yet, so the window is parked here and adopted the first time each node is
// reconciled (see Sts2RuntimeSceneWatcher._pendingFlightWindows). Two REACHES exist — a SUBTREE window (the node and
// everything below it, carried by the capture walk's depth sentinel) and a SELF-ONLY window (the node's own
// transform writes and nothing else) — so a parked entry has to say which one it is.
//
// The merge rule below is the parked twin of "an open window is never shortened": one slot per node, so two requests
// for the same not-yet-tracked node collapse into the one that withholds MORE — the later deadline, and the wider
// reach if they disagree. Widening beats the alternative (silently dropping a subtree request behind a self-only
// one), and in practice never fires: a flight's subtree windows and its self-only window land on disjoint nodes.
internal readonly record struct Sts2PendingFlightWindow(long Deadline, bool SelfOnly)
{
    internal static Sts2PendingFlightWindow Merge(Sts2PendingFlightWindow existing, Sts2PendingFlightWindow incoming)
        => new(
            Deadline: existing.Deadline >= incoming.Deadline ? existing.Deadline : incoming.Deadline,
            SelfOnly: existing.SelfOnly && incoming.SelfOnly);
}
