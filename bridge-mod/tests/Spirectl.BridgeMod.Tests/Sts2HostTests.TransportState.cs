using Spirectl.Sts2;
using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Perspective;
using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2.Core.State;
using Spirectl.Sts2.Live;
using Spirectl.Sts2.Live.EncounterVisuals;
using Spirectl.Proto.V0;
using Google.Protobuf;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Serialization;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

extern alias GodotLive;
#if ENABLE_STS2_LIVE_HOST && ENABLE_STALE_STS2_SCREEN_FIXTURE_TESTS
extern alias Sts2Live;
#endif

using GodotLive::Godot;
#if ENABLE_STS2_LIVE_HOST && ENABLE_STALE_STS2_SCREEN_FIXTURE_TESTS
using Sts2Live::MegaCrit.Sts2.Core.Entities.Players;
using Sts2Live::MegaCrit.Sts2.Core.Map;
using Sts2Live::MegaCrit.Sts2.Core.Models;
using Sts2Live::MegaCrit.Sts2.Core.Models.Cards.Mocks;
using Sts2Live::MegaCrit.Sts2.Core.Rooms;
using Sts2Live::MegaCrit.Sts2.Core.Runs;
using Sts2Live::MegaCrit.Sts2.Core.Runs.History;
#endif

file sealed class TestSynchronizationContext : SynchronizationContext, IMainThreadDispatcherDiagnostics
{
    private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = [];

    public int QueueDepth
    {
        get
        {
            lock (_queue)
            {
                return _queue.Count;
            }
        }
    }

    public DateTimeOffset? LastDrainAtUtc { get; private set; }

    public string? DispatcherNote => null;

    public bool? IsApplicationFocused { get; private set; }

    public DateTimeOffset? LastFocusChangedAtUtc { get; private set; }

    public override void Post(SendOrPostCallback d, object? state)
    {
        lock (_queue)
        {
            _queue.Enqueue((d, state));
        }
    }

    public void DrainOne()
    {
        (SendOrPostCallback Callback, object? State) work;
        lock (_queue)
        {
            work = _queue.Dequeue();
        }

        var previous = Current;
        try
        {
            SetSynchronizationContext(this);
            work.Callback(work.State);
        }
        finally
        {
            SetSynchronizationContext(previous);
        }

        LastDrainAtUtc = DateTimeOffset.UtcNow;
    }

    public void DrainEmpty()
    {
        LastDrainAtUtc = DateTimeOffset.UtcNow;
    }
}

public sealed partial class Sts2HostTests
{
    [Fact]
    public void ResolveSocketPathUsesConfiguredPathFirst()
    {
        var socketPath = UnixSocketBridgeOptions.ResolveSocketPath("/tmp/custom-spirectl.sock");

        Assert.Equal("/tmp/custom-spirectl.sock", socketPath);
    }

    [Fact]
    public void BridgeEndpointOptionsDefaultsToUnixSocketEndpoint()
    {
        using var _ = WithoutConfiguredBridgeEndpointEnvironment();

        var endpoint = BridgeEndpointOptions.Resolve();

        Assert.Equal("ipc", endpoint.TransportKind);
        Assert.Equal("unix-socket", endpoint.EndpointKind);
        Assert.Equal(UnixSocketBridgeOptions.DefaultSocketPath, endpoint.EndpointDisplay);
    }

    [Fact]
    public void BridgeEndpointOptionsRejectsConflictingConfiguredInputs()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            BridgeEndpointOptions.Resolve(
                configuredSocketPath: "/tmp/spirectl.sock",
                configuredPipeName: null,
                configuredTcpAddress: "127.0.0.1:51173"));

        Assert.Contains("multiple live bridge endpoints", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BridgeEndpointOptionsUsesConfiguredPipeName()
    {
        using var _ = WithoutConfiguredBridgeEndpointEnvironment();

        var endpoint = BridgeEndpointOptions.Resolve(configuredPipeName: "spirectl-test-pipe");

        Assert.Equal("ipc", endpoint.TransportKind);
        Assert.Equal("named-pipe", endpoint.EndpointKind);
        Assert.Equal("spirectl-test-pipe", endpoint.PipeName);
    }




#if ENABLE_STS2_LIVE_HOST

#endif

    private static IDisposable WithoutConfiguredBridgeEndpointEnvironment()
        => new EnvironmentVariableScope(
            "SPIRECTL_BRIDGE_SOCKET_PATH",
            "SPIRECTL_BRIDGE_PIPE_NAME",
            "SPIRECTL_BRIDGE_TCP_ADDRESS");

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly IReadOnlyDictionary<string, string?> _previousValues;

        public EnvironmentVariableScope(params string[] names)
        {
            _previousValues = names.ToDictionary(
                name => name,
                System.Environment.GetEnvironmentVariable);
            foreach (var name in names)
            {
                System.Environment.SetEnvironmentVariable(name, null);
            }
        }

        public void Dispose()
        {
            foreach (var (name, value) in _previousValues)
            {
                System.Environment.SetEnvironmentVariable(name, value);
            }
        }
    }


    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TempDirectory Create(string prefix)
            => new(Directory.CreateTempSubdirectory(prefix).FullName);

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    [Fact]
    public void HostedBridgeHostReportsUpdatedRunningStatus()
    {
        var host = new HostedBridgeHost("ipc", false, "starting");

        host.MarkRunning("listening");
        var running = host.DescribeStatus();

        Assert.True(running.IsRunning);
        Assert.Equal("ipc", running.TransportKind);
        Assert.Equal("listening", running.Message);

        host.MarkStopped("stopped");
        var stopped = host.DescribeStatus();

        Assert.False(stopped.IsRunning);
        Assert.Equal("stopped", stopped.Message);
    }

    [Fact]
    public void MainThreadDispatcherUsesExplicitContextForWorkerCalls()
    {
        var previous = SynchronizationContext.Current;
        var context = new TestSynchronizationContext();

        try
        {
            Sts2MainThreadDispatcher.Capture(context);
            var mainThreadId = System.Environment.CurrentManagedThreadId;

            var task = Task.Run(() => Sts2MainThreadDispatcher.Invoke(
                () => System.Environment.CurrentManagedThreadId));

            Assert.False(task.Wait(TimeSpan.FromMilliseconds(50)));

            context.DrainOne();

            Assert.True(task.Wait(TimeSpan.FromSeconds(1)));
            Assert.Equal(mainThreadId, task.Result);
        }
        finally
        {
            Sts2MainThreadDispatcher.ResetForTests();
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    [Fact]
    public void MainThreadDispatcherAsyncContinuationsUseExplicitContext()
    {
        var previous = SynchronizationContext.Current;
        var context = new TestSynchronizationContext();

        try
        {
            Sts2MainThreadDispatcher.Capture(context);
            var mainThreadId = System.Environment.CurrentManagedThreadId;

            var task = Task.Run(() => Sts2MainThreadDispatcher.InvokeAsync(async () =>
            {
                await Task.Yield();
                return System.Environment.CurrentManagedThreadId;
            }).GetAwaiter().GetResult());

            Assert.False(task.Wait(TimeSpan.FromMilliseconds(50)));

            context.DrainOne();
            Assert.False(task.Wait(TimeSpan.FromMilliseconds(50)));
            context.DrainOne();

            Assert.True(task.Wait(TimeSpan.FromSeconds(1)));
            Assert.Equal(mainThreadId, task.Result);
        }
        finally
        {
            Sts2MainThreadDispatcher.ResetForTests();
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    [Fact]
    public void MainThreadDispatcherAsyncDirectCallsUseExplicitContext()
    {
        var previous = SynchronizationContext.Current;
        var context = new TestSynchronizationContext();

        try
        {
            Sts2MainThreadDispatcher.Capture(context);
            var mainThreadId = System.Environment.CurrentManagedThreadId;

            var task = Sts2MainThreadDispatcher.InvokeAsync(async () =>
            {
                await Task.Yield();
                return System.Environment.CurrentManagedThreadId;
            });

            Assert.False(task.Wait(TimeSpan.FromMilliseconds(50)));

            context.DrainOne();

            Assert.True(task.Wait(TimeSpan.FromSeconds(1)));
            Assert.Equal(mainThreadId, task.Result);
        }
        finally
        {
            Sts2MainThreadDispatcher.ResetForTests();
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    [Fact]
    public async Task UnixSocketBridgeServerServesStandaloneHandshakeRequests()
    {
        var scaffold = BridgeRuntimeBootstrap.CreateScaffold();
        var host = new HostedBridgeHost("ipc", false, "starting");
        var runtime = BridgeRuntime.Create(new BridgeRuntimeOptions
        {
            StateExtractor = scaffold.StateExtractor,
            ActionHandler = scaffold.ActionHandler,
            LogStream = scaffold.LogStream,
            PerspectiveProvider = scaffold.PerspectiveProvider,
            FixtureLoader = scaffold.FixtureLoader,
            ScreenshotProvider = scaffold.ScreenshotProvider,
            AssetExtractor = scaffold.AssetExtractor,
            AssetExplainer = scaffold.AssetExplainer,
            AssetCatalogProvider = scaffold.AssetCatalogProvider,
            SpineCatalogProvider = scaffold.SpineCatalogProvider,
            SpineGeoClipBaker = scaffold.SpineGeoClipBaker,
            BridgeHost = host,
        });
        var socketDir = Directory.CreateTempSubdirectory("spirectl-host-test-");
        var socketPath = Path.Combine(socketDir.FullName, "bridge.sock");

        await using var server = new UnixSocketBridgeServer(runtime, host, socketPath);
        try
        {
            await server.StartAsync();

            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await client.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));
            await using var stream = new NetworkStream(client, ownsSocket: true);
            var requestPayload = new HandshakeRequest
            {
                CliVersion = "0.0.0",
                RequestedSchemaVersion = "spirectl/v0",
                Mode = "normal",
                TransportKind = TransportKind.Ipc,
            }.ToByteArray();
            var requestHeader = new byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(requestHeader, 0x4c545053);
            BinaryPrimitives.WriteUInt16LittleEndian(requestHeader.AsSpan(4), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(requestHeader.AsSpan(6), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(
                requestHeader.AsSpan(8),
                checked((uint)requestPayload.Length));
            await stream.WriteAsync(requestHeader);
            await stream.WriteAsync(requestPayload);
            await stream.FlushAsync();

            var responseHeader = new byte[12];
            await stream.ReadExactlyAsync(responseHeader);
            Assert.Equal(0x4c545053u, BinaryPrimitives.ReadUInt32LittleEndian(responseHeader));
            Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(responseHeader.AsSpan(4)));
            Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(responseHeader.AsSpan(6)));
            var responseLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(responseHeader.AsSpan(8)));
            var responsePayload = new byte[responseLength];
            await stream.ReadExactlyAsync(responsePayload);

            var handshake = HandshakeResult.Parser.ParseFrom(responsePayload);
            Assert.NotNull(handshake.Success);
            Assert.Equal(TransportKind.Ipc, handshake.Success.TransportKind);
            Assert.Equal(AttachmentState.Attached, handshake.Success.AttachmentState);
            Assert.Equal("spirectl/v0", handshake.Success.SchemaVersion);
        }
        finally
        {
            Directory.Delete(socketDir.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task TcpBridgeServerServesStandaloneHandshakeRequests()
    {
        var scaffold = BridgeRuntimeBootstrap.CreateScaffold();
        var host = new HostedBridgeHost("tcp", false, "starting");
        var runtime = BridgeRuntime.Create(new BridgeRuntimeOptions
        {
            StateExtractor = scaffold.StateExtractor,
            ActionHandler = scaffold.ActionHandler,
            LogStream = scaffold.LogStream,
            PerspectiveProvider = scaffold.PerspectiveProvider,
            FixtureLoader = scaffold.FixtureLoader,
            ScreenshotProvider = scaffold.ScreenshotProvider,
            AssetExtractor = scaffold.AssetExtractor,
            AssetExplainer = scaffold.AssetExplainer,
            AssetCatalogProvider = scaffold.AssetCatalogProvider,
            SpineCatalogProvider = scaffold.SpineCatalogProvider,
            SpineGeoClipBaker = scaffold.SpineGeoClipBaker,
            BridgeHost = host,
        });
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        listener.Stop();

        await using var server = new TcpBridgeServer(runtime, host, endpoint.ToString()!);
        await server.StartAsync();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, endpoint.Port);
        await using var stream = client.GetStream();
        await WriteHandshakeRequestAsync(stream, TransportKind.Tcp);
        var handshake = await ReadHandshakeResponseAsync(stream);

        Assert.NotNull(handshake.Success);
        Assert.Equal(TransportKind.Tcp, handshake.Success.TransportKind);
        Assert.Equal(AttachmentState.Attached, handshake.Success.AttachmentState);
        Assert.Equal("spirectl/v0", handshake.Success.SchemaVersion);
    }


    [Fact]
    public async Task TcpBridgeServerServesStandaloneHotReloadStatusRequests()
    {
        var scaffold = BridgeRuntimeBootstrap.CreateScaffold();
        var host = new HostedBridgeHost("tcp", false, "starting");
        var runtime = BridgeRuntime.Create(new BridgeRuntimeOptions
        {
            StateExtractor = scaffold.StateExtractor,
            ActionHandler = scaffold.ActionHandler,
            LogStream = scaffold.LogStream,
            PerspectiveProvider = scaffold.PerspectiveProvider,
            FixtureLoader = scaffold.FixtureLoader,
            ScreenshotProvider = scaffold.ScreenshotProvider,
            AssetExtractor = scaffold.AssetExtractor,
            AssetExplainer = scaffold.AssetExplainer,
            AssetCatalogProvider = scaffold.AssetCatalogProvider,
            SpineCatalogProvider = scaffold.SpineCatalogProvider,
            SpineGeoClipBaker = scaffold.SpineGeoClipBaker,
            BridgeHost = host,
        });
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        listener.Stop();

        await using var server = new TcpBridgeServer(runtime, host, endpoint.ToString()!);
        await server.StartAsync();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, endpoint.Port);
        await using var stream = client.GetStream();
        await WriteBridgeRequestAsync(stream, 29, new HotReloadStatusRequest
        {
            RequestId = "hr-status-standalone",
            ProjectId = "my-hot-mod",
            ShellModId = "missing-shell",
        });
        var responsePayload = await ReadSuccessBridgeResponseAsync(stream);

        var result = HotReloadStatusResult.Parser.ParseFrom(responsePayload);
        Assert.NotNull(result.Success);
        Assert.False(result.Success.Status.Supported);
    }

    [Fact]
    public async Task TcpBridgeServerLogsStandaloneRpcStartAndCompletionDiagnostics()
    {
        var scaffold = BridgeRuntimeBootstrap.CreateScaffold();
        var host = new HostedBridgeHost("tcp", false, "starting");
        var runtime = BridgeRuntime.Create(new BridgeRuntimeOptions
        {
            StateExtractor = scaffold.StateExtractor,
            ActionHandler = scaffold.ActionHandler,
            LogStream = scaffold.LogStream,
            PerspectiveProvider = scaffold.PerspectiveProvider,
            FixtureLoader = scaffold.FixtureLoader,
            ScreenshotProvider = scaffold.ScreenshotProvider,
            AssetExtractor = scaffold.AssetExtractor,
            AssetExplainer = scaffold.AssetExplainer,
            AssetCatalogProvider = scaffold.AssetCatalogProvider,
            SpineCatalogProvider = scaffold.SpineCatalogProvider,
            SpineGeoClipBaker = scaffold.SpineGeoClipBaker,
            BridgeHost = host,
        });
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        listener.Stop();

        await using var server = new TcpBridgeServer(runtime, host, endpoint.ToString()!);
        await server.StartAsync();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, endpoint.Port);
        await using var stream = client.GetStream();
        await WriteBridgeRequestAsync(stream, 29, new HotReloadStatusRequest
        {
            RequestId = "hr-diagnostics",
            ProjectId = "my-hot-mod",
            ShellModId = "missing-shell",
        });
        _ = await ReadSuccessBridgeResponseAsync(stream);

        var logs = runtime.LogStream.Read(new LogQuery(
            Limit: 10,
            AfterCursor: null,
            MinimumLevel: null,
            TargetFilter: "bridge.rpc"));

        Assert.Collection(
            logs.Entries,
            start =>
            {
                Assert.Equal(BridgeLogLevel.Debug, start.Level);
                Assert.Contains("phase=start", start.Message, StringComparison.Ordinal);
                Assert.Contains("transport=tcp", start.Message, StringComparison.Ordinal);
                Assert.Contains("method=get_hot_reload_status", start.Message, StringComparison.Ordinal);
                Assert.Contains("requestId=hr-diagnostics", start.Message, StringComparison.Ordinal);
                Assert.Contains("mainThreadCaptured=", start.Message, StringComparison.Ordinal);
                Assert.Contains("currentThreadId=", start.Message, StringComparison.Ordinal);
            },
            complete =>
            {
                Assert.Equal(BridgeLogLevel.Debug, complete.Level);
                Assert.Contains("phase=complete", complete.Message, StringComparison.Ordinal);
                Assert.Contains("method=get_hot_reload_status", complete.Message, StringComparison.Ordinal);
                Assert.Contains("requestId=hr-diagnostics", complete.Message, StringComparison.Ordinal);
                Assert.Contains("elapsedMs=", complete.Message, StringComparison.Ordinal);
                Assert.Contains("response=HotReloadStatusResult", complete.Message, StringComparison.Ordinal);
            });
    }

    [Fact]
    public void MainThreadDispatcherDescribeStatusReportsCapturedContext()
    {
        var previous = SynchronizationContext.Current;
        var context = new TestSynchronizationContext();

        try
        {
            Sts2MainThreadDispatcher.Capture(context);

            var status = Sts2MainThreadDispatcher.DescribeStatus();

            Assert.True(status.HasCapturedContext);
            Assert.Equal(System.Environment.CurrentManagedThreadId, status.CapturedThreadId);
            Assert.Equal(System.Environment.CurrentManagedThreadId, status.CurrentThreadId);
            Assert.True(status.IsOnCapturedThread);
            Assert.Equal(0, status.QueueDepth);
            Assert.Null(status.LastQueueDrainAtUtc);
            Assert.Null(status.DispatcherNote);

            var task = Task.Run(() => Sts2MainThreadDispatcher.Invoke(
                () => System.Environment.CurrentManagedThreadId));
            Assert.False(task.Wait(TimeSpan.FromMilliseconds(50)));

            status = Sts2MainThreadDispatcher.DescribeStatus();
            Assert.Equal(1, status.QueueDepth);
            Assert.Null(status.LastQueueDrainAtUtc);

            context.DrainOne();

            Assert.True(task.Wait(TimeSpan.FromSeconds(1)));
            status = Sts2MainThreadDispatcher.DescribeStatus();
            Assert.Equal(0, status.QueueDepth);
            Assert.NotNull(status.LastQueueDrainAtUtc);

            var firstDrainAt = status.LastQueueDrainAtUtc;
            context.DrainEmpty();

            status = Sts2MainThreadDispatcher.DescribeStatus();
            Assert.Equal(0, status.QueueDepth);
            Assert.NotNull(status.LastQueueDrainAtUtc);
            Assert.True(status.LastQueueDrainAtUtc >= firstDrainAt);
        }
        finally
        {
            Sts2MainThreadDispatcher.ResetForTests();
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    [Fact]
    public async Task TcpBridgeServerLogsInFlightRpcSnapshotDuringLogsRequest()
    {
        var scaffold = BridgeRuntimeBootstrap.CreateScaffold();
        var actionHandler = new BlockingActionHandler();
        var host = new HostedBridgeHost("tcp", false, "starting");
        var runtime = BridgeRuntime.Create(new BridgeRuntimeOptions
        {
            StateExtractor = scaffold.StateExtractor,
            ActionHandler = actionHandler,
            LogStream = scaffold.LogStream,
            PerspectiveProvider = scaffold.PerspectiveProvider,
            FixtureLoader = scaffold.FixtureLoader,
            ScreenshotProvider = scaffold.ScreenshotProvider,
            AssetExtractor = scaffold.AssetExtractor,
            AssetExplainer = scaffold.AssetExplainer,
            AssetCatalogProvider = scaffold.AssetCatalogProvider,
            SpineCatalogProvider = scaffold.SpineCatalogProvider,
            SpineGeoClipBaker = scaffold.SpineGeoClipBaker,
            BridgeHost = host,
        });
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        listener.Stop();

        await using var server = new TcpBridgeServer(runtime, host, endpoint.ToString()!);
        await server.StartAsync();

        using var actionClient = new TcpClient();
        await actionClient.ConnectAsync(IPAddress.Loopback, endpoint.Port);
        await using var actionStream = actionClient.GetStream();
        await WriteBridgeRequestAsync(actionStream, 3, new ActionRequest
        {
            RequestId = "action-stuck",
            EndTurn = new EndTurnAction(),
        });

        try
        {
            Assert.True(actionHandler.Entered.Wait(TimeSpan.FromSeconds(2)));

            using var logsClient = new TcpClient();
            await logsClient.ConnectAsync(IPAddress.Loopback, endpoint.Port);
            await using var logsStream = logsClient.GetStream();
            await WriteBridgeRequestAsync(logsStream, 5, new LogsRequest
            {
                Limit = 20,
                TargetFilter = "bridge.rpc",
            });
            var logsPayload = await ReadSuccessBridgeResponseAsync(logsStream);
            var logs = LogsResult.Parser.ParseFrom(logsPayload);

            Assert.NotNull(logs.Success);
            Assert.Contains(logs.Success.Entries, entry =>
                entry.Message.Contains("phase=in-flight", StringComparison.Ordinal)
                && entry.Message.Contains("method=execute_action", StringComparison.Ordinal)
                && entry.Message.Contains("requestId=action-stuck", StringComparison.Ordinal)
                && entry.Message.Contains("activePhase=handler", StringComparison.Ordinal)
                && entry.Message.Contains("elapsedMs=", StringComparison.Ordinal));
        }
        finally
        {
            actionHandler.Release.Set();
        }

        var actionPayload = await ReadSuccessBridgeResponseAsync(actionStream);
        var actionResult = ActionResult.Parser.ParseFrom(actionPayload);
        Assert.NotNull(actionResult.Success);
    }

    [Fact]
    public async Task TcpBridgeServerServesStandaloneHotReloadRequests()
    {
        var scaffold = BridgeRuntimeBootstrap.CreateScaffold();
        var host = new HostedBridgeHost("tcp", false, "starting");
        var runtime = BridgeRuntime.Create(new BridgeRuntimeOptions
        {
            StateExtractor = scaffold.StateExtractor,
            ActionHandler = scaffold.ActionHandler,
            LogStream = scaffold.LogStream,
            PerspectiveProvider = scaffold.PerspectiveProvider,
            FixtureLoader = scaffold.FixtureLoader,
            ScreenshotProvider = scaffold.ScreenshotProvider,
            AssetExtractor = scaffold.AssetExtractor,
            AssetExplainer = scaffold.AssetExplainer,
            AssetCatalogProvider = scaffold.AssetCatalogProvider,
            SpineCatalogProvider = scaffold.SpineCatalogProvider,
            SpineGeoClipBaker = scaffold.SpineGeoClipBaker,
            BridgeHost = host,
        });
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        listener.Stop();

        await using var server = new TcpBridgeServer(runtime, host, endpoint.ToString()!);
        await server.StartAsync();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, endpoint.Port);
        await using var stream = client.GetStream();
        await WriteBridgeRequestAsync(stream, 30, new HotReloadRequest
        {
            RequestId = "hr-request-standalone",
            ProjectId = "my-hot-mod",
            ShellModId = "missing-shell",
            LogicArtifactPath = "/tmp/MyHotMod.Logic.dll",
            ExpectedContractVersion = 0,
            WaitForCompletion = true,
            TimeoutMs = 30_000,
        });
        var responsePayload = await ReadSuccessBridgeResponseAsync(stream);

        var result = HotReloadResult.Parser.ParseFrom(responsePayload);
        Assert.NotNull(result.Error);
        Assert.Equal(BridgeErrorCode.NotImplemented, result.Error.Code);
    }

    [Fact]
    public void ActionCatalogOnlyAdvertisesExecutableMainMenuChoice()
    {
        var actions = Sts2ActionCatalog.MainMenuActions();

        Assert.Single(actions);
        Assert.Equal("menu:start-run", actions[0].Arguments?.ChoiceId);
        Assert.False(actions[0].Provisional);
    }

    [Fact]
    public void LobbyChoicesExposeVisibleReadyAndCharacterSelectionForLocalPlayer()
    {
        var localPlayer = new LobbyPlayerSnapshot(
            "p:100",
            "not-ready",
            "Local",
            "ironclad",
            IsReady: false,
            SlotId: 0,
            IsLocal: true,
            IsHost: true,
            IsRemote: false);
        var remotePlayer = new LobbyPlayerSnapshot(
            "p:200",
            "ready",
            "Remote",
            "silent",
            IsReady: true,
            SlotId: 1,
            IsLocal: false,
            IsHost: false,
            IsRemote: true);
        var ironclad = new LobbyCharacterSnapshot("ironclad", "Ironclad", true);
        var silent = new LobbyCharacterSnapshot("silent", "Silent", true);
        var lobby = new LobbyStateSnapshot(
            "start-run",
            "selecting",
            [localPlayer, remotePlayer],
            [ironclad, silent],
            LocalPlayerId: localPlayer.Id,
            HostPlayerId: localPlayer.Id,
            LocalPlayerRole: "host",
            PlayersById: new Dictionary<string, LobbyPlayerSnapshot>(StringComparer.Ordinal)
            {
                [localPlayer.Id] = localPlayer,
                [remotePlayer.Id] = remotePlayer,
            },
            AvailableCharactersById: new Dictionary<string, LobbyCharacterSnapshot>(StringComparer.Ordinal)
            {
                [ironclad.Id] = ironclad,
                [silent.Id] = silent,
            });

        var choices = Sts2ActionCatalog.LobbyChoices(lobby, includeCharacterChoices: true);

        Assert.Equal(3, choices.Count);
        Assert.Collection(
            choices,
            choice =>
            {
                Assert.Equal("lobby:ready", choice.Id);
                Assert.Equal("Ready", choice.Label);
                Assert.Equal("lobby-flow", choice.Kind);
                Assert.Equal(localPlayer.Id, choice.OwnerPlayerId);
            },
            choice =>
            {
                Assert.Equal("lobby:character:ironclad", choice.Id);
                Assert.Equal("Select Ironclad", choice.Label);
                Assert.Equal("lobby-character", choice.Kind);
                Assert.Equal(localPlayer.Id, choice.OwnerPlayerId);
            },
            choice =>
            {
                Assert.Equal("lobby:character:silent", choice.Id);
                Assert.Equal("Select Silent", choice.Label);
                Assert.Equal("lobby-character", choice.Kind);
                Assert.Equal(localPlayer.Id, choice.OwnerPlayerId);
            });

#if ENABLE_STS2_LIVE_HOST
        var screen = new TestLobbyPresentationScreen();
        var geometry = Sts2LobbyPresentationGeometryResolver.Resolve(
            screen,
            lobby,
            new ScreenLocatorResult(
                "Screens.CharacterSelect.NCharacterSelectScreen",
                "Lobby",
                "screen:test",
                "test",
                "TestLobbyPresentationScreen",
                "TestLobbyPresentationScreen"),
            new Dictionary<string, Node>(StringComparer.Ordinal)
            {
                [ironclad.Id] = screen.IroncladButton,
                [silent.Id] = screen.SilentButton,
            });

        Assert.Empty(geometry.Missing);
        AssertGeometry(geometry, "background", 0, 0, 1280, 720, "_background");
        AssertGeometry(geometry, "character:ironclad", 100, 400, 80, 90, "_charButtonContainer/*[ironclad]");
        AssertGeometry(geometry, "character:ironclad:portrait", 110, 410, 60, 55, "MarginContainer/Mask/Icon");
        AssertGeometry(geometry, "character:ironclad:player-marker:p:100", 125, 474, 24, 24, "PlayerIconContainer/CharSelectPlayerIcon");
        Assert.Equal("#ffffffff", geometry.RectsByKey["character:ironclad:player-marker:p:100"].AssetTintColor);
        AssertGeometry(geometry, "character:silent", 190, 400, 80, 90, "_charButtonContainer/*[silent]");
        AssertGeometry(geometry, "character:silent:portrait", 200, 410, 60, 55, "MarginContainer/Mask/Icon");
        AssertGeometry(geometry, "character:silent:player-marker:p:200", 215, 474, 24, 24, "PlayerIconContainer");
        Assert.Equal("#87ceebff", geometry.RectsByKey["character:silent:player-marker:p:200"].AssetTintColor);
        AssertGeometry(geometry, "selected-character", 700, 120, 220, 320, "_selectedCharacter");
        AssertGeometry(geometry, "selected-character:stats", 520, 240, 180, 120, "_selectedCharacterStats");
        AssertGeometry(geometry, "player:p:100", 40, 80, 170, 56, "_playerContainer/*[p:100]");
        AssertGeometry(geometry, "player:p:200", 40, 150, 170, 56, "_playerContainer/*[p:200]");
        AssertGeometry(geometry, "player:p:200:ready-indicator", 48, 158, 28, 28, "CharacterIcon/ReadyIndicator");
        Assert.Equal("#7fff00ff", geometry.RectsByKey["player:p:200:ready-indicator"].AssetTintColor);
        AssertGeometry(geometry, "control:ready", 840, 490, 120, 52, "_readyButton");
        AssertGeometry(geometry, "ready", 840, 490, 120, 52, "_readyButton");
        AssertGeometry(geometry, "control:back", 30, 490, 110, 52, "_backButton");
        AssertGeometry(geometry, "back", 30, 490, 110, 52, "_backButton");
        AssertGeometry(geometry, "action:action:lobby:ready", 840, 490, 120, 52, "_readyButton");
        AssertGeometry(geometry, "action:action:lobby:select-character:ironclad", 100, 400, 80, 90, "_charButtonContainer/*[ironclad]");
        AssertGeometry(geometry, "action:action:lobby:select-character:silent", 190, 400, 80, 90, "_charButtonContainer/*[silent]");
        Assert.DoesNotContain(geometry.RectsByKey.Keys, key => key.Contains("Qr", StringComparison.OrdinalIgnoreCase));
#endif
    }

    [Fact]
    public void LobbyChoicesOmitCharacterSelectionWhenLocalPlayerIsReady()
    {
        var localPlayer = new LobbyPlayerSnapshot(
            "p:100",
            "ready",
            "Local",
            "ironclad",
            IsReady: true,
            SlotId: 0,
            IsLocal: true,
            IsHost: true,
            IsRemote: false);
        var lobby = new LobbyStateSnapshot(
            "start-run",
            "ready",
            [localPlayer],
            [new LobbyCharacterSnapshot("ironclad", "Ironclad", true)],
            LocalPlayerId: localPlayer.Id,
            HostPlayerId: localPlayer.Id,
            LocalPlayerRole: "host",
            PlayersById: new Dictionary<string, LobbyPlayerSnapshot>(StringComparer.Ordinal)
            {
                [localPlayer.Id] = localPlayer,
            },
            AvailableCharactersById: new Dictionary<string, LobbyCharacterSnapshot>(StringComparer.Ordinal)
            {
                ["ironclad"] = new LobbyCharacterSnapshot("ironclad", "Ironclad", true),
            });

        var choices = Sts2ActionCatalog.LobbyChoices(lobby, includeCharacterChoices: true);

        var choice = Assert.Single(choices);
        Assert.Equal("lobby:unready", choice.Id);
        Assert.Equal("Unready", choice.Label);
        Assert.Equal("lobby-flow", choice.Kind);
        Assert.Equal(localPlayer.Id, choice.OwnerPlayerId);
    }

    [Fact]
    public void UnsupportedObservationUsesFamilySpecificNoticeForNamedScreens()
    {
        var notice = Sts2UnsupportedScreenNotice.Create(
            "card-overlay",
            "Card Overlay",
            "overlay",
            "MegaCrit.Sts2.Core.Nodes.Screens.Overlays.NExperimentalCardOverlay");

        Assert.Equal("card-overlay-unsupported", notice.Code);
        Assert.Contains("source 'overlay'", notice.Message, StringComparison.Ordinal);
        Assert.Contains("NExperimentalCardOverlay", notice.Message, StringComparison.Ordinal);
        Assert.Equal("screen", notice.Path);
        Assert.Equal("unsupported", notice.Severity);
        Assert.Equal("Sts2UnsupportedScreenNotice", notice.Source);
    }

    [Fact]
    public void UnsupportedObservationKeepsGenericUnknownNotice()
    {
        var notice = Sts2UnsupportedScreenNotice.Create(
            "unknown",
            "Unknown",
            "fallback",
            "fallback:unknown");

        Assert.Equal("screen-unsupported", notice.Code);
        Assert.Equal("screen", notice.Path);
        Assert.Equal("unsupported", notice.Severity);
        Assert.Equal("Sts2UnsupportedScreenNotice", notice.Source);
    }

    [Fact]
    public void OverlayPartialStateCatalogOmitsCloseBackActionsWithoutValidatedAffordances()
    {
        var overlay = new CardOverlayStateSnapshot(
            Cards: [],
            Close: null,
            Back: null,
            FollowThroughControls: []);

        var actions = Sts2ActionCatalog.CardOverlayActions(overlay, []);

        Assert.Empty(actions);
    }

#if ENABLE_STS2_LIVE_HOST
    [Fact]
    public void OverlayPartialStateExtractsTypedCardOverlayPayload()
    {
        var notices = new List<StateNoticeSnapshot>();
        var screen = new Spirectl.Sts2.Live.ScreenLocatorResult(
            "card-overlay",
            "Card Overlay",
            "screen:card-overlay:test",
            "overlay-stack",
            "NExperimentalCardOverlay",
            "MegaCrit.Sts2.Core.Nodes.Screens.Overlays.NExperimentalCardOverlay");
        var overlay = new TestCardOverlay
        {
            Card = new TestOverlayCard
            {
                Id = "strike-red",
                Name = "Jab",
                Description = "Land 6 harm.",
                Cost = 1,
                CardType = "Attack",
                Rarity = "Basic",
                TargetType = "Enemy",
                UpgradeLevel = 1,
                OwnerPlayerId = "p:local",
            },
            PreviewText = "Inspect Jab.",
            SourceScreenType = "combat",
            CloseButton = new TestOverlayControl { Id = "card-overlay:close", Text = "Close", Visible = true, Enabled = true },
        };

        var inspection = Sts2CardOverlayInspector.Inspect(overlay, screen, "p:local", notices);
        var diagnostics = OverlayPartialStateDiagnostics(screen, inspection, notices);

        var state = inspection.State;
        Assert.Equal("blocking", state.OverlayPolicy);
        Assert.True(state.Blocking);
        Assert.False(state.Passive);
        Assert.Equal("p:local", state.OwnerPlayerId);
        Assert.Equal("Inspect Jab.", state.PreviewText);
        Assert.Equal("combat", Assert.Single(state.Breadcrumbs!).ScreenType);
        var card = Assert.Single(state.Cards);
        Assert.Equal("strike-red", card.Id);
        Assert.Equal("Jab", card.Name);
        Assert.Equal("Land 6 harm.", card.Description);
        Assert.Equal("attack", card.Type);
        Assert.Equal("basic", card.Rarity);
        Assert.Equal("enemy", card.TargetType);
        Assert.Equal(1, card.UpgradeLevel);
        Assert.Equal("1", card.CostLabel);
        Assert.True(inspection.Choices.Count == 0, diagnostics);
        Assert.True(
            !notices.Any(notice => notice.Code == "card-overlay-card-partial"),
            diagnostics);
    }

    [Fact]
    public void OverlayPartialStateGatesCloseAndBackActionsOnValidatedHooks()
    {
        var screen = new Spirectl.Sts2.Live.ScreenLocatorResult("card-overlay", "Card Overlay", "screen:card-overlay:test", "overlay", "Overlay", "Overlay");
        var withoutHook = new TestCardOverlay
        {
            Card = new TestOverlayCard { Id = "bash", Name = "Bash" },
            CloseButton = new TestOverlayControl { Id = "card-overlay:close", Text = "Close", Visible = true, Enabled = true },
        };
        var withoutHookInspection = Sts2CardOverlayInspector.Inspect(withoutHook, screen, null);
        Assert.Null(withoutHookInspection.State.Close);
        Assert.Empty(Sts2ActionCatalog.CardOverlayActions(withoutHookInspection.State, withoutHookInspection.Choices));

        var withHook = new TestHookedCardOverlay
        {
            Card = new TestOverlayCard { Id = "bash", Name = "Bash" },
            CloseButton = new TestOverlayControl { Id = "card-overlay:close", Text = "Close", Visible = true, Enabled = true },
        };
        var withHookInspection = Sts2CardOverlayInspector.Inspect(withHook, screen, null);
        Assert.NotNull(withHookInspection.State.Close);
        var action = Assert.Single(Sts2ActionCatalog.CardOverlayActions(withHookInspection.State, withHookInspection.Choices));
        Assert.Equal("action:card-overlay:choose:card-overlay:close", action.Id);
        Assert.Equal("card-overlay:close", action.Arguments?.ChoiceId);
        Assert.Equal("close-overlay", action.IntentKind);
    }

    [Fact]
    public void OverlayPartialStateEmitsStableNoticesWithoutHiddenOrFabricatedControls()
    {
        var notices = new List<StateNoticeSnapshot>();
        var screen = new Spirectl.Sts2.Live.ScreenLocatorResult("card-overlay", "Card Overlay", "screen:card-overlay:test", "overlay", "Overlay", "Overlay");
        var overlay = new TestCardOverlay
        {
            Buttons =
            [
                new TestOverlayControl { Id = "visible-fallback", Text = "Continue", Visible = true, Enabled = true },
                new TestOverlayControl { Id = "hidden-fallback", Text = "Hidden", Visible = false, Enabled = true },
            ],
        };

        var inspection = Sts2CardOverlayInspector.Inspect(overlay, screen, null, notices);
        var diagnostics = OverlayPartialStateDiagnostics(screen, inspection, notices);

        Assert.Empty(inspection.State.Cards);
        Assert.True(notices.Any(notice =>
            notice.Code == "card-overlay-card-partial"
            && notice.Path == "cardOverlay.cards"
            && notice.Severity == "partial"
            && notice.Source == "Sts2CardOverlayInspector"
            && notice.Stability == "stable"), diagnostics);
        Assert.True(
            notices.Any(notice => notice.Code == "card-overlay-close-hook-unavailable"),
            diagnostics);
        var choice = Assert.Single(inspection.Choices);
        Assert.Equal("visible-fallback", choice.Id);
        Assert.DoesNotContain(inspection.Choices, item => item.Id == "hidden-fallback");
        var action = Assert.Single(Sts2ActionCatalog.CardOverlayActions(inspection.State, inspection.Choices));
        Assert.Equal("visible-fallback", action.Arguments?.ChoiceId);
    }

    [Fact]
    public void OverlayPartialStateReportsAmbiguousOwnershipWithoutFabricatingOverlayOwner()
    {
        var notices = new List<StateNoticeSnapshot>();
        var screen = new Spirectl.Sts2.Live.ScreenLocatorResult("card-overlay", "Card Overlay", "screen:card-overlay:test", "overlay", "Overlay", "Overlay");
        var overlay = new TestCardOverlay
        {
            Cards =
            [
                new TestOverlayCard { Id = "alpha", Name = "Alpha", OwnerPlayerId = "p1" },
                new TestOverlayCard { Id = "beta", Name = "Beta", OwnerPlayerId = "p2" },
            ],
        };

        var inspection = Sts2CardOverlayInspector.Inspect(overlay, screen, null, notices);
        var diagnostics = OverlayPartialStateDiagnostics(screen, inspection, notices);

        Assert.Null(inspection.State.OwnerPlayerId);
        Assert.Equal(2, inspection.State.Cards.Count);
        Assert.True(notices.Any(notice =>
            notice.Code == "card-overlay-ambiguous-ownership"
            && notice.Path == "cardOverlay.ownerPlayerId"
            && notice.Severity == "partial"
            && notice.Source == "Sts2CardOverlayInspector"
            && notice.Stability == "stable"), diagnostics);
    }

    [Fact]
    public void OverlayPartialStateReachableFailureDiagnosticsIncludeOverlayContext()
    {
        var notices = new List<StateNoticeSnapshot>();
        var screen = new Spirectl.Sts2.Live.ScreenLocatorResult(
            "card-overlay",
            "Card Overlay",
            "screen:card-overlay:test",
            "overlay-stack",
            "NExperimentalCardOverlay",
            "MegaCrit.Sts2.Core.Nodes.Screens.Overlays.NExperimentalCardOverlay");
        var inspection = Sts2CardOverlayInspector.Inspect(new TestCardOverlay(), screen, null, notices);

        var diagnostics = OverlayPartialStateDiagnostics(screen, inspection, notices);

        Assert.Contains("\"source\":\"overlay-stack\"", diagnostics, StringComparison.Ordinal);
        Assert.Contains("\"rawType\":\"NExperimentalCardOverlay\"", diagnostics, StringComparison.Ordinal);
        Assert.Contains("\"className\":\"MegaCrit.Sts2.Core.Nodes.Screens.Overlays.NExperimentalCardOverlay\"", diagnostics, StringComparison.Ordinal);
        Assert.Contains("\"cardOverlay\":{\"present\":true", diagnostics, StringComparison.Ordinal);
        Assert.Contains("card-overlay-card-partial", diagnostics, StringComparison.Ordinal);
    }

    private static string OverlayPartialStateDiagnostics(
        Spirectl.Sts2.Live.ScreenLocatorResult screen,
        CardOverlayInspection inspection,
        IReadOnlyList<StateNoticeSnapshot> notices)
    {
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            overlayScreen = new
            {
                screenType = screen.ScreenType,
                source = screen.Source,
                rawType = screen.ScreenRawType,
                className = screen.ScreenClassName,
            },
            notices = notices.Select(notice => new
            {
                notice.Code,
                notice.Path,
                notice.Severity,
                notice.Source,
                notice.Stability,
                notice.Message,
            }),
            cardOverlay = new
            {
                present = inspection.State is not null,
                cardCount = inspection.State.Cards.Count,
                hasClose = inspection.State.Close is not null,
                hasBack = inspection.State.Back is not null,
                followThroughControlCount = inspection.State.FollowThroughControls?.Count ?? 0,
                inspection.State.OverlayPolicy,
                inspection.State.OwnerPlayerId,
                inspection.State.Perspective,
            },
        });
    }
#endif

#if ENABLE_STS2_LIVE_HOST && ENABLE_STALE_STS2_SCREEN_FIXTURE_TESTS
    [Fact]
    public void CardOverlayObservationUsesDedicatedPartialCapturePath()
    {
        var runtimeObservationProviderType = typeof(Sts2ActionCatalog).Assembly
            .GetType("Spirectl.Sts2.Live.Sts2RuntimeObservationProvider");
        Assert.NotNull(runtimeObservationProviderType);

        var method = runtimeObservationProviderType!.GetMethod(
            "CaptureCardOverlay",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var screenLocatorResultType = typeof(Sts2ActionCatalog).Assembly
            .GetType("Spirectl.Sts2.Live.ScreenLocatorResult");
        Assert.NotNull(screenLocatorResultType);

        var screen = Activator.CreateInstance(
            screenLocatorResultType!,
            "card-overlay",
            "Card Overlay",
            "screen:card-overlay:test",
            "overlay",
            "NExperimentalCardOverlay",
            "MegaCrit.Sts2.Core.Nodes.Screens.Overlays.NExperimentalCardOverlay");
        Assert.NotNull(screen);

        var observation = Assert.IsType<BridgeRuntimeObservation>(method!.Invoke(null, [screen, new GameStateQuery(null, false)]));

        Assert.Equal("card-overlay", observation.ScreenType);
        Assert.Empty(observation.Choices);
        Assert.Empty(observation.AvailableActions);
        var notice = Assert.Single(observation.Notices);
        Assert.Equal("card-overlay-partial", notice.Code);
        Assert.Contains("screen identity only", notice.Message, StringComparison.Ordinal);
        Assert.True(notice.Provisional);
        Assert.Equal("screen", notice.Path);
        Assert.Equal("partial", notice.Severity);
        Assert.Equal("Sts2RuntimeObservationProvider", notice.Source);
    }

    [Fact]
    public void CardOverlayObservationCopiesScreenLocatorMetadata()
    {
        var runtimeObservationProviderType = typeof(Sts2ActionCatalog).Assembly
            .GetType("Spirectl.Sts2.Live.Sts2RuntimeObservationProvider");
        Assert.NotNull(runtimeObservationProviderType);

        var method = runtimeObservationProviderType!.GetMethod(
            "CaptureCardOverlay",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var screenLocatorResultType = typeof(Sts2ActionCatalog).Assembly
            .GetType("Spirectl.Sts2.Live.ScreenLocatorResult");
        Assert.NotNull(screenLocatorResultType);

        var screen = Activator.CreateInstance(
            screenLocatorResultType!,
            "card-overlay",
            "Card Overlay",
            "screen:card-overlay:test",
            "overlay-stack",
            "NExperimentalCardOverlay",
            "MegaCrit.Sts2.Core.Nodes.Screens.Overlays.NExperimentalCardOverlay");
        Assert.NotNull(screen);

        var observation = Assert.IsType<BridgeRuntimeObservation>(method!.Invoke(null, [screen, new GameStateQuery(null, false)]));

        Assert.Equal("overlay-stack", observation.ScreenSource);
        Assert.Equal("NExperimentalCardOverlay", observation.ScreenRawType);
        Assert.Equal(
            "MegaCrit.Sts2.Core.Nodes.Screens.Overlays.NExperimentalCardOverlay",
            observation.ScreenClassName);
    }
#endif

    [Fact]
    public void PartialChoiceNoticeIsOmittedWhenAllVisibleChoicesAreExecutable()
    {
        var notice = Sts2PartialChoiceNotice.TryCreate(
            "map-visible-choices-partial",
            "Visible map nodes include non-travelable entries; only currently travelable nodes appear in availableActions.",
            visibleChoiceCount: 2,
            executableChoiceCount: 2);

        Assert.Null(notice);
    }

    [Fact]
    public void PartialChoiceNoticeExplainsVisibleButNonExecutableChoices()
    {
        var notice = Sts2PartialChoiceNotice.TryCreate(
            "shop-visible-choices-partial",
            "Visible shop choices can remain unavailable when a slot is out of stock or the active player cannot afford it.",
            visibleChoiceCount: 3,
            executableChoiceCount: 1);

        Assert.NotNull(notice);
        Assert.Equal("shop-visible-choices-partial", notice.Code);
        Assert.Equal(
            "Visible shop choices can remain unavailable when a slot is out of stock or the active player cannot afford it.",
            notice.Message);
        Assert.True(notice.Provisional);
        Assert.Equal("availableActions", notice.Path);
        Assert.Equal("partial", notice.Severity);
        Assert.Equal("Sts2PartialChoiceNotice", notice.Source);
    }

}
