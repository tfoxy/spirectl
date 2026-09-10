using Spirectl.Sts2;
using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Artifacts;
using Spirectl.Sts2.Core.ConsoleCommands;
using Spirectl.Sts2.Core.Debugging;
using Spirectl.Sts2.Core.Fixtures;
using Spirectl.Sts2.Core.HotReload;
using Spirectl.Sts2.Core.Lifecycle;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Perspective;
using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2.Core.Scenarios;
using Spirectl.Sts2.Core.SceneInspection;
using Spirectl.Sts2.Core.State;
using Spirectl.Sts2.Core.Transport;
using Spirectl.BridgeMod.Services;
using Spirectl.Sts2.Common;
using Spirectl.Proto.V0;
using Google.Protobuf;
using System.Text.Json;
using Xunit;
using RestoreLobbyCharacterSnapshot = Spirectl.Sts2.Core.Restore.LobbyCharacterSnapshot;
using RestoreMultiplayerLobbySnapshot = Spirectl.Sts2.Core.Restore.MultiplayerLobbySnapshot;
using RestoreMultiplayerPlayerSnapshot = Spirectl.Sts2.Core.Restore.MultiplayerPlayerSnapshot;
using RestoreMultiplayerRestoreModeSnapshot = Spirectl.Sts2.Core.Restore.MultiplayerRestoreModeSnapshot;
using RestoreMultiplayerRestoreResultSnapshot = Spirectl.Sts2.Core.Restore.MultiplayerRestoreResultSnapshot;
using RestoreMultiplayerRestoreSnapshot = Spirectl.Sts2.Core.Restore.MultiplayerRestoreSnapshot;

namespace Spirectl.BridgeMod.Tests;

public sealed partial class GrpcBridgeServiceTests
{
    [Fact]
    public void HandshakeAndStateShareBridgeVersionFormat()
    {
        var service = new GrpcBridgeService(BridgeRuntimeBootstrap.CreateScaffold());

        var handshake = service.HandleHandshake(new HandshakeRequest
        {
            CliVersion = "0.0.0",
            RequestedSchemaVersion = "spirectl/v0",
            Mode = "normal",
            TransportKind = TransportKind.Mock,
        });
        var state = service.HandleGetState(new StateRequest());

        Assert.NotNull(handshake.Success);
        Assert.NotNull(state.Success);
        Assert.StartsWith("spirectl-bridge/", handshake.Success.BridgeVersion);
        Assert.NotNull(handshake.Success.BuildIdentity);
        Assert.Equal(handshake.Success.BridgeVersion, handshake.Success.BuildIdentity.BridgeVersion);
        Assert.Equal("0.1.0", handshake.Success.BuildIdentity.BridgeSemver);
        Assert.False(string.IsNullOrWhiteSpace(handshake.Success.BuildIdentity.AssemblyInformationalVersion));
        Assert.False(string.IsNullOrWhiteSpace(handshake.Success.BuildIdentity.BuiltAtUtc));
    }

    [Fact]
    public void GetLogsReturnsEntryCursorsAndNextCursor()
    {
        var stream = new InMemoryLogStream(capacity: 5, source: DataSourceKind.Live, provisional: false);
        stream.Write(BridgeLogLevel.Debug, "bridge.state", "state-alpha");
        stream.Write(BridgeLogLevel.Warn, "bridge.action", "action-1");
        stream.Write(BridgeLogLevel.Error, "bridge.action", "action-2");
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
            LogStream = stream,
            PerspectiveProvider = new DefaultPerspectiveProvider(),
            BridgeHost = new FixedBridgeHost(new BridgeHostStatus("ipc", true, "Live IPC host running.")),
        });
        var service = new GrpcBridgeService(runtime);

        var result = service.HandleGetLogs(new LogsRequest
        {
            Limit = 2,
        });

        Assert.NotNull(result.Success);
        Assert.Equal((ulong)4, result.Success.NextCursor);
        Assert.Collection(result.Success.Entries,
            entry =>
            {
                Assert.Equal((ulong)3, entry.Cursor);
                Assert.Equal("action-1", entry.Message);
            },
            entry =>
            {
                Assert.Equal((ulong)4, entry.Cursor);
                Assert.Equal("action-2", entry.Message);
            });
    }

    [Fact]
    public void RuntimeSceneTreeReturnsNotImplementedForScaffoldRuntime()
    {
        var service = new GrpcBridgeService(BridgeRuntimeBootstrap.CreateScaffold());

        var result = service.HandleGetRuntimeSceneTree(new RuntimeSceneTreeRequest());

        Assert.NotNull(result.Error);
        Assert.Equal(BridgeErrorCode.NotImplemented, result.Error.Code);
        Assert.Equal("command", result.Error.Details[0].Field);
        Assert.Equal("dev scene tree", result.Error.Details[0].Value);
    }

}
