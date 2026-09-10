using System.Reflection;

namespace Spirectl.Sts2;

public static class BridgeBuildInfo
{
    private static readonly Assembly BridgeAssembly = typeof(BridgeBuildInfo).Assembly;

    private static readonly string InformationalVersion = BridgeAssembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion
        ?? "0.1.0";

    private static readonly string BuiltAtUtc = BridgeAssembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => attribute.Key == "SpirectlBridgeBuiltAtUtc")
        ?.Value
        ?? "unknown";

    public static string BridgeSemVer => InformationalVersion.Split('+', 2)[0];

    public static string BridgeVersion => $"spirectl-bridge/{BridgeSemVer}";

    public static string AssemblyInformationalVersion => InformationalVersion;

    public static BridgeBuildIdentity BuildIdentity => new(
        BridgeSemVer,
        BridgeVersion,
        AssemblyInformationalVersion,
        BuiltAtUtc);
}

public sealed record BridgeBuildIdentity(
    string BridgeSemVer,
    string BridgeVersion,
    string AssemblyInformationalVersion,
    string BuiltAtUtc);
