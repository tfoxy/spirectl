using System;
using System.Threading.Tasks;
using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// Round-8 item 14 (spine bake budget), extended in R21 when the budget stopped being spine-only: the Godot-free
// primitives the couch-coop host drives —
//   * WHICH animation time a single-frame (still / degraded) bake samples,
//   * how many encode threads the process may use while N game instances share the machine, and
//   * the PROCESS-WIDE admission gate that makes that number mean the same thing across concurrent renders.
public sealed class Sts2RenderEncodeBudgetTests
{
    // The default: the MIDDLE of the clip. t=0 is the wind-up/entry pose (for many one-shots a near-empty frame),
    // which is exactly what made single-frame bakes look broken.
    [Fact]
    public void ChooseSampleTime_DefaultsToTheMidFrame()
    {
        Assert.Equal(0.5f, Sts2SpineStillFrame.ChooseSampleTime("idle_loop", 1.0f), 4);
        Assert.Equal(1.2f, Sts2SpineStillFrame.ChooseSampleTime("attack", 2.4f), 4);
        Assert.Equal(0.35f, Sts2SpineStillFrame.ChooseSampleTime(null, 0.7f), 4);
    }

    // A TERMINAL animation's resting state is its END pose (the corpse) — freezing a death mid-collapse is wrong.
    [Fact]
    public void ChooseSampleTime_TerminalAnimationsSampleTheLastFrame()
    {
        Assert.Equal(2.0f, Sts2SpineStillFrame.ChooseSampleTime("die", 2.0f), 4);
        Assert.Equal(2.0f, Sts2SpineStillFrame.ChooseSampleTime("die_loop", 2.0f), 4);
        Assert.Equal(2.0f, Sts2SpineStillFrame.ChooseSampleTime("boss_defeat", 2.0f), 4);
        Assert.Equal(2.0f, Sts2SpineStillFrame.ChooseSampleTime("DEATH_ANIM", 2.0f), 4);
        Assert.Equal(2.0f, Sts2SpineStillFrame.ChooseSampleTime("dead", 2.0f), 4);
    }

    // Terminal detection is per NAME TOKEN, never a bare substring: an unrelated clip that merely contains the
    // letters must keep the mid-frame default.
    [Fact]
    public void IsTerminalAnimation_MatchesTokensNotSubstrings()
    {
        Assert.True(Sts2SpineStillFrame.IsTerminalAnimation("die"));
        Assert.True(Sts2SpineStillFrame.IsTerminalAnimation("hurt_die"));
        Assert.False(Sts2SpineStillFrame.IsTerminalAnimation("audience_idle"));
        Assert.False(Sts2SpineStillFrame.IsTerminalAnimation("idle_loop"));
        Assert.False(Sts2SpineStillFrame.IsTerminalAnimation("breathe"));
        Assert.False(Sts2SpineStillFrame.IsTerminalAnimation(""));
        Assert.False(Sts2SpineStillFrame.IsTerminalAnimation(null));
    }

    // A degenerate duration has exactly one sample: 0.
    [Fact]
    public void ChooseSampleTime_DegenerateDurationSamplesZero()
    {
        Assert.Equal(0f, Sts2SpineStillFrame.ChooseSampleTime("idle_loop", 0f));
        Assert.Equal(0f, Sts2SpineStillFrame.ChooseSampleTime("die", -1f));
        Assert.Equal(0f, Sts2SpineStillFrame.ChooseSampleTime("die", float.NaN));
    }

    // ---- R10 `&t=` explicit sample time (the closed treasure chest) ---------------------------------------------

    // THE chest case: its sole clip is named "animation" (the lid opening) and the room freezes it at t=0 with
    // SetTimeScale(0) for a CLOSED chest. The mid-frame default renders a half-open lid; the explicit request wins.
    [Fact]
    public void ChooseSampleTime_ExplicitRequestOverridesTheHeuristic()
    {
        Assert.Equal(0f, Sts2SpineStillFrame.ChooseSampleTime("animation", 1.6f, 0f), 4);
        Assert.Equal(0.8f, Sts2SpineStillFrame.ChooseSampleTime("animation", 1.6f), 4); // unchanged without one
        Assert.Equal(0.25f, Sts2SpineStillFrame.ChooseSampleTime("idle_loop", 2.0f, 0.25f), 4);
        // …including for a terminal animation, whose default would be the LAST frame.
        Assert.Equal(0.1f, Sts2SpineStillFrame.ChooseSampleTime("die", 2.0f, 0.1f), 4);
    }

    // A wall-clock-derived track time can overshoot the clip's end by a frame — clamp rather than fail.
    [Fact]
    public void ChooseSampleTime_ExplicitRequestClampsToTheDuration()
    {
        Assert.Equal(2.0f, Sts2SpineStillFrame.ChooseSampleTime("idle_loop", 2.0f, 9.5f), 4);
        Assert.Equal(2.0f, Sts2SpineStillFrame.ChooseSampleTime("idle_loop", 2.0f, 2.0f), 4);
    }

    // A garbled request never addresses anything outside the clip: it is IGNORED, falling back to the heuristic.
    [Fact]
    public void ChooseSampleTime_GarbledRequestFallsBackToTheHeuristic()
    {
        Assert.Equal(1.0f, Sts2SpineStillFrame.ChooseSampleTime("idle_loop", 2.0f, -1f), 4);
        Assert.Equal(1.0f, Sts2SpineStillFrame.ChooseSampleTime("idle_loop", 2.0f, float.NaN), 4);
        Assert.Equal(1.0f, Sts2SpineStillFrame.ChooseSampleTime("idle_loop", 2.0f, float.PositiveInfinity), 4);
        // A degenerate duration still has exactly one sample, whatever was asked for.
        Assert.Equal(0f, Sts2SpineStillFrame.ChooseSampleTime("animation", 0f, 5f), 4);
    }

    // max(1, cores - instances): a lone host keeps almost the whole machine; every extra instance gives a core back
    // to the games; the floor of 1 keeps the encode pipeline alive on any core count.
    [Fact]
    public void ComputeEncodeThreads_LeavesOneCorePerGameInstance()
    {
        Assert.Equal(11, Sts2RenderEncodeBudget.ComputeEncodeThreads(12, 1));
        Assert.Equal(8, Sts2RenderEncodeBudget.ComputeEncodeThreads(12, 4));
        Assert.Equal(1, Sts2RenderEncodeBudget.ComputeEncodeThreads(4, 4));
        Assert.Equal(1, Sts2RenderEncodeBudget.ComputeEncodeThreads(2, 8));
        Assert.Equal(1, Sts2RenderEncodeBudget.ComputeEncodeThreads(1, 1));
        Assert.Equal(1, Sts2RenderEncodeBudget.ComputeEncodeThreads(0, 0)); // degenerate inputs still yield a live gate
    }

    // The published instance count floors at 1 (this instance always counts) and is readable back.
    [Fact]
    public void GameInstances_FloorsAtOne()
    {
        var original = Sts2RenderEncodeBudget.GameInstances;
        try
        {
            Sts2RenderEncodeBudget.GameInstances = 0;
            Assert.Equal(1, Sts2RenderEncodeBudget.GameInstances);
            Sts2RenderEncodeBudget.GameInstances = 3;
            Assert.Equal(3, Sts2RenderEncodeBudget.GameInstances);
            Assert.True(Sts2RenderEncodeBudget.EncodeThreads >= 1);
        }
        finally
        {
            Sts2RenderEncodeBudget.GameInstances = original;
        }
    }

    // The offload is opt-OUT and the flag name is documented: it is both the way back to the pre-R21 render path
    // without a rebuild, and the seam that lets ONE build produce both halves of the before/after measurement.
    [Fact]
    public void OffloadIsOnByDefaultAndNamedByItsEnvVar()
    {
        Assert.Equal("SPIRECTL_RENDER_ENCODE_OFFLOAD", Sts2RenderEncodeBudget.OffloadEnvVar);
        Assert.True(
            Environment.GetEnvironmentVariable(Sts2RenderEncodeBudget.OffloadEnvVar) == "0"
            || Sts2RenderEncodeBudget.OffloadEnabled,
            "the flag mirrors its env var, and absent means ON");
    }

    // The DEFAULT itself, which the test above cannot assert: OffloadEnabled reads its variable once at startup
    // (the flag has to mean the same thing for every render in a process), so a test that set the variable would
    // be asserting nothing. The pure seam is where "absent means ON" is checkable.
    //
    // It matters because three more render paths now depend on it — the texture, atlas-texture and
    // SubViewport-fallback extracts hand their encode to a worker the same way the background render does — so a
    // default that silently flipped to OFF would put every one of them back on the main thread with nothing
    // failing.
    [Fact]
    public void OffloadEnabledDefaultsTrue()
    {
        Assert.True(Sts2RenderEncodeBudget.ResolveOffloadEnabled(null));
        Assert.True(Sts2RenderEncodeBudget.ResolveOffloadEnabled(string.Empty));
        Assert.True(Sts2RenderEncodeBudget.ResolveOffloadEnabled("1"));
        // Only the exact opt-out spelling disables it: an unrecognised value must leave the shipped path alone
        // rather than silently reverting it.
        Assert.True(Sts2RenderEncodeBudget.ResolveOffloadEnabled("off"));
        Assert.True(Sts2RenderEncodeBudget.ResolveOffloadEnabled("false"));
        Assert.True(Sts2RenderEncodeBudget.ResolveOffloadEnabled(" 0 "));
        Assert.False(Sts2RenderEncodeBudget.ResolveOffloadEnabled("0"));
    }

    // ---- R21: the gate is PROCESS-WIDE ---------------------------------------------------------------------------

    // The whole point of the rename. A per-bake semaphore capped one bake's fan-out, so two concurrent encoders
    // (a second bake, or the single-image lane now that it encodes off-thread) could each take the full budget.
    // Two acquires taken from DIFFERENT logical bakes must therefore contend with each other.
    [Fact]
    public async Task AcquireAsync_SharesOneGateAcrossConcurrentBakes()
    {
        var original = Sts2RenderEncodeBudget.GameInstances;
        try
        {
            // Force a single-slot budget: instances >= cores => max(1, cores - instances) == 1.
            Sts2RenderEncodeBudget.GameInstances = Environment.ProcessorCount + 4;
            Sts2RenderEncodeBudget.ResetGate();
            Assert.Equal(1, Sts2RenderEncodeBudget.CurrentGateThreads);

            var first = await Sts2RenderEncodeBudget.AcquireAsync();
            var second = Sts2RenderEncodeBudget.AcquireAsync();
            Assert.False(second.IsCompleted); // the SECOND "bake" waits on the FIRST one's slot

            first.Dispose();
            using var admitted = await second.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(admitted);
        }
        finally
        {
            Sts2RenderEncodeBudget.GameInstances = original;
            Sts2RenderEncodeBudget.ResetGate();
        }
    }

    // An uncontended acquire must not schedule anything: the clip lane takes a slot per FRAME, and on the Godot main
    // thread every scheduling hop costs up to a main-loop tick (~10ms). A free slot has to complete inline.
    [Fact]
    public async Task AcquireAsync_UncontendedCompletesSynchronously()
    {
        Sts2RenderEncodeBudget.ResetGate();
        var acquire = Sts2RenderEncodeBudget.AcquireAsync();
        Assert.True(acquire.IsCompletedSuccessfully);
        (await acquire).Dispose();
    }

    // Re-sizing is best-effort but must never strand a waiter or over-release: a lease taken against the OLD gate
    // releases into the old gate, and the new gate is live at its new width immediately.
    [Fact]
    public async Task AcquireAsync_RebuildsTheGateWhenTheBudgetChanges()
    {
        var original = Sts2RenderEncodeBudget.GameInstances;
        try
        {
            Sts2RenderEncodeBudget.GameInstances = Environment.ProcessorCount + 4;
            Sts2RenderEncodeBudget.ResetGate();
            var acrossResize = await Sts2RenderEncodeBudget.AcquireAsync();

            Sts2RenderEncodeBudget.GameInstances = 1;
            var widened = Sts2RenderEncodeBudget.EncodeThreads;
            var next = Sts2RenderEncodeBudget.AcquireAsync(); // rebuilds; must not be blocked by the old lease
            Assert.True(next.IsCompletedSuccessfully);
            Assert.Equal(widened, Sts2RenderEncodeBudget.CurrentGateThreads);

            (await next).Dispose();
            acrossResize.Dispose(); // releases into the OLD gate; no ObjectDisposedException, no over-release
            Assert.Equal(widened, Sts2RenderEncodeBudget.CurrentGateThreads);
        }
        finally
        {
            Sts2RenderEncodeBudget.GameInstances = original;
            Sts2RenderEncodeBudget.ResetGate();
        }
    }

    // Disposing a lease twice (a defensive finally plus a using) must not hand back a slot that was never taken.
    [Fact]
    public async Task Lease_DisposeIsIdempotent()
    {
        var original = Sts2RenderEncodeBudget.GameInstances;
        try
        {
            Sts2RenderEncodeBudget.GameInstances = Environment.ProcessorCount + 4;
            Sts2RenderEncodeBudget.ResetGate();

            var lease = await Sts2RenderEncodeBudget.AcquireAsync();
            lease.Dispose();
            lease.Dispose();

            // One slot total: if the double dispose had leaked an extra permit, both of these would be free.
            var held = await Sts2RenderEncodeBudget.AcquireAsync();
            var blocked = Sts2RenderEncodeBudget.AcquireAsync();
            Assert.False(blocked.IsCompleted);

            held.Dispose();
            (await blocked.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
        }
        finally
        {
            Sts2RenderEncodeBudget.GameInstances = original;
            Sts2RenderEncodeBudget.ResetGate();
        }
    }
}
