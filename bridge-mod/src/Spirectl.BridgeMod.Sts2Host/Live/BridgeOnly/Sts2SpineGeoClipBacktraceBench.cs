// Copyright (c) spirectl contributors.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace Spirectl.Sts2.Live;

/// <summary>
/// A micro-bench for the geoclip sweep's per-candidate cost, run ON THE MAIN THREAD AT THE REAL CALL SITE —
/// inside the acquisition, on the same async continuation the dense arm probes from — because the thing under
/// suspicion is a function of the managed stack that reaches the probe, and a bench on a fresh worker stack
/// cannot see it.
///
/// <para>The question it answers: the dense sweep spends 33-43 microseconds per candidate on what should be a
/// hash lookup. The hypothesis is that a failed <c>MeshGetSurfaceCount</c> raises an engine error, and the engine
/// captures a per-scripting-language backtrace as a CALL ARGUMENT to <c>print_error</c> — so it is built before
/// anything consults the print-enabled flag, and <c>Engine.PrintErrorMessages = false</c> cannot suppress it.
/// The arms are designed so that a NEGATIVE result is as legible as a positive one: if A1 comes back cheap, the
/// hypothesis is dead and no amount of A0 being expensive rescues it.</para>
///
/// <para>Armed only by <c>SPIRECTL_SPINE_GEOCLIP_BT_BENCH</c>; an unarmed bake pays one environment read.</para>
/// </summary>
internal static class Sts2SpineGeoClipBacktraceBench
{
    private const string ArmsEnv = "SPIRECTL_SPINE_GEOCLIP_BT_BENCH";
    private const string IterEnv = "SPIRECTL_SPINE_GEOCLIP_BT_BENCH_N";

    // A0/A1/A5 default. ~0.7 s at the suspected 33 us; enough that a per-call number is not dominated by the
    // stopwatch, few enough that the main thread is not visibly frozen between the frame yields below.
    private const int DefaultIterations = 20_000;

    // The print-enabled arm writes one engine error line per iteration to godot.log, so it is deliberately
    // short: the per-call cost is what is wanted, not a 3 MB log.
    private const int PrintArmIterations = 2_000;

    // Extra managed frames pushed under the probe, to turn "is the stack depth the cost?" into a slope in
    // microseconds per frame rather than a two-point deep-vs-shallow anecdote.
    private static readonly int[] DepthLadder = [0, 8, 32, 64];

    private static long _sink;
    private static int _ran;

    /// <summary>
    /// A RID that fails to resolve THE WAY THE SWEEP'S CANDIDATES FAIL, which is the only kind of failure worth
    /// timing. `RID_Alloc::get_or_null` splits the id into a low-32 INDEX and a high-32 VALIDATOR: an index at or
    /// above `max_alloc` returns null immediately, without ever reading the validator chunk, while an in-range
    /// index with a mismatched validator does the chunk read and then returns null. The sweep enumerates indices
    /// in the tens-to-low-hundreds against live validators, so its dominant path is the SECOND one — hence a low
    /// index (0x28 = 40, comfortably inside any populated mesh owner) against a validator so large no allocation
    /// has ever minted it. Picking a huge index instead would measure the short-circuit and quietly understate
    /// the cost. A9 grades this choice: if A0 does not land on the real sweep's per-candidate number, the RID is
    /// not representative and the arm is void.
    /// </summary>
    private const ulong BogusRidId = 0x7FFF_FFFF_0000_0028UL;

    internal static bool Armed => (System.Environment.GetEnvironmentVariable(ArmsEnv) ?? string.Empty).Trim().Length > 0;

    /// <summary>
    /// Run the armed subset once. <paramref name="validMeshRid"/> should be a mesh the caller knows is live (the
    /// baker's own probe meshes are), so arm A5 measures a SUCCEEDING call and can exonerate marshalling.
    /// </summary>
    internal static async Task RunAsync(
        Viewport rootViewport,
        ulong? validMeshRid,
        Action<string> log,
        Func<int, Task> awaitFrames)
    {
        var spec = (System.Environment.GetEnvironmentVariable(ArmsEnv) ?? string.Empty).Trim();
        if (spec.Length == 0 || Interlocked.Exchange(ref _ran, 1) != 0)
        {
            return;
        }

        var arms = spec.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry => entry.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
        var all = arms.Contains("all");
        bool Want(string arm) => all || arms.Contains(arm);

        var iterations = ReadIterations();
        log($"BENCH armed spec='{spec}' n={iterations} "
            + $"validMeshRid={validMeshRid?.ToString(CultureInfo.InvariantCulture) ?? "<none>"}");

        // ── A7: is there a render thread to hop to at all? ────────────────────────────────────────────────
        if (Want("a7"))
        {
            var onRender = TryDescribe(() => RenderingServer.IsOnRenderThread().ToString());
            var managedThread = System.Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture);
            log($"BENCH a7 isOnRenderThread={onRender} managedThreadId={managedThread} "
                + $"isMainThread={TryDescribe(() => (System.Threading.Thread.CurrentThread.ManagedThreadId == 1).ToString())}");
        }

        // ── A5: a SUCCEEDING probe. If this is cheap, marshalling and ptrcall are exonerated. ─────────────
        if (Want("a5"))
        {
            if (validMeshRid is { } liveId && TryComposeRid(liveId, out var liveRid))
            {
                var surfaces = TryDescribe(() => RenderingServer.MeshGetSurfaceCount(liveRid)
                    .ToString(CultureInfo.InvariantCulture));
                var us = TimeCalls(iterations, () => RenderingServer.MeshGetSurfaceCount(liveRid));
                log($"BENCH a5 validRid surfaceCount={surfaces} {Format(us, iterations)}");
            }
            else
            {
                log("BENCH a5 SKIPPED: no live mesh RID was available to probe.");
            }

            await awaitFrames(1);
        }

        // ── A0: today's failing probe, exactly as the dense arm makes it. ─────────────────────────────────
        if (Want("a0"))
        {
            if (TryComposeRid(BogusRidId, out var bogus))
            {
                var previous = TryGet(() => Engine.PrintErrorMessages, true);
                double us;
                try
                {
                    TryRun(() => Engine.PrintErrorMessages = false);
                    us = TimeCalls(iterations, () => RenderingServer.MeshGetSurfaceCount(bogus));
                }
                finally
                {
                    TryRun(() => Engine.PrintErrorMessages = previous);
                }

                log($"BENCH a0 bogusRid printErrors=false {Format(us, iterations)}");
            }

            await awaitFrames(1);
        }

        // ── A1: THE DECIDING ARM. The backtrace capture alone, with no RenderingServer call at all. ───────
        if (Want("a1"))
        {
            DescribeCapture(log);
            var us = TimeCalls(iterations, () => Consume(Engine.CaptureScriptBacktraces(false)));
            log($"BENCH a1 captureScriptBacktraces ALONE (no RenderingServer call) {Format(us, iterations)}");
            await awaitFrames(1);
        }

        // ── A4: does the managed stack depth actually price it? ───────────────────────────────────────────
        if (Want("a4"))
        {
            var perDepth = Math.Max(500, iterations / 4);
            var samples = new List<(int Depth, double Us)>();
            foreach (var depth in DepthLadder)
            {
                var us = RunAtDepth(depth, () => TimeCalls(perDepth, () => Consume(Engine.CaptureScriptBacktraces(false))));
                samples.Add((depth, us));
                log($"BENCH a4 depth=+{depth} frames {Format(us, perDepth)}");
                await awaitFrames(1);
            }

            if (samples.Count >= 2)
            {
                var first = samples[0];
                var last = samples[^1];
                var slope = (last.Us - first.Us) / Math.Max(1, last.Depth - first.Depth);
                log($"BENCH a4 slope={slope.ToString("0.####", CultureInfo.InvariantCulture)} us/frame "
                    + $"across +{first.Depth}..+{last.Depth} frames");
            }

            // The shallow comparison the deep continuation cannot make for itself: a SceneTreeTimer callback
            // resumes on a stack that is the engine's, not the bake's.
            if (rootViewport.GetTree() is { } tree)
            {
                var timer = tree.CreateTimer(0.0);
                await rootViewport.ToSignal(timer, SceneTreeTimer.SignalName.Timeout);
                DescribeCapture(log, "a4-shallow");
                var shallow = TimeCalls(perDepth, () => Consume(Engine.CaptureScriptBacktraces(false)));
                log($"BENCH a4 shallow SceneTreeTimer callback {Format(shallow, perDepth)}");
            }

            await awaitFrames(1);
        }

        // ── A2: what does the logger/Sentry half add once the print is actually allowed? ──────────────────
        if (Want("a2"))
        {
            if (TryComposeRid(BogusRidId, out var bogus))
            {
                var previous = TryGet(() => Engine.PrintErrorMessages, true);
                double us;
                try
                {
                    TryRun(() => Engine.PrintErrorMessages = true);
                    us = TimeCalls(PrintArmIterations, () => RenderingServer.MeshGetSurfaceCount(bogus));
                }
                finally
                {
                    TryRun(() => Engine.PrintErrorMessages = previous);
                }

                log($"BENCH a2 bogusRid printErrors=TRUE {Format(us, PrintArmIterations)}");
            }

            await awaitFrames(1);
        }

        log("BENCH complete.");
    }

    /// <summary>
    /// Log what one capture actually returns — how many languages answer, which, and with how many frames each.
    /// This is the number the fix's guard is graded against later: a patch that binds drives it to zero.
    /// </summary>
    private static void DescribeCapture(Action<string> log, string arm = "a1")
    {
        try
        {
            var backtraces = Engine.CaptureScriptBacktraces(false);
            var parts = new List<string>();
            foreach (var backtrace in backtraces)
            {
                var name = TryDescribe(() => backtrace.GetLanguageName());
                var frames = TryDescribe(() => backtrace.GetFrameCount().ToString(CultureInfo.InvariantCulture));
                parts.Add($"{name}:{frames}");
            }

            log($"BENCH {arm} capture returns {backtraces.Count} backtrace(s) [{string.Join(", ", parts)}]");
        }
        catch (Exception ex)
        {
            log($"BENCH {arm} capture description failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static double TimeCalls(int iterations, Action body)
    {
        // Warm up so the first-call JIT and any lazy metadata load are not charged to the measurement.
        var warmup = Math.Min(200, iterations);
        for (var i = 0; i < warmup; i += 1)
        {
            body();
        }

        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i += 1)
        {
            body();
        }

        stopwatch.Stop();
        return stopwatch.Elapsed.TotalMilliseconds * 1000.0 / Math.Max(1, iterations);
    }

    // Real frames, not a loop counter: NoInlining keeps each level on the stack, and the Volatile.Write after
    // the recursive call denies the JIT a tail call that would collapse the ladder into one frame.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static double RunAtDepth(int depth, Func<double> body)
    {
        if (depth <= 0)
        {
            return body();
        }

        var result = RunAtDepth(depth - 1, body);
        Volatile.Write(ref _sink, (long)result);
        return result;
    }

    private static void Consume(Godot.Collections.Array<ScriptBacktrace> backtraces)
        => Volatile.Write(ref _sink, backtraces.Count);

    private static string Format(double microsecondsPerCall, int iterations)
        => $"n={iterations.ToString(CultureInfo.InvariantCulture)} "
            + $"perCall={microsecondsPerCall.ToString("0.###", CultureInfo.InvariantCulture)}us "
            + $"total={(microsecondsPerCall * iterations / 1000.0).ToString("0.#", CultureInfo.InvariantCulture)}ms";

    private static int ReadIterations()
    {
        var raw = (System.Environment.GetEnvironmentVariable(IterEnv) ?? string.Empty).Trim();
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, 100, 2_000_000)
            : DefaultIterations;
    }

    private static bool TryComposeRid(ulong id, out Rid rid)
    {
        var value = id;
        rid = Unsafe.As<ulong, Rid>(ref value);
        return rid.Id == id;
    }

    private static string TryDescribe(Func<string> read)
    {
        try
        {
            return read() ?? "<null>";
        }
        catch (Exception ex)
        {
            return $"<{ex.GetType().Name}>";
        }
    }

    private static T TryGet<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch
        {
            return fallback;
        }
    }

    private static void TryRun(Action run)
    {
        try
        {
            run();
        }
        catch
        {
            // The bench never takes the host down.
        }
    }
}
