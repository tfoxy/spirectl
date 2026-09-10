using Spirectl.Sts2.Embedding;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

public sealed class RuntimeSubscriptionScopeTests
{
    [Fact]
    public void DisposalReleasesOnlyActiveSubscriptionsOnceAndRejectsNewOnes()
    {
        var scope = new RuntimeSubscriptionScope();
        var first = new Probe();
        var second = new Probe();
        var subscription = scope.Subscribe(() => first);
        scope.Subscribe(() => second);
        subscription.Dispose();
        scope.Dispose();
        scope.Dispose();
        subscription.Dispose();
        Assert.Equal(1, first.Disposals);
        Assert.Equal(1, second.Disposals);
        Assert.Throws<ObjectDisposedException>(() => scope.Subscribe(() => throw new Exception("must not subscribe")));
    }

    [Fact]
    public void OneFailingSubscriptionDoesNotLeakOtherSubscriptions()
    {
        var scope = new RuntimeSubscriptionScope();
        var throwing = new Probe { ThrowOnDispose = true };
        var other = new Probe();
        scope.Subscribe(() => throwing);
        scope.Subscribe(() => other);
        Assert.Throws<AggregateException>(scope.Dispose);
        Assert.Equal(1, other.Disposals);
        scope.Dispose();
        Assert.Equal(1, throwing.Disposals);
    }

    private sealed class Probe : IDisposable
    {
        public int Disposals;
        public bool ThrowOnDispose;
        public void Dispose()
        {
            Disposals++;
            if (ThrowOnDispose) throw new InvalidOperationException("test disposal failure");
        }
    }
}
