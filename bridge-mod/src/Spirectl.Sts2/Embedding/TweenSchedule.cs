using System.Runtime.CompilerServices;

namespace Spirectl.Sts2.Embedding;

/// <summary>The endpoint type represented by a step's components.</summary>
public enum TweenValueKind { Unknown, Float, Int, Vector2, Vector3, Vector4, Color }

/// <summary>Absolute endpoint components: scalar, vector axes, or color channels.</summary>
public sealed record TweenScheduleStep(
    string Property, IReadOnlyList<double> To, IReadOnlyList<double>? From,
    double StartMs, double DurationMs, int Trans, int Ease, bool Relative,
    TweenValueKind Kind = TweenValueKind.Unknown);

/// <summary>A raw schedule for one target; Loops is the Godot run count (0 means infinite).</summary>
public sealed record TweenSchedule(
    long TweenId, ulong ObjectId, ulong StartedUsec, bool IsNode2D, int Loops,
    IReadOnlyList<TweenScheduleStep> Steps);

/// <summary>Why a published schedule stopped owning its target.</summary>
public enum TweenEndReason { Finished, Killed, Stopped, Paused, Retimed }

public readonly record struct TweenEnd(long TweenId, TweenEndReason Reason);

/// <summary>Main-thread, unbuffered fan-out. Subscription is the producer's allocation gate.</summary>
public sealed class EmbeddableTweenScheduleHub
{
    public static EmbeddableTweenScheduleHub Shared { get; } = new();
    private readonly object _gate = new();
    private readonly Dictionary<long, (Action<TweenSchedule> Begin, Action<TweenEnd> End)> _listeners = [];
    private long _nextId;
    public bool HasSubscribers
    {
        get { lock (_gate) return _listeners.Count != 0; }
    }

    public IDisposable Subscribe(Action<TweenSchedule> begin, Action<long> cancel)
    {
        ArgumentNullException.ThrowIfNull(cancel);
        return Subscribe(begin, (TweenEnd end) => cancel(end.TweenId));
    }

    public IDisposable Subscribe(Action<TweenSchedule> begin, Action<TweenEnd> end)
    {
        ArgumentNullException.ThrowIfNull(begin);
        ArgumentNullException.ThrowIfNull(end);
        lock (_gate)
        {
            var id = ++_nextId;
            _listeners.Add(id, (begin, end));
            return new Subscription(this, id);
        }
    }

    internal void Publish(TweenSchedule schedule)
    {
        lock (_gate)
            foreach (var listener in _listeners.Values.ToArray())
                try { listener.Begin(schedule); }
                catch { /* Observation must never interrupt the game. */ }
    }

    internal void End(TweenEnd end)
    {
        lock (_gate)
            foreach (var listener in _listeners.Values.ToArray())
                try { listener.End(end); }
                catch { /* Observation must never interrupt the game. */ }
    }

    private sealed class Subscription(EmbeddableTweenScheduleHub hub, long id) : IDisposable
    {
        public void Dispose() { lock (hub._gate) hub._listeners.Remove(id); }
    }
}

/// <summary>Event-driven retirement; weak keys do not retain abandoned tweens' records.</summary>
internal sealed class TweenScheduleRegistry
{
    private readonly ConditionalWeakTable<TweenScheduleRecord, object> _published = new();
    private double _lastTimeScale = 1;

    internal void Add(TweenScheduleRecord record) => _published.Add(record, new object());
    internal void Remove(TweenScheduleRecord record) => _published.Remove(record);
    internal bool Contains(TweenScheduleRecord record) => _published.TryGetValue(record, out _);

    internal void TimeScaleChanged(EmbeddableTweenScheduleHub hub, double timeScale)
    {
        var retimed = _lastTimeScale == 1 && timeScale != 1;
        _lastTimeScale = timeScale;
        if (!retimed) return;
        foreach (var pair in _published.ToArray()) pair.Key.End(hub, TweenEndReason.Retimed);
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
        public TweenValueKind Kind;
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
    private bool _ended;
    private TweenScheduleRegistry? _registry;
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

    internal void Publish(EmbeddableTweenScheduleHub hub, double timeScale = 1,
        TweenScheduleRegistry? registry = null)
    {
        if (Finalized) return;
        Finalized = true;
        // An event-time sample also resynchronizes transitions missed without subscribers.
        registry?.TimeScaleChanged(hub, timeScale);
        if (_ended || !hub.HasSubscribers || timeScale != 1) return;
        var schedules = Flatten();
        if (schedules.Count != 0)
        {
            _registry = registry;
            _registry?.Add(this);
        }
        foreach (var schedule in schedules)
        {
            if (_ended) break;
            Emitted.Add(schedule.TweenId);
            hub.Publish(schedule);
        }
    }

    internal void End(EmbeddableTweenScheduleHub hub, TweenEndReason reason)
    {
        if (_ended) return;
        _ended = true;
        _registry?.Remove(this);
        _registry = null;
        var emitted = Emitted.ToArray();
        Emitted.Clear();
        foreach (var id in emitted) hub.End(new TweenEnd(id, reason));
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
                step.DurationMs, step.Trans, step.Ease, step.Relative, step.Kind));
        }
        return groups.Select(pair => new TweenSchedule(Interlocked.Increment(ref _nextTweenId),
            pair.Key, StartedUsec, pair.Value.Node2D, Loops, pair.Value.Steps)).ToArray();
    }
}
