using System.Reflection;
using MegaCrit.Sts2.Core.Modding;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Protocol;

namespace Spirectl.Sts2.Live.GameApi;

/// <summary>Resolves the lane manifest once; only explicitly critical misses refuse startup.</summary>
internal static partial class Sts2GameApiProbe
{
    private static readonly System.Threading.Lock Sync = new();
    private static bool _ran;
    private static Exception? _failure;

    /// <summary>
    /// Resolve the current lane once per process. Non-critical misses are retained in the report;
    /// a critical refusal is remembered and rethrown on subsequent calls.
    /// </summary>
    internal static void EnsureCompatible(ILogStream? logStream = null)
    {
        lock (Sync)
        {
            if (_ran)
            {
                if (_failure is not null)
                {
                    throw _failure;
                }

                return;
            }

            _ran = true;
            logStream ??= new InMemoryLogStream();
            var report = Sts2CompatibilityReport.Process;
            report.SetGameBuild(GameApiLane.Name, DescribeGameVersion(), logStream);

            IReadOnlyList<GameApiRequirement> requirements;
            try
            {
                requirements = GameApiManifest.Requirements;
            }
            catch (Exception exception) when (exception is TypeLoadException or TypeInitializationException)
            {
                report.Record("manifest_load_failed", "GameApiManifest",
                    DescribeLaneLoadFailure(exception, report.GameBuild), logStream);
                return;
            }

            try
            {
                Sts2GameApiRequirements.Verify(requirements, report, logStream, FindGameType);
            }
            catch (InvalidOperationException exception)
            {
                _failure = exception;
                throw;
            }
        }
    }

    /// <summary>
    /// The per-install leg's clean verdict: the member count reports how much the manifest declares.
    /// </summary>
    internal static string DescribeVerdict(int resolvedCount, string gameVersion)
        => $"Game API lane {GameApiLane.Name} verified against game build {gameVersion}: "
            + $"{resolvedCount} members resolved.";

    /// <summary>Every requirement that does not resolve, rendered for a human. Empty means compatible.</summary>
    internal static IReadOnlyList<string> FindMissing(IEnumerable<GameApiRequirement> requirements)
        => [.. Sts2GameApiRequirements.FindMissing(requirements, FindGameType)
            .Select(requirement => requirement.Describe())];

    // The game assembly is reached through ModManager, the public type the reference-data provider uses as its
    // handle on sts2.dll, rather than an assembly-qualified Type.GetType string. Full name first (a direct
    // lookup); then the simple name, which is how the lane files already spell inaccessible game types.
    private static Type? FindGameType(string typeName)
    {
        var assembly = typeof(ModManager).Assembly;
        if (assembly.GetType(typeName, throwOnError: false) is { } exact)
        {
            return exact;
        }

        Type?[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // One unloadable type must not blind the probe to the rest.
            types = ex.Types;
        }

        return types.FirstOrDefault(type =>
            type is not null && string.Equals(type.Name, typeName, StringComparison.Ordinal));
    }

    /// <summary>
    /// The diagnostic text for a lane whose manifest could not be loaded at all. Exposed so its shape is
    /// testable — the failing path itself needs a mismatched game install to reproduce.
    /// </summary>
    /// <remarks>
    /// Says which build this payload IS before it says which build is installed, because that is the fact
    /// the reader does not have. <see cref="BridgeBuildInfo"/> reads it off this assembly's own metadata
    /// and touches nothing in the game, so it is answerable even though the manifest is not.
    /// </remarks>
    internal static string DescribeLaneLoadFailure(Exception exception, string gameVersion)
    {
        var compiledFor = string.IsNullOrWhiteSpace(BridgeBuildInfo.BuiltAgainstGameVersion)
            ? "<a build it does not record>"
            : BridgeBuildInfo.BuiltAgainstGameVersion;

        return $"The spirectl live bridge is degraded: its '{GameApiLane.Name}' API lane cannot be "
            + $"loaded against the game that is running. This bridge was compiled for game build "
            + $"{compiledFor}; the loaded game is {gameVersion}. The lane names game types directly, and one "
            + $"of them is not in this build's assembly — {exception.GetType().Name}: {exception.Message}"
            + Environment.NewLine
            + "Rebuild the bridge against this install (`sts2 game install-bridge`), or install the payload "
            + $"built for this game build. The version-to-lane table is {Sts2GameApiTableFile}.";
    }

    /// <summary>Where the lane table lives, named by every refusal so a reader never has to hunt for it.</summary>
    private const string Sts2GameApiTableFile = "bridge-mod/Sts2GameApi.props";

    /// <summary>Detailed missing-member text for the per-install verification leg.</summary>
    internal static string DescribeMissing(IReadOnlyList<string> missing, string gameVersion)
    {
        var lines = string.Join(Environment.NewLine, missing.Select(entry => $"  - {entry}"));
        return $"The spirectl compatibility check found {missing.Count} missing game member(s) required by "
            + $"API lane '{GameApiLane.Name}' in the loaded game assembly (build {gameVersion})."
            + Environment.NewLine
            + lines
            + Environment.NewLine
            + "Either this game build moved under the lane it is mapped to, or the wrong lane was compiled. "
            + $"The version-to-lane table is {Sts2GameApiTableFile}; the members are declared in "
            + $"bridge-mod/src/Spirectl.Sts2/GameApi/{GameApiLane.Name.ToUpperInvariant()}/GameApiManifest.cs.";
    }
}
