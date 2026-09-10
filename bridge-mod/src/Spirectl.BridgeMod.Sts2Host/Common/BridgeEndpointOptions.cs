namespace Spirectl.BridgeMod.Sts2Host;

public sealed record BridgeEndpointOptions(
    string TransportKind,
    string EndpointKind,
    string EndpointDisplay,
    string? SocketPath,
    string? PipeName,
    string? TcpAddress)
{
    public const string SocketPathEnvironmentVariable = "SPIRECTL_BRIDGE_SOCKET_PATH";
    public const string PipeNameEnvironmentVariable = "SPIRECTL_BRIDGE_PIPE_NAME";
    public const string TcpAddressEnvironmentVariable = "SPIRECTL_BRIDGE_TCP_ADDRESS";
    public const string DefaultPipeName = "spirectl-bridge";
    public const string DefaultTcpAddress = "127.0.0.1:51173";

    public static BridgeEndpointOptions Resolve(
        string? configuredSocketPath = null,
        string? configuredPipeName = null,
        string? configuredTcpAddress = null)
    {
        var socketPath = FirstConfiguredValue(
            configuredSocketPath,
            Environment.GetEnvironmentVariable(SocketPathEnvironmentVariable));
        var pipeName = FirstConfiguredValue(
            configuredPipeName,
            Environment.GetEnvironmentVariable(PipeNameEnvironmentVariable));
        var tcpAddress = FirstConfiguredValue(
            configuredTcpAddress,
            Environment.GetEnvironmentVariable(TcpAddressEnvironmentVariable));

        var configuredEndpoints = 0;
        configuredEndpoints += string.IsNullOrWhiteSpace(pipeName) ? 0 : 1;
        configuredEndpoints += string.IsNullOrWhiteSpace(tcpAddress) ? 0 : 1;
        configuredEndpoints += string.IsNullOrWhiteSpace(socketPath) ? 0 : 1;
        if (configuredEndpoints > 1)
        {
            throw new InvalidOperationException(
                "Multiple live bridge endpoints were configured. Set only one of SPIRECTL_BRIDGE_PIPE_NAME, SPIRECTL_BRIDGE_TCP_ADDRESS, or SPIRECTL_BRIDGE_SOCKET_PATH.");
        }

        if (!string.IsNullOrWhiteSpace(pipeName))
        {
            return new BridgeEndpointOptions(
                TransportKind: "ipc",
                EndpointKind: "named-pipe",
                EndpointDisplay: pipeName,
                SocketPath: null,
                PipeName: pipeName,
                TcpAddress: null);
        }

        if (!string.IsNullOrWhiteSpace(tcpAddress))
        {
            return new BridgeEndpointOptions(
                TransportKind: "tcp",
                EndpointKind: "tcp",
                EndpointDisplay: tcpAddress,
                SocketPath: null,
                PipeName: null,
                TcpAddress: tcpAddress);
        }

        var resolvedSocketPath = UnixSocketBridgeOptions.ResolveSocketPath(socketPath);
        return new BridgeEndpointOptions(
            TransportKind: "ipc",
            EndpointKind: "unix-socket",
            EndpointDisplay: resolvedSocketPath,
            SocketPath: resolvedSocketPath,
            PipeName: null,
            TcpAddress: null);
    }

    private static string? FirstConfiguredValue(string? configured, string? environment)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return string.IsNullOrWhiteSpace(environment) ? null : environment;
    }
}
