using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.State;
using Spirectl.Sts2.Embedding;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

public sealed class EmbeddableMultiplayerConnectionHubTests
{
    [Fact]
    public void UnsupportedFacadeHasNoConnectionSnapshotOrSubscription()
    {
        var runtime = SpirectlRuntimeFacade.FromFactory(
            new PlaceholderStateExtractor(),
            new PlaceholderActionHandler(),
            new Spirectl.Sts2.Core.Logging.InMemoryLogStream(),
            new Spirectl.Sts2.Core.Perspective.DefaultPerspectiveProvider(),
            new Spirectl.Sts2.Core.Artifacts.PlaceholderAssetExtractProvider());
        var received = new List<MultiplayerConnectionSnapshot>();

        using var subscription = runtime.SubscribeMultiplayerConnection(received.Add);

        Assert.Null(runtime.GetCurrentMultiplayerConnection());
        Assert.Empty(received);
        Assert.Contains(runtime.GetCapabilities().Capabilities, capability =>
            capability.Id == "multiplayer-connection" && !capability.Supported);
    }

    [Fact]
    public void DeliversOrderedObservationsAndReplaysCurrentToNewSubscriber()
    {
        var hub = new EmbeddableMultiplayerConnectionHub();
        var first = new List<MultiplayerConnectionSnapshot>();
        using var firstSubscription = hub.Subscribe(first.Add);

        hub.Publish(MultiplayerConnectionPhase.Connecting, null, DateTimeOffset.UnixEpoch);
        hub.Publish(
            MultiplayerConnectionPhase.Failed,
            new MultiplayerConnectionError("native-network-error", "timeout"),
            DateTimeOffset.UnixEpoch.AddSeconds(1));

        var replay = new List<MultiplayerConnectionSnapshot>();
        using var replaySubscription = hub.Subscribe(replay.Add);

        Assert.Equal([1UL, 2UL], first.Select(entry => entry.Sequence));
        Assert.Single(replay);
        Assert.Equal(2UL, replay[0].Sequence);
        Assert.Equal(MultiplayerConnectionPhase.Failed, replay[0].Phase);
        var error = Assert.IsType<MultiplayerConnectionError>(replay[0].Error);
        Assert.Equal("native-network-error", error.Code);
        Assert.Equal("timeout", error.NativeDetail);
    }

    [Fact]
    public void StopsDeliveryAfterDisposal()
    {
        var hub = new EmbeddableMultiplayerConnectionHub();
        var received = new List<MultiplayerConnectionSnapshot>();
        var subscription = hub.Subscribe(received.Add);

        hub.Publish(MultiplayerConnectionPhase.Connecting, null, DateTimeOffset.UnixEpoch);
        subscription.Dispose();
        hub.Publish(MultiplayerConnectionPhase.Disconnected, null, DateTimeOffset.UnixEpoch.AddSeconds(1));

        Assert.Single(received);
        Assert.Equal(MultiplayerConnectionPhase.Connecting, received[0].Phase);
    }

    [Fact]
    public void FaultingAndSelfRemovingSubscribersDoNotBlockDelivery()
    {
        var hub = new EmbeddableMultiplayerConnectionHub();
        var received = new List<MultiplayerConnectionSnapshot>();
        IDisposable? selfRemoving = null;
        using var faulting = hub.Subscribe(_ => throw new InvalidOperationException("subscriber failure"));
        selfRemoving = hub.Subscribe(_ => selfRemoving!.Dispose());
        using var healthy = hub.Subscribe(received.Add);

        hub.Publish(MultiplayerConnectionPhase.Connecting, null, DateTimeOffset.UnixEpoch);
        hub.Publish(MultiplayerConnectionPhase.Disconnected, null, DateTimeOffset.UnixEpoch.AddSeconds(1));

        Assert.Equal([MultiplayerConnectionPhase.Connecting, MultiplayerConnectionPhase.Disconnected], received.Select(value => value.Phase));
    }

    [Fact]
    public async Task ConcurrentPublishDoesNotReorderOneSubscriber()
    {
        var hub = new EmbeddableMultiplayerConnectionHub();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new List<ulong>();
        using var subscription = hub.Subscribe(snapshot =>
        {
            received.Add(snapshot.Sequence);
            if (snapshot.Sequence == 1)
            {
                entered.SetResult();
                release.Task.GetAwaiter().GetResult();
            }
        });

        var first = Task.Run(() => hub.Publish(MultiplayerConnectionPhase.Connecting, null, DateTimeOffset.UnixEpoch));
        await entered.Task;
        var second = Task.Run(() => hub.Publish(MultiplayerConnectionPhase.Disconnected, null, DateTimeOffset.UnixEpoch.AddSeconds(1)));
        await Task.Delay(20);
        Assert.Equal([1UL], received);
        release.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal([1UL, 2UL], received);
    }

    [Fact]
    public void ReentrantPublishRetainsSequenceOrder()
    {
        var hub = new EmbeddableMultiplayerConnectionHub();
        var received = new List<ulong>();
        using var subscription = hub.Subscribe(snapshot =>
        {
            received.Add(snapshot.Sequence);
            if (snapshot.Sequence == 1)
                hub.Publish(MultiplayerConnectionPhase.Disconnected, null, DateTimeOffset.UnixEpoch.AddSeconds(1));
        });

        hub.Publish(MultiplayerConnectionPhase.Connecting, null, DateTimeOffset.UnixEpoch);

        Assert.Equal([1UL, 2UL], received);
    }
}
