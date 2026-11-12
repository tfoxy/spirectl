using Spirectl.BridgeMod.Sts2Host;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

public sealed class BridgeEndpointOptionsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public BridgeEndpointOptionsTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private BridgeEndpointOptions Resolve(bool windows = false, Action<string>? logError = null, string? envPipe = null) =>
        BridgeEndpointOptions.Resolve(
            modDirectory: _directory,
            isWindows: windows,
            logError: logError,
            readEnvironment: name => name == BridgeEndpointOptions.PipeNameEnvironmentVariable ? envPipe : null);

    private void Write(string content) => File.WriteAllText(Path.Combine(_directory, BridgeEndpointOptions.EndpointFileName), content);

    [Theory]
    [InlineData(true, "named-pipe", "spirectl-bridge")]
    [InlineData(false, "unix-socket", "/tmp/spirectl-bridge.sock")]
    public void MissingFileUsesPlatformDefault(bool windows, string kind, string address)
    {
        var endpoint = Resolve(windows);
        Assert.Equal(kind, endpoint.EndpointKind);
        Assert.Equal(address, endpoint.EndpointDisplay);
        Assert.Equal("default", endpoint.Source);
    }

    [Theory]
    [InlineData("tcp", "127.0.0.1:51173")]
    [InlineData("named-pipe", "custom-pipe")]
    [InlineData("unix-socket", "/tmp/custom.sock")]
    public void FileUsedWithoutEnvironment(string kind, string address)
    {
        Write($$"""{"schema":"spirectl-bridge-endpoint/1","kind":"{{kind}}","address":"{{address}}"}""");
        var endpoint = Resolve();
        Assert.Equal(kind, endpoint.EndpointKind);
        Assert.Equal(address, endpoint.EndpointDisplay);
        Assert.Equal("file", endpoint.Source);
    }

    [Fact]
    public void EnvironmentWinsOverFile()
    {
        Write("invalid json");
        var endpoint = Resolve(logError: _ => Assert.Fail("File must not be read"), envPipe: "seat-pipe");
        Assert.Equal("seat-pipe", endpoint.PipeName);
        Assert.Equal("env", endpoint.Source);
    }

    [Theory]
    [InlineData("invalid json")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"schema\":\"wrong\"}")]
    [InlineData("{\"schema\":\"spirectl-bridge-endpoint/1\",\"kind\":\"tcp\",\"address\":\"0.0.0.0:1234\"}")]
    public void MalformedFileLogsAndUsesDefault(string content)
    {
        Write(content);
        var errors = new List<string>();
        var endpoint = Resolve(windows: true, logError: errors.Add);
        Assert.Equal("default", endpoint.Source);
        Assert.Equal("spirectl-bridge", endpoint.PipeName);
        Assert.Single(errors);
        Assert.Contains(BridgeEndpointOptions.EndpointFileName, errors[0]);
    }
}
