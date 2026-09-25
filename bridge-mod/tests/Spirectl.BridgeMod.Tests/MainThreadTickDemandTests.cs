using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

public sealed class MainThreadTickDemandTests : IDisposable
{
    public MainThreadTickDemandTests() => Sts2MainThreadDispatcher.ResetForTests();

    [Fact]
    public void IndependentLeasesArmOnFirstAndDisarmOnLast()
    {
        var transitions = 0;
        Sts2MainThreadDispatcher.MainThreadTickDemandChanged += () => transitions++;

        var first = Sts2MainThreadDispatcher.AcquireMainThreadTickLease();
        var second = Sts2MainThreadDispatcher.AcquireMainThreadTickLease();

        Assert.True(Sts2MainThreadDispatcher.HasMainThreadTickDemand);
        Assert.Equal(2, Sts2MainThreadDispatcher.MainThreadTickLeaseCount);
        Assert.Equal(1, transitions);

        first.Dispose();
        Assert.True(Sts2MainThreadDispatcher.HasMainThreadTickDemand);
        Assert.Equal(1, transitions);

        second.Dispose();
        second.Dispose();
        Assert.False(Sts2MainThreadDispatcher.HasMainThreadTickDemand);
        Assert.Equal(0, Sts2MainThreadDispatcher.MainThreadTickLeaseCount);
        Assert.Equal(2, transitions);
    }

    [Fact]
    public void LeaseFromAnOldTestGenerationCannotDisarmNewDemand()
    {
        var old = Sts2MainThreadDispatcher.AcquireMainThreadTickLease();
        Sts2MainThreadDispatcher.ResetForTests();
        var current = Sts2MainThreadDispatcher.AcquireMainThreadTickLease();

        old.Dispose();

        Assert.True(Sts2MainThreadDispatcher.HasMainThreadTickDemand);
        Assert.Equal(1, Sts2MainThreadDispatcher.MainThreadTickLeaseCount);
        current.Dispose();
    }

    public void Dispose() => Sts2MainThreadDispatcher.ResetForTests();
}
