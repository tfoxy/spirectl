namespace Spirectl.Sts2.Embedding;

/// <summary>Process-wide ordered observations published by the native multiplayer hooks.</summary>
internal sealed class EmbeddableMultiplayerConnectionHub
{
    public static EmbeddableMultiplayerConnectionHub Shared { get; } = new();

    private readonly object _gate = new();
    private readonly Dictionary<ulong, Subscriber> _subscribers = [];
    private MultiplayerConnectionSnapshot? _current;
    private ulong _nextSequence = 1;
    private ulong _nextSubscriberId = 1;

    public MultiplayerConnectionSnapshot? GetCurrent()
    {
        lock (_gate) return _current;
    }

    public IDisposable Subscribe(Action<MultiplayerConnectionSnapshot> onEvent)
    {
        ArgumentNullException.ThrowIfNull(onEvent);
        MultiplayerConnectionSnapshot? current;
        Subscriber subscriber;
        Subscription subscription;
        lock (_gate)
        {
            var id = _nextSubscriberId++;
            subscriber = new Subscriber(onEvent);
            _subscribers.Add(id, subscriber);
            current = _current;
            subscription = new Subscription(this, id);
        }
        if (current is not null) subscriber.Deliver(current);
        return subscription;
    }

    public void Publish(MultiplayerConnectionPhase phase, MultiplayerConnectionError? error, DateTimeOffset observedAtUtc)
    {
        MultiplayerConnectionSnapshot snapshot;
        Subscriber[] subscribers;
        lock (_gate)
        {
            snapshot = new MultiplayerConnectionSnapshot(_nextSequence++, observedAtUtc, phase, error);
            _current = snapshot;
            subscribers = _subscribers.Values.ToArray();
        }

        foreach (var subscriber in subscribers) subscriber.Deliver(snapshot);
    }

    internal void ResetForTests()
    {
        lock (_gate)
        {
            _current = null;
            _subscribers.Clear();
            _nextSequence = 1;
            _nextSubscriberId = 1;
        }
    }

    private void Unsubscribe(ulong id)
    {
        lock (_gate) _subscribers.Remove(id);
    }

    private sealed class Subscriber(Action<MultiplayerConnectionSnapshot> callback)
    {
        private readonly object _dispatchGate = new();
        private ulong _lastSequence;

        public void Deliver(MultiplayerConnectionSnapshot snapshot)
        {
            lock (_dispatchGate)
            {
                if (snapshot.Sequence <= _lastSequence) return;
                _lastSequence = snapshot.Sequence;
                try { callback(snapshot); }
                catch { }
            }
        }
    }

    private sealed class Subscription(EmbeddableMultiplayerConnectionHub owner, ulong id) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Unsubscribe(id);
        }
    }
}
