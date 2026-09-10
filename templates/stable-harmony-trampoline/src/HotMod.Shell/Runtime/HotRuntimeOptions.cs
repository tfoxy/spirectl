namespace HotMod.Shell.Runtime;

public sealed record HotRuntimeOptions(
    bool HotReloadEnabled,
    bool DevOverlayEnabled,
    string LogicAssemblyPath,
    string ReloadMarkerPath,
    string? EntryTypeName,
    string ShadowRoot,
    string ShellModId = "hotmod.template")
{
    public static HotRuntimeOptions FromEnvironment(string baseDirectory)
    {
        var hotReloadValue = Environment.GetEnvironmentVariable("HOTMOD_HOT_RELOAD");
        var hotReloadEnabled = string.Equals(hotReloadValue, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(hotReloadValue, "true", StringComparison.OrdinalIgnoreCase);
        var devOverlayValue = Environment.GetEnvironmentVariable("HOTMOD_DEV_OVERLAY");
        var devOverlayEnabled = string.Equals(devOverlayValue, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(devOverlayValue, "true", StringComparison.OrdinalIgnoreCase);

        var hotReloadDirectory = Path.Combine(baseDirectory, "hot-reload");
        return new HotRuntimeOptions(
            hotReloadEnabled,
            devOverlayEnabled,
            Environment.GetEnvironmentVariable("HOTMOD_LOGIC_PATH")
                ?? Path.Combine(hotReloadDirectory, "HotMod.Logic.dll"),
            Environment.GetEnvironmentVariable("HOTMOD_RELOAD_MARKER")
                ?? Path.Combine(hotReloadDirectory, "reload.marker"),
            NormalizeOptional(Environment.GetEnvironmentVariable("HOTMOD_ENTRY_TYPE")),
            Environment.GetEnvironmentVariable("HOTMOD_SHADOW_ROOT")
                ?? Path.Combine(hotReloadDirectory, ".shadow"),
            Environment.GetEnvironmentVariable("HOTMOD_SHELL_MOD_ID")
                ?? "hotmod.template");
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
