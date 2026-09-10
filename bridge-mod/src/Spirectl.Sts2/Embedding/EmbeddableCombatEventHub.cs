namespace Spirectl.Sts2.Embedding;

/// <summary>
/// Push-driven, append-only, NON-deduplicating hub for transient combat events
/// (floating damage numbers today; block/death/card-play later). Deliberately NOT the
/// fingerprint-dedup <see cref="EmbeddableStateSubscriptionHub{TSnapshot,TEvent}"/>:
/// every published event is delivered with a monotonically increasing <c>Sequence</c>,
/// and a bounded ring buffer lets a (re)connecting subscriber resume from
/// <c>sinceSequence</c>.
///
/// The producer is an in-game Harmony hook (see <c>Sts2DamageEventHooks</c>) that runs on
/// the game thread, so <see cref="PublishDamage"/> MUST NOT block — subscriber callbacks
/// are invoked synchronously and are required to be non-blocking (the facade wraps each as
/// a bounded-channel <c>TryWrite</c>). Process-global (one game, one combat) via
/// <see cref="Shared"/>, matching the static scope of the Harmony patch.
///
/// <para>Subscription drives the producers, exactly as it does for
/// <see cref="EmbeddableAnimationHintHub"/>: <see cref="ActiveChanged"/> fires <c>true</c> when the first
/// subscriber attaches and <c>false</c> when the last detaches, and each capture hook holds the answer in a
/// volatile bool it early-outs on. Without that, every damage number, card upgrade and VFX spawn in an
/// ordinary single-player game paid for a resolve, an allocation and this lock while NOBODY was watching —
/// and the couch-coop mod, which is the main embedder, never subscribes to combat events at all. Keeping the
/// toggle here rather than calling the engine-bound hooks directly keeps this hub engine-free and
/// unit-testable.</para>
///
/// <para><b>Consequence, and it is deliberate.</b> The ring now only accumulates while somebody is
/// listening, so <see cref="Subscribe"/>'s <c>sinceSequence</c> replay covers events captured since the FIRST
/// subscriber attached, not the whole combat that preceded it. Resume across a re-subscribe is unaffected as
/// long as one subscriber stays attached; a cold subscribe mid-combat starts from now. That is the same trade
/// the animation-hint path has always made, and it is worth it: the alternative is charging every player who
/// never watches for the benefit of a watcher who might attach later.</para>
/// </summary>
internal sealed class EmbeddableCombatEventHub
{
    /// <summary>Process-wide instance shared by the static damage hook and the facade.</summary>
    public static EmbeddableCombatEventHub Shared { get; } = new();

    private const int RingCapacity = 512;

    private readonly object _gate = new();
    private readonly Queue<CombatWatchEvent> _ring = new();
    private readonly Dictionary<ulong, Action<CombatWatchEvent>> _subscribers = [];
    private ulong _nextSequence = 1;
    private ulong _nextSubscriberId = 1;

    /// <summary>
    /// Raised OUTSIDE the fan-out lock with <c>true</c> when the subscriber count goes 0 -&gt; 1 and
    /// <c>false</c> when it goes 1 -&gt; 0. The capture hooks use this to arm and disarm themselves.
    /// </summary>
    public event Action<bool>? ActiveChanged;

    /// <summary>
    /// Whether anything is listening. Takes the fan-out lock, so a hook on a per-frame or per-node-attach
    /// path must NOT call this directly — it should cache the value from <see cref="ActiveChanged"/> in a
    /// volatile field and read that instead.
    /// </summary>
    public bool HasSubscribers
    {
        get
        {
            lock (_gate)
            {
                return _subscribers.Count > 0;
            }
        }
    }

    /// <summary>
    /// Record a creature taking unblocked damage. No-op when <paramref name="amount"/> ≤ 0
    /// (fully-blocked hits show no number) or the target id is empty. Runs on the game thread.
    /// </summary>
    public void PublishDamage(
        string targetCreatureId,
        int amount,
        string? dealerCreatureId,
        string? sourceCardModelId,
        DateTimeOffset observedAtUtc)
    {
        if (string.IsNullOrEmpty(targetCreatureId) || amount <= 0)
        {
            return;
        }

        lock (_gate)
        {
            var evt = new CombatWatchEvent(
                CombatWatchEventType.Damage,
                _nextSequence++,
                observedAtUtc,
                new CombatDamagePayload(
                    targetCreatureId,
                    amount,
                    string.IsNullOrEmpty(dealerCreatureId) ? null : dealerCreatureId,
                    string.IsNullOrEmpty(sourceCardModelId) ? null : sourceCardModelId),
                Error: null);

            _ring.Enqueue(evt);
            while (_ring.Count > RingCapacity)
            {
                _ring.Dequeue();
            }

            // Non-blocking fan-out (each onEvent is a channel TryWrite). Invoked under the
            // lock so a concurrent Subscribe replay can't interleave with live delivery.
            foreach (var onEvent in _subscribers.Values)
            {
                onEvent(evt);
            }
        }
    }

    /// <summary>
    /// Record a card being upgraded mid-combat (e.g. STONE_CRACKER at room entry). No-op when
    /// the model id is empty. Runs on the game thread; MUST NOT block (see <see cref="PublishDamage"/>).
    /// </summary>
    public void PublishCardUpgrade(
        string? cardId,
        string cardModelId,
        string? sourceRelicModelId,
        DateTimeOffset observedAtUtc)
    {
        if (string.IsNullOrEmpty(cardModelId))
        {
            return;
        }

        lock (_gate)
        {
            var evt = new CombatWatchEvent(
                CombatWatchEventType.CardUpgrade,
                _nextSequence++,
                observedAtUtc,
                Damage: null,
                Error: null,
                CardUpgrade: new CombatCardUpgradePayload(
                    cardId ?? string.Empty,
                    cardModelId,
                    string.IsNullOrEmpty(sourceRelicModelId) ? null : sourceRelicModelId));

            _ring.Enqueue(evt);
            while (_ring.Count > RingCapacity)
            {
                _ring.Dequeue();
            }

            foreach (var onEvent in _subscribers.Values)
            {
                onEvent(evt);
            }
        }
    }

    /// <summary>
    /// Record a game VFX node spawning in combat (the native VFX backbone): one event per spawn,
    /// naming the res://-relative scene it instances plus where it anchors. No-op when the scene path
    /// is empty. Runs on the game thread; MUST NOT block (see <see cref="PublishDamage"/>).
    /// </summary>
    public void PublishVfx(
        string scenePath,
        string? anchorCreatureId,
        int amount,
        DateTimeOffset observedAtUtc)
    {
        if (string.IsNullOrEmpty(scenePath))
        {
            return;
        }

        lock (_gate)
        {
            var evt = new CombatWatchEvent(
                CombatWatchEventType.Vfx,
                _nextSequence++,
                observedAtUtc,
                Damage: null,
                Error: null,
                CardUpgrade: null,
                Vfx: new CombatVfxPayload(
                    scenePath,
                    string.IsNullOrEmpty(anchorCreatureId) ? null : anchorCreatureId,
                    amount));

            _ring.Enqueue(evt);
            while (_ring.Count > RingCapacity)
            {
                _ring.Dequeue();
            }

            foreach (var onEvent in _subscribers.Values)
            {
                onEvent(evt);
            }
        }
    }

    /// <summary>
    /// Subscribe to the live stream. Buffered events with <c>Sequence &gt; sinceSequence</c>
    /// are replayed in order before live delivery begins (under the lock, so a concurrent
    /// publish neither duplicates nor reorders). <paramref name="onEvent"/> MUST be non-blocking.
    /// </summary>
    public IDisposable Subscribe(ulong sinceSequence, Action<CombatWatchEvent> onEvent)
    {
        ulong id;
        bool becameActive;
        lock (_gate)
        {
            id = _nextSubscriberId++;
            becameActive = _subscribers.Count == 0;
            _subscribers[id] = onEvent;
            foreach (var evt in _ring)
            {
                if (evt.Sequence > sinceSequence)
                {
                    onEvent(evt);
                }
            }
        }

        // Outside the lock, and AFTER the replay: arming the producers first would let a live event
        // interleave ahead of the buffered ones this subscriber is still being handed.
        if (becameActive)
        {
            ActiveChanged?.Invoke(true);
        }

        return new Subscription(this, id);
    }

    /// <summary>Drop all buffered events and subscribers (tests / between runs).</summary>
    public void Reset()
    {
        bool wasActive;
        lock (_gate)
        {
            wasActive = _subscribers.Count > 0;
            _ring.Clear();
            _subscribers.Clear();
            _nextSequence = 1;
            _nextSubscriberId = 1;
        }

        if (wasActive)
        {
            ActiveChanged?.Invoke(false);
        }
    }

    private void Unsubscribe(ulong id)
    {
        bool becameInactive;
        lock (_gate)
        {
            if (!_subscribers.Remove(id))
            {
                return;
            }

            becameInactive = _subscribers.Count == 0;
        }

        if (becameInactive)
        {
            ActiveChanged?.Invoke(false);
        }
    }

    private sealed class Subscription(EmbeddableCombatEventHub hub, ulong id) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            hub.Unsubscribe(id);
        }
    }
}
