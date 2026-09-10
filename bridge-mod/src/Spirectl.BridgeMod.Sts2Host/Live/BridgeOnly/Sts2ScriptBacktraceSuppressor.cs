// Copyright (c) spirectl contributors.

using System;
using System.Globalization;
using System.Reflection;
using System.Threading;
using Godot;
using HarmonyLib;

namespace Spirectl.Sts2.Live;


/// <summary>
/// Stops the engine building a C# stack trace for errors the geoclip sweep RAISES ON PURPOSE.
///
/// <para><b>Why this is needed at all.</b> The sweep asks the rendering server for a surface count on tens of
/// thousands of candidate RIDs, almost all of which are not meshes. Each miss trips an <c>ERR_FAIL_NULL_V</c>,
/// and the engine's error path passes a freshly captured per-scripting-language backtrace as a CALL ARGUMENT to
/// its print function — so the capture happens before anything consults the print-enabled flag.
/// <c>Engine.PrintErrorMessages = false</c> therefore cannot suppress it: it only skips the print, after the
/// expensive part has already run. Measured live at the real call site (phase-6 WS-1, byrdonis, 20 000
/// iterations each): a SUCCEEDING surface-count call costs 0.019 us, a failing one costs 28.4 us, and the
/// backtrace capture ALONE — with no rendering-server call in it at all — costs 26.8 us. The capture is 94 % of
/// the probe. Turning printing back on costs a further 135 us, so the existing flag is doing real work; this
/// hook is about the 28 us it cannot reach.</para>
///
/// <para><b>What it patches.</b> The engine reaches managed code through
/// <c>GDMonoCache::managed_callbacks.DebuggingUtils_GetCurrentStackInfo</c>, a function pointer taken once at
/// startup from <c>Godot.DebuggingUtils.GetCurrentStackInfo</c>. That method builds a
/// <c>StackTrace(skipFrames: 2, fNeedFileInfo: true)</c> and then, per frame, resolves a method, tests an
/// attribute, builds a signature string and marshals two strings to native — which is why the cost scales with
/// stack depth (measured 1.79 us per added frame). A prefix that skips it leaves the C# backtrace empty, which
/// is exactly what a suppressed error wants.</para>
///
/// <para><b>Why it is scoped, not global.</b> Armed around the sweep and disarmed in a finally on every path.
/// While armed it is further gated on <see cref="Suppressed"/>, which the sweep sets only across the stretches
/// where it already sets <c>PrintErrorMessages = false</c> — so the frames the sweep yields back to the game
/// keep full-fidelity error reporting. An error raised anywhere else, at any time, is unaffected.</para>
///
/// <para><b>It refuses to lie about binding.</b> <c>GetCurrentStackInfo</c> is
/// <c>[UnmanagedCallersOnly]</c>, and whether a detour on such a method takes is a property of the runtime, not
/// something that can be reasoned out here. So arming VERIFIES: it captures one backtrace and requires the C#
/// frame count to be zero, and it counts prefix invocations. A patch that silently failed to bind reports
/// itself as a failure and the sweep runs exactly as it does today.</para>
/// </summary>
internal static class Sts2ScriptBacktraceSuppressor
{
    // Default ON. Set to 0 to run the sweep with the engine's stock error path — which is what the before/after
    // measurement does, so that both arms are the same build.
    private const string EnableEnv = "SPIRECTL_SPINE_GEOCLIP_SUPPRESS_BACKTRACE";

    private static readonly object Sync = new();
    private static Harmony? _harmony;
    private static MethodBase? _patched;
    private static long _skipped;

    /// <summary>
    /// Whether the CURRENT thread is inside a stretch of deliberately-failing probes. Thread-static so a patch
    /// armed by the bake can never silence an error raised on another thread.
    /// </summary>
    [ThreadStatic]
    public static bool Suppressed;

    /// <summary>How many captures the prefix has actually skipped. Zero after a sweep means it never bound.</summary>
    public static long Skipped => Interlocked.Read(ref _skipped);

    internal static bool Enabled
    {
        get
        {
            var raw = (System.Environment.GetEnvironmentVariable(EnableEnv) ?? string.Empty).Trim();
            return raw.Length == 0
                || !(raw.Equals("0", StringComparison.Ordinal)
                    || raw.Equals("false", StringComparison.OrdinalIgnoreCase)
                    || raw.Equals("off", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Install the patch and prove it bound. Returns a scope that disarms; never throws, and returns a scope
    /// whose disposal is safe even when nothing was installed.
    /// </summary>
    public static IDisposable Arm(Action<string> log)
    {
        if (!Enabled)
        {
            log($"backtrace suppression is disabled by {EnableEnv}; the sweep pays the engine's stock error path.");
            return new Scope(false);
        }

        lock (Sync)
        {
            if (_patched is not null)
            {
                return new Scope(false);
            }

            try
            {
                // Resolved off the loaded GodotSharp rather than by a compile-time reference: the type is
                // `internal`, so there is nothing to bind to at build time.
                var type = typeof(Engine).Assembly.GetType("Godot.DebuggingUtils", throwOnError: false);
                var original = type is null
                    ? null
                    : AccessTools.Method(type, "GetCurrentStackInfo");
                if (original is null)
                {
                    log("backtrace suppression NOT armed: Godot.DebuggingUtils.GetCurrentStackInfo was not found; "
                        + "the sweep pays the engine's stock error path.");
                    return new Scope(false);
                }

                var prefix = typeof(Sts2ScriptBacktraceSuppressor).GetMethod(
                    nameof(GetCurrentStackInfoPrefix),
                    BindingFlags.NonPublic | BindingFlags.Static);
                if (prefix is null)
                {
                    return new Scope(false);
                }

                // Both deployed copies of this runtime (the bridge's and the embedder's) can reach this, so the
                // id carries the assembly name and each copy unpatches only its own.
                var harmony = new Harmony(
                    $"spirectl.geoclip.backtrace.{typeof(Sts2ScriptBacktraceSuppressor).Assembly.GetName().Name}");
                harmony.Patch(original, prefix: new HarmonyMethod(prefix));
                _harmony = harmony;
                _patched = original;
            }
            catch (Exception ex)
            {
                log($"backtrace suppression NOT armed: Harmony patching failed ({ex.GetType().Name}: {ex.Message}); "
                    + "the sweep pays the engine's stock error path.");
                _harmony = null;
                _patched = null;
                return new Scope(false);
            }
        }

        // THE ANTI-VACUITY GATE. A prefix that did not bind produces a backtrace with the same frame count as
        // before, and every downstream number would then be a measurement of nothing.
        var bound = VerifyBound(log);
        if (!bound)
        {
            Disarm(log);
            return new Scope(false);
        }

        return new Scope(true);
    }

    private static bool VerifyBound(Action<string> log)
    {
        var previous = Suppressed;
        Suppressed = true;
        try
        {
            var frames = -1;
            var language = "<none>";
            foreach (var backtrace in Engine.CaptureScriptBacktraces(false))
            {
                var name = backtrace.GetLanguageName();
                if (string.Equals(name, "C#", StringComparison.Ordinal))
                {
                    frames = backtrace.GetFrameCount();
                    language = name;
                }
            }

            if (frames == 0)
            {
                log("backtrace suppression ARMED and verified: the C# backtrace now reports 0 frames while "
                    + "suppressed.");
                return true;
            }

            log($"backtrace suppression FAILED TO BIND: the C# backtrace still reports "
                + $"{frames.ToString(CultureInfo.InvariantCulture)} frame(s) (language='{language}') while "
                + "suppressed, so the patch is not on the path the engine calls. Standing down; the sweep pays "
                + "the engine's stock error path.");
            return false;
        }
        catch (Exception ex)
        {
            log($"backtrace suppression could not be verified ({ex.GetType().Name}: {ex.Message}); standing down.");
            return false;
        }
        finally
        {
            Suppressed = previous;
        }
    }

    private static void Disarm(Action<string> log)
    {
        lock (Sync)
        {
            try
            {
                if (_harmony is not null && _patched is not null)
                {
                    _harmony.Unpatch(_patched, HarmonyPatchType.Prefix, _harmony.Id);
                }
            }
            catch (Exception ex)
            {
                log($"backtrace suppression could not be unpatched ({ex.GetType().Name}: {ex.Message}).");
            }
            finally
            {
                _harmony = null;
                _patched = null;
                Suppressed = false;
            }
        }
    }

    // Returning false skips the original, leaving the destination vector empty — which is precisely the
    // "no C# frames" answer the engine already produces when the runtime is not initialised, so no caller
    // downstream is seeing a shape it has no handling for.
    private static bool GetCurrentStackInfoPrefix()
    {
        if (!Suppressed)
        {
            return true;
        }

        Interlocked.Increment(ref _skipped);
        return false;
    }

    private sealed class Scope(bool armed) : IDisposable
    {
        private readonly bool _armed = armed;
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Suppressed = false;
            if (_armed)
            {
                Disarm(_ => { });
            }
        }
    }
}
