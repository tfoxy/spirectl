namespace Spirectl.Sts2.Embedding;

/// <summary>
/// Push-driven, NON-buffering fan-out hub for lite Godot-tween "animation hints" (timing only:
/// scene/node/property + duration + easing). Deliberately simpler than
/// <see cref="EmbeddableCombatEventHub"/>: hints are a hot, ephemeral, pre-arm signal — there is no
/// sequence, ring buffer, or replay. A (re)connecting subscriber just starts receiving live hints.
///
/// <para>The producer is a Godot-tween Harmony hook (<c>Sts2TweenRecorderHooks</c>) whose deferred
/// finalize runs on the game main thread, so <see cref="Publish"/> MUST NOT block — subscriber callbacks
/// are invoked synchronously and are required to be non-blocking (the facade wraps each as a bounded-channel
/// <c>TryWrite</c>). Process-global via <see cref="Shared"/>, matching the static scope of the hook.</para>
///
/// <para>Subscription drives the producer: <see cref="ActiveChanged"/> fires <c>true</c> when the first
/// subscriber attaches and <c>false</c> when the last detaches. The hook subscribes to that event and flips
/// its lite-mode flag accordingly, so the (moderately expensive) capture path only runs while someone is
/// listening. Keeping the toggle here — rather than calling the engine-bound hook directly — keeps this hub
/// engine-free and unit-testable.</para>
/// </summary>
internal sealed class EmbeddableAnimationHintHub
{
    /// <summary>Process-wide instance shared by the static tween hook and the facade.</summary>
    public static EmbeddableAnimationHintHub Shared { get; } = new();

    private readonly object _gate = new();
    private readonly Dictionary<ulong, Action<TweenAnimationHint>> _subscribers = [];
    private ulong _nextSubscriberId = 1;

    /// <summary>
    /// Raised OUTSIDE the fan-out lock with <c>true</c> when the subscriber count goes 0 -&gt; 1 and
    /// <c>false</c> when it goes 1 -&gt; 0. The producer uses this to enable/disable its lite capture path.
    /// </summary>
    public event Action<bool>? ActiveChanged;

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

    /// <summary>Deliver a hint to every current subscriber. Runs on the game thread; MUST NOT block.</summary>
    public void Publish(TweenAnimationHint hint)
    {
        lock (_gate)
        {
            foreach (var onHint in _subscribers.Values)
            {
                onHint(hint);
            }
        }
    }

    /// <summary>Attach a live listener. <paramref name="onHint"/> MUST be non-blocking.</summary>
    public IDisposable Subscribe(Action<TweenAnimationHint> onHint)
    {
        ArgumentNullException.ThrowIfNull(onHint);

        ulong id;
        bool becameActive;
        lock (_gate)
        {
            id = _nextSubscriberId++;
            becameActive = _subscribers.Count == 0;
            _subscribers[id] = onHint;
        }

        if (becameActive)
        {
            ActiveChanged?.Invoke(true);
        }

        return new Subscription(this, id);
    }

    /// <summary>Drop all subscribers (tests / between runs).</summary>
    public void Reset()
    {
        bool wasActive;
        lock (_gate)
        {
            wasActive = _subscribers.Count > 0;
            _subscribers.Clear();
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

    private sealed class Subscription(EmbeddableAnimationHintHub hub, ulong id) : IDisposable
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
