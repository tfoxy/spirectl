namespace Spirectl.Sts2.Embedding;

/// <summary>Absolute endpoint components: one scalar, two vector axes, or four color channels.</summary>
public sealed record TweenScheduleStep(
    string Property, IReadOnlyList<double> To, IReadOnlyList<double>? From,
    double StartMs, double DurationMs, int Trans, int Ease, bool Relative);

/// <summary>A raw schedule for one target; Loops is the Godot run count (0 means infinite).</summary>
public sealed record TweenSchedule(
    long TweenId, ulong ObjectId, ulong StartedUsec, bool IsNode2D, int Loops,
    IReadOnlyList<TweenScheduleStep> Steps);

/// <summary>Main-thread, unbuffered fan-out. Subscription is the producer's allocation gate.</summary>
public sealed class EmbeddableTweenScheduleHub
{
    public static EmbeddableTweenScheduleHub Shared { get; } = new();
    private readonly object _gate = new();
    private readonly Dictionary<long, (Action<TweenSchedule> Begin, Action<long> Cancel)> _listeners = [];
    private long _nextId;
    public bool HasSubscribers
    {
        get { lock (_gate) return _listeners.Count != 0; }
    }

    public IDisposable Subscribe(Action<TweenSchedule> begin, Action<long> cancel)
    {
        ArgumentNullException.ThrowIfNull(begin);
        ArgumentNullException.ThrowIfNull(cancel);
        lock (_gate)
        {
            var id = ++_nextId;
            _listeners.Add(id, (begin, cancel));
            return new Subscription(this, id);
        }
    }

    internal void Publish(TweenSchedule schedule)
    {
        lock (_gate)
            foreach (var listener in _listeners.Values)
                listener.Begin(schedule);
    }

    internal void Cancel(long id)
    {
        lock (_gate)
            foreach (var listener in _listeners.Values)
                listener.Cancel(id);
    }

    private sealed class Subscription(EmbeddableTweenScheduleHub hub, long id) : IDisposable
    {
        public void Dispose() { lock (hub._gate) hub._listeners.Remove(id); }
    }
}

/// <summary>Godot-free schedule construction; groups include non-property tweeners.</summary>
internal sealed class TweenScheduleRecord(ulong startedUsec)
{
    private static long _nextTweenId;
    internal sealed class Step
    {
        public string? Property;
        public ulong ObjectId;
        public bool IsNode2D;
        public IReadOnlyList<double> To = [];
        public IReadOnlyList<double>? From;
        public bool Parallel;
        public bool Relative;
        public double DurationMs;
        public double DelayMs;
        public int Trans;
        public int Ease;
    }

    internal readonly List<Step> Steps = [];
    internal readonly List<long> Emitted = [];
    internal bool DefaultParallel;
    internal bool PendingParallel;
    internal int Trans;
    internal int Ease = 2;
    internal int Loops = 1;
    internal bool Cancelled;
    internal bool Finalized;
    internal ulong StartedUsec { get; } = startedUsec;

    internal Step Append(double durationMs)
    {
        var step = new Step
        {
            DurationMs = durationMs, Parallel = Steps.Count > 0 && PendingParallel,
            Trans = Trans, Ease = Ease
        };
        Steps.Add(step);
        PendingParallel = DefaultParallel;
        return step;
    }

    internal void Publish(EmbeddableTweenScheduleHub hub)
    {
        if (Finalized) return;
        Finalized = true;
        if (Cancelled || !hub.HasSubscribers) return;
        foreach (var schedule in Flatten())
        {
            Emitted.Add(schedule.TweenId);
            hub.Publish(schedule);
        }
    }

    internal void Cancel(EmbeddableTweenScheduleHub hub)
    {
        Cancelled = true;
        foreach (var id in Emitted) hub.Cancel(id);
        Emitted.Clear();
    }

    internal IReadOnlyList<TweenSchedule> Flatten()
    {
        var groups = new Dictionary<ulong, (bool Node2D, List<TweenScheduleStep> Steps)>();
        double groupStart = 0, groupEnd = 0;
        foreach (var step in Steps)
        {
            if (!step.Parallel) groupStart = groupEnd;
            var start = groupStart + step.DelayMs;
            groupEnd = Math.Max(groupEnd, start + step.DurationMs);
            if (step.Property is not { } property) continue;
            if (!groups.TryGetValue(step.ObjectId, out var group))
                groups.Add(step.ObjectId, group = (step.IsNode2D, []));
            group.Steps.Add(new TweenScheduleStep(property, step.To, step.From, start,
                step.DurationMs, step.Trans, step.Ease, step.Relative));
        }
        return groups.Select(pair => new TweenSchedule(Interlocked.Increment(ref _nextTweenId),
            pair.Key, StartedUsec, pair.Value.Node2D, Loops, pair.Value.Steps)).ToArray();
    }
}
