#if ENABLE_STS2_LIVE_HOST
using System.Collections.Concurrent;
using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

public sealed class Sts2GodotSynchronizationContextTests
{
    [Fact]
    public void PostCoalescesWakeAndDrainsFifoIncludingReentrantWork()
    {
        var scheduled = new Queue<Action>();
        Sts2GodotSynchronizationContext? context = null;
        context = new Sts2GodotSynchronizationContext(() => scheduled.Enqueue(context.Drain));
        var order = new List<int>();

        context.Post(_ =>
        {
            order.Add(1);
            context.Post(_ => order.Add(3), null);
        }, null);
        context.Post(_ => order.Add(2), null);

        Assert.Single(scheduled);
        scheduled.Dequeue()();
        Assert.Equal([1, 2, 3], order);
        Assert.Empty(scheduled);
        Assert.Equal(0, context.QueueDepth);
    }

    [Fact]
    public async Task ConcurrentPostAndDrainNeverLeavesQueuedWorkWithoutAWake()
    {
        const int count = 10_000;
        var scheduled = new ConcurrentQueue<Action>();
        using var wake = new SemaphoreSlim(0);
        Sts2GodotSynchronizationContext? context = null;
        context = new Sts2GodotSynchronizationContext(() =>
        {
            scheduled.Enqueue(context.Drain);
            wake.Release();
        });
        var observed = new List<int>(count);

        var producer = Task.Run(() =>
        {
            for (var index = 0; index < count; index++)
            {
                var value = index;
                context.Post(_ => observed.Add(value), null);
            }
        });

        while (!producer.IsCompleted || context.QueueDepth > 0 || !scheduled.IsEmpty)
        {
            await wake.WaitAsync(TimeSpan.FromSeconds(2));
            while (scheduled.TryDequeue(out var drain))
            {
                drain();
            }
        }
        await producer;

        Assert.Equal(Enumerable.Range(0, count), observed);
        Assert.Equal(0, context.QueueDepth);
        Assert.Empty(scheduled);
    }

    [Fact]
    public void TickDrainMakesQueuedInputVisibleBeforeObserversRun()
    {
        var scheduled = new Queue<Action>();
        Sts2GodotSynchronizationContext? context = null;
        context = new Sts2GodotSynchronizationContext(() => scheduled.Enqueue(context.Drain));
        var inputState = 0;
        var observedState = -1;

        context.Post(_ => inputState = 42, null);
        context.DrainBefore(() => observedState = inputState);

        Assert.Equal(42, observedState);
        Assert.Equal(0, context.QueueDepth);
        Assert.Single(scheduled); // the already-deferred drain is harmless when Godot later flushes it
        scheduled.Dequeue()();
        Assert.Empty(scheduled);
    }

    [Fact]
    public void FailedWakeDoesNotPoisonScheduledBitOrStrandWork()
    {
        var scheduled = new Queue<Action>();
        var failFirstWake = true;
        Sts2GodotSynchronizationContext? context = null;
        context = new Sts2GodotSynchronizationContext(() =>
        {
            if (failFirstWake)
            {
                failFirstWake = false;
                throw new InvalidOperationException("transient schedule failure");
            }
            scheduled.Enqueue(context.Drain);
        });
        var order = new List<int>();

        Assert.Throws<InvalidOperationException>(() => context.Post(_ => order.Add(1), null));
        context.Post(_ => order.Add(2), null);

        Assert.Single(scheduled);
        scheduled.Dequeue()();
        Assert.Equal([1, 2], order);
        Assert.Equal(0, context.QueueDepth);
    }
}
#endif
