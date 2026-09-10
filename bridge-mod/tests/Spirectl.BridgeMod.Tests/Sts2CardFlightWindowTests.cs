using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// R13 — the parked card-flight suppression window (Sts2PendingFlightWindow). A window is armed from a `_Ready`
// hook, before the nodes it names have ever been reconciled, so it waits in one slot per node until the watcher
// first sees them. These tests pin the two properties the watcher relies on when two requests collide in that slot:
// the freeze is never made SHORTER, and it is never made NARROWER.
public sealed class Sts2CardFlightWindowTests
{
    [Fact]
    public void Merge_KeepsTheLaterDeadline_WhicheverSideItCameFrom()
    {
        var early = new Sts2PendingFlightWindow(1_000, SelfOnly: false);
        var late = new Sts2PendingFlightWindow(4_300, SelfOnly: false);

        Assert.Equal(4_300, Sts2PendingFlightWindow.Merge(early, late).Deadline);
        Assert.Equal(4_300, Sts2PendingFlightWindow.Merge(late, early).Deadline);
    }

    [Fact]
    public void Merge_NeverShortensEvenWhenTheReachDiffers()
    {
        // A short subtree freeze already parked, then a longer self-only one: the deadline must still grow.
        var parked = new Sts2PendingFlightWindow(1_000, SelfOnly: false);
        var incoming = new Sts2PendingFlightWindow(4_300, SelfOnly: true);

        Assert.Equal(4_300, Sts2PendingFlightWindow.Merge(parked, incoming).Deadline);
        Assert.Equal(4_300, Sts2PendingFlightWindow.Merge(incoming, parked).Deadline);
    }

    [Fact]
    public void Merge_KeepsSelfOnly_OnlyWhenBothSidesAskedForIt()
    {
        var self = new Sts2PendingFlightWindow(2_000, SelfOnly: true);
        var subtree = new Sts2PendingFlightWindow(2_000, SelfOnly: false);

        Assert.True(Sts2PendingFlightWindow.Merge(self, self).SelfOnly);

        // A subtree request must not be narrowed to self-only by a later self-only one (or by arriving second):
        // narrowing would silently un-freeze descendants the caller asked to freeze.
        Assert.False(Sts2PendingFlightWindow.Merge(self, subtree).SelfOnly);
        Assert.False(Sts2PendingFlightWindow.Merge(subtree, self).SelfOnly);
        Assert.False(Sts2PendingFlightWindow.Merge(subtree, subtree).SelfOnly);
    }

    [Fact]
    public void Merge_IsIdempotentForARepeatedRequest()
    {
        var window = new Sts2PendingFlightWindow(3_800, SelfOnly: true);
        Assert.Equal(window, Sts2PendingFlightWindow.Merge(window, window));
    }
}
