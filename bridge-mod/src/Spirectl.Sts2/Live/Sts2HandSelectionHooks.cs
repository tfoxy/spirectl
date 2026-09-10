using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using Spirectl.Sts2.Core.Logging;

namespace Spirectl.Sts2.Live;

// Captures which model opened in-hand selection mode (NPlayerHand.SelectCards's
// `source` argument, e.g. the played SURVIVOR card) so run.view.handSelection
// can report it. Best-effort: when the hook is not installed the state field
// stays empty; everything else about hand selection is read live off
// NPlayerHand.
internal static class Sts2HandSelectionHooks
{
    private static readonly object Sync = new();
    private static bool _installed;
    private static object? _source;

    public static void Install(ILogStream logStream)
    {
        lock (Sync)
        {
            if (_installed)
            {
                return;
            }

            Sts2MonoModNativeDependencies.EnsureLoaded(logStream);

            var target = typeof(NPlayerHand)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .SingleOrDefault(method =>
                    string.Equals(method.Name, "SelectCards", StringComparison.Ordinal)
                    && method.GetParameters() is { Length: 4 } parameters
                    && parameters[2].ParameterType == typeof(AbstractModel));
            var postfix = typeof(Sts2HandSelectionHooks).GetMethod(
                nameof(CaptureHandSelectionSourcePostfix),
                BindingFlags.NonPublic | BindingFlags.Static);
            if (target is null || postfix is null)
            {
                logStream.Write(
                    BridgeLogLevel.Warn,
                    "bridge.state.hand-selection",
                    "Unable to install hand-selection source hook; NPlayerHand.SelectCards was not found.");
                return;
            }

            try
            {
                new Harmony("spirectl.state.hand-selection")
                    .Patch(target, postfix: new HarmonyMethod(postfix));
                _installed = true;
                logStream.Write(
                    BridgeLogLevel.Info,
                    "bridge.state.hand-selection",
                    "Installed hand-selection source hook.");
            }
            catch (Exception ex)
            {
                logStream.Write(
                    BridgeLogLevel.Warn,
                    "bridge.state.hand-selection",
                    $"Hand-selection source hook was not installed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    public static object? CurrentSource
    {
        get
        {
            lock (Sync)
            {
                return _source;
            }
        }
    }

    // Fixture seam: the loader enters selection mode with source: null (a real
    // source would subscribe canonical-model lifecycle events) and records the
    // authored source here so run.view.handSelection still reports it. Set
    // AFTER calling SelectCards — the postfix overwrites the slot with the
    // call's own (null) source first.
    internal static void SetAuthoredSource(object? source)
    {
        lock (Sync)
        {
            _source = source;
        }
    }

    private static void CaptureHandSelectionSourcePostfix(
        AbstractModel? source,
        Task<IEnumerable<CardModel>> __result)
    {
        lock (Sync)
        {
            _source = source;
        }

        // In-hand selection opening changes what the seat can do (accelerator only).
        Sts2SemanticStateRevision.Bump();

        _ = __result.ContinueWith(
            _ => ClearSource(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void ClearSource()
    {
        lock (Sync)
        {
            _source = null;
        }

        Sts2SemanticStateRevision.Bump();
    }
}
