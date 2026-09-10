using System;

namespace Spirectl.Sts2.Live;


/// <summary>
/// Opt-in switch for the bridge's background-throttle overrides
/// (<c>Sts2BackgroundThrottleHooks</c>).
///
/// A windowed STS2 instance stops being usable as an automation target once its window is in the
/// background: the game drops its own frame cap on window focus-out, and — once the compositor stops
/// releasing buffers for a surface nobody can see — the engine's unbounded swapchain acquire parks
/// the whole main loop, so bridge RPCs time out, live state/scene streaming stalls, and queued
/// actions all replay at once when the window becomes visible again.
///
/// That behavior is correct for players (it saves battery/GPU), so it stays ON by default and is only
/// overridden when the launcher sets <see cref="EnvVar"/> — `sts2 game launch
/// --disable-background-throttle` or `game.disableBackgroundThrottle: true`.
///
/// The two levers can be disabled individually while the mode is on, so a live session can bisect
/// which one is actually carrying a fix without relaunching with a different config.
/// </summary>
internal static class Sts2BackgroundThrottleOptions
{
    /// <summary>Master opt-in. Unset/empty means the shipped background behavior is untouched.</summary>
    public const string EnvVar = "SPIRECTL_BRIDGE_DISABLE_BACKGROUND_THROTTLE";

    /// <summary>Per-lever kill switch for the background FPS-cap skip. Default on.</summary>
    public const string FpsUncapEnvVar = "SPIRECTL_BRIDGE_BACKGROUND_FPS_UNCAP";

    /// <summary>Per-lever kill switch for the pinned vertical-sync mode. Default on.</summary>
    public const string VsyncPinEnvVar = "SPIRECTL_BRIDGE_BACKGROUND_VSYNC_ON";

    /// <summary>
    /// True only when <see cref="EnvVar"/> is set to a truthy value
    /// (<c>1</c>/<c>true</c>/<c>yes</c>/<c>on</c>, case-insensitive). Default false.
    /// </summary>
    public static bool IsEnabled() => ParseTruthy(Environment.GetEnvironmentVariable(EnvVar));

    /// <summary>
    /// Skip the game's on-focus-out frame cap so a backgrounded instance keeps running at its normal
    /// frame limit. On unless <see cref="FpsUncapEnvVar"/> is falsey; only consulted when
    /// <see cref="IsEnabled"/>.
    /// </summary>
    public static bool FpsUncapEnabled() => ParseOptOut(FpsUncapEnvVar);

    /// <summary>
    /// Pin vertical sync ON, which is what lets the engine notice that an invisible surface is not
    /// worth drawing and stop presenting instead of parking the main loop in the swapchain acquire.
    /// On unless <see cref="VsyncPinEnvVar"/> is falsey; only consulted when <see cref="IsEnabled"/>.
    /// </summary>
    public static bool VsyncPinEnabled() => ParseOptOut(VsyncPinEnvVar);

    private static bool ParseOptOut(string name)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        return raw.Trim().ToLowerInvariant() is not ("0" or "false" or "off" or "no");
    }

    private static bool ParseTruthy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            _ => false,
        };
    }
}
