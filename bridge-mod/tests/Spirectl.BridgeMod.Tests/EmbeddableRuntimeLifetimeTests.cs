using Spirectl.Sts2;
using Spirectl.Sts2.Embedding;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

public sealed class EmbeddableRuntimeLifetimeTests
{
    [Fact]
    public async Task DisposingRuntimeCompletesIdleStreamsAndRejectsNewSubscriptions()
    {
        using var runtime = EmbeddedRuntimeTestFactory.Create();
        await using var events = runtime.WatchCombatEventsAsync(new CombatEventSubscriptionRequest()).GetAsyncEnumerator();
        var pending = events.MoveNextAsync().AsTask();
        Assert.False(pending.IsCompleted);
        runtime.Dispose();
        Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Throws<ObjectDisposedException>(() => runtime.SubscribeCombatEvents(new CombatEventSubscriptionRequest(), _ => { }));
    }
}
