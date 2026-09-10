using System;
using System.Collections.Generic;
using Spirectl.Sts2.Embedding;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

public sealed class EmbeddableCombatEventHubTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch;

    [Fact]
    public void DeliversDamageEventsWithMonotonicSequence()
    {
        var hub = new EmbeddableCombatEventHub();
        var received = new List<CombatWatchEvent>();
        using var _ = hub.Subscribe(sinceSequence: 0, received.Add);

        hub.PublishDamage("creature:1", 12, dealerCreatureId: "creature:9", sourceCardModelId: null, At);
        hub.PublishDamage("creature:2", 7, dealerCreatureId: null, sourceCardModelId: null, At);

        Assert.Equal(2, received.Count);
        Assert.Equal((ulong)1, received[0].Sequence);
        Assert.Equal((ulong)2, received[1].Sequence);
        Assert.Equal(CombatWatchEventType.Damage, received[0].Type);
        Assert.Equal("creature:1", received[0].Damage!.TargetCreatureId);
        Assert.Equal(12, received[0].Damage!.Amount);
        Assert.Equal("creature:9", received[0].Damage!.DealerCreatureId);
        // Empty dealer/source normalize to null.
        Assert.Null(received[1].Damage!.DealerCreatureId);
        Assert.Null(received[1].Damage!.SourceCardModelId);
    }

    [Fact]
    public void SkipsBlockedAndEmptyTargets()
    {
        var hub = new EmbeddableCombatEventHub();
        var received = new List<CombatWatchEvent>();
        using var _ = hub.Subscribe(sinceSequence: 0, received.Add);

        hub.PublishDamage("creature:1", 0, null, null, At); // fully blocked -> no number
        hub.PublishDamage("creature:1", -3, null, null, At); // never negative
        hub.PublishDamage("", 5, null, null, At); // no target id
        hub.PublishDamage("creature:1", 4, null, null, At); // the only real hit

        Assert.Single(received);
        Assert.Equal((ulong)1, received[0].Sequence);
        Assert.Equal(4, received[0].Damage!.Amount);
    }

    [Fact]
    public void ReplaysBufferedEventsAfterSinceSequence()
    {
        var hub = new EmbeddableCombatEventHub();
        hub.PublishDamage("creature:1", 1, null, null, At); // seq 1
        hub.PublishDamage("creature:1", 2, null, null, At); // seq 2
        hub.PublishDamage("creature:1", 3, null, null, At); // seq 3

        var received = new List<CombatWatchEvent>();
        // Resume after sequence 1: replays 2 and 3, then live.
        using var _ = hub.Subscribe(sinceSequence: 1, received.Add);

        Assert.Equal(2, received.Count);
        Assert.Equal((ulong)2, received[0].Sequence);
        Assert.Equal((ulong)3, received[1].Sequence);

        hub.PublishDamage("creature:1", 4, null, null, At); // live seq 4
        Assert.Equal(3, received.Count);
        Assert.Equal((ulong)4, received[2].Sequence);
    }

    [Fact]
    public void EvictsOldestBeyondRingCapacity()
    {
        var hub = new EmbeddableCombatEventHub();
        const int total = 600; // > RingCapacity (512)
        for (var i = 0; i < total; i++)
        {
            hub.PublishDamage("creature:1", 1, null, null, At);
        }

        var received = new List<CombatWatchEvent>();
        using var _ = hub.Subscribe(sinceSequence: 0, received.Add);

        // Only the most recent 512 survive; the earliest replayed sequence is total-512+1.
        Assert.Equal(512, received.Count);
        Assert.Equal((ulong)(total - 512 + 1), received[0].Sequence);
        Assert.Equal((ulong)total, received[^1].Sequence);
    }

    [Fact]
    public void FansOutToEverySubscriberAndStopsAfterDispose()
    {
        var hub = new EmbeddableCombatEventHub();
        var a = new List<CombatWatchEvent>();
        var b = new List<CombatWatchEvent>();
        var subA = hub.Subscribe(sinceSequence: 0, a.Add);
        using var subB = hub.Subscribe(sinceSequence: 0, b.Add);

        hub.PublishDamage("creature:1", 1, null, null, At);
        Assert.Single(a);
        Assert.Single(b);

        subA.Dispose();
        hub.PublishDamage("creature:1", 2, null, null, At);
        Assert.Single(a); // unsubscribed
        Assert.Equal(2, b.Count);
    }
}
