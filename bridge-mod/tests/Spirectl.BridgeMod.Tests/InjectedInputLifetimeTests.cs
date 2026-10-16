using Spirectl.Sts2.Core.Actions;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

public sealed class InjectedInputLifetimeTests
{
    [Fact]
    public void CancellationReleasesOnlyOwnedHoldsAndFlushesBeforeRestoringDelivery()
    {
        var input = new InjectedInputLifetime();
        var log = new List<string>();
        var disabled = false;
        foreach (var id in new[] { "mouse:left", "mouse:right", "key:Escape", "pad:accept" })
            input.Send(0, id, true, () => { }, () =>
            {
                Assert.True(disabled);
                log.Add(id);
            });
        input.Send(0, "mouse:right", false, () => { }, () => Assert.Fail("Already released"));
        input.Cancel(() =>
        {
            disabled = true;
            return new Scope(() => { log.Add("restore"); disabled = false; });
        }, () => { Assert.True(disabled); log.Add("flush"); });
        Assert.Equal(new[] { "flush", "mouse:left", "key:Escape", "pad:accept", "flush", "restore" }, log);
        Assert.False(disabled);
        input.Cancel(() => throw new Exception("Must be dormant"), () => Assert.Fail("Must be dormant"));
    }

    [Fact]
    public void SynchronousRevocationDuringPressCancelsHoldAndRejectsRemainingTap()
    {
        var input = new InjectedInputLifetime();
        var releases = 0;
        Assert.False(input.Send(0, "key:Escape", true,
            () => input.Cancel(() => new Scope(() => { }), () => { }),
            () => releases++));
        Assert.Equal(1, releases);
        Assert.False(input.Send(0, "key:Escape", false, () => Assert.Fail("Stale tap release"), () => { }));
        Assert.True(input.Send(input.Generation, "key:Escape", true, () => { }, () => releases++));
        input.Cancel(() => new Scope(() => { }), () => { });
        Assert.Equal(2, releases);
    }

    [Fact]
    public void SynchronousRevocationDuringMotionPreventsFollowingPress()
    {
        var input = new InjectedInputLifetime();
        Assert.False(input.Run(0, () => input.Cancel(() => new Scope(() => { }), () => { })));
        Assert.False(input.Send(0, "mouse:left", true, () => Assert.Fail("Stale press"), () => { }));
    }

    [Fact]
    public void CancellationAttemptsAllReleasesAndRestoresDeliveryEvenAfterFailure()
    {
        var input = new InjectedInputLifetime();
        var disabled = true;
        var restored = false;
        var otherReleased = false;
        var flushes = 0;
        var failRelease = true;
        input.Send(0, "key:a", true, () => { }, () =>
        {
            if (failRelease) throw new InvalidOperationException("release failed");
        });
        input.Send(0, "key:b", true, () => { }, () => otherReleased = true);
        Assert.Throws<AggregateException>(() => input.Cancel(() =>
        {
            var previous = disabled;
            disabled = true;
            return new Scope(() => { disabled = previous; restored = true; });
        }, () => flushes++));
        Assert.True(restored);
        Assert.True(disabled);
        Assert.True(otherReleased);
        Assert.Equal(2, flushes);
        failRelease = false;
        input.Cancel(() => new Scope(() => { }), () => { });
        input.Cancel(() => throw new Exception("Cleared after successful retry"), () => { });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompletedHoverOrTapDoesNotDisablePhysicalInput(bool tap)
    {
        var input = new InjectedInputLifetime();
        if (tap)
        {
            input.Send(0, "mouse:left", true, () => { }, () => Assert.Fail("Tap already released"));
            input.Send(0, "mouse:left", false, () => { }, () => { });
        }
        else input.Run(0, () => { });
        var flushes = 0;
        input.Complete(() => flushes++);
        Assert.Equal(1, flushes);
        input.Cancel(() => throw new Exception("No injected holds or buffered events"), () => { });
        Assert.False(input.Run(0, () => Assert.Fail("Generation still invalidated")));
    }

    [Fact]
    public void FailedCancellationCannotFlushPendingEventsOrInjectUntilRetrySucceeds()
    {
        var input = new InjectedInputLifetime();
        input.Send(0, "key:a", true, () => { }, () => { });
        Assert.Throws<InvalidOperationException>(() => input.Cancel(
            () => throw new InvalidOperationException("viewport failure"), () => { }));
        input.Complete(() => Assert.Fail("Unsafe pending events"));
        Assert.False(input.Run(input.Generation, () => Assert.Fail("Unsafe new input")));
        input.Cancel(() => new Scope(() => { }), () => { });
        Assert.True(input.Run(input.Generation, () => { }));
    }

    [Fact]
    public void FailedSuppressionDoesNotLoseOwnedHolds()
    {
        var input = new InjectedInputLifetime();
        var released = false;
        input.Send(0, "key:a", true, () => { }, () => released = true);
        Assert.Throws<InvalidOperationException>(() => input.Cancel(
            () => throw new InvalidOperationException("no viewport"), () => { }));
        Assert.False(released);
        input.Cancel(() => new Scope(() => { }), () => { });
        Assert.True(released);
    }

    [Fact]
    public void DormantCancellationStillInvalidatesAlreadyCapturedGeneration()
    {
        var input = new InjectedInputLifetime();
        input.Cancel(() => throw new Exception("No input was injected"), () => { });
        Assert.False(input.Run(0, () => Assert.Fail("Stale work")));
        Assert.True(input.Run(input.Generation, () => { }));
    }

    [Fact]
    public void ReentrantCancellationCannotReleaseTwiceOrAcceptNewInput()
    {
        var input = new InjectedInputLifetime();
        var releases = 0;
        input.Send(0, "key:a", true, () => { }, () =>
        {
            releases++;
            input.Cancel(() => throw new Exception("Nested suppression"), () => { });
            Assert.False(input.Run(input.Generation, () => Assert.Fail("Input during cancellation")));
        });
        input.Cancel(() => new Scope(() => { }), () => { });
        Assert.Equal(1, releases);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeliverySuppressionHandlesSyntheticReleaseBeforeDisablingAndRestoresOriginalFlag(bool initial)
    {
        var disabled = initial;
        var handled = false;
        var input = new InjectedInputLifetime();
        input.Send(0, "mouse:left", true, () => { }, () => throw new Exception("injection failed"));
        Assert.Throws<AggregateException>(() => input.Cancel(() => new InjectedInputDeliverySuppression(
            () => disabled,
            value => { Assert.True(handled); disabled = value; },
            () => handled = true), () => Assert.True(disabled)));
        Assert.Equal(initial, disabled);
    }

    [Fact]
    public void DeliverySuppressionRestoresFlagWhenDisableSetterThrows()
    {
        var disabled = false;
        Assert.Throws<InvalidOperationException>(() => new InjectedInputDeliverySuppression(
            () => disabled,
            value =>
            {
                disabled = value;
                if (value) throw new InvalidOperationException("setter failed");
            },
            () => { }));
        Assert.False(disabled);
    }

    private sealed class Scope(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
