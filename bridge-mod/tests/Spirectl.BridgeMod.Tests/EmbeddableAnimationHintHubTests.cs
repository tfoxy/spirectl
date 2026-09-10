using System;
using System.Collections.Generic;
using Spirectl.Sts2.Embedding;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

public sealed class EmbeddableAnimationHintHubTests
{
    private static TweenAnimationHint Hint(string prop) =>
        new("combat/combat_screen", ".", prop, To: null, DurationMs: 120, Trans: "Quad", Ease: "Out");

    [Fact]
    public void FansOutToEverySubscriberAndStopsAfterDispose()
    {
        var hub = new EmbeddableAnimationHintHub();
        var a = new List<TweenAnimationHint>();
        var b = new List<TweenAnimationHint>();
        var subA = hub.Subscribe(a.Add);
        using var subB = hub.Subscribe(b.Add);

        hub.Publish(Hint("modulate:a"));
        Assert.Single(a);
        Assert.Single(b);
        Assert.Equal("modulate:a", a[0].Property);

        subA.Dispose();
        hub.Publish(Hint("scale"));
        Assert.Single(a); // unsubscribed
        Assert.Equal(2, b.Count);
    }

    [Fact]
    public void PublishWithoutSubscribersIsANoOp()
    {
        var hub = new EmbeddableAnimationHintHub();
        Assert.False(hub.HasSubscribers);
        // Must not throw with nobody listening (the producer publishes unconditionally while enabled).
        hub.Publish(Hint("position"));
    }

    [Fact]
    public void ActiveChangedTogglesOnFirstAndLastSubscriberOnly()
    {
        var hub = new EmbeddableAnimationHintHub();
        var transitions = new List<bool>();
        hub.ActiveChanged += transitions.Add;

        var subA = hub.Subscribe(_ => { });   // 0 -> 1 : true
        var subB = hub.Subscribe(_ => { });   // 1 -> 2 : no event
        Assert.True(hub.HasSubscribers);
        Assert.Equal(new[] { true }, transitions);

        subA.Dispose();                        // 2 -> 1 : no event
        Assert.Equal(new[] { true }, transitions);
        Assert.True(hub.HasSubscribers);

        subB.Dispose();                        // 1 -> 0 : false
        Assert.Equal(new[] { true, false }, transitions);
        Assert.False(hub.HasSubscribers);
    }

    [Fact]
    public void DoubleDisposeDoesNotDoubleFireInactive()
    {
        var hub = new EmbeddableAnimationHintHub();
        var transitions = new List<bool>();
        hub.ActiveChanged += transitions.Add;

        var sub = hub.Subscribe(_ => { });
        sub.Dispose();
        sub.Dispose(); // idempotent
        Assert.Equal(new[] { true, false }, transitions);
    }

    [Fact]
    public void ResetDropsSubscribersAndFiresInactive()
    {
        var hub = new EmbeddableAnimationHintHub();
        var transitions = new List<bool>();
        hub.ActiveChanged += transitions.Add;
        var received = new List<TweenAnimationHint>();
        hub.Subscribe(received.Add);

        hub.Reset();
        Assert.False(hub.HasSubscribers);
        Assert.Equal(new[] { true, false }, transitions);

        hub.Publish(Hint("modulate:a"));
        Assert.Empty(received);
    }
}
