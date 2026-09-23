using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;

// Standalone experiment only. No shipped project references this executable.
if (args.Contains("--self-test"))
{
    foreach (uint control in new uint[] { 0, 1, 4, 5, 0x80 })
    foreach (uint state in new uint[] { 0, 1, 4, 5, 0x80 })
    foreach (uint requested in new uint[] { 1, 4, 5 })
    {
        var original = new PowerState { Version = 1, ControlMask = control, StateMask = state };
        var changed = PowerState.WithDisabledThrottling(original, requested);
        if (changed.Version != 1 || changed.ControlMask != (control | requested) ||
            changed.StateMask != (state & ~requested) || original.ControlMask != control ||
            original.StateMask != state)
            throw new Exception("Power-state mask preservation failed");
    }
    if ((MacActivity.AllowIdleSystemSleep & (1UL << 20)) != 0 ||
        (MacActivity.AllowIdleSystemSleep & (1UL << 40)) != 0)
        throw new Exception("The probe must allow idle system/display sleep");
    Console.WriteLine("75 mask cases and activity-option checks passed; native interop untested.");
    return;
}

var highQos = args.Contains("--high-qos");
var honorTimer = args.Contains("--honor-timer-resolution");
var activity = args.Contains("--activity");
var durationIndex = Array.IndexOf(args, "--duration-ms");
var duration = durationIndex < 0 ? 1000 : int.Parse(args[durationIndex + 1]);
if (duration is < 0 or > 60000) throw new ArgumentException("duration must be between 0 and 60000 ms");
var result = new Dictionary<string, object?>
{
    ["schema"] = "seat-scheduling-probe/1",
    ["pid"] = Environment.ProcessId,
    ["os"] = RuntimeInformation.OSDescription,
    ["enabled"] = highQos || honorTimer || activity,
    ["scope"] = "this probe process only; no game policy is changed",
    ["nativeInterop"] = "unmeasured",
    ["gamePerformance"] = "unmeasured",
    ["effectiveQos"] = "unmeasured; configured masks are not effective scheduler classification",
};
try
{
    if (!(highQos || honorTimer || activity))
    {
        result["status"] = "disabled";
    }
    else
    {
        if (!args.Contains("--enable-native-experiment"))
            throw new ArgumentException("explicit --enable-native-experiment is required");
        if (activity && (highQos || honorTimer))
            throw new ArgumentException("macOS activity and Windows probes are separate experiments");
        if (highQos || honorTimer)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows probe requires Windows");
            var before = WindowsPower.Read();
            result["configuredBefore"] = before;
            var changed = PowerState.WithDisabledThrottling(before, (highQos ? 1u : 0u) | (honorTimer ? 4u : 0u));
            WindowsPower.Write(changed);
            try
            {
                result["configuredDuring"] = WindowsPower.Read();
                await Task.Delay(duration);
            }
            finally
            {
                WindowsPower.Write(before);
                var after = WindowsPower.Read();
                result["configuredAfter"] = after;
                if (!after.Equals(before)) throw new InvalidOperationException("Power configuration did not restore exactly");
            }
        }
        else
        {
            if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Activity probe requires macOS");
            using (var assertion = new MacActivity())
            {
                result["activityOptions"] = MacActivity.AllowIdleSystemSleep;
                await Task.Delay(duration);
            }
            result["activityEnded"] = true;
        }
        result["nativeInterop"] = "passed";
        result["status"] = "restored";
    }
}
catch (Exception error)
{
    result["status"] = "failed";
    result["error"] = error.ToString();
    Environment.ExitCode = 1;
}
Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));

[StructLayout(LayoutKind.Sequential)]
internal struct PowerState
{
    public uint Version;
    public uint ControlMask;
    public uint StateMask;

    internal static PowerState WithDisabledThrottling(PowerState original, uint mask) => new()
    {
        Version = original.Version,
        ControlMask = original.ControlMask | mask,
        StateMask = original.StateMask & ~mask,
    };
}

internal static class WindowsPower
{
    private const int ProcessPowerThrottling = 4;
    // GetCurrentProcess returns a pseudo-handle; it must not be closed.
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessInformation(nint process, int kind, ref PowerState state, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(nint process, int kind, ref PowerState state, uint size);

    internal static PowerState Read()
    {
        var state = new PowerState { Version = 1 };
        if (!GetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, 12))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (state.Version != 1) throw new InvalidOperationException("Unknown power-state version");
        return state;
    }

    internal static void Write(PowerState state)
    {
        if (!SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, 12))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
}

internal sealed class MacActivity : IDisposable
{
    // NSActivityUserInitiatedAllowingIdleSystemSleep: no latency-critical or sleep-denial flags.
    internal const ulong AllowIdleSystemSleep = 0x00ffffffUL & ~(1UL << 20);
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private nint _token;
    private readonly nint _processInfo;

    [DllImport(ObjC)] private static extern nint objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(ObjC)] private static extern nint sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint Send(nint obj, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendObject(nint obj, nint selector, nint value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint SendString(nint obj, nint selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint Begin(nint obj, nint selector, ulong options, nint reason);
    [DllImport(ObjC)] private static extern nint objc_retain(nint obj);
    [DllImport(ObjC)] private static extern void objc_release(nint obj);

    internal MacActivity()
    {
        // Load Foundation before looking up its Objective-C classes. Keep it mapped for the process lifetime.
        _ = NativeLibrary.Load("/System/Library/Frameworks/Foundation.framework/Foundation");
        var pool = Send(objc_getClass("NSAutoreleasePool"), sel_registerName("new"));
        try
        {
            _processInfo = Send(objc_getClass("NSProcessInfo"), sel_registerName("processInfo"));
            var reason = SendString(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), "Explicit seat scheduling experiment");
            if (_processInfo == 0 || reason == 0) throw new InvalidOperationException("Foundation setup failed");
            var token = Begin(_processInfo, sel_registerName("beginActivityWithOptions:reason:"), AllowIdleSystemSleep, reason);
            if (token == 0) throw new InvalidOperationException("No activity token returned");
            _token = objc_retain(token);
        }
        finally { Send(pool, sel_registerName("drain")); }
    }

    public void Dispose()
    {
        var token = Interlocked.Exchange(ref _token, 0);
        if (token == 0) return;
        SendObject(_processInfo, sel_registerName("endActivity:"), token);
        objc_release(token);
    }
}
