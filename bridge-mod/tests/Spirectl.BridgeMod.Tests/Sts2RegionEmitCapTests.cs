using Spirectl.Sts2.Core.SceneInspection;
using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// R11 producer region-emit cap pacing (Sts2RegionEmitCap.Decide) — a pure, Godot-free unit exercise of the flame
// frame-swap cap the watcher applies before ApplyIfChanged. Verifies: a pure same-size swap pins inside the window
// and emits (restarting the window) once it elapses; a size-changing swap and a null/equal region always pass; and
// cap=0 disables the pacing.
public sealed class Sts2RegionEmitCapTests
{
    private static RuntimeSceneRect2Snapshot Rect(double x, double y, double w, double h)
        => new(new RuntimeSceneVector2Snapshot(x, y), new RuntimeSceneVector2Snapshot(w, h));

    [Fact]
    public void SameSizeSwap_WithinWindow_Pins()
    {
        // last emitted at t=1000; a same-size (64x128) crop moved from x=0 to x=64 at t=1050, cap=100ms → still pinned.
        var d = Sts2RegionEmitCap.Decide(Rect(0, 0, 64, 128), Rect(64, 0, 64, 128), lastEmitAtMs: 1000, now: 1050, capMs: 100);
        Assert.Equal(Sts2RegionEmitCap.Decision.Pin, d);
    }

    [Fact]
    public void SameSizeSwap_WindowElapsed_Emits()
    {
        // 120ms since the last emit ≥ 100ms cap → let this frame through (the caller restarts the window).
        var d = Sts2RegionEmitCap.Decide(Rect(0, 0, 64, 128), Rect(64, 0, 64, 128), lastEmitAtMs: 1000, now: 1120, capMs: 100);
        Assert.Equal(Sts2RegionEmitCap.Decision.Emit, d);
    }

    [Fact]
    public void SameSizeSwap_ExactlyAtWindow_Emits()
    {
        // now - lastEmit == cap → NOT < cap → Emit (the boundary lets the frame through, so pacing never stalls).
        var d = Sts2RegionEmitCap.Decide(Rect(0, 0, 64, 128), Rect(64, 0, 64, 128), lastEmitAtMs: 1000, now: 1100, capMs: 100);
        Assert.Equal(Sts2RegionEmitCap.Decision.Emit, d);
    }

    [Fact]
    public void FirstSwap_NeverEmittedYet_Emits()
    {
        // lastEmitAtMs=0 (never emitted) → a large now-0 gap ≥ cap → the first frame always ships.
        var d = Sts2RegionEmitCap.Decide(Rect(0, 0, 64, 128), Rect(64, 0, 64, 128), lastEmitAtMs: 0, now: 5000, capMs: 100);
        Assert.Equal(Sts2RegionEmitCap.Decision.Emit, d);
    }

    [Fact]
    public void SizeChangingSwap_AlwaysPasses()
    {
        // A crop that changed SIZE (64x128 → 80x128) is a real LocalRect Draw — never capped, even within the window.
        var d = Sts2RegionEmitCap.Decide(Rect(0, 0, 64, 128), Rect(0, 0, 80, 128), lastEmitAtMs: 1000, now: 1010, capMs: 100);
        Assert.Equal(Sts2RegionEmitCap.Decision.Pass, d);
    }

    [Fact]
    public void EqualRegion_Passes()
    {
        // Same size AND same position → no change → Pass (nothing to pace).
        var d = Sts2RegionEmitCap.Decide(Rect(0, 0, 64, 128), Rect(0, 0, 64, 128), lastEmitAtMs: 1000, now: 1010, capMs: 100);
        Assert.Equal(Sts2RegionEmitCap.Decision.Pass, d);
    }

    [Fact]
    public void NullEndpoints_Pass()
    {
        // A null↔non-null transition (a crop first appears / disappears) is a real Draw, not a frame swap → Pass.
        Assert.Equal(Sts2RegionEmitCap.Decision.Pass,
            Sts2RegionEmitCap.Decide(null, Rect(0, 0, 64, 128), 1000, 1010, 100));
        Assert.Equal(Sts2RegionEmitCap.Decision.Pass,
            Sts2RegionEmitCap.Decide(Rect(0, 0, 64, 128), null, 1000, 1010, 100));
    }

    [Fact]
    public void CapDisabled_Passes()
    {
        // capMs=0 (SPIRECTL_REGION_EMIT_CAP_MS=0) disables the pacing entirely → every swap passes through.
        var d = Sts2RegionEmitCap.Decide(Rect(0, 0, 64, 128), Rect(64, 0, 64, 128), lastEmitAtMs: 1000, now: 1050, capMs: 0);
        Assert.Equal(Sts2RegionEmitCap.Decision.Pass, d);
    }
}
