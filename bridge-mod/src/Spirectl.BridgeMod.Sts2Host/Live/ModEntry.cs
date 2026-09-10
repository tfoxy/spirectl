using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using Spirectl.BridgeMod.Services;
using Spirectl.Sts2;
using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.ConsoleCommands;
using Spirectl.Sts2.Core.Debugging;
using Spirectl.Sts2.Core.HotReload;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Perspective;
using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2.Core.State;
using Spirectl.Sts2.Live;
using Spirectl.Sts2.Live.Debugging;
using Spirectl.Sts2.Live.EncounterVisuals;

namespace Spirectl.BridgeMod.Sts2Host.Live;

[ModInitializer("Init")]
public static class ModEntry
{
    private static IHostedBridgeServer? _server;
    private static HostedBridgeHost? _host;

    public static void Init()
    {
        InMemoryLogStream? logStream = null;

        try
        {
            var mainThreadContext = Sts2GodotMainThreadPump.Install();
            Sts2MainThreadDispatcher.Capture(mainThreadContext);
            Log.Info("[spirectl] Live bridge main-thread dispatcher captured.");
            var endpoint = BridgeEndpointOptions.Resolve();
            HostedBridgeServerFactory.ClearNonSelectedEndpointVariables(endpoint);

            _host = new HostedBridgeHost(
                endpoint.TransportKind,
                false,
                $"Live {endpoint.EndpointKind} bridge host is initializing.");
            logStream = new InMemoryLogStream(
                capacity: 500,
                source: DataSourceKind.Live,
                provisional: false);
            logStream.Write(BridgeLogLevel.Info, "bridge.bootstrap", "Live bridge runtime created.");
            logStream.Write(BridgeLogLevel.Info, "bridge.bootstrap", "Live bridge main-thread dispatcher installed.");
            var runtime = Sts2BridgeRuntimeFactory.CreateLiveRuntime(_host, logStream);
            new Sts2KaiserCrabVisualHooks(Sts2EncounterVisualEventStore.Shared, logStream).Install();

            _server = HostedBridgeServerFactory.Create(runtime, _host, endpoint);
            _server.StartAsync().GetAwaiter().GetResult();
            logStream.Write(
                BridgeLogLevel.Info,
                "bridge.bootstrap",
                $"Live {endpoint.EndpointKind} bridge host started.");
            Log.Info($"[spirectl] Live {endpoint.EndpointKind} bridge host started.");
        }
        catch (Exception ex)
        {
            _host?.MarkStopped($"Live bridge host failed to start: {ex.Message}");
            logStream?.Write(BridgeLogLevel.Error, "bridge.bootstrap", $"Live bridge host failed: {ex}");
            Log.Error($"[spirectl] Live bridge host failed: {ex}");
        }
    }
}
