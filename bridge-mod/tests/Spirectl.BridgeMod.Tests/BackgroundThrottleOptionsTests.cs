using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// The background-throttle override changes how a launched game behaves for every user of that
// instance, so the master switch must stay OFF unless the launcher explicitly opts in, while the two
// levers underneath it default ON (they only ever run inside an opted-in session) and can be turned
// off individually to bisect which one carries a fix.
public class BackgroundThrottleOptionsTests
{
    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("yes")]
    [InlineData(" on ")]
    public void EnabledOnlyForTruthyValues(string value)
    {
        using var _ = new EnvironmentVariableScope(Sts2BackgroundThrottleOptions.EnvVar);
        Environment.SetEnvironmentVariable(Sts2BackgroundThrottleOptions.EnvVar, value);

        Assert.True(Sts2BackgroundThrottleOptions.IsEnabled());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("off")]
    [InlineData("no")]
    [InlineData("maybe")]
    public void DisabledByDefaultAndForNonTruthyValues(string? value)
    {
        using var _ = new EnvironmentVariableScope(Sts2BackgroundThrottleOptions.EnvVar);
        Environment.SetEnvironmentVariable(Sts2BackgroundThrottleOptions.EnvVar, value);

        Assert.False(Sts2BackgroundThrottleOptions.IsEnabled());
    }

    [Fact]
    public void BothLeversDefaultOn()
    {
        using var _ = new EnvironmentVariableScope(
            Sts2BackgroundThrottleOptions.FpsUncapEnvVar,
            Sts2BackgroundThrottleOptions.VsyncPinEnvVar);

        Assert.True(Sts2BackgroundThrottleOptions.FpsUncapEnabled());
        Assert.True(Sts2BackgroundThrottleOptions.VsyncPinEnabled());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("OFF")]
    [InlineData("no")]
    public void EachLeverHasItsOwnKillSwitch(string falsey)
    {
        using var _ = new EnvironmentVariableScope(
            Sts2BackgroundThrottleOptions.FpsUncapEnvVar,
            Sts2BackgroundThrottleOptions.VsyncPinEnvVar);

        Environment.SetEnvironmentVariable(Sts2BackgroundThrottleOptions.FpsUncapEnvVar, falsey);
        Assert.False(Sts2BackgroundThrottleOptions.FpsUncapEnabled());
        Assert.True(Sts2BackgroundThrottleOptions.VsyncPinEnabled());

        Environment.SetEnvironmentVariable(Sts2BackgroundThrottleOptions.FpsUncapEnvVar, null);
        Environment.SetEnvironmentVariable(Sts2BackgroundThrottleOptions.VsyncPinEnvVar, falsey);
        Assert.True(Sts2BackgroundThrottleOptions.FpsUncapEnabled());
        Assert.False(Sts2BackgroundThrottleOptions.VsyncPinEnabled());
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly IReadOnlyDictionary<string, string?> _previousValues;

        public EnvironmentVariableScope(params string[] names)
        {
            _previousValues = names.ToDictionary(
                name => name,
                Environment.GetEnvironmentVariable);
            foreach (var name in names)
            {
                Environment.SetEnvironmentVariable(name, null);
            }
        }

        public void Dispose()
        {
            foreach (var (name, value) in _previousValues)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }
}
