using System.Collections.Concurrent;
using System.Reflection;
using Godot;
using HarmonyLib;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Embedding;

namespace Spirectl.Sts2.Live;

/// <summary>
/// Drives the mirror's particle-restart epoch off the ACTUAL <c>Restart()</c> / <c>Emitting = true</c> call
/// rather than the streamed <c>Emitting</c> false→true edge. A per-node monotonic counter is bumped by
/// Harmony postfixes on <c>GpuParticles2D</c>/<c>CpuParticles2D</c> <c>Restart()</c> and their
/// <c>set_Emitting</c> setters; the scene watcher folds a change in that count into the burst epoch it ships.
///
/// <para>Why the count, not the edge: the couch-coop headless CPU saver permanently freezes particle nodes
/// (<c>ProcessMode.Disabled</c>). A frozen one-shot node's <c>Emitting</c> LATCHES true (the node's own
/// processing normally auto-resets it to false at cycle end), so a subsequent <c>Restart()</c> produces no
/// false→true edge and the epoch never bumps — repeated bursts (e.g. the energy-counter glow pulsing on each
/// energy gain) stop replaying. Hooking the call itself survives the freeze (and fixes a latent active-mode
/// bug where a <c>Restart()</c> that doesn't reset <c>Emitting</c> is invisible today). The emitting-edge bump
/// remains as a fallback in the watcher.</para>
///
/// <para>Telemetry must never disrupt the game: every postfix body is wrapped in try/catch, and a patch that
/// fails to resolve/apply is logged and skipped rather than thrown.</para>
/// </summary>
internal static class Sts2ParticleRestartHooks
{
    private static readonly object Sync = new();
    private static bool _installed;

    // Per particle-node instance id → monotonic restart count. A watcher tick reads the current count and
    // bumps the node's burst epoch when it differs from the last-seen value. Multiple bumps within one tick
    // collapse to a single epoch change (harmless).
    private static readonly ConcurrentDictionary<ulong, long> Counts = new();

    public static bool IsInstalled
    {
        get
        {
            lock (Sync)
            {
                return _installed;
            }
        }
    }

    public static void Install(ILogStream logStream)
    {
        lock (Sync)
        {
            if (_installed)
            {
                return;
            }

            Sts2MonoModNativeDependencies.EnsureLoaded(logStream);

            Harmony harmony;
            try
            {
                harmony = new Harmony("spirectl.particle-restart");
            }
            catch (Exception ex)
            {
                logStream.Write(
                    BridgeLogLevel.Warn,
                    "bridge.mirror.particle-restart",
                    $"Skipping particle-restart hooks because Harmony init failed: {ex.GetType().Name}: {ex.Message}");
                return;
            }

            var restartPostfix = typeof(Sts2ParticleRestartHooks).GetMethod(
                nameof(RestartPostfix),
                BindingFlags.NonPublic | BindingFlags.Static);
            var setEmittingPostfix = typeof(Sts2ParticleRestartHooks).GetMethod(
                nameof(SetEmittingPostfix),
                BindingFlags.NonPublic | BindingFlags.Static);
            if (restartPostfix is null || setEmittingPostfix is null)
            {
                logStream.Write(
                    BridgeLogLevel.Warn,
                    "bridge.mirror.particle-restart",
                    "Skipping particle-restart hooks; postfix methods were not found.");
                return;
            }

            // Patch both particle types × two triggers. Each patch is best-effort: a missing target or a
            // patching failure is logged and skipped so the others (and the game) carry on.
            var patched = 0;
            patched += TryPatchRestart(harmony, typeof(GpuParticles2D), restartPostfix, logStream) ? 1 : 0;
            patched += TryPatchRestart(harmony, typeof(CpuParticles2D), restartPostfix, logStream) ? 1 : 0;
            patched += TryPatchSetEmitting(harmony, typeof(GpuParticles2D), setEmittingPostfix, logStream) ? 1 : 0;
            patched += TryPatchSetEmitting(harmony, typeof(CpuParticles2D), setEmittingPostfix, logStream) ? 1 : 0;

            _installed = true;
            logStream.Write(
                BridgeLogLevel.Info,
                "bridge.mirror.particle-restart",
                $"Installed particle-restart hooks ({patched}/4 targets); mirror burst epochs now track real Restart()/Emitting=true calls.");
        }
    }

    // Godot's binding may expose Restart as Restart(bool keepSeed = false) (an overload) or a parameterless
    // Restart(); resolve by name and take the first instance method so we don't couple to a specific signature.
    private static bool TryPatchRestart(Harmony harmony, Type type, MethodInfo postfix, ILogStream logStream)
    {
        try
        {
            var target = type
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(m => m.Name == "Restart" && !m.IsGenericMethodDefinition);
            if (target is null)
            {
                logStream.Write(
                    BridgeLogLevel.Warn,
                    "bridge.mirror.particle-restart",
                    $"Skipping {type.Name}.Restart hook; method was not found.");
                return false;
            }

            harmony.Patch(target, postfix: new HarmonyMethod(postfix));
            return true;
        }
        catch (Exception ex)
        {
            logStream.Write(
                BridgeLogLevel.Warn,
                "bridge.mirror.particle-restart",
                $"Skipping {type.Name}.Restart hook; patching failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static bool TryPatchSetEmitting(Harmony harmony, Type type, MethodInfo postfix, ILogStream logStream)
    {
        try
        {
            var target = type.GetProperty("Emitting")?.GetSetMethod();
            if (target is null)
            {
                logStream.Write(
                    BridgeLogLevel.Warn,
                    "bridge.mirror.particle-restart",
                    $"Skipping {type.Name}.set_Emitting hook; property setter was not found.");
                return false;
            }

            harmony.Patch(target, postfix: new HarmonyMethod(postfix));
            return true;
        }
        catch (Exception ex)
        {
            logStream.Write(
                BridgeLogLevel.Warn,
                "bridge.mirror.particle-restart",
                $"Skipping {type.Name}.set_Emitting hook; patching failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    // Harmony binds __instance to the receiver. Restart() sets emitting NATIVELY (it does not invoke the
    // managed set_Emitting), so this path never double-counts with SetEmittingPostfix.
    private static void RestartPostfix(GodotObject __instance) => Bump(__instance);

    // The property setter's parameter is named `value`; Harmony binds postfix params by name. Count only a
    // set to true (Emitting = false is a stop, not a re-trigger).
    private static void SetEmittingPostfix(GodotObject __instance, bool value)
    {
        if (value)
        {
            Bump(__instance);
        }
    }

    private static void Bump(GodotObject node)
    {
        try
        {
            Counts.AddOrUpdate(node.GetInstanceId(), 1, (_, v) => v + 1);
        }
        catch
        {
            // Particle telemetry must never disrupt the game.
        }
    }

    // Current restart count for a node id (0 when never restarted / not a particle node).
    public static long GetRestartCount(ulong id) => Counts.TryGetValue(id, out var v) ? v : 0;

    // Drop a vanished node's counter so the map stays bounded (called when the watcher untracks the node).
    public static void Forget(ulong id) => Counts.TryRemove(id, out _);
}
