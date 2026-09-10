using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// The geoclip bake's SCENE-TREE PAUSE lever: the budget parse, the lease and its refusal table, the process-wide
// disarm latch, and the liveness assertion that stands between a paused bake and an artifact in which every frame
// is the same pose. Pure — the engine hops are injected — so all of it runs in the ordinary suite.
//
// THE LEVER IS DEFAULT OFF AND IS NOT ARMED THIS ROUND. GeoClipPauseVerdict.ArmingAllowed is false on every bake
// profile measured so far (blocking 0.30-0.39, parked 0.36-0.57, totals ~290-355 ms), and only a bake short
// enough to clear GeoClipPauseVerdict.SafeTotalMs could ever open that gate. What is being landed is a tested,
// documented, env-gated lever for a future where bakes are short enough — not an arm.
public sealed class Sts2SpineGeoClipPauseTests : IDisposable
{
    public Sts2SpineGeoClipPauseTests() => GeoClipPauseDisarm.ResetForTests();

    public void Dispose() => GeoClipPauseDisarm.ResetForTests();

    // ── The budget parse ─────────────────────────────────────────────────────────────────────────────

    // OFF unless explicitly affirmative — the opposite polarity from the frame-cap lever's kill-switch, because
    // the cost of a typo is different: a mis-typed frame cap costs a slower bake, a mis-typed pause would freeze
    // the game the player is looking at.
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("off", false)]
    [InlineData("no", false)]
    [InlineData("maybe", false)]
    [InlineData("yes please", false)]
    [InlineData("truthy", false)]
    [InlineData("1", true)]
    [InlineData("on", true)]
    [InlineData(" TRUE ", true)]
    [InlineData("Yes", true)]
    public void PauseBudget_ArmsOnlyOnAnExplicitAffirmative(string? raw, bool expected)
        => Assert.Equal(expected, GeoClipPauseBudget.Parse(raw, null).PauseTree);

    [Fact]
    public void PauseBudget_DefaultsToOffWithTheBoundsAssertion()
    {
        Assert.False(GeoClipPauseBudget.Disabled.PauseTree);
        Assert.False(GeoClipPauseBudget.Parse(null, null).PauseTree);
        Assert.Equal(GeoClipPauseAssertMode.Bounds, GeoClipPauseBudget.Parse(null, null).Assert);
        Assert.Equal("SPIRECTL_SPINE_GEOCLIP_BAKE_PAUSE_TREE", GeoClipPauseBudget.PauseEnv);
        Assert.Equal("SPIRECTL_SPINE_GEOCLIP_BAKE_PAUSE_ASSERT", GeoClipPauseBudget.AssertEnv);
    }

    // ASYMMETRIC ON PURPOSE: only a word the parser actually knows can turn the safety net off, so a typo lands
    // on the default rather than silently disabling the one check that stops a frozen rig shipping.
    // A [Fact] with an inline table rather than a [Theory]: the modes are `internal`, so they cannot appear in
    // the signature of a public xUnit test method at all.
    [Fact]
    public void PauseBudget_UnknownAssertModesFallBackToTheCheckRatherThanRemovingIt()
    {
        (string? Raw, GeoClipPauseAssertMode Expected)[] cases =
        [
            (null, GeoClipPauseAssertMode.Bounds),
            ("", GeoClipPauseAssertMode.Bounds),
            ("bounds", GeoClipPauseAssertMode.Bounds),
            ("  BOUNDS ", GeoClipPauseAssertMode.Bounds),
            ("strict", GeoClipPauseAssertMode.Strict),
            ("Strict", GeoClipPauseAssertMode.Strict),
            ("off", GeoClipPauseAssertMode.Off),
            ("0", GeoClipPauseAssertMode.Off),
            ("false", GeoClipPauseAssertMode.Off),
            ("no", GeoClipPauseAssertMode.Off),
            // The whole point: near-misses land on the CHECK, never on "no check".
            ("offf", GeoClipPauseAssertMode.Bounds),
            ("none", GeoClipPauseAssertMode.Bounds),
            ("disabled", GeoClipPauseAssertMode.Bounds),
            ("strictly", GeoClipPauseAssertMode.Bounds),
        ];

        foreach (var (raw, expected) in cases)
        {
            Assert.Equal(expected, GeoClipPauseBudget.ParseAssert(raw));
        }
    }

    // ── The lease's refusal table ────────────────────────────────────────────────────────────────────

    private sealed class TreeDouble(bool paused = false)
    {
        internal bool Paused { get; private set; } = paused;

        internal List<bool> Writes { get; } = [];

        internal int Reads { get; private set; }

        internal Exception? ThrowOnRead { get; set; }

        internal Exception? ThrowOnWrite { get; set; }

        internal bool Read()
        {
            Reads += 1;
            return ThrowOnRead is null ? Paused : throw ThrowOnRead;
        }

        internal void Write(bool paused)
        {
            Writes.Add(paused);
            if (ThrowOnWrite is not null)
            {
                // Thrown AFTER the write is recorded, so a test can see what the lease attempted.
                var pending = ThrowOnWrite;
                ThrowOnWrite = null;
                throw pending;
            }

            Paused = paused;
        }
    }

    private static GeoClipScenePauseLease Acquire(
        TreeDouble tree,
        string? arm = "1",
        Action? applyRigAlways = null)
        => GeoClipScenePauseLease.Acquire(
            GeoClipPauseBudget.Parse(arm, null),
            tree.Read,
            tree.Write,
            applyRigAlways);

    // The default-off path must not so much as READ the tree: a lever nobody armed has to be invisible, and a
    // read hop is still an engine hop.
    [Fact]
    public void ScenePauseLease_TouchesNothingWhenTheLeverIsOff()
    {
        var tree = new TreeDouble();
        var rigExempt = 0;

        using var lease = Acquire(tree, arm: null, applyRigAlways: () => rigExempt += 1);
        lease.Dispose();

        Assert.False(lease.Paused);
        Assert.Equal(0, tree.Reads);
        Assert.Empty(tree.Writes);
        Assert.Equal(0, rigExempt);
        Assert.Equal("off (SPIRECTL_SPINE_GEOCLIP_BAKE_PAUSE_TREE)", lease.Note);
    }

    [Fact]
    public void ScenePauseLease_PausesAndRestoresOnTheHappyPath()
    {
        var tree = new TreeDouble();
        var rigExempt = 0;

        using (var lease = Acquire(tree, applyRigAlways: () => rigExempt += 1))
        {
            Assert.True(lease.Paused);
            Assert.False(lease.PriorPaused);
            Assert.True(tree.Paused);
            Assert.Equal(1, rigExempt);
            Assert.Equal("running -> paused", lease.Note);
        }

        Assert.False(tree.Paused);
        Assert.Equal([true, false], tree.Writes);
    }

    // Disposal writes the value the read hop OBSERVED, out of the lease's own field. On today's two-state lever a
    // `true` prior is refused and never reaches disposal, so this and a `false` literal cannot be told apart by
    // behaviour — which is why the claims that actually defend the game are the two below, about a lease that did
    // not pause writing NOTHING. Pinned here as the shape, and stated as such rather than dressed up as proof.
    [Fact]
    public void ScenePauseLease_RestoresTheValueItObservedRatherThanRecomputingOne()
    {
        var tree = new TreeDouble();

        var lease = Acquire(tree);
        lease.Dispose();

        Assert.Equal(lease.PriorPaused, tree.Paused);
        Assert.Equal([true, lease.PriorPaused], tree.Writes);
    }

    // THE REFUSAL THAT MATTERS, half one: a bake cannot know WHY the game paused itself, so it must not pause on
    // top of that and start believing it owns the pause.
    [Fact]
    public void ScenePauseLease_DoesNotPauseAGameThatAlreadyPausedItself()
    {
        var tree = new TreeDouble(paused: true);
        var rigExempt = 0;

        using var lease = Acquire(tree, applyRigAlways: () => rigExempt += 1);

        Assert.False(lease.Paused);
        Assert.True(lease.PriorPaused);
        Assert.Empty(tree.Writes);
        Assert.Equal(0, rigExempt);
        Assert.Equal("already paused by the game", lease.Note);
    }

    // …and half two, which is the one with teeth: disposal must not hand control of the game back to a player who
    // never asked for it, in the middle of whatever paused it.
    [Fact]
    public void ScenePauseLease_DoesNotUnpauseAGameItDidNotPause()
    {
        var tree = new TreeDouble(paused: true);

        using (var lease = Acquire(tree))
        {
            Assert.False(lease.Paused);
        }

        Assert.True(tree.Paused);
        Assert.Empty(tree.Writes);
    }

    [Fact]
    public void ScenePauseLease_ReportsAnUnreadableTreeWithoutWritingToIt()
    {
        var tree = new TreeDouble { ThrowOnRead = new InvalidOperationException("no tree") };

        using var lease = Acquire(tree);

        Assert.False(lease.Paused);
        Assert.Empty(tree.Writes);
        Assert.Equal("unreadable (InvalidOperationException)", lease.Note);
    }

    // A write that throws may or may not have taken. The lease puts back what it READ either way, and reports the
    // failure instead of pretending it holds a pause.
    [Fact]
    public void ScenePauseLease_RestoresBestEffortWhenTheWriteThrows()
    {
        var tree = new TreeDouble { ThrowOnWrite = new NotSupportedException("read-only") };

        using var lease = Acquire(tree);

        Assert.False(lease.Paused);
        Assert.Equal("unwritable (NotSupportedException)", lease.Note);
        Assert.Equal([true, false], tree.Writes);
        Assert.False(tree.Paused);
    }

    // A pause whose rig exemption failed is worse than no pause: it froze the thing being measured. It is undone
    // on the spot rather than held.
    [Fact]
    public void ScenePauseLease_UnpausesWhenTheRigExemptionFails()
    {
        var tree = new TreeDouble();

        using var lease = Acquire(tree, applyRigAlways: () => throw new ArgumentException("no viewport"));

        Assert.False(lease.Paused);
        Assert.Equal("rig-exempt-failed (ArgumentException)", lease.Note);
        Assert.Equal([true, false], tree.Writes);
        Assert.False(tree.Paused);
    }

    [Fact]
    public void ScenePauseLease_DisposalIsIdempotentAndSwallows()
    {
        var tree = new TreeDouble();
        var lease = Acquire(tree);

        lease.Release();
        tree.ThrowOnWrite = new InvalidOperationException("the tree went away");
        lease.Dispose();
        lease.Dispose();
        lease.Release();

        // One write to pause, one to restore, and nothing after: the second and third disposals never reach the
        // hop that would have thrown.
        Assert.Equal([true, false], tree.Writes);
    }

    // A throwing restore must not take the host down with it — the bake's contract is that it never does.
    [Fact]
    public void ScenePauseLease_SwallowsAThrowingRestore()
    {
        var tree = new TreeDouble();
        var lease = Acquire(tree);
        tree.ThrowOnWrite = new InvalidOperationException("the tree went away");

        lease.Dispose();

        Assert.Equal([true, false], tree.Writes);
    }

    // The bake body throws on a rig it cannot read, and it must not leave the game paused when it does.
    [Fact]
    public void ScenePauseLease_RestoresOnTheThrowPathToo()
    {
        var tree = new TreeDouble();

        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using var lease = Acquire(tree);
            Assert.True(tree.Paused);
            throw new InvalidOperationException("the bake threw");
        }));

        Assert.False(tree.Paused);
    }

    // The note ships INSIDE the artifact (bake.pauseNote), so its vocabulary is checked for authoring
    // identifiers the way the artifact policy requires: env var names and exception type names only.
    [Fact]
    public void ScenePauseLease_NotesCarryNoAuthoringIdentifiers()
    {
        var notes = new List<string>
        {
            Acquire(new TreeDouble(), arm: null).Note,
            Acquire(new TreeDouble(paused: true)).Note,
            Acquire(new TreeDouble { ThrowOnRead = new InvalidOperationException() }).Note,
            Acquire(new TreeDouble { ThrowOnWrite = new NotSupportedException() }).Note,
            Acquire(new TreeDouble()).Note,
        };

        foreach (var note in notes)
        {
            Assert.DoesNotContain("res://", note, StringComparison.Ordinal);
            Assert.DoesNotContain(".tscn", note, StringComparison.Ordinal);
            Assert.DoesNotContain("/", note.Replace("res://", string.Empty, StringComparison.Ordinal));
        }
    }

    // ── The process-wide disarm latch ────────────────────────────────────────────────────────────────

    // ONE FAILURE COSTS ONE BAKE, NEVER A SWEEP. The deadline probe spends 500 ms discovering that a paused tree
    // emits no frame; a sweep of 40 rigs must not spend 20 seconds rediscovering it, so the refusal is a property
    // of the PROCESS. Without this, every bake in the run pays again.
    [Fact]
    public void Disarm_LatchedLeverRefusesWithoutTouchingTheTree()
    {
        var tree = new TreeDouble();
        Assert.True(GeoClipPauseDisarm.Latch("a paused tree emitted no ProcessFrame"));

        using var lease = Acquire(tree);

        Assert.False(lease.Paused);
        Assert.Equal(0, tree.Reads);
        Assert.Empty(tree.Writes);
        Assert.Equal("disarmed (a paused tree emitted no ProcessFrame)", lease.Note);
    }

    // The lever works right up until the latch, and never again after it. Stated as one sequence because "before"
    // and "after" being different is the whole claim.
    [Fact]
    public void Disarm_TheLeverWorksBeforeTheLatchAndNeverAfterIt()
    {
        using (var before = Acquire(new TreeDouble()))
        {
            Assert.True(before.Paused);
        }

        GeoClipPauseDisarm.Latch("refused");

        using var after = Acquire(new TreeDouble());
        Assert.False(after.Paused);
    }

    [Fact]
    public void Disarm_OnlyTheFirstLatchWinsSoTheLoudLineIsPrintedOnce()
    {
        Assert.True(GeoClipPauseDisarm.Latch("the first reason"));
        Assert.False(GeoClipPauseDisarm.Latch("a later reason"));

        Assert.True(GeoClipPauseDisarm.Latched);
        Assert.Equal("the first reason", GeoClipPauseDisarm.Reason);
    }

    // A refuted pause disarms on the same act — a liveness failure is never a reason to try again — and the count
    // survives, because the pass that failed writes no manifest and only LATER artifacts can carry the record.
    [Fact]
    public void Disarm_AnAssertFailureCountsAndDisarmsTogether()
    {
        Assert.Equal(0, GeoClipPauseDisarm.AssertFailures);

        Assert.True(GeoClipPauseDisarm.RecordAssertFailure("a paused tree froze the rig"));

        Assert.Equal(1, GeoClipPauseDisarm.AssertFailures);
        Assert.True(GeoClipPauseDisarm.Latched);
        Assert.Equal("a paused tree froze the rig", GeoClipPauseDisarm.Reason);
    }

    [Fact]
    public void Disarm_ResetPutsEverythingBack()
    {
        GeoClipPauseDisarm.RecordAssertFailure("a paused tree froze the rig");

        GeoClipPauseDisarm.ResetForTests();

        Assert.False(GeoClipPauseDisarm.Latched);
        Assert.Equal(string.Empty, GeoClipPauseDisarm.Reason);
        Assert.Equal(0, GeoClipPauseDisarm.AssertFailures);
    }

    // ── A1: the free bounds check ────────────────────────────────────────────────────────────────────

    // THE PIN. Equal bounds are a normal property of a LIVE rig — a skeleton's bounding box legitimately stays
    // put across two poses — so the cheap check may never read equality as health. Treating equal as a pass is
    // the single change that would make this whole lever decorative, and this is the test that refuses it.
    [Fact]
    public void BoundsCheck_EqualBoundsAreInconclusiveAndNeverAPass()
    {
        double[] bounds = [-10d, -20d, 100d, 200d];

        Assert.Equal(
            GeoClipPauseLiveness.Inconclusive,
            GeoClipPauseLivenessCheck.FromBounds(bounds, [.. bounds]));
    }

    [Theory]
    // One component moving is enough: the rig demonstrably re-posed.
    [InlineData(-10d, -20d, 100d, 200d, -9d, -20d, 100d, 200d)]
    [InlineData(-10d, -20d, 100d, 200d, -10d, -19d, 100d, 200d)]
    [InlineData(-10d, -20d, 100d, 200d, -10d, -20d, 101d, 200d)]
    [InlineData(-10d, -20d, 100d, 200d, -10d, -20d, 100d, 201d)]
    public void BoundsCheck_AnyMovedComponentPasses(
        double ax, double ay, double aw, double ah, double bx, double by, double bw, double bh)
        => Assert.Equal(
            GeoClipPauseLiveness.Passed,
            GeoClipPauseLivenessCheck.FromBounds([ax, ay, aw, ah], [bx, by, bw, bh]));

    // Missing, short and degenerate bounds are things the check cannot read, not things it may convict on. A
    // whole-clip bake never fills RestBounds at all, so the null case is the ordinary one rather than an edge.
    [Fact]
    public void BoundsCheck_UnreadableBoundsAreInconclusiveRatherThanAFailure()
    {
        double[] good = [0d, 0d, 100d, 100d];

        Assert.Equal(GeoClipPauseLiveness.Inconclusive, GeoClipPauseLivenessCheck.FromBounds(null, good));
        Assert.Equal(GeoClipPauseLiveness.Inconclusive, GeoClipPauseLivenessCheck.FromBounds(good, null));
        Assert.Equal(GeoClipPauseLiveness.Inconclusive, GeoClipPauseLivenessCheck.FromBounds([0d, 0d], good));
        Assert.Equal(
            GeoClipPauseLiveness.Inconclusive,
            GeoClipPauseLivenessCheck.FromBounds([0d, 0d, 0d, 100d], good));
        Assert.Equal(
            GeoClipPauseLiveness.Inconclusive,
            GeoClipPauseLivenessCheck.FromBounds(good, [0d, 0d, 100d, 0d]));
    }

    // The cheap check can confirm life and can never refute it. Asserted as a property over the whole reachable
    // input space rather than case by case, because "A1 returns Failed" is precisely the bug that would let a
    // healthy rig be thrown away.
    [Fact]
    public void BoundsCheck_NeverReturnsFailedForAnyInput()
    {
        // Written with explicit `new double[]` rather than collection expressions: inside a collection
        // initializer, a bare `[…]` element parses as an INDEXER assignment, not as an array.
        double[]?[] inputs =
        [
            null,
            new double[0],
            new[] { 1d },
            new[] { 0d, 0d, 0d, 0d },
            new[] { 0d, 0d, 100d, 100d },
            new[] { 1d, 2d, 3d, 4d },
            new[] { -5d, -5d, 10d, 10d },
        ];

        foreach (var first in inputs)
        {
            foreach (var second in inputs)
            {
                Assert.NotEqual(
                    GeoClipPauseLiveness.Failed,
                    GeoClipPauseLivenessCheck.FromBounds(first, second));
            }
        }
    }

    // ── A3: the two-frame checksum check ─────────────────────────────────────────────────────────────

    [Fact]
    public void ChecksumCheck_OneMovedMeshIsEnoughToPass()
        => Assert.Equal(
            GeoClipPauseLiveness.Passed,
            GeoClipPauseLivenessCheck.FromChecksums([1d, 2d, 3d], [1d, 2d, 3.5d]));

    // The one check that IS entitled to convict: it chose two deliberately different track times and read the
    // actual posed vertices of the meshes the artifact is built from. Identical is a frozen rig.
    [Fact]
    public void ChecksumCheck_IdenticalPositionsAtTwoDifferentTimesIsAFailure()
        => Assert.Equal(
            GeoClipPauseLiveness.Failed,
            GeoClipPauseLivenessCheck.FromChecksums([1d, 2d, 3d], [1d, 2d, 3d]));

    // A read that came back differently shaped is a broken READ, not a frozen rig, and convicting on it would
    // throw away a healthy bake because a mesh RID was recycled between the two frames.
    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 2)]
    [InlineData(2, 3)]
    public void ChecksumCheck_MismatchedOrEmptyReadsAreInconclusive(int firstCount, int secondCount)
        => Assert.Equal(
            GeoClipPauseLiveness.Inconclusive,
            GeoClipPauseLivenessCheck.FromChecksums(
                [.. Enumerable.Repeat(1d, firstCount)],
                [.. Enumerable.Repeat(1d, secondCount)]));

    [Fact]
    public void ChecksumCheck_NullReadsAreInconclusive()
    {
        Assert.Equal(GeoClipPauseLiveness.Inconclusive, GeoClipPauseLivenessCheck.FromChecksums(null, [1d]));
        Assert.Equal(GeoClipPauseLiveness.Inconclusive, GeoClipPauseLivenessCheck.FromChecksums([1d], null));
    }

    // ── Escalation and the verdict ───────────────────────────────────────────────────────────────────

    // Inline table for the same accessibility reason as the assert-mode parse above.
    [Fact]
    public void Escalation_TheExpensiveCheckRunsOnlyWhenTheCheapOneCouldNotAnswer()
    {
        (GeoClipPauseLiveness Bounds, GeoClipPauseAssertMode Mode, bool Expected)[] cases =
        [
            // A pass on the free check spends nothing more…
            (GeoClipPauseLiveness.Passed, GeoClipPauseAssertMode.Bounds, false),
            // …and an inconclusive one is exactly what buys the two frames.
            (GeoClipPauseLiveness.Inconclusive, GeoClipPauseAssertMode.Bounds, true),
            // Strict pays regardless, so a live arm can be graded without trusting the cheap check's coverage.
            (GeoClipPauseLiveness.Passed, GeoClipPauseAssertMode.Strict, true),
            (GeoClipPauseLiveness.Inconclusive, GeoClipPauseAssertMode.Strict, true),
            // Off is the operator saying they have already answered this on this build.
            (GeoClipPauseLiveness.Passed, GeoClipPauseAssertMode.Off, false),
            (GeoClipPauseLiveness.Inconclusive, GeoClipPauseAssertMode.Off, false),
        ];

        foreach (var (bounds, mode, expected) in cases)
        {
            Assert.Equal(expected, GeoClipPauseLivenessCheck.NeedsChecksumCheck(bounds, mode));
        }
    }

    // ONLY the expensive check can refute a pause. An inconclusive free check that was never escalated — because
    // the operator turned the assertion off — is not evidence of anything, and throwing a rig's whole pass away
    // on it would make ASSERT=off strictly more destructive than ASSERT=bounds.
    [Fact]
    public void Verdict_OnlyTheChecksumCheckCanRefuteThePause()
    {
        Assert.False(GeoClipPauseLivenessCheck.PauseIsRefuted(GeoClipPauseLiveness.Inconclusive, null));
        Assert.False(GeoClipPauseLivenessCheck.PauseIsRefuted(GeoClipPauseLiveness.Passed, null));
        Assert.False(
            GeoClipPauseLivenessCheck.PauseIsRefuted(
                GeoClipPauseLiveness.Inconclusive, GeoClipPauseLiveness.Inconclusive));
        Assert.False(
            GeoClipPauseLivenessCheck.PauseIsRefuted(GeoClipPauseLiveness.Inconclusive, GeoClipPauseLiveness.Passed));

        Assert.True(
            GeoClipPauseLivenessCheck.PauseIsRefuted(
                GeoClipPauseLiveness.Inconclusive, GeoClipPauseLiveness.Failed));

        // Even a free check that passed is overruled by the measurement that actually read the geometry.
        Assert.True(
            GeoClipPauseLivenessCheck.PauseIsRefuted(GeoClipPauseLiveness.Passed, GeoClipPauseLiveness.Failed));
    }

    // ── Choosing the two times, and the meshes to read ───────────────────────────────────────────────

    [Fact]
    public void ChecksumTimes_SeekBetweenZeroAndThePoseWhenThePoseIsNotZero()
    {
        var times = GeoClipPauseLivenessCheck.ChooseChecksumTimes(0.75d, 1.5d);

        Assert.NotNull(times);
        Assert.Equal(0d, times!.Value.First);
        Assert.Equal(0.75d, times.Value.Second);
    }

    // A pose sampled AT zero cannot be contrasted with zero, so the mid point of the clip stands in.
    [Fact]
    public void ChecksumTimes_FallBackToTheClipMidPointWhenThePoseIsAtZero()
    {
        var times = GeoClipPauseLivenessCheck.ChooseChecksumTimes(0d, 2d);

        Assert.NotNull(times);
        Assert.Equal(0d, times!.Value.First);
        Assert.Equal(1d, times.Value.Second);
    }

    // A zero-length clip sampled at zero offers no two distinguishable times. "The vertices did not change" would
    // be the CORRECT answer there, so the check must decline to run rather than convict a healthy rig.
    [Theory]
    [InlineData(0d, 0d)]
    [InlineData(0d, -1d)]
    public void ChecksumTimes_DeclineRatherThanConvictWhenNoTwoTimesDiffer(double pose, double duration)
        => Assert.Null(GeoClipPauseLivenessCheck.ChooseChecksumTimes(pose, duration));

    [Fact]
    public void LargestMeshes_TakesTheBiggestAndBreaksTiesDeterministically()
    {
        var counts = new Dictionary<ulong, int>
        {
            [10] = 4, [11] = 90, [12] = 40, [13] = 90, [14] = 0,
        };

        // 11 and 13 tie on size and are ordered by RID, so two runs of the same bake read the same meshes and a
        // disagreement between them is about the rig rather than about which meshes were sampled.
        Assert.Equal([11ul, 13ul, 12ul], GeoClipPauseLivenessCheck.LargestMeshes(counts, 3));
    }

    // A mesh that read back with no vertices cannot answer the question either way, so it is never selected —
    // otherwise a rig with three empty meshes would produce two identical checksum lists and convict itself.
    [Fact]
    public void LargestMeshes_DropsMeshesThatReadBackEmpty()
    {
        var counts = new Dictionary<ulong, int> { [1] = 0, [2] = 0, [3] = 7 };

        Assert.Equal([3ul], GeoClipPauseLivenessCheck.LargestMeshes(counts, 3));
        Assert.Empty(GeoClipPauseLivenessCheck.LargestMeshes(new Dictionary<ulong, int> { [1] = 0 }, 3));
        Assert.Empty(GeoClipPauseLivenessCheck.LargestMeshes(counts, 0));
    }

    // ── The arming gate this lever still has to pass ─────────────────────────────────────────────────

    // WHY THE DEFAULT IS OFF, restated against the numbers rather than against an opinion. These are the profiles
    // measured on both available rigs this round; the gate that would authorize the lever is false on every one
    // of them, which is the reason this workstream ships a lever and not an arm.
    [Theory]
    // totalMs, blockingMs, parkedMs — the measured envelope: blocking 0.30-0.39, parked 0.36-0.57.
    [InlineData(290d, 87d, 165d)]
    [InlineData(355d, 138d, 128d)]
    [InlineData(320d, 112d, 148d)]
    public void ArmingGate_IsClosedOnEveryProfileMeasuredSoFar(double totalMs, double blockingMs, double parkedMs)
    {
        var profile = new GeoClipBakeProfile(totalMs, blockingMs, parkedMs, 0, 0, 0, []);

        Assert.False(GeoClipPauseVerdict.ArmingAllowed(profile));
        Assert.True(
            totalMs > GeoClipPauseVerdict.SafeTotalMs,
            "if a measured bake ever comes in under the short-bake threshold, this lever's gate can open and "
            + "this test is the place that says so");
    }
}
