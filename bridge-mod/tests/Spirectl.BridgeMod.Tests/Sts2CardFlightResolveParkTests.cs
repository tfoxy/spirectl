using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// R14 — the parked card-flight RESOLVE store. A flight's hint can only be built once the watcher has tracked the
// node it describes, and the producer's `_Ready` publish frequently runs before that has happened; the request waits
// here until the next reconcile can answer it. These tests pin the four properties the watcher's drain depends on:
// a second request for the same mover replaces the payload without ever shortening the wait, an entry that is never
// claimed expires instead of leaking, a full park refuses rather than evicting a live entry, and a scene rebuild
// empties the whole thing.
//
// All times are plain numbers (the live path feeds Environment.TickCount64), so nothing here sleeps or is flaky.
public sealed class Sts2CardFlightResolveParkTests
{
    private const ulong Mover = 4001;
    private const ulong OtherMover = 4002;

    [Fact]
    public void Merge_KeepsTheNewerPayload()
    {
        var parked = Entry(Mover, duration: 1.0, deadlineMs: 1_000);
        var incoming = Entry(Mover, duration: 1.75, deadlineMs: 1_000);

        var merged = Sts2ParkedFlightResolve.Merge(parked, incoming);

        // The older request describes an animation that no longer exists on this instance id, so every scalar comes
        // from the newer one — not just the ones that happen to differ.
        Assert.Equal(1.75, merged.Duration);
        Assert.Equal(incoming.Speed0, merged.Speed0);
        Assert.Equal(incoming.StartX, merged.StartX);
        Assert.Equal(incoming.ParkedAtMs, merged.ParkedAtMs);
        Assert.Equal(incoming.Family, merged.Family);
    }

    [Fact]
    public void Merge_NeverShortensTheDeadline()
    {
        var patient = Entry(Mover, deadlineMs: 4_000);
        var hasty = Entry(Mover, deadlineMs: 1_000);

        // Whichever side is willing to wait longer for the node to appear sets how long the slot lives, and the rule
        // must not depend on which of the two arrived first.
        Assert.Equal(4_000, Sts2ParkedFlightResolve.Merge(patient, hasty).DeadlineMs);
        Assert.Equal(4_000, Sts2ParkedFlightResolve.Merge(hasty, patient).DeadlineMs);
    }

    [Fact]
    public void TryPark_MergesASecondRequestForTheSameMoverInsteadOfAddingASlot()
    {
        var store = new Sts2CardFlightResolveParkStore();

        Assert.True(store.TryPark(Entry(Mover, duration: 1.0, deadlineMs: 4_000)));
        Assert.True(store.TryPark(Entry(Mover, duration: 1.75, deadlineMs: 1_000)));

        Assert.Equal(1, store.Count);
        var live = Assert.Single(store.SnapshotLive(nowMs: 0));
        Assert.Equal(1.75, live.Duration);
        Assert.Equal(4_000, live.DeadlineMs);
    }

    [Fact]
    public void TryPark_RefusesANewMoverAtTheCap()
    {
        var store = new Sts2CardFlightResolveParkStore();
        for (var i = 0; i < Sts2CardFlightResolveParkStore.CardFlightResolveParkCap; i++)
        {
            Assert.True(store.TryPark(Entry((ulong)(9_000 + i), deadlineMs: 10_000)));
        }

        // Full of LIVE entries: the newcomer is refused (its flight keeps streaming) rather than evicting someone
        // else's still-resolvable request.
        Assert.False(store.TryPark(Entry(Mover, deadlineMs: 10_000)));
        Assert.Equal(Sts2CardFlightResolveParkStore.CardFlightResolveParkCap, store.Count);

        // …but a repeat of an ALREADY parked mover is a merge, not a new slot, so the cap can't block it.
        Assert.True(store.TryPark(Entry(9_000, deadlineMs: 12_000)));
        Assert.Equal(Sts2CardFlightResolveParkStore.CardFlightResolveParkCap, store.Count);
    }

    [Fact]
    public void Sweep_DropsExpiredEntriesAndReportsThemForAttribution()
    {
        var store = new Sts2CardFlightResolveParkStore();
        store.TryPark(Entry(Mover, deadlineMs: 1_000, family: Sts2FlightFamily.Shuffle));
        store.TryPark(Entry(OtherMover, deadlineMs: 5_000, family: Sts2FlightFamily.Discard));

        var expired = new List<Sts2ParkedFlightResolve>();
        Assert.Equal(1, store.Sweep(nowMs: 1_000, expired));

        // The deadline is inclusive-expired, and the swept entry is handed back so the caller can charge the drop to
        // the right producer family.
        Assert.Equal(1, store.Count);
        Assert.Equal(Sts2FlightFamily.Shuffle, Assert.Single(expired).Family);
        Assert.Equal(OtherMover, Assert.Single(store.SnapshotLive(nowMs: 1_000)).MoverInstanceId);

        // Nothing left to expire ⇒ no work, no collection.
        Assert.Equal(0, store.Sweep(nowMs: 1_000, expired));
        Assert.Single(expired);
    }

    [Fact]
    public void SnapshotLive_ExcludesExpiredEntriesEvenBeforeTheyAreSwept()
    {
        var store = new Sts2CardFlightResolveParkStore();
        store.TryPark(Entry(Mover, deadlineMs: 1_000));
        store.TryPark(Entry(OtherMover, deadlineMs: 5_000));

        // The drain must never resolve a hint for an animation whose window has already passed, so the snapshot
        // filters on its own rather than trusting a sweep to have run first.
        var live = store.SnapshotLive(nowMs: 2_000);
        Assert.Equal(OtherMover, Assert.Single(live).MoverInstanceId);
        Assert.Equal(2, store.Count);

        Assert.Empty(store.SnapshotLive(nowMs: 9_000));
    }

    [Fact]
    public void SnapshotLive_IsACopyTheCallerMayRemoveFrom()
    {
        var store = new Sts2CardFlightResolveParkStore();
        store.TryPark(Entry(Mover, deadlineMs: 5_000));
        store.TryPark(Entry(OtherMover, deadlineMs: 5_000));

        // The drain removes each slot as it resolves it, WHILE iterating this list.
        var live = store.SnapshotLive(nowMs: 0);
        foreach (var entry in live)
        {
            Assert.True(store.Remove(entry.MoverInstanceId));
        }

        Assert.Equal(2, live.Count);
        Assert.Equal(0, store.Count);
        Assert.False(store.Remove(Mover));
    }

    [Fact]
    public void Clear_EmptiesThePark()
    {
        var store = new Sts2CardFlightResolveParkStore();
        store.TryPark(Entry(Mover, deadlineMs: 5_000));
        store.TryPark(Entry(OtherMover, deadlineMs: 5_000));

        // The watched scene root changing invalidates every parked mover at once (their nodes went away with it).
        store.Clear();

        Assert.Equal(0, store.Count);
        Assert.Empty(store.SnapshotLive(nowMs: 0));
    }

    [Fact]
    public void DeadlineFrom_AddsTheTtlToTheParkInstant()
    {
        Assert.Equal(
            1_000 + Sts2CardFlightResolveParkStore.ResolveParkTtlMs,
            Sts2CardFlightResolveParkStore.DeadlineFrom(1_000));

        // Several reconciles' worth of retries at the watcher's 16-128ms capture cadence, and short enough that a
        // dropped entry is dropped while its animation is still on screen.
        Assert.True(Sts2CardFlightResolveParkStore.ResolveParkTtlMs >= 128);
    }

    private static Sts2ParkedFlightResolve Entry(
        ulong moverInstanceId,
        long deadlineMs = 1_000,
        double duration = 1.5,
        Sts2FlightFamily family = Sts2FlightFamily.Shuffle) =>
        new(
            Family: family,
            MoverInstanceId: moverInstanceId,
            TrailInstanceId: moverInstanceId + 1,
            TrailStrokeInstanceIds: [moverInstanceId + 2, moverInstanceId + 3],
            StartX: 100 + duration,
            StartY: 200,
            EndX: 900,
            EndY: 300,
            ControlX: 500,
            ControlY: -100,
            Speed0: 1.1 + duration,
            Accel: 2.25,
            Duration: duration,
            Scale0: 1.0,
            ParkedAtMs: deadlineMs - Sts2CardFlightResolveParkStore.ResolveParkTtlMs,
            DeadlineMs: deadlineMs);
}
