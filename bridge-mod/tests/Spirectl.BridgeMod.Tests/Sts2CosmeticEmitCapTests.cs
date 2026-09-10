using Spirectl.Sts2.Core.SceneInspection;
using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// R11 producer cosmetic-emit cap pacing (Sts2CosmeticEmitCap.Decide) — a pure, Godot-free exercise of the sustained
// per-frame transform-churn cap the watcher applies before ApplyIfChanged. Verifies: sustained churn pins inside the
// window and emits (restarting the window) once it elapses; a one-shot (non-sustained) move streams instantly; churn
// exit resets the streak so the settle frame passes through; and cap=0 disables the pacing entirely.
public sealed class Sts2CosmeticEmitCapTests
{
    private static RuntimeSceneTransform2DSnapshot Xf(double ox, double oy)
        => new(
            new RuntimeSceneVector2Snapshot(1, 0),
            new RuntimeSceneVector2Snapshot(0, 1),
            new RuntimeSceneVector2Snapshot(ox, oy));

    // Drive Decide across a sequence of real transforms, returning the decision + streak each tick. `moves` is the real
    // origin per tick; the caller supplies `now` per tick and a fixed cap/K. Mirrors the watcher's caller: PrevReal is
    // advanced to the real read every tick, LastEmitAt is updated only on Emit.
    private static Sts2CosmeticEmitCap.Decision Step(
        ref RuntimeSceneTransform2DSnapshot? prev, ref int streak, ref long lastEmit,
        RuntimeSceneTransform2DSnapshot cur, long now, int capMs, int k)
    {
        var d = Sts2CosmeticEmitCap.Decide(prev, cur, streak, lastEmit, now, capMs, k, out var s);
        streak = s;
        prev = cur;
        if (d == Sts2CosmeticEmitCap.Decision.Emit)
        {
            lastEmit = now;
        }

        return d;
    }

    [Fact]
    public void SustainedChurn_PacesToOneEmitPerWindow()
    {
        // A node moving every tick (dt=33ms, cap=100ms, K=3). After the K-tick warmup (which streams instantly) the cap
        // engages: it EMITs then PINs until 100ms elapse, i.e. one emit roughly every 3 ticks.
        RuntimeSceneTransform2DSnapshot? prev = null;
        int streak = 0;
        long lastEmit = 0;
        var decisions = new System.Collections.Generic.List<Sts2CosmeticEmitCap.Decision>();
        for (var i = 0; i < 12; i++)
        {
            long now = 1000 + i * 33;
            var d = Step(ref prev, ref streak, ref lastEmit, Xf(i, 0), now, capMs: 100, k: 3);
            decisions.Add(d);
        }

        // i=0 has no prior real transform → Pass (streak stays 0). i=1,2 move but streak (1,2) < K → Pass (instant).
        // i=3 reaches streak==K with lastEmit=0 → Emit. Then Pin until the 100ms window elapses, then Emit again — so
        // after warmup we see a repeating Emit,Pin,Pin,Pin,Emit,... cadence (~one emit per 100ms).
        Assert.Equal(Sts2CosmeticEmitCap.Decision.Pass, decisions[0]);
        Assert.Equal(Sts2CosmeticEmitCap.Decision.Pass, decisions[1]);
        Assert.Equal(Sts2CosmeticEmitCap.Decision.Pass, decisions[2]);
        Assert.Equal(Sts2CosmeticEmitCap.Decision.Emit, decisions[3]);
        // Steady state: at most one Emit per 100ms window → far fewer Emits than the 12 uncapped ticks.
        var emits = decisions.FindAll(d => d == Sts2CosmeticEmitCap.Decision.Emit).Count;
        var pins = decisions.FindAll(d => d == Sts2CosmeticEmitCap.Decision.Pin).Count;
        Assert.True(emits <= 5, $"expected paced (<=5) emits over 12 ticks, got {emits}");
        Assert.True(pins >= 5, $"expected the withheld motion to be pinned, got {pins} pins");
    }

    [Fact]
    public void OneShotMove_StreamsInstantly_NeverPinned()
    {
        // A node that moves for only 2 ticks (a card snap) then stops — with K=10 it never reaches the sustained
        // threshold, so every moving tick PASSES (instant, never delayed) and the stationary ticks reset the streak.
        RuntimeSceneTransform2DSnapshot? prev = null;
        int streak = 0;
        long lastEmit = 0;
        Assert.Equal(Sts2CosmeticEmitCap.Decision.Pass, Step(ref prev, ref streak, ref lastEmit, Xf(0, 0), 1000, 100, 10));
        Assert.Equal(Sts2CosmeticEmitCap.Decision.Pass, Step(ref prev, ref streak, ref lastEmit, Xf(5, 0), 1033, 100, 10)); // moved (streak 1)
        Assert.Equal(Sts2CosmeticEmitCap.Decision.Pass, Step(ref prev, ref streak, ref lastEmit, Xf(9, 0), 1066, 100, 10)); // moved (streak 2)
        Assert.Equal(Sts2CosmeticEmitCap.Decision.Pass, Step(ref prev, ref streak, ref lastEmit, Xf(9, 0), 1099, 100, 10)); // stationary → streak reset
        Assert.Equal(0, streak);
    }

    [Fact]
    public void ChurnExit_ResetsStreak_SoSettleFramePasses()
    {
        // Sustained churn (K=3) drives the cap into Pin, then the node stops moving: the stationary tick must PASS with
        // streak 0 so the watcher's own change test ships the exact final transform (never leaving the stale pin).
        RuntimeSceneTransform2DSnapshot? prev = null;
        int streak = 0;
        long lastEmit = 0;
        for (var i = 0; i < 6; i++)
        {
            Step(ref prev, ref streak, ref lastEmit, Xf(i, 0), 1000 + i * 33, capMs: 100, k: 3);
        }

        Assert.True(streak >= 3, "expected the node to be in sustained churn before it stops");
        // Now it settles at the same value → churn breaks.
        var settle = Xf(5, 0);
        var d = Step(ref prev, ref streak, ref lastEmit, settle, now: 1000 + 6 * 33, capMs: 100, k: 3);
        Assert.Equal(Sts2CosmeticEmitCap.Decision.Pass, d);
        Assert.Equal(0, streak);
    }

    [Fact]
    public void WithinWindow_Pins_AfterSustained()
    {
        // Directly: streak already at K, last emit 50ms ago (< 100ms cap), transform moved → Pin.
        var d = Sts2CosmeticEmitCap.Decide(Xf(0, 0), Xf(1, 0), streakBefore: 10, lastEmitAtMs: 1000, now: 1050, capMs: 100, k: 10, out var s);
        Assert.Equal(Sts2CosmeticEmitCap.Decision.Pin, d);
        Assert.Equal(11, s);
    }

    [Fact]
    public void WindowElapsed_Emits_AndBoundaryEmits()
    {
        // 120ms since last emit ≥ 100ms cap → Emit; exactly-at-window (100ms) is NOT < cap → Emit (never stalls).
        Assert.Equal(Sts2CosmeticEmitCap.Decision.Emit,
            Sts2CosmeticEmitCap.Decide(Xf(0, 0), Xf(1, 0), 10, 1000, 1120, 100, 10, out _));
        Assert.Equal(Sts2CosmeticEmitCap.Decision.Emit,
            Sts2CosmeticEmitCap.Decide(Xf(0, 0), Xf(1, 0), 10, 1000, 1100, 100, 10, out _));
    }

    [Fact]
    public void StationaryTick_Passes_EvenWhenSustained()
    {
        // A node whose transform did NOT move (within epsilon) → Pass + streak reset, regardless of the prior streak.
        var d = Sts2CosmeticEmitCap.Decide(Xf(4, 4), Xf(4, 4), streakBefore: 50, lastEmitAtMs: 1000, now: 1010, capMs: 100, k: 10, out var s);
        Assert.Equal(Sts2CosmeticEmitCap.Decision.Pass, d);
        Assert.Equal(0, s);
    }

    [Fact]
    public void SubThresholdMotion_StillCountsAsChurn()
    {
        // Motion far below the watcher's 2-dp emit threshold (0.005) but above the cap's fine epsilon still counts as
        // churn — this is what keeps a flame that only crosses 2-dp intermittently recognised as continuously churning.
        var d = Sts2CosmeticEmitCap.Decide(Xf(0, 0), Xf(0.005, 0), streakBefore: 10, lastEmitAtMs: 1000, now: 1050, capMs: 100, k: 10, out var s);
        Assert.Equal(Sts2CosmeticEmitCap.Decision.Pin, d);
        Assert.Equal(11, s);
    }

    [Fact]
    public void NullEndpoints_Pass()
    {
        // First tick (no prior real transform) passes and leaves the streak at 0.
        Assert.Equal(Sts2CosmeticEmitCap.Decision.Pass,
            Sts2CosmeticEmitCap.Decide(null, Xf(0, 0), 0, 0, 1000, 100, 10, out var s1));
        Assert.Equal(0, s1);
        Assert.Equal(Sts2CosmeticEmitCap.Decision.Pass,
            Sts2CosmeticEmitCap.Decide(Xf(0, 0), null, 5, 1000, 1010, 100, 10, out var s2));
        Assert.Equal(0, s2);
    }

    [Fact]
    public void CapDisabled_Passes_ByteIdentical()
    {
        // capMs=0 (SPIRECTL_COSMETIC_EMIT_CAP_MS=0) disables pacing → every tick passes, streak never advances, so the
        // watcher emits exactly as it would with no cap present (byte-identical stream).
        RuntimeSceneTransform2DSnapshot? prev = null;
        int streak = 0;
        long lastEmit = 0;
        for (var i = 0; i < 20; i++)
        {
            var d = Step(ref prev, ref streak, ref lastEmit, Xf(i, 0), 1000 + i * 33, capMs: 0, k: 10);
            Assert.Equal(Sts2CosmeticEmitCap.Decision.Pass, d);
            Assert.Equal(0, streak);
        }
    }
}
