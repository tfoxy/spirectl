using System.Reflection;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Protocol;
#if ENABLE_STS2_LIVE_HOST
using Spirectl.Sts2.Live;
#endif
using Xunit;

namespace Spirectl.BridgeMod.Tests;

public sealed class Sts2ConsoleCommandExecutorTests
{
#if ENABLE_STS2_LIVE_HOST
    [Fact]
    public void ConstructorDoesNotCreateGameDevConsole()
    {
        var logStream = new InMemoryLogStream(
            capacity: 10,
            source: DataSourceKind.Live,
            provisional: false);

        var executor = new Sts2ConsoleCommandExecutor(logStream);
        var consoleField = typeof(Sts2ConsoleCommandExecutor).GetField(
            "_console",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(consoleField);
        Assert.Null(consoleField.GetValue(executor));
        Assert.Equal(
            "MegaCrit.Sts2.Core.DevConsole.DevConsole",
            consoleField.FieldType.FullName);
    }
#else
    [Fact]
    public void SourceKeepsGameDevConsoleLazy()
    {
        var repositoryRoot = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "bridge-mod/src/Spirectl.BridgeMod.Sts2Host/Live/BridgeOnly/Sts2ConsoleCommandExecutor.cs"));

        Assert.Contains("private DevConsole? _console;", source, StringComparison.Ordinal);
        Assert.Contains(
            "=> _console ??= new DevConsole(shouldAllowDebugCommands: true);",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("readonly DevConsole _console = new", source, StringComparison.Ordinal);
    }
#endif

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
