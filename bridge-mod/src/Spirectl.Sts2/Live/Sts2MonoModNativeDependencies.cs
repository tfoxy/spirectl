using System.Runtime.InteropServices;
using Spirectl.Sts2.Core.Logging;

namespace Spirectl.Sts2.Live;

/// <summary>
/// The one native precondition Harmony has in this game, and the one an embedder has to satisfy itself.
/// </summary>
/// <remarks>
/// <para>
/// Harmony detours go through MonoMod, which extracts a native exec-helper (<c>/tmp/mm-exhelper.so.*</c>) and
/// dlopens it. That helper carries NO <c>DT_NEEDED</c> entries at all and needs libgcc's unwinder
/// (<c>_Unwind_RaiseException</c> and friends) from the process's GLOBAL dynamic-symbol namespace; the game's
/// bundled "MegaDot" runtime does not leave them there, so the dlopen fails
/// (<c>DllNotFoundException: … undefined symbol: _Unwind_RaiseException</c>) and EVERY <c>harmony.Patch</c> in
/// the live game dies. Loading <c>libgcc_s.so.1</c> with <c>RTLD_GLOBAL</c> before the first patch puts the
/// unwind symbols where the helper's relocation can find them (verified standalone: the extracted helper fails a
/// plain dlopen and loads cleanly after this preload). The handle is never released, so a later
/// <c>dlclose</c> by anything else — the game drops its own crash reporter when it detects mods — cannot take
/// the unwinder back out from under a patch applied afterwards.
/// </para>
/// <para>
/// <b>Embedders must call <see cref="EnsureLoaded()"/> themselves if they patch before composing the runtime.</b>
/// The composition path (<c>Sts2ReusableLiveCompositionFactory</c>) preloads on the way to installing the
/// bridge's own hooks, which is early enough for everything the bridge does — but an embedded consumer that
/// applies its own Harmony patches during mod init runs BEFORE that, and every one of those patches fails
/// silently-and-degraded. That is not hypothetical: CouchCoop lost its whole patch set, and with it the lobby
/// QR button, to exactly this ordering.
/// </para>
/// <para>
/// Whether the symbols happen to be present without the preload is startup luck, not a property of the game
/// build — in one observed session two dlopens 18ms apart disagreed — so nothing here is conditional on a
/// version or a branch.
/// </para>
/// </remarks>
public static class Sts2MonoModNativeDependencies
{
    private const string LibgccName = "libgcc_s.so.1";
    private const int RtldNow = 0x002;
    private const int RtldGlobal = 0x100;

    private static readonly object Sync = new();
    private static bool _attempted;
    private static Sts2NativeUnwinderPreload _result = Sts2NativeUnwinderPreload.NotAttempted;

    /// <summary>
    /// Put libgcc's unwinder in the global symbol namespace, once per process. Safe to call from anywhere,
    /// including before any Godot or game type has been touched.
    /// </summary>
    /// <returns>
    /// What happened — including on the second and later calls, which return the FIRST call's cached answer
    /// rather than repeating the dlopen.
    /// </returns>
    public static Sts2NativeUnwinderPreload EnsureLoaded()
    {
        lock (Sync)
        {
            if (_attempted)
            {
                return _result;
            }

            if (!OperatingSystem.IsLinux())
            {
                // Only the Linux helper resolves its unwinder this way; elsewhere there is nothing to preload
                // and reporting a failure would read as a problem.
                _attempted = true;
                _result = Sts2NativeUnwinderPreload.NotNeeded;
                return _result;
            }

            _attempted = true;
            try
            {
                _result = DlOpenGlobal(LibgccName) != IntPtr.Zero
                    ? Sts2NativeUnwinderPreload.Success
                    : Sts2NativeUnwinderPreload.Failure(LastDlError() ?? "dlopen returned null");
            }
            catch (Exception ex)
            {
                _result = Sts2NativeUnwinderPreload.Failure($"{ex.GetType().Name}: {ex.Message}");
            }

            return _result;
        }
    }

    /// <summary>
    /// <see cref="EnsureLoaded()"/>, narrated into <paramref name="logStream"/>. The bridge's own call path.
    /// </summary>
    /// <remarks>
    /// Logs the cached outcome even when an embedder got here first, so the bridge log still says what the
    /// unwinder did in this process rather than going quiet because someone else asked before it.
    /// </remarks>
    internal static void EnsureLoaded(ILogStream logStream)
    {
        var result = EnsureLoaded();
        if (!result.Supported)
        {
            return;
        }

        if (result.Loaded)
        {
            logStream.Write(
                BridgeLogLevel.Info,
                "bridge.hooks.native-deps",
                $"Preloaded {LibgccName} (RTLD_GLOBAL) so MonoMod's exec-helper can resolve libgcc unwind symbols.");
        }
        else
        {
            logStream.Write(
                BridgeLogLevel.Warn,
                "bridge.hooks.native-deps",
                $"Failed to preload {LibgccName}: {result.Error}. Harmony patching will likely fail with 'undefined symbol: _Unwind_RaiseException'.");
        }
    }

    private static IntPtr DlOpenGlobal(string library)
    {
        try
        {
            return Libdl.DlOpen(library, RtldNow | RtldGlobal);
        }
        catch (DllNotFoundException)
        {
            // glibc >= 2.34 merged libdl into libc; older distros only have libdl.so.2.
            return Libc.DlOpen(library, RtldNow | RtldGlobal);
        }
    }

    private static string? LastDlError()
    {
        try
        {
            return Marshal.PtrToStringAnsi(Libdl.DlError());
        }
        catch (DllNotFoundException)
        {
            return Marshal.PtrToStringAnsi(Libc.DlError());
        }
    }

    private static class Libdl
    {
        [DllImport("libdl.so.2", EntryPoint = "dlopen")]
        internal static extern IntPtr DlOpen(string path, int flags);

        [DllImport("libdl.so.2", EntryPoint = "dlerror")]
        internal static extern IntPtr DlError();
    }

    private static class Libc
    {
        [DllImport("libc.so.6", EntryPoint = "dlopen")]
        internal static extern IntPtr DlOpen(string path, int flags);

        [DllImport("libc.so.6", EntryPoint = "dlerror")]
        internal static extern IntPtr DlError();
    }
}

/// <summary>
/// The outcome of <see cref="Sts2MonoModNativeDependencies.EnsureLoaded()"/>, so a caller can log it in its own
/// voice instead of the bridge's.
/// </summary>
/// <param name="Supported">
/// Whether this platform needs the preload at all. <see langword="false"/> off Linux, where there is nothing to
/// do and nothing went wrong.
/// </param>
/// <param name="Loaded">Whether the unwinder is now in the global symbol namespace.</param>
/// <param name="Error">Why not, when <paramref name="Loaded"/> is <see langword="false"/> on a supported platform.</param>
public readonly record struct Sts2NativeUnwinderPreload(bool Supported, bool Loaded, string? Error)
{
    internal static Sts2NativeUnwinderPreload NotAttempted { get; } = new(Supported: true, Loaded: false, Error: null);

    internal static Sts2NativeUnwinderPreload NotNeeded { get; } = new(Supported: false, Loaded: false, Error: null);

    internal static Sts2NativeUnwinderPreload Success { get; } = new(Supported: true, Loaded: true, Error: null);

    internal static Sts2NativeUnwinderPreload Failure(string error) => new(Supported: true, Loaded: false, Error: error);
}
