using System.Net;
using System.Text.Json;

namespace Spirectl.BridgeMod.Sts2Host;

public sealed record BridgeEndpointOptions(
    string TransportKind,
    string EndpointKind,
    string EndpointDisplay,
    string? SocketPath,
    string? PipeName,
    string? TcpAddress,
    string Source = "default")
{
    public const string SocketPathEnvironmentVariable = "SPIRECTL_BRIDGE_SOCKET_PATH";
    public const string PipeNameEnvironmentVariable = "SPIRECTL_BRIDGE_PIPE_NAME";
    public const string TcpAddressEnvironmentVariable = "SPIRECTL_BRIDGE_TCP_ADDRESS";
    public const string EndpointFileName = "spirectl-bridge.endpoint.json";
    public const string EndpointFileSchema = "spirectl-bridge-endpoint/1";
    public const string DefaultPipeName = "spirectl-bridge";
    public const string DefaultTcpAddress = "127.0.0.1:51173";

    public static BridgeEndpointOptions Resolve(
        string? configuredSocketPath = null,
        string? configuredPipeName = null,
        string? configuredTcpAddress = null,
        string? modDirectory = null,
        bool? isWindows = null,
        Action<string>? logError = null,
        Func<string, string?>? readEnvironment = null)
    {
        readEnvironment ??= Environment.GetEnvironmentVariable;
        var socketPath = FirstConfiguredValue(
            configuredSocketPath,
            readEnvironment(SocketPathEnvironmentVariable));
        var pipeName = FirstConfiguredValue(
            configuredPipeName,
            readEnvironment(PipeNameEnvironmentVariable));
        var tcpAddress = FirstConfiguredValue(
            configuredTcpAddress,
            readEnvironment(TcpAddressEnvironmentVariable));

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
                TcpAddress: null,
                Source: "env");
        }

        if (!string.IsNullOrWhiteSpace(tcpAddress))
        {
            return new BridgeEndpointOptions(
                TransportKind: "tcp",
                EndpointKind: "tcp",
                EndpointDisplay: tcpAddress,
                SocketPath: null,
                PipeName: null,
                TcpAddress: tcpAddress,
                Source: "env");
        }

        if (!string.IsNullOrWhiteSpace(socketPath))
        {
            return Create("unix-socket", socketPath, "env");
        }

        modDirectory ??= Path.GetDirectoryName(typeof(BridgeEndpointOptions).Assembly.Location);
        if (!string.IsNullOrWhiteSpace(modDirectory))
        {
            var path = Path.Combine(modDirectory, EndpointFileName);
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;
                if (root.GetProperty("schema").GetString() != EndpointFileSchema)
                {
                    throw new FormatException("Unsupported endpoint file schema.");
                }

                var kind = root.GetProperty("kind").GetString();
                var address = root.GetProperty("address").GetString();
                if (string.IsNullOrWhiteSpace(address) || kind is not ("tcp" or "named-pipe" or "unix-socket"))
                {
                    throw new FormatException("Expected an endpoint kind and non-empty address.");
                }

                if (kind == "tcp" && (!IPEndPoint.TryParse(address, out var endpoint) || !IPAddress.IsLoopback(endpoint.Address)))
                {
                    throw new FormatException("TCP endpoint must be a loopback host:port pair.");
                }

                return Create(kind, address, "file");
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
            {
                (logError ?? Console.Error.WriteLine)($"Unable to read bridge endpoint file '{path}': {ex.Message}. Using the platform default.");
            }
        }

        return (isWindows ?? OperatingSystem.IsWindows())
            ? Create("named-pipe", DefaultPipeName, "default")
            : Create("unix-socket", UnixSocketBridgeOptions.DefaultSocketPath, "default");
    }

    private static BridgeEndpointOptions Create(string kind, string address, string source) => new(
        TransportKind: kind == "tcp" ? "tcp" : "ipc",
        EndpointKind: kind,
        EndpointDisplay: address,
        SocketPath: kind == "unix-socket" ? address : null,
        PipeName: kind == "named-pipe" ? address : null,
        TcpAddress: kind == "tcp" ? address : null,
        Source: source);

    private static string? FirstConfiguredValue(string? configured, string? environment)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return string.IsNullOrWhiteSpace(environment) ? null : environment;
    }
}
