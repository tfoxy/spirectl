using Xunit;

namespace Spirectl.BridgeMod.Tests;

/// <summary>
/// Guards the ONE invariant the geoclip sweep's error suppression cannot be allowed to lose: the print flag and
/// the backtrace gate are turned off together and restored together.
///
/// <para>They are not interchangeable. Turning printing off skips the logger for a deliberately-failing probe;
/// it does nothing about the stack trace the engine captures BEFORE it consults that flag, because the capture
/// is an argument to the print call. A site that sets only the print flag therefore looks correct, logs
/// correctly, and costs an order of magnitude more per probe than one that sets both.</para>
///
/// <para>This is a SOURCE-TEXT test because the thing being protected is unobservable from outside: both
/// spellings produce identical artifacts and identical logs, and the only difference is wall-clock on a machine
/// with the game running. The sweep's tiering has already moved these toggle sites once — out of a per-window
/// candidate loop and around a single call that drives both tiers of both windows — and the next restructure
/// will move them again.</para>
/// </summary>
public sealed class Sts2SpineGeoClipProbeSuppressionTests
{
    private const string BakerSource = "bridge-mod/src/Spirectl.Sts2/Live/Sts2SpineGeoClipBaker.cs";

    [Fact]
    public void EveryPrintErrorToggleInTheSweepAlsoMovesTheBacktraceGate()
    {
        var lines = File.ReadAllLines(Path.Combine(FindRepositoryRoot(), BakerSource));

        // Assignments only. The one READ of the flag (`TryGet(() => Engine.PrintErrorMessages, true)`) is how
        // the sweep learns what to restore to, and has nothing to pair with.
        var offenders = new List<string>();
        var assignments = 0;
        for (var index = 0; index < lines.Length; index += 1)
        {
            if (!lines[index].Contains("Engine.PrintErrorMessages = ", StringComparison.Ordinal))
            {
                continue;
            }

            assignments += 1;
            var next = index + 1;
            while (next < lines.Length && lines[next].Trim().Length == 0)
            {
                next += 1;
            }

            var paired = next < lines.Length
                && lines[next].Contains("Sts2GeoClipDiagnostics.Current.Suppressed = ", StringComparison.Ordinal);
            if (!paired)
            {
                offenders.Add($"{BakerSource}:{index + 1}: {lines[index].Trim()}");
            }
        }

        // A rename that emptied the file of assignments would satisfy Assert.Empty vacuously.
        Assert.True(assignments >= 2, $"expected the paired suppression helpers to survive; found {assignments} "
            + "assignment(s) to Engine.PrintErrorMessages.");
        Assert.Empty(offenders);
    }

    [Fact]
    public void TheBacktraceSuppressorIsArmedWithAScopeSoAThrowStillDisarmsIt()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), BakerSource));

        // `using` and not a bare call: the sweep can throw anywhere between arming and its own finally, and a
        // Harmony patch left installed on Godot.DebuggingUtils outlives the bake that wanted it.
        Assert.Contains(
            "using var backtraceSuppression = Sts2GeoClipDiagnostics.Current.Arm(",
            source,
            StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "spirectl.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find repository root.");
    }
}
