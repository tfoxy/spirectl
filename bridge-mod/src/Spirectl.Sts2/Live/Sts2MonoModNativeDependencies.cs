using System.Runtime.InteropServices;
using Spirectl.Sts2.Core.Logging;

namespace Spirectl.Sts2.Live;

// Harmony detours go through MonoMod, which extracts a native exec-helper
// (`/tmp/mm-exhelper.so.*`) and dlopens it. That helper needs libgcc's unwinder
// (`_Unwind_RaiseException` and friends), and the game's bundled "MegaDot"
// runtime does not leave those symbols in the process's GLOBAL dynamic-symbol
// namespace — so the dlopen fails (`DllNotFoundException: ... undefined symbol:
// _Unwind_RaiseException`) and EVERY `harmony.Patch` in the live game dies.
// Loading libgcc_s.so.1 with RTLD_GLOBAL before the first patch puts the unwind
// symbols where the helper's relocation can find them (verified standalone: the
// extracted helper fails a plain dlopen and loads cleanly after this preload).
internal static class Sts2MonoModNativeDependencies
{
    private const string LibgccName = "libgcc_s.so.1";
    private const int RtldNow = 0x002;
    private const int RtldGlobal = 0x100;

    private static readonly object Sync = new();
    private static bool _attempted;

    public static void EnsureLoaded(ILogStream logStream)
    {
        lock (Sync)
        {
            if (_attempted || !OperatingSystem.IsLinux())
            {
                return;
            }

            _attempted = true;
            try
            {
                if (DlOpenGlobal(LibgccName) != IntPtr.Zero)
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
                        $"Failed to preload {LibgccName}: {LastDlError() ?? "dlopen returned null"}. Harmony patching will likely fail with 'undefined symbol: _Unwind_RaiseException'.");
                }
            }
            catch (Exception ex)
            {
                logStream.Write(
                    BridgeLogLevel.Warn,
                    "bridge.hooks.native-deps",
                    $"Failed to preload {LibgccName}: {ex.GetType().Name}: {ex.Message}");
            }
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
