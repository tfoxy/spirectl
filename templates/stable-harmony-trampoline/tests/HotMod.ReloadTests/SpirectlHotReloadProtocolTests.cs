using System.Text.Json;
using HotMod.Shell.Runtime;
using Xunit;

namespace HotMod.ReloadTests;

public sealed class SpirectlHotReloadProtocolTests
{
    [Fact]
    public async Task StatusBeforeReloadReportsProtocolAndGenerationZero()
    {
        var directory = Directory.CreateTempSubdirectory("hotmod-protocol-");
        var runtime = new HotRuntime();
        await runtime.InitializeAsync(Options(directory.FullName, Path.Combine(directory.FullName, "missing.dll")));

        var statusJson = runtime.DescribeSpirectlHotReloadStatusJson();
        using var document = JsonDocument.Parse(statusJson);

        Assert.Equal("spirectl.m57.hot-reload-shell", document.RootElement.GetProperty("protocol").GetProperty("id").GetString());
        Assert.Equal(0, document.RootElement.GetProperty("protocol").GetProperty("version").GetInt32());
        Assert.Equal("test-shell", document.RootElement.GetProperty("shellModId").GetString());
        Assert.Equal(0, document.RootElement.GetProperty("activeGeneration").GetInt32());
    }

    [Fact]
    public async Task MismatchedArtifactPathReturnsFailureWithoutChangingGeneration()
    {
        var directory = Directory.CreateTempSubdirectory("hotmod-protocol-");
        var runtime = new HotRuntime();
        await runtime.InitializeAsync(Options(directory.FullName, Path.Combine(directory.FullName, "logic.dll")));

        var responseJson = await runtime.RequestSpirectlHotReloadJsonAsync("""
            {
              "requestId": "hr-test",
              "projectId": "my-hot-mod",
              "shellModId": "test-shell",
              "logicArtifactPath": "/wrong/path.dll",
              "expectedContractVersion": 0,
              "waitForCompletion": true,
              "timeoutMs": 1000
            }
            """);
        using var document = JsonDocument.Parse(responseJson);

        Assert.False(document.RootElement.GetProperty("accepted").GetBoolean());
        Assert.Equal("failed", document.RootElement.GetProperty("report").GetProperty("status").GetString());
        Assert.Equal(0, document.RootElement.GetProperty("status").GetProperty("activeGeneration").GetInt32());
    }

    private static HotRuntimeOptions Options(string hotReloadDirectory, string logicPath)
    {
        return new HotRuntimeOptions(
            HotReloadEnabled: false,
            DevOverlayEnabled: false,
            LogicAssemblyPath: logicPath,
            ReloadMarkerPath: Path.Combine(hotReloadDirectory, "reload.marker"),
            EntryTypeName: null,
            ShadowRoot: Path.Combine(hotReloadDirectory, ".shadow"),
            ShellModId: "test-shell");
    }
}
