#if ENABLE_STS2_LIVE_HOST
using System.Collections.Concurrent;
using Spirectl.Sts2.Core.SceneInspection;
using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

public sealed class Sts2RuntimeSceneWatcherLifecycleTests : IDisposable
{
    public Sts2RuntimeSceneWatcherLifecycleTests()
    {
        Sts2MainThreadDispatcher.ResetForTests();
    }

    [Fact]
    public async Task LateSubscriberGetsFreshFullCaptureAndNoEarlierPartialOrFull()
    {
        Sts2MainThreadDispatcher.Capture(new SynchronizationContext());
        using var watcher = new Sts2RuntimeSceneWatcher();
        var firstEvents = new ConcurrentQueue<RuntimeSceneDelta>();
        var lateEvents = new ConcurrentQueue<RuntimeSceneDelta>();
        var latestEvents = new ConcurrentQueue<RuntimeSceneDelta>();
        using var first = watcher.Subscribe(firstEvents.Enqueue);

        var firstAdmission = watcher.AdmitCapture()!.Value;
        Assert.True(firstAdmission.NeedsFull);
        using var late = watcher.Subscribe(lateEvents.Enqueue); // joins after the first full was claimed
        var firstFull = Delta(full: true);
        Assert.True(watcher.TryAcceptCapture(firstAdmission, firstFull));
        await watcher.Dispatch(firstFull, firstAdmission);
        Assert.Single(firstEvents);
        Assert.Empty(lateEvents);

        var lateAdmission = watcher.AdmitCapture()!.Value;
        Assert.True(lateAdmission.NeedsFull); // the first full acknowledged only its captured request version
        var lateFull = Delta(full: true);
        Assert.True(watcher.TryAcceptCapture(lateAdmission, lateFull));
        await watcher.Dispatch(lateFull, lateAdmission);
        Assert.Single(lateEvents);
        Assert.True(lateEvents.Single().Full);

        var partialAdmission = watcher.AdmitCapture()!.Value;
        Assert.False(partialAdmission.NeedsFull);
        Assert.Same(lateAdmission.Subscribers, partialAdmission.Subscribers); // no per-capture array allocation
        using var latest = watcher.Subscribe(latestEvents.Enqueue); // joins after a partial was claimed
        var partial = Delta(full: false);
        Assert.True(watcher.TryAcceptCapture(partialAdmission, partial));
        await watcher.Dispatch(partial, partialAdmission);
        Assert.Empty(latestEvents);

        var latestAdmission = watcher.AdmitCapture()!.Value;
        Assert.True(latestAdmission.NeedsFull);
        var latestFull = Delta(full: true);
        Assert.True(watcher.TryAcceptCapture(latestAdmission, latestFull));
        await watcher.Dispatch(latestFull, latestAdmission);
        Assert.Single(latestEvents);
        Assert.True(latestEvents.Single().Full);
    }

    [Fact]
    public async Task DiscardedGenerationAndUnavailableRootKeepFullRequestPending()
    {
        Sts2MainThreadDispatcher.Capture(new SynchronizationContext());
        using var watcher = new Sts2RuntimeSceneWatcher();
        var resumedEvents = new ConcurrentQueue<RuntimeSceneDelta>();
        var first = watcher.Subscribe(_ => { });
        var oldAdmission = watcher.AdmitCapture()!.Value;
        Assert.True(oldAdmission.NeedsFull);

        first.Dispose();
        using var resumed = watcher.Subscribe(resumedEvents.Enqueue);
        Assert.False(watcher.TryAcceptCapture(oldAdmission, Delta(full: true)));

        var unavailableRootAdmission = watcher.AdmitCapture()!.Value;
        Assert.True(unavailableRootAdmission.NeedsFull);
        Assert.True(watcher.TryAcceptCapture(unavailableRootAdmission, delta: null));

        var freshAdmission = watcher.AdmitCapture()!.Value;
        Assert.True(freshAdmission.NeedsFull);
        var freshFull = Delta(full: true);
        Assert.True(watcher.TryAcceptCapture(freshAdmission, freshFull));
        await watcher.Dispatch(freshFull, freshAdmission);
        Assert.Single(resumedEvents);
        Assert.True(resumedEvents.Single().Full);
    }

    [Fact]
    public async Task UnsubscribedEntryIsSkippedAfterAcceptanceBeforeAsyncDispatch()
    {
        Sts2MainThreadDispatcher.Capture(new SynchronizationContext());
        using var watcher = new Sts2RuntimeSceneWatcher();
        var departedEvents = new ConcurrentQueue<RuntimeSceneDelta>();
        var continuingEvents = new ConcurrentQueue<RuntimeSceneDelta>();
        var newcomerEvents = new ConcurrentQueue<RuntimeSceneDelta>();
        var departed = watcher.Subscribe(departedEvents.Enqueue);
        using var continuing = watcher.Subscribe(continuingEvents.Enqueue);

        var admission = watcher.AdmitCapture()!.Value;
        var full = Delta(full: true);
        Assert.True(watcher.TryAcceptCapture(admission, full));
        departed.Dispose();
        using var newcomer = watcher.Subscribe(newcomerEvents.Enqueue);
        await watcher.Dispatch(full, admission);

        Assert.Empty(departedEvents);
        Assert.Single(continuingEvents);
        Assert.Empty(newcomerEvents);
        Assert.True(watcher.AdmitCapture()!.Value.NeedsFull);
    }

    private static RuntimeSceneDelta Delta(bool full)
        => new(full, "test", "screen:test", [], [], full ? Array.Empty<string>() : null);

    public void Dispose()
    {
        Sts2MainThreadDispatcher.ResetForTests();
    }
}
#endif
