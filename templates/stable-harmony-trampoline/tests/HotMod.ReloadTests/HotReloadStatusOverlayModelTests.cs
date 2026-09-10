using HotMod.Shell.DevOverlay;
using HotMod.Shell.Runtime;
using Xunit;

namespace HotMod.ReloadTests;

public sealed class HotReloadStatusOverlayModelTests
{
    [Fact]
    public void DisplayTextContainsGenerationReloadStatusUnloadAndRestart()
    {
        var report = ReloadReport.Loaded(
            generation: 3,
            requestedAt: DateTimeOffset.Parse("2026-04-24T00:00:00Z"),
            sourceAssemblyPath: "/logic.dll",
            shadowAssemblyPath: "/.shadow/generation-3/logic.dll",
            contractVersion: 0,
            logicAssemblyName: "HotMod.Logic",
            entryType: "HotMod.Logic.HotLogic",
            previousGeneration: 2,
            previousDisposed: true,
            previousUnloadRequested: true,
            previousCollected: false,
            durationMs: 42);
        var status = new SpirectlHotReloadStatus(
            new SpirectlHotReloadProtocolInfo("spirectl.m57.hot-reload-shell", 0),
            "hotmod.template",
            3,
            "/logic.dll",
            1,
            ReloadInProgress: false,
            report,
            RestartRequired: true);

        var text = HotReloadStatusOverlayModel.FromStatus(status).ToDisplayText();

        Assert.Contains("shell: hotmod.template", text);
        Assert.Contains("generation: 3", text);
        Assert.Contains("status: loaded", text);
        Assert.Contains("duration: 42 ms", text);
        Assert.Contains("unload: not collected", text);
        Assert.Contains("restart required", text);
    }
}
