using HotMod.Shell.Runtime;
using Xunit;

namespace HotMod.ReloadTests;

public sealed class HotRuntimeTests
{
    [Fact]
    public async Task ReloadBeforeRuntimeInitializationReportsRestartRequired()
    {
        var runtime = new HotRuntime();

        var report = await runtime.ReloadAsync("test");

        Assert.Equal(ReloadStatus.Failed, report.Status);
        Assert.Equal(ReloadErrorCode.ReloadRestartRequired, report.Error?.Code);
        Assert.True(report.Error?.RestartRequired);
    }

    [Fact]
    public async Task RuntimeDisposesAndUnloadsPreviousGenerationAfterSuccessfulSwap()
    {
        var runtime = new HotRuntime();
        var hotReloadDirectory = Directory.CreateTempSubdirectory("hotmod-runtime-");
        var logicPath = Path.Combine(hotReloadDirectory.FullName, "HotMod.Logic.dll");
        var options = Options(hotReloadDirectory.FullName, logicPath);
        await CopyFixtureAsync("ValidLogic", logicPath);
        await runtime.InitializeAsync(options);

        await CopyFixtureAsync("SecondValidLogic", logicPath);
        var report = await runtime.ReloadAsync("test");

        Assert.Equal(ReloadStatus.Loaded, report.Status);
        Assert.Equal(2, report.Generation);
        Assert.True(report.PreviousDisposed);
        Assert.True(report.PreviousUnloadRequested);
        Assert.True(report.PreviousCollected);
    }

    [Fact]
    public async Task PreviousDisposeFailureIsReportedAfterSuccessfulSwap()
    {
        var runtime = new HotRuntime();
        var hotReloadDirectory = Directory.CreateTempSubdirectory("hotmod-runtime-");
        var logicPath = Path.Combine(hotReloadDirectory.FullName, "HotMod.Logic.dll");
        var options = Options(hotReloadDirectory.FullName, logicPath);
        await CopyFixtureAsync("DisposeFailedLogic", logicPath);
        await runtime.InitializeAsync(options);

        await CopyFixtureAsync("ValidLogic", logicPath);
        var report = await runtime.ReloadAsync("test");

        Assert.Equal(ReloadStatus.Loaded, report.Status);
        Assert.Contains(report.Warnings, warning => warning.Code == ReloadErrorCode.ReloadPreviousDisposeFailed);
    }

    [Fact]
    public async Task DispatchHookRoutesToLoadedLogicGeneration()
    {
        var runtime = new HotRuntime();
        var hotReloadDirectory = Directory.CreateTempSubdirectory("hotmod-runtime-");
        var logicPath = Path.Combine(hotReloadDirectory.FullName, "HotMod.Logic.dll");
        var options = Options(hotReloadDirectory.FullName, logicPath);
        await CopyFixtureAsync("ValidLogic", logicPath);
        await runtime.InitializeAsync(options);

        var result = runtime.DispatchHook("combat.turn", new Dictionary<string, string>());

        Assert.NotNull(result);
        Assert.True(result.Handled);
        Assert.Contains("valid", result.Messages);
    }

    [Fact]
    public async Task RuntimeReportsWhenPreviousGenerationIsNotCollected()
    {
        var runtime = new HotRuntime();
        var hotReloadDirectory = Directory.CreateTempSubdirectory("hotmod-runtime-");
        var logicPath = Path.Combine(hotReloadDirectory.FullName, "HotMod.Logic.dll");
        var options = Options(hotReloadDirectory.FullName, logicPath);
        await CopyFixtureAsync("LeakyLogic", logicPath);
        await runtime.InitializeAsync(options);

        await CopyFixtureAsync("ValidLogic", logicPath);
        var report = await runtime.ReloadAsync("test");

        Assert.Equal(ReloadStatus.Loaded, report.Status);
        Assert.False(report.PreviousCollected);
        Assert.Contains(report.Warnings, warning => warning.Code == ReloadErrorCode.ReloadUnloadNotCollected);
    }

    private static HotRuntimeOptions Options(string hotReloadDirectory, string logicPath)
    {
        return new HotRuntimeOptions(
            HotReloadEnabled: false,
            DevOverlayEnabled: false,
            LogicAssemblyPath: logicPath,
            ReloadMarkerPath: Path.Combine(hotReloadDirectory, "reload.marker"),
            EntryTypeName: null,
            ShadowRoot: Path.Combine(hotReloadDirectory, ".shadow"));
    }

    private static async Task CopyFixtureAsync(string fixtureName, string destination)
    {
        var source = await FixtureLogicBuilder.BuildAsync(fixtureName);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
        foreach (var sidecar in Directory.EnumerateFiles(Path.GetDirectoryName(source)!, "*.*"))
        {
            var fileName = Path.GetFileName(sidecar);
            if (string.Equals(fileName, Path.GetFileName(source), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            File.Copy(sidecar, Path.Combine(Path.GetDirectoryName(destination)!, fileName), overwrite: true);
        }
    }
}
