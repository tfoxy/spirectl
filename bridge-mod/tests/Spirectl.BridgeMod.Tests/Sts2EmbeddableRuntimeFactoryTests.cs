using Spirectl.Sts2;
using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2.Embedding;
#if ENABLE_STS2_LIVE_HOST
using Spirectl.Sts2.Live;
#endif
using Xunit;

namespace Spirectl.BridgeMod.Tests;

public sealed class Sts2EmbeddableRuntimeFactoryTests
{
    [Fact]
    public void EmbeddingSurfacesDoNotReferenceTransportOrServiceHosts()
    {
        var repositoryRoot = FindRepositoryRoot();
        var files = Directory
            .EnumerateFiles(Path.Combine(repositoryRoot, "bridge-mod/src/Spirectl.Sts2/Embedding"), "*.cs")
            .Append(Path.Combine(
                repositoryRoot,
                "bridge-mod/src/Spirectl.Sts2/Common/Sts2EmbeddableRuntimeFactory.cs"));
        var forbidden = new[]
        {
            "Process.Start",
            "HostedBridgeServerFactory",
            "UnixSocketBridgeServer",
            "NamedPipeBridgeServer",
            "TcpBridgeServer",
            "BridgeEndpointOptions",
            "Mcp",
            "AutomationService",
        };

        var matches = files
            .SelectMany(file => forbidden
                .Where(token => File.ReadAllText(file).Contains(token, StringComparison.Ordinal))
                .Select(token => $"{Path.GetRelativePath(repositoryRoot, file)}: {token}"))
            .ToArray();

        Assert.Empty(matches);
    }

#if ENABLE_STS2_LIVE_HOST
    [Fact]
    public void StandaloneFactoryLivesInTheHostAssemblyAndOwnsBridgeComposition()
    {
        Assert.NotSame(typeof(ISpirectlRuntime).Assembly, typeof(Sts2BridgeRuntimeFactory).Assembly);
        Assert.NotNull(typeof(Sts2BridgeRuntimeFactory).GetMethod(nameof(Sts2BridgeRuntimeFactory.CreateLiveRuntime)));

        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "bridge-mod/src/Spirectl.BridgeMod.Sts2Host/Live/Sts2BridgeRuntimeFactory.cs"));
        Assert.Contains("new BridgeRuntimeServices(", source, StringComparison.Ordinal);
        Assert.Contains("new Sts2DevelopmentActionHandler(", source, StringComparison.Ordinal);
        Assert.Contains("new Sts2ScreenshotProvider(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Sts2EmbeddableRuntimeFactory.CreateLiveRuntime", source, StringComparison.Ordinal);
    }

    [Fact]
    public void FixtureLoaderReflectionResolvesFromTheHostAssembly()
    {
        const string fixtureLoaderName = "Spirectl.Sts2.Live.Sts2FixtureLoader";
        Assert.NotNull(typeof(Sts2BridgeRuntimeFactory).Assembly.GetType(fixtureLoaderName));
        Assert.Null(typeof(ISpirectlRuntime).Assembly.GetType(fixtureLoaderName));
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
