namespace Spirectl.Sts2.Common;

/// <summary>
/// A thread-safe, fixed-capacity least-recently-used map. Adding past the capacity evicts the entry that was
/// read or written longest ago, so a long session holds at most <see cref="Capacity"/> entries.
/// </summary>
internal sealed class Sts2BoundedLruCache<TKey, TValue>
    where TKey : notnull
{
    private readonly Lock _gate = new();
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _entries;
    private readonly LinkedList<(TKey Key, TValue Value)> _recency = new();

    internal Sts2BoundedLruCache(int capacity, IEqualityComparer<TKey>? comparer = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
        _entries = new Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>>(comparer);
    }

    internal int Capacity { get; }

    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    internal bool TryGet(TKey key, out TValue value)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var node))
            {
                _recency.Remove(node);
                _recency.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }

        value = default!;
        return false;
    }

    internal void Set(TKey key, TValue value)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                _recency.Remove(existing);
            }

            var node = _recency.AddFirst((key, value));
            _entries[key] = node;
            while (_entries.Count > Capacity && _recency.Last is { } victim)
            {
                _recency.RemoveLast();
                _entries.Remove(victim.Value.Key);
            }
        }
    }

    internal void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _recency.Clear();
        }
    }
}
