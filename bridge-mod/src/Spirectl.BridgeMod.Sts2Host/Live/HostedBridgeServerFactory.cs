using Spirectl.Sts2;
using Spirectl.Sts2.Core.Logging;

namespace Spirectl.BridgeMod.Sts2Host.Live;

public interface IHostedBridgeServer : IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken = default);
}

public static class HostedBridgeServerFactory
{
    public static IHostedBridgeServer Create(
        BridgeRuntime runtime,
        HostedBridgeHost hostStatus,
        BridgeEndpointOptions endpoint)
    {
        return endpoint.EndpointKind switch
        {
            "unix-socket" => new UnixSocketBridgeServer(
                runtime,
                hostStatus,
                endpoint.SocketPath ?? UnixSocketBridgeOptions.DefaultSocketPath),
            "named-pipe" => new NamedPipeBridgeServer(
                runtime,
                hostStatus,
                endpoint.PipeName ?? BridgeEndpointOptions.DefaultPipeName),
            "tcp" => new TcpBridgeServer(
                runtime,
                hostStatus,
                endpoint.TcpAddress ?? BridgeEndpointOptions.DefaultTcpAddress),
            _ => throw new InvalidOperationException(
                $"Unsupported bridge endpoint kind '{endpoint.EndpointKind}'."),
        };
    }

    public static void ClearNonSelectedEndpointVariables(BridgeEndpointOptions endpoint)
    {
        Environment.SetEnvironmentVariable(
            BridgeEndpointOptions.SocketPathEnvironmentVariable,
            endpoint.SocketPath);
        Environment.SetEnvironmentVariable(
            BridgeEndpointOptions.PipeNameEnvironmentVariable,
            endpoint.PipeName);
        Environment.SetEnvironmentVariable(
            BridgeEndpointOptions.TcpAddressEnvironmentVariable,
            endpoint.TcpAddress);
    }

    public static string StartupMessage(BridgeEndpointOptions endpoint)
    {
        return $"Live {endpoint.EndpointKind} bridge host listening at {endpoint.EndpointDisplay}.";
    }

    public static string StoppedMessage(BridgeEndpointOptions endpoint)
    {
        return $"Live {endpoint.EndpointKind} bridge host stopped.";
    }

    public static void LogTransportMessage(
        BridgeRuntime runtime,
        BridgeLogLevel level,
        string message)
    {
        runtime.LogStream.Write(level, "bridge.transport", message);
    }
}
