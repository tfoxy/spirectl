using System;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Saves;
using Spirectl.Sts2.Core.Logging;

namespace Spirectl.Sts2.Live;


/// <summary>
/// Background-throttle overrides: keep a windowed instance usable as an automation target while its
/// window sits in the background. Opt-in only (<see cref="Sts2BackgroundThrottleOptions"/>), because
/// the behavior it overrides is deliberate power saving for players.
///
/// Two independent layers make a backgrounded instance unusable, so there are two levers:
///
/// 1. <b>Frame cap.</b> The game reacts to window focus-out by lowering <c>Engine.MaxFps</c> for as
///    long as the window is unfocused, which halves every main-thread cadence the bridge depends on
///    (dispatcher drain, state capture, scene streaming). The prefix below makes the enter-background
///    transition a no-op, so the cap is never applied — nothing else needs patching, because the
///    matching restore path is already a no-op when the transition never happened, and no saved
///    setting is touched (in memory or on disk). Also re-asserts the configured frame limit at
///    install, in case the window was already backgrounded before the bridge attached.
///
/// 2. <b>Parked main loop.</b> The larger stall is engine-level. The engine draws every iteration and
///    takes a swapchain image with an unbounded wait; when a compositor stops compositing an
///    invisible surface it also stops releasing that surface's buffers, so the acquire never returns
///    and the entire main thread parks inside it — measured as 0% CPU in <c>poll()</c> with zero
///    protocol traffic — until the window is visible again. Everything on that thread (this bridge's
///    dispatcher, the embedder's producer, the game's own action queue) stops with it, which is why
///    the backlog replays in a burst on un-hide.
///    <para>
///    The engine's own guard against this is to stop drawing entirely while the surface is suspended,
///    but it can only reach that state two ways: the compositor advertising the toplevel as suspended
///    (not sent by every compositor generation), or its legacy emulated-vsync path, which it enables
///    only when vertical sync is ON and the compositor has no fifo protocol. So the cure is
///    counter-intuitive: pin vsync ON. With vsync OFF neither route is available, the engine keeps
///    drawing into a surface nobody will release, and it hangs. This was measured on the reporting
///    machine — vsync off froze on every hide; vsync on did not.
///    </para>
///    The frame limit still comes from the game's own setting, so this does not change the frame rate
///    while the window is visible, and nothing is persisted (the saved graphics setting is untouched).
///    Re-applied after the game re-applies its own sync setting, so opening the options screen cannot
///    drop the instance back into the hanging configuration mid-session.
///
/// Headless instances need neither lever (no window, and the game already skips its background
/// handling when the display server is headless), so lever 2 is skipped there.
/// </summary>
internal static class Sts2BackgroundThrottleHooks
{
    private const string LogTarget = "bridge.lifecycle.background-throttle";

    private static readonly object Sync = new();
    private static bool _installed;
    private static bool _pinVsync;

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

            var harmony = new Harmony("spirectl.lifecycle.background-throttle");
            var uncappedFps = Sts2BackgroundThrottleOptions.FpsUncapEnabled()
                && TryInstallFpsUncap(harmony, logStream);
            var pinnedVsync = Sts2BackgroundThrottleOptions.VsyncPinEnabled()
                && TryInstallVsyncPin(harmony, logStream);

            _installed = uncappedFps || pinnedVsync;
            if (!_installed)
            {
                logStream.Write(
                    BridgeLogLevel.Warn,
                    LogTarget,
                    "Background-throttle override requested but no lever could be installed; "
                        + "this instance will still throttle while its window is in the background.");
                return;
            }

            logStream.Write(
                BridgeLogLevel.Info,
                LogTarget,
                $"Installed background-throttle override [backgroundFpsCapSkipped={uncappedFps}, "
                    + $"vsyncPinned={pinnedVsync}]; this instance stays responsive while backgrounded.");
        }
    }

    private static bool TryInstallFpsUncap(Harmony harmony, ILogStream logStream)
    {
        var enterBackground = AccessTools.Method(typeof(NBackgroundModeHandler), "EnterBackgroundMode");
        var prefix = typeof(Sts2BackgroundThrottleHooks).GetMethod(
            nameof(EnterBackgroundModePrefix),
            BindingFlags.NonPublic | BindingFlags.Static);

        if (enterBackground is null || prefix is null)
        {
            logStream.Write(
                BridgeLogLevel.Warn,
                LogTarget,
                "Unable to skip the background frame cap; the game's background-mode handler was not found.");
            return false;
        }

        try
        {
            harmony.Patch(enterBackground, prefix: new HarmonyMethod(prefix));
        }
        catch (Exception ex)
        {
            logStream.Write(
                BridgeLogLevel.Warn,
                LogTarget,
                $"Skipping the background frame-cap override because Harmony patching failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }

        // The window may already have been backgrounded before the bridge attached (a relaunch while
        // the terminal holds focus), in which case the cap is live and the prefix above only covers
        // future transitions. Re-assert the configured limit on the game thread.
        Callable.From(RestoreConfiguredFpsLimit).CallDeferred();
        return true;
    }

    private static bool TryInstallVsyncPin(Harmony harmony, ILogStream logStream)
    {
        if (IsHeadless())
        {
            return false;
        }

        var applySync = AccessTools.Method(typeof(NGame), nameof(NGame.ApplySyncSetting));
        var postfix = typeof(Sts2BackgroundThrottleHooks).GetMethod(
            nameof(ApplySyncSettingPostfix),
            BindingFlags.NonPublic | BindingFlags.Static);

        if (applySync is null || postfix is null)
        {
            logStream.Write(
                BridgeLogLevel.Warn,
                LogTarget,
                "The game's sync-setting entry point was not found; vertical sync will be pinned once "
                    + "but can be undone from the in-game graphics settings.");
        }
        else
        {
            try
            {
                harmony.Patch(applySync, postfix: new HarmonyMethod(postfix));
            }
            catch (Exception ex)
            {
                logStream.Write(
                    BridgeLogLevel.Warn,
                    LogTarget,
                    $"Could not keep vertical sync pinned across settings changes: {ex.GetType().Name}: {ex.Message}");
            }
        }

        _pinVsync = true;
        Callable.From(() => ApplyVsyncPin(logStream)).CallDeferred();
        return true;
    }

    private static bool EnterBackgroundModePrefix() => false;

    private static void ApplySyncSettingPostfix()
    {
        if (_pinVsync)
        {
            ApplyVsyncPin(null);
        }
    }

    private static void ApplyVsyncPin(ILogStream? logStream)
    {
        if (IsHeadless())
        {
            return;
        }

        try
        {
            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Enabled);
            logStream?.Write(
                BridgeLogLevel.Info,
                LogTarget,
                $"Pinned vertical sync for this session [mode={DisplayServer.WindowGetVsyncMode()}]; "
                    + "the saved graphics setting is untouched.");
        }
        catch (Exception)
        {
            // A display server without a real window (or mid-teardown) is not worth failing a launch
            // over; the frame-cap lever is unaffected.
        }
    }

    private static void RestoreConfiguredFpsLimit()
    {
        try
        {
            var fpsLimit = SaveManager.Instance?.SettingsSave?.FpsLimit;
            if (fpsLimit is > 0 && Engine.MaxFps != fpsLimit)
            {
                Engine.MaxFps = fpsLimit.Value;
            }
        }
        catch (Exception)
        {
            // Saves not loaded yet (or unavailable): the prefix above still covers every later
            // focus-out, which is the case that matters.
        }
    }

    private static bool IsHeadless()
    {
        try
        {
            return DisplayServer.GetName().Equals("headless", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
