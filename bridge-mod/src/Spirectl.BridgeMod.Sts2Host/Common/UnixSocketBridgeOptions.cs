namespace Spirectl.BridgeMod.Sts2Host;

public static class UnixSocketBridgeOptions
{
    public const string DefaultSocketPath = "/tmp/spirectl-bridge.sock";

    public static string ResolveSocketPath(string? configuredPath = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath;
        }

        var environmentPath = Environment.GetEnvironmentVariable("SPIRECTL_BRIDGE_SOCKET_PATH");
        return string.IsNullOrWhiteSpace(environmentPath) ? DefaultSocketPath : environmentPath;
    }
}
