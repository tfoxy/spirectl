namespace Spirectl.Sts2.Embedding;

/// <summary>Retains only active subscriptions so a runtime can release every callback it owns.</summary>
internal sealed class RuntimeSubscriptionScope : IDisposable
{
    private readonly object _gate = new();
    private readonly HashSet<Subscription> _active = [];
    private bool _disposed;

    public IDisposable Subscribe(Func<IDisposable> subscribe)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var subscription = new Subscription(this, subscribe());
            _active.Add(subscription);
            return subscription;
        }
    }

    public void Dispose()
    {
        Subscription[] active;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            active = [.. _active];
            _active.Clear();
        }
        List<Exception>? failures = null;
        foreach (var subscription in active)
        {
            try { subscription.Dispose(); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        if (failures is not null) throw new AggregateException(failures);
    }

    private sealed class Subscription(RuntimeSubscriptionScope owner, IDisposable inner) : IDisposable
    {
        private IDisposable? _inner = inner;

        public void Dispose()
        {
            var subscription = Interlocked.Exchange(ref _inner, null);
            if (subscription is null) return;
            lock (owner._gate) owner._active.Remove(this);
            subscription.Dispose();
        }
    }
}
