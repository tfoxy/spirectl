using System.Reflection;

namespace Spirectl.Sts2;

public static class BridgeBuildInfo
{
    private static readonly Assembly BridgeAssembly = typeof(BridgeBuildInfo).Assembly;

    private static readonly string InformationalVersion = BridgeAssembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion
        ?? "0.1.0";

    private static readonly string BuiltAtUtc = Metadata("SpirectlBridgeBuiltAtUtc") ?? "unknown";

    // Stamped by bridge-mod/Directory.Build.props from the STS2 API lane props. An empty value means
    // "this payload cannot claim one" — never "matches any build". A released payload compiles against
    // the declaration-only reference SDK, which has no release_info.json and no game hash, so only the
    // lane is populated there; a source build populates all three.
    private static readonly string Sts2ApiLaneValue = Metadata("SpirectlSts2ApiLane") ?? string.Empty;
    private static readonly string BuiltAgainstGameVersionValue =
        Metadata("SpirectlBuiltAgainstGameVersion") ?? string.Empty;
    private static readonly string BuiltAgainstMainAssemblyHashValue =
        Metadata("SpirectlBuiltAgainstMainAssemblyHash") ?? string.Empty;

    public static string BridgeSemVer => InformationalVersion.Split('+', 2)[0];

    public static string BridgeVersion => $"spirectl-bridge/{BridgeSemVer}";

    public static string AssemblyInformationalVersion => InformationalVersion;

    /// <summary>The `src/Spirectl.Sts2/GameApi/&lt;lane&gt;/` sources this build compiled.</summary>
    public static string Sts2ApiLane => Sts2ApiLaneValue;

    /// <summary>The install's release_info.json `version`, or empty for a reference-SDK build.</summary>
    public static string BuiltAgainstGameVersion => BuiltAgainstGameVersionValue;

    /// <summary>
    /// The install's release_info.json `main_assembly_hash` as written, or empty for a reference-SDK
    /// build. Kept as text so "unknown" stays expressible and the game's own token round-trips.
    /// </summary>
    public static string BuiltAgainstMainAssemblyHash => BuiltAgainstMainAssemblyHashValue;

    public static BridgeBuildIdentity BuildIdentity => new(
        BridgeSemVer,
        BridgeVersion,
        AssemblyInformationalVersion,
        BuiltAtUtc,
        Sts2ApiLane,
        BuiltAgainstGameVersion,
        BuiltAgainstMainAssemblyHash);

    private static string? Metadata(string key)
    {
        var value = BridgeAssembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == key)
            ?.Value;
        return string.IsNullOrEmpty(value) ? null : value;
    }
}

public sealed record BridgeBuildIdentity(
    string BridgeSemVer,
    string BridgeVersion,
    string AssemblyInformationalVersion,
    string BuiltAtUtc,
    string Sts2ApiLane,
    string BuiltAgainstGameVersion,
    string BuiltAgainstMainAssemblyHash);
