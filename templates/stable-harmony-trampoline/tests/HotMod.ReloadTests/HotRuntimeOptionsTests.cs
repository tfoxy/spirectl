using HotMod.Shell.Runtime;
using Xunit;

namespace HotMod.ReloadTests;

public sealed class HotRuntimeOptionsTests : IDisposable
{
    private static readonly string[] Keys =
    [
        "HOTMOD_HOT_RELOAD",
        "HOTMOD_LOGIC_PATH",
        "HOTMOD_RELOAD_MARKER",
        "HOTMOD_ENTRY_TYPE",
        "HOTMOD_SHADOW_ROOT",
        "HOTMOD_DEV_OVERLAY"
    ];

    public HotRuntimeOptionsTests()
    {
        ClearEnvironment();
    }

    public void Dispose()
    {
        ClearEnvironment();
    }

    [Fact]
    public void FromEnvironmentUsesDisabledDefaultsUnderBaseDirectory()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        var options = HotRuntimeOptions.FromEnvironment(baseDirectory);

        Assert.False(options.HotReloadEnabled);
        Assert.False(options.DevOverlayEnabled);
        Assert.Equal(Path.Combine(baseDirectory, "hot-reload", "HotMod.Logic.dll"), options.LogicAssemblyPath);
        Assert.Equal(Path.Combine(baseDirectory, "hot-reload", "reload.marker"), options.ReloadMarkerPath);
        Assert.Equal(Path.Combine(baseDirectory, "hot-reload", ".shadow"), options.ShadowRoot);
        Assert.Null(options.EntryTypeName);
    }

    [Fact]
    public void FromEnvironmentUsesExplicitOverrides()
    {
        Environment.SetEnvironmentVariable("HOTMOD_HOT_RELOAD", "true");
        Environment.SetEnvironmentVariable("HOTMOD_DEV_OVERLAY", "1");
        Environment.SetEnvironmentVariable("HOTMOD_LOGIC_PATH", "/custom/logic.dll");
        Environment.SetEnvironmentVariable("HOTMOD_RELOAD_MARKER", "/custom/reload.marker");
        Environment.SetEnvironmentVariable("HOTMOD_ENTRY_TYPE", "Example.HotLogic");
        Environment.SetEnvironmentVariable("HOTMOD_SHADOW_ROOT", "/custom/shadow");

        var options = HotRuntimeOptions.FromEnvironment("/base");

        Assert.True(options.HotReloadEnabled);
        Assert.True(options.DevOverlayEnabled);
        Assert.Equal("/custom/logic.dll", options.LogicAssemblyPath);
        Assert.Equal("/custom/reload.marker", options.ReloadMarkerPath);
        Assert.Equal("Example.HotLogic", options.EntryTypeName);
        Assert.Equal("/custom/shadow", options.ShadowRoot);
    }

    private static void ClearEnvironment()
    {
        foreach (var key in Keys)
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }
}
