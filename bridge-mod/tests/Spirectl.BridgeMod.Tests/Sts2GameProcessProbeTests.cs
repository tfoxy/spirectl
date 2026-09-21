using Spirectl.Sts2;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

public sealed class Sts2GameProcessProbeTests
{
    [Fact]
    public void BothGameAndGodotAssembliesLoadedIsInsideTheGameProcess()
    {
        Assert.True(Sts2GameProcessProbe.IsInsideGameProcess([
            "System.Private.CoreLib",
            Sts2GameProcessProbe.GameAssemblySimpleName,
            "Spirectl.Sts2",
            Sts2GameProcessProbe.GodotAssemblySimpleName,
        ]));
    }

    [Fact]
    public void GameAssemblyAloneIsNotInsideTheGameProcess()
    {
        // A tool can reference the game's types for compilation with no engine anywhere near it.
        Assert.False(Sts2GameProcessProbe.IsInsideGameProcess([
            "System.Private.CoreLib",
            Sts2GameProcessProbe.GameAssemblySimpleName,
        ]));
    }

    [Fact]
    public void GodotAssemblyAloneIsNotInsideTheGameProcess()
    {
        // Any other Godot .NET application loads the binding; it is not this game.
        Assert.False(Sts2GameProcessProbe.IsInsideGameProcess([
            "System.Private.CoreLib",
            Sts2GameProcessProbe.GodotAssemblySimpleName,
        ]));
    }

    [Fact]
    public void NeitherAssemblyLoadedIsNotInsideTheGameProcess()
    {
        Assert.False(Sts2GameProcessProbe.IsInsideGameProcess([
            "System.Private.CoreLib",
            "Spirectl.Sts2",
            "Spirectl.BridgeMod",
        ]));
        Assert.False(Sts2GameProcessProbe.IsInsideGameProcess([]));
    }

    [Fact]
    public void AssemblySimpleNamesMatchCaseInsensitivelyAndWholly()
    {
        Assert.True(Sts2GameProcessProbe.IsInsideGameProcess(["STS2", "godotsharp"]));
        Assert.True(Sts2GameProcessProbe.IsInsideGameProcess(["Sts2", "GodotSharp"]));
        // Whole simple names, not substrings — a neighbouring assembly is not the one being looked for.
        Assert.False(Sts2GameProcessProbe.IsInsideGameProcess(["sts2core", "GodotSharpEditor"]));
    }

    [Fact]
    public void NullAndBlankEntriesAreIgnoredRatherThanMatched()
    {
        Assert.False(Sts2GameProcessProbe.IsInsideGameProcess([null, "  ", string.Empty]));
        Assert.True(Sts2GameProcessProbe.IsInsideGameProcess([null, "sts2", "   ", "GodotSharp"]));
    }

#if !ENABLE_STS2_LIVE_HOST
    [Fact]
    public void TheBridgeTestHostIsNotMistakenForTheGameProcess()
    {
        // This host has GodotSharp copy-local (it declares Godot-typed doubles) and resolves the game's types
        // by name in other suites, so a gate that asked "can these types be resolved?" would answer yes here.
        // The probe reads the ALREADY-LOADED set instead, so it answers no — which is what keeps a demand-load
        // out of the implementation. Asserted only in the game-assembly-free leg: the live-host leg deliberately
        // loads both assemblies into this process, and there the affirmative answer is the correct one.
        Assert.False(Sts2GameProcessProbe.IsInsideGameProcess());
    }
#endif

    [Fact]
    public void ProbeResolvesNothingOnDemand()
    {
        // Code only — the file's own commentary names these APIs precisely because it must not call them.
        var code = ExecutableLines(Path.Combine(
            FindRepositoryRoot(),
            "bridge-mod/src/Spirectl.Sts2/Common/Sts2GameProcessProbe.cs"));

        Assert.DoesNotContain("Type.GetType", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Assembly.Load", code, StringComparison.Ordinal);
        Assert.Contains("AppDomain.CurrentDomain", code, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeFactoryNoLongerGatesOnTheProcessName()
    {
        // The game's macOS executable is named with spaces, so a ProcessName substring test was false on every
        // Mac and the factory silently composed a stub runtime whose observations looked like a real main menu.
        // A name cannot rule a match IN safely either. If this token returns, the gate has regressed to
        // something a process name can answer.
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "bridge-mod/src/Spirectl.Sts2/Common/Sts2EmbeddableRuntimeFactory.cs"));

        Assert.DoesNotContain("ProcessName", source, StringComparison.Ordinal);
        Assert.Contains(nameof(Sts2GameProcessProbe), source, StringComparison.Ordinal);
    }

    private static string ExecutableLines(string path)
        => string.Join(
            '\n',
            File.ReadAllLines(path).Where(line =>
            {
                var trimmed = line.TrimStart();
                return !trimmed.StartsWith("//", StringComparison.Ordinal)
                    && !trimmed.StartsWith("/*", StringComparison.Ordinal)
                    && !trimmed.StartsWith('*');
            }));

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
