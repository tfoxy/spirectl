using Spirectl.Sts2;
using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.HotReload;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Perspective;
using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2.Core.State;
using Spirectl.Sts2.Core.Transport;
using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

public sealed class BridgeRuntimeTests
{
    [Fact]
    public void BootstrapUsesLocalPerspectiveByDefault()
    {
        var runtime = BridgeRuntimeBootstrap.CreateScaffold();

        var status = runtime.DescribeStatus();

        Assert.Equal(PlayerScope.Local, status.DefaultPerspective.Scope);
        Assert.Null(status.DefaultPerspective.PlayerId);
        Assert.True(status.DefaultPerspective.UsesDefault);
    }

    [Fact]
    public void BootstrapWiresNeutralHostStatus()
    {
        var runtime = BridgeRuntimeBootstrap.CreateScaffold();

        var status = runtime.DescribeStatus();

        Assert.False(status.Host.IsRunning);
        Assert.Equal("unbound", status.Host.TransportKind);
        Assert.Contains("Loader integration", status.Host.Message);
    }

    [Fact]
    public void SelectedCardViewStateTogglesByPlayerAndCard()
    {
        var state = new Sts2SelectedCardViewState();

        var selected = state.Toggle("p:100", "card:p:100:hand:0");
        Assert.Equal(new Sts2SelectedCardViewEntry("card:p:100:hand:0", "p:100"), selected);
        Assert.Equal(selected, state.Get("p:100"));

        var replaced = state.Toggle("p:100", "card:p:100:hand:1");
        Assert.Equal(new Sts2SelectedCardViewEntry("card:p:100:hand:1", "p:100"), replaced);
        Assert.Equal(replaced, state.Get("p:100"));

        var cleared = state.Toggle("p:100", "card:p:100:hand:1");
        Assert.Null(cleared);
        Assert.Null(state.Get("p:100"));
    }

    [Fact]
    public void HandshakeReflectsLiveHostStatus()
    {
        var runtime = BridgeRuntime.Create(new BridgeRuntimeOptions
        {
            StateExtractor = new FixedSnapshotExtractor(
                new GameStateSnapshot(
                    SchemaVersion: "spirectl/v0",
                    GameVersion: "sts2-live",
                    BridgeVersion: BridgeBuildInfo.BridgeVersion,
                    Source: DataSourceKind.Live,
                    Provisional: false,
                    ScreenType: "main-menu",
                    ScreenTitle: "Main Menu",
                    ScreenInstanceId: "screen:main-menu:live",
                    ResolvedPerspective: new PlayerPerspective(PlayerScope.Local, null, UsesDefault: true),
                    Menu: new MenuStateSnapshot("main-menu", "Main Menu"),
                    Lobby: null,
                    Run: null,
                    Combat: null,
                    Choices: [],
                    AvailableActions: [],
                    Notices: [],
                    Debug: null)),
            ActionHandler = new PlaceholderActionHandler(),
            LogStream = new InMemoryLogStream(),
            PerspectiveProvider = new DefaultPerspectiveProvider(),
            BridgeHost = new FixedBridgeHost(new BridgeHostStatus("ipc", true, "Live IPC host running.")),
        });

        var handshake = runtime.DescribeHandshake(new BridgeHandshakeRequest(
            "0.0.0",
            "spirectl/v0",
            "normal",
            "ipc"));

        Assert.Equal("ipc", handshake.TransportKind);
        Assert.Equal(RuntimeAttachmentState.Attached, handshake.AttachmentState);
        Assert.Equal(DataSourceKind.Live, handshake.Source);
        Assert.False(handshake.Provisional);
    }

    [Fact]
    public void PlaceholderHotReloadStatusIsUnsupported()
    {
        var runtime = BridgeRuntimeBootstrap.CreateScaffold();

        var result = runtime.GetHotReloadStatus(new HotReloadStatusRequestSnapshot(
            "hr-status-1",
            "my-hot-mod",
            "missing-shell"));

        Assert.Null(result.Error);
        Assert.NotNull(result.Status);
        Assert.False(result.Status.Supported);
        Assert.Contains(result.Notices, notice => notice.Code == "hot-reload-shell-unsupported");
    }

    [Fact]
    public void ReflectionHotReloadControlReturnsFakeShellStatus()
    {
        var logStream = new InMemoryLogStream();
        var control = new ReflectionHotReloadControl(logStream);

        var result = control.GetStatus(new HotReloadStatusRequestSnapshot(
            "hr-status-2",
            "my-hot-mod",
            "bridge-test-shell"));

        Assert.Null(result.Error);
        Assert.NotNull(result.Status);
        Assert.True(result.Status.Supported);
        Assert.Equal("bridge-test-shell", result.Status.ShellModId);
        Assert.Equal((uint)7, result.Status.ActiveGeneration);
        Assert.Contains(logStream.Read(new LogQuery(20, AfterCursor: null, MinimumLevel: null, TargetFilter: null)).Entries, entry => entry.Target == "bridge.hot_reload");
    }

    [Fact]
    public void ReflectionHotReloadControlReportsMissingShellAsUnsupportedStatus()
    {
        var control = new ReflectionHotReloadControl(new InMemoryLogStream());

        var result = control.GetStatus(new HotReloadStatusRequestSnapshot(
            "hr-status-missing",
            "my-hot-mod",
            "not-loaded-shell"));

        Assert.Null(result.Error);
        Assert.NotNull(result.Status);
        Assert.False(result.Status.Supported);
        Assert.Equal("not-loaded-shell", result.Status.ShellModId);
        Assert.Contains(result.Notices, notice => notice.Code == "hot-reload-shell-not-running");
    }

    [Fact]
    public async Task ReflectionHotReloadControlReturnsFakeReloadReport()
    {
        var control = new ReflectionHotReloadControl(new InMemoryLogStream());

        var result = await control.RequestReloadAsync(new HotReloadRequestSnapshot(
            "hr-request-1",
            "my-hot-mod",
            "bridge-test-shell",
            "/mods/MyHotMod.Shell/hot-reload/MyHotMod.Logic.dll",
            1,
            WaitForCompletion: true,
            TimeoutMs: 30_000));

        Assert.Null(result.Error);
        Assert.True(result.Accepted);
        Assert.NotNull(result.Report);
        Assert.Equal("loaded", result.Report.Status);
        Assert.Equal((uint)8, result.Report.Generation);
    }

    private sealed class FixedSnapshotExtractor(GameStateSnapshot snapshot) : IGameStateExtractor
    {
        public GameStateSnapshot Extract(GameStateQuery query, PlayerPerspective perspective)
        {
            return snapshot with { ResolvedPerspective = perspective };
        }
    }

    private sealed class FixedBridgeHost(BridgeHostStatus status) : IBridgeHost
    {
        public BridgeHostStatus DescribeStatus() => status;
    }
}

public static class BridgeTestHotReloadShell
{
    public static string DescribeSpirectlHotReloadStatusJson()
    {
        return """
            {
              "protocol": { "id": "spirectl.m57.hot-reload-shell", "version": 0 },
              "shellModId": "bridge-test-shell",
              "activeGeneration": 7,
              "expectedLogicArtifactPath": "/mods/MyHotMod.Shell/hot-reload/MyHotMod.Logic.dll",
              "contractVersion": 0,
              "reloadInProgress": false,
              "lastReloadReport": null,
              "restartRequired": false
            }
            """;
    }

    public static Task<string> RequestSpirectlHotReloadJsonAsync(string requestJson)
    {
        return Task.FromResult("""
            {
              "accepted": true,
              "status": {
                "protocol": { "id": "spirectl.m57.hot-reload-shell", "version": 0 },
                "shellModId": "bridge-test-shell",
                "activeGeneration": 8,
                "expectedLogicArtifactPath": "/mods/MyHotMod.Shell/hot-reload/MyHotMod.Logic.dll",
                "contractVersion": 0,
                "reloadInProgress": false,
                "lastReloadReport": null,
                "restartRequired": false
              },
              "report": {
                "status": "loaded",
                "generation": 8,
                "requestedAt": "2026-04-24T12:00:00Z",
                "sourceAssemblyPath": "/mods/MyHotMod.Shell/hot-reload/MyHotMod.Logic.dll",
                "shadowAssemblyPath": "/mods/MyHotMod.Shell/hot-reload/.shadow/generation-8/MyHotMod.Logic.dll",
                "contractVersion": 0,
                "logicAssemblyName": "MyHotMod.Logic",
                "entryType": "MyHotMod.Logic.HotLogic",
                "previousGeneration": 7,
                "previousRemainsActive": false,
                "previousDisposed": true,
                "previousUnloadRequested": true,
                "previousCollected": true,
                "durationMs": 12,
                "error": null,
                "warnings": []
              },
              "notices": []
            }
            """);
    }
}
