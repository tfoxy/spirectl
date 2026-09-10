using System.Collections.Concurrent;
using System.Diagnostics;
using Spirectl.Sts2.Core.State;
using Spirectl.Sts2.Core.Perspective;
using Spirectl.Sts2.Live;

namespace Spirectl.Sts2.Embedding;

/// <summary>
/// The capture dimensions for a state subscription: the observation perspective (which
/// player's screen-aware view, and whether local-only transient UI is included) plus the
/// per-capture timeout.
/// </summary>
internal sealed record StateProjection(
    PerspectiveSelection? Perspective,
    TimeSpan? StateTimeout);

/// <summary>Snapshot-agnostic capture result shape for the state capture.</summary>
internal readonly record struct StateCaptureOutcome<TSnapshot>(
    bool Success,
    TSnapshot? State,
    EmbeddableRuntimeError? Error,
    EmbeddableRuntimeHealthSnapshot? Health)
    where TSnapshot : class;

/// <summary>Builds a concrete watch event from the shared dedup bookkeeping.</summary>
internal delegate TEvent StateWatchEventFactory<TSnapshot, out TEvent>(
    CurrentStateWatchEventType type,
    ulong sequence,
    DateTimeOffset observedAtUtc,
    string? fingerprint,
    ulong? semanticRevision,
    TSnapshot? state,
    EmbeddableRuntimeError? error,
    EmbeddableRuntimeHealthSnapshot? health)
    where TSnapshot : class;

/// <summary>
/// Tick-driven, fingerprint-deduplicating push hub. Subscribes to the game's
/// main-thread tick, captures state per eligible tick (paced by each subscriber's
/// MinCaptureInterval and idle backoff), and only emits a Changed event when the semantic
/// fingerprint differs from the last emission. Generic over the snapshot/event type so the same
/// scheduling and dedup logic can serve future snapshot shapes.
/// <para>
/// <b>Pacing, in three layers.</b> (1) The main-thread tick fires at ~100 Hz and is GATED on a
/// precomputed next-due timestamp, so a tick with nothing due costs one volatile read and returns —
/// it does not take the hub lock and does not schedule a pool task. (2) A subscriber whose capture
/// produced an unchanged fingerprint DOUBLES its interval, from its MinCaptureInterval floor up to
/// <see cref="Sts2StateWatchRuntimeSettings.MaxIdleInterval"/>, and snaps back to the floor the moment
/// anything changes, an action forces a refresh, or the semantic revision moves. (3)
/// <see cref="Sts2SemanticStateRevision"/> can collapse that backoff a tick after a covered change,
/// as an accelerator — the idle poll, not the revision, is what guarantees a change is always found.
/// </para>
/// <para>
/// The whole capture runs the semantic state walk on the GAME MAIN THREAD (the provider marshals
/// itself there), so a capture that nobody needed is main-thread time taken from the frame budget.
/// That is what layers (1) and (2) exist to stop; with backoff disabled the pacing is exactly what it
/// was before them.
/// </para>
/// </summary>
internal sealed class EmbeddableStateSubscriptionHub<TSnapshot, TEvent>(
    Func<StateProjection, StateCaptureOutcome<TSnapshot>> capture,
    Func<TSnapshot, string> stateFingerprint,
    Func<EmbeddableRuntimeError, string> errorFingerprint,
    StateWatchEventFactory<TSnapshot, TEvent> makeEvent,
    Func<DateTimeOffset>? clock = null,
    ISts2RuntimeInstrumentation? instrumentation = null)
    where TSnapshot : class
{
    private static readonly TimeSpan DefaultMinCaptureInterval = TimeSpan.FromMilliseconds(50);

    private readonly Lock _gate = new();
    private readonly Func<StateProjection, StateCaptureOutcome<TSnapshot>> _capture = capture;
    private readonly Func<TSnapshot, string> _stateFingerprint = stateFingerprint;
    private readonly Func<EmbeddableRuntimeError, string> _errorFingerprint = errorFingerprint;
    private readonly StateWatchEventFactory<TSnapshot, TEvent> _makeEvent = makeEvent;
    private readonly ISts2RuntimeInstrumentation _instrumentation = instrumentation ?? Sts2RuntimeInstrumentation.None;

    // Injectable so the backoff schedule can be asserted exactly instead of slept through; there is no
    // TimeProvider anywhere in this assembly to lean on.
    private readonly Func<DateTimeOffset> _clock = clock ?? (static () => DateTimeOffset.UtcNow);
    private readonly Dictionary<ulong, StateSubscriber> _subscribers = [];
    private ulong _nextSubscriberId;
    private bool _dispatcherTickSubscribed;
    private bool _refreshScheduled;
    private bool _refreshRequested;
    private bool _forceRequested;

    /// <summary>
    /// Earliest UtcTicks at which ANY subscriber is due. Read without the lock on the game main thread
    /// (the tick gate) and written under it; <see cref="long.MaxValue"/> means "nothing subscribed".
    /// </summary>
    private long _nextDueUtcTicks = long.MaxValue;

    private ulong _lastObservedRevision;

    public IDisposable Subscribe(
        StateProjection projection,
        bool emitInitial,
        TimeSpan? minCaptureInterval,
        TimeSpan? maxIdleInterval,
        bool idleBackoff,
        Action<TEvent> onEvent,
        Action<EmbeddableRuntimeError>? onError)
    {
        ArgumentNullException.ThrowIfNull(onEvent);

        StateSubscriber subscriber;
        lock (_gate)
        {
            subscriber = new StateSubscriber(
                ++_nextSubscriberId,
                projection,
                emitInitial,
                minCaptureInterval,
                maxIdleInterval,
                idleBackoff,
                onEvent,
                onError);
            _subscribers[subscriber.Id] = subscriber;
            EnsureDispatcherTickSubscribedLocked();
            RecomputeNextDueLocked();
        }

        RequestRefresh(force: true);

        return new Subscription(this, subscriber.Id);
    }

    public void RequestRefresh(bool force = true)
    {
        lock (_gate)
        {
            if (_subscribers.Count == 0)
            {
                return;
            }

            if (force)
            {
                // A forced refresh is the signal that something REALLY happened (a subscription starting, an
                // accepted action): every subscriber goes back to its floor interval, so the next unchanged
                // capture starts the backoff ramp from the bottom rather than from wherever it had crept to.
                foreach (var subscriber in _subscribers.Values)
                {
                    subscriber.CollapseBackoff();
                }

                RecomputeNextDueLocked();
            }

            _refreshRequested = true;
            _forceRequested |= force;
            if (_refreshScheduled)
            {
                return;
            }

            _refreshScheduled = true;
        }

        _ = Task.Run(RefreshUntilQuiet);
    }

    /// <summary>
    /// Runs on the GAME MAIN THREAD at the dispatcher's tick rate (~100 Hz). Everything here is on the frame
    /// budget, so the common case — nothing due — must not lock, allocate or schedule anything.
    /// </summary>
    private void OnMainThreadTick()
    {
        var revisionWake = false;
        if (Sts2StateWatchRuntimeSettings.RevisionWake)
        {
            var revision = Sts2SemanticStateRevision.Current;
            if (revision != Volatile.Read(ref _lastObservedRevision))
            {
                Volatile.Write(ref _lastObservedRevision, revision);
                revisionWake = true;
            }
        }

        if (revisionWake)
        {
            _instrumentation.RecordStateRevisionWake();
            lock (_gate)
            {
                if (_subscribers.Count == 0)
                {
                    return;
                }

                foreach (var subscriber in _subscribers.Values)
                {
                    subscriber.CollapseBackoff();
                }

                RecomputeNextDueLocked();
            }
        }
        else if (_clock().UtcTicks < Volatile.Read(ref _nextDueUtcTicks))
        {
            _instrumentation.RecordStateSkippedByBackoff(1);
            return;
        }

        RequestRefresh(force: false);
    }

    private void RefreshUntilQuiet()
    {
        while (true)
        {
            StateSubscriber[] subscribers;
            bool force;
            lock (_gate)
            {
                _refreshRequested = false;
                force = _forceRequested;
                _forceRequested = false;
                subscribers = [.. _subscribers.Values];
            }

            RefreshSubscribers(subscribers, force);

            lock (_gate)
            {
                if (_refreshRequested)
                {
                    continue;
                }

                _refreshScheduled = false;
                return;
            }
        }
    }

    private void RefreshSubscribers(IReadOnlyList<StateSubscriber> subscribers, bool force)
    {
        if (subscribers.Count == 0)
        {
            return;
        }

        var now = _clock();

        // Built by hand rather than with Where/GroupBy/ToArray: this runs per eligible tick and the common shape
        // is a single subscriber, which the LINQ pipeline allocated four objects to discover.
        List<StateSubscriber>? due = null;
        var skipped = 0;
        foreach (var subscriber in subscribers)
        {
            if (!force && !subscriber.ShouldCapture(now))
            {
                skipped++;
                continue;
            }

            (due ??= new List<StateSubscriber>(subscribers.Count)).Add(subscriber);
        }

        _instrumentation.RecordStateSkippedByBackoff(skipped);
        if (due is null)
        {
            return;
        }

        if (due.Count == 1)
        {
            CaptureGroup(due);
            return;
        }

        // Two or more: group by capture identity so subscribers sharing a projection share one walk.
        var grouped = new Dictionary<SubscriptionCaptureKey, List<StateSubscriber>>();
        foreach (var subscriber in due)
        {
            var key = new SubscriptionCaptureKey(subscriber.Key, subscriber.Projection.StateTimeout);
            if (grouped.TryGetValue(key, out var bucket))
            {
                bucket.Add(subscriber);
            }
            else
            {
                grouped[key] = [subscriber];
            }
        }

        foreach (var bucket in grouped.Values)
        {
            CaptureGroup(bucket);
        }
    }

    private void CaptureGroup(List<StateSubscriber> group)
    {
        if (group.Count == 0)
        {
            return;
        }

        var profiling = _instrumentation.StateWatchProfilingEnabled;
        var started = profiling ? Stopwatch.GetTimestamp() : 0L;

        var result = _capture(group[0].Projection);
        var observedAt = _clock();

        var walkMs = profiling ? Elapsed(started) : 0.0;
        var fingerprintMs = 0.0;
        var emitted = false;
        foreach (var subscriber in group)
        {
            var outcome = ProcessResult(subscriber, result, observedAt, profiling);
            emitted |= outcome.Emitted;
            fingerprintMs += outcome.FingerprintMs;
        }

        if (profiling)
        {
            _instrumentation.RecordStateCapture(walkMs + fingerprintMs, fingerprintMs, emitted);
        }
    }

    private readonly record struct ProcessOutcome(bool Emitted, double FingerprintMs);

    private ProcessOutcome ProcessResult(
        StateSubscriber subscriber,
        StateCaptureOutcome<TSnapshot> result,
        DateTimeOffset observedAt,
        bool profiling)
    {
        TEvent? evt = default;
        var hasEvent = false;
        var fingerprintMs = 0.0;

        if (result.Success && result.State is not null)
        {
            var started = profiling ? Stopwatch.GetTimestamp() : 0L;
            var fingerprint = _stateFingerprint(result.State);
            if (profiling)
            {
                fingerprintMs = Elapsed(started);
            }

            lock (_gate)
            {
                if (!_subscribers.ContainsKey(subscriber.Id))
                {
                    return new ProcessOutcome(false, fingerprintMs);
                }

                subscriber.LastCaptureAtUtc = observedAt;
                subscriber.LastErrorFingerprint = null;
                if (subscriber.LastSemanticFingerprint is null)
                {
                    subscriber.LastSemanticFingerprint = fingerprint;
                    subscriber.NoteChanged();
                    if (subscriber.EmitInitial)
                    {
                        subscriber.Sequence++;
                        subscriber.SemanticRevision++;
                        evt = _makeEvent(
                            CurrentStateWatchEventType.Initial,
                            subscriber.Sequence,
                            observedAt,
                            fingerprint,
                            subscriber.SemanticRevision,
                            result.State,
                            null,
                            result.Health);
                        hasEvent = true;
                    }
                }
                else if (!string.Equals(fingerprint, subscriber.LastSemanticFingerprint, StringComparison.Ordinal))
                {
                    subscriber.LastSemanticFingerprint = fingerprint;
                    subscriber.NoteChanged();
                    subscriber.Sequence++;
                    subscriber.SemanticRevision++;
                    evt = _makeEvent(
                        CurrentStateWatchEventType.Changed,
                        subscriber.Sequence,
                        observedAt,
                        fingerprint,
                        subscriber.SemanticRevision,
                        result.State,
                        null,
                        result.Health);
                    hasEvent = true;
                }
                else
                {
                    subscriber.NoteUnchanged();
                }

                subscriber.ScheduleNext(observedAt);
                RecomputeNextDueLocked();
            }
        }
        else
        {
            var error = result.Error ?? new EmbeddableRuntimeError("state-unavailable", "Runtime state is unavailable.");
            var fingerprint = _errorFingerprint(error);
            lock (_gate)
            {
                if (!_subscribers.ContainsKey(subscriber.Id))
                {
                    return new ProcessOutcome(false, fingerprintMs);
                }

                subscriber.LastCaptureAtUtc = observedAt;
                if (!string.Equals(fingerprint, subscriber.LastErrorFingerprint, StringComparison.Ordinal))
                {
                    subscriber.LastErrorFingerprint = fingerprint;
                    subscriber.NoteChanged();
                    subscriber.Sequence++;
                    evt = _makeEvent(
                        CurrentStateWatchEventType.Error,
                        subscriber.Sequence,
                        observedAt,
                        fingerprint,
                        null,
                        null,
                        error,
                        result.Health);
                    hasEvent = true;
                }
                else
                {
                    // A runtime that has been unavailable for a while is exactly the case idle backoff is for:
                    // keep re-checking, but stop hammering the dispatcher every floor interval.
                    subscriber.NoteUnchanged();
                }

                subscriber.ScheduleNext(observedAt);
                RecomputeNextDueLocked();
            }
        }

        if (hasEvent)
        {
            subscriber.Enqueue(evt!);
        }

        return new ProcessOutcome(hasEvent, fingerprintMs);
    }

    private static double Elapsed(long startTimestamp)
        => 1000.0 * (Stopwatch.GetTimestamp() - startTimestamp) / Stopwatch.Frequency;

    private void RecomputeNextDueLocked()
    {
        if (_subscribers.Count == 0)
        {
            Volatile.Write(ref _nextDueUtcTicks, long.MaxValue);
            return;
        }

        var next = long.MaxValue;
        foreach (var subscriber in _subscribers.Values)
        {
            if (subscriber.NextDueUtcTicks < next)
            {
                next = subscriber.NextDueUtcTicks;
            }
        }

        Volatile.Write(ref _nextDueUtcTicks, next);
    }

    private void EnsureDispatcherTickSubscribedLocked()
    {
        if (_dispatcherTickSubscribed)
        {
            return;
        }

        // Adopt the current revision rather than 0, so attaching does not read as "something changed".
        Volatile.Write(ref _lastObservedRevision, Sts2SemanticStateRevision.Current);
        Sts2MainThreadDispatcher.MainThreadTick += OnMainThreadTick;
        _dispatcherTickSubscribed = true;
    }

    private void Dispose(ulong subscriberId)
    {
        lock (_gate)
        {
            if (_subscribers.Remove(subscriberId, out var subscriber))
            {
                subscriber.MarkDisposed();
            }

            if (_subscribers.Count == 0 && _dispatcherTickSubscribed)
            {
                Sts2MainThreadDispatcher.MainThreadTick -= OnMainThreadTick;
                _dispatcherTickSubscribed = false;
            }

            RecomputeNextDueLocked();
        }
    }

    private sealed class Subscription(EmbeddableStateSubscriptionHub<TSnapshot, TEvent> hub, ulong subscriberId) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                hub.Dispose(subscriberId);
            }
        }
    }

    private sealed class StateSubscriber
    {
        private readonly ConcurrentQueue<TEvent> _pending = new();
        private int _draining;
        private int _disposed;

        public StateSubscriber(
            ulong id,
            StateProjection projection,
            bool emitInitial,
            TimeSpan? minCaptureInterval,
            TimeSpan? maxIdleInterval,
            bool idleBackoff,
            Action<TEvent> onEvent,
            Action<EmbeddableRuntimeError>? onError)
        {
            Id = id;
            Projection = projection;
            EmitInitial = emitInitial;
            MinCaptureInterval = minCaptureInterval is { } interval && interval >= TimeSpan.Zero
                ? interval
                : DefaultMinCaptureInterval;
            // Clamped to the hard never-miss ceiling, and never below the floor: an idle ceiling under the
            // subscriber's own MinCaptureInterval would be a backoff that speeds capture up.
            var requestedMaxIdle = maxIdleInterval is { } max && max > TimeSpan.Zero
                ? TimeSpan.FromMilliseconds(Sts2StateWatchRuntimeSettings.ClampMaxIdleMs((long)max.TotalMilliseconds))
                : Sts2StateWatchRuntimeSettings.MaxIdleInterval;
            MaxIdleInterval = requestedMaxIdle < MinCaptureInterval ? MinCaptureInterval : requestedMaxIdle;
            IdleBackoffRequested = idleBackoff;
            CurrentInterval = MinCaptureInterval;
            OnEvent = onEvent;
            OnError = onError;
            Key = ProjectionCacheKey.From(Projection.Perspective);
        }

        public ulong Id { get; }

        public StateProjection Projection { get; }

        public bool EmitInitial { get; }

        /// <summary>Floor: the fastest this subscriber ever captures.</summary>
        public TimeSpan MinCaptureInterval { get; }

        /// <summary>Ceiling the interval doubles toward while nothing changes.</summary>
        public TimeSpan MaxIdleInterval { get; }

        /// <summary>What the subscription asked for; the global lever can still turn it off.</summary>
        public bool IdleBackoffRequested { get; }

        /// <summary>Effective, re-read per capture so an embedder can flip the lever live.</summary>
        public bool IdleBackoffEnabled => IdleBackoffRequested && Sts2StateWatchRuntimeSettings.IdleBackoff;

        /// <summary>Current pacing interval, between the floor and the ceiling.</summary>
        public TimeSpan CurrentInterval { get; private set; }

        public Action<TEvent> OnEvent { get; }

        public Action<EmbeddableRuntimeError>? OnError { get; }

        public ProjectionCacheKey Key { get; }

        public ulong Sequence { get; set; }

        public ulong SemanticRevision { get; set; }

        public string? LastSemanticFingerprint { get; set; }

        public string? LastErrorFingerprint { get; set; }

        public DateTimeOffset? LastCaptureAtUtc { get; set; }

        /// <summary>0 until the first capture: a fresh subscriber is due immediately.</summary>
        public long NextDueUtcTicks { get; private set; }

        public bool ShouldCapture(DateTimeOffset now) => now.UtcTicks >= NextDueUtcTicks;

        public void ScheduleNext(DateTimeOffset observedAt)
        {
            var interval = IdleBackoffEnabled ? CurrentInterval : MinCaptureInterval;
            NextDueUtcTicks = (observedAt + interval).UtcTicks;
        }

        /// <summary>Something changed — go back to the floor.</summary>
        public void NoteChanged() => CurrentInterval = MinCaptureInterval;

        /// <summary>
        /// Nothing changed — double the interval, capped at the ceiling. A floor of zero stays zero: a
        /// subscriber that asked to capture as fast as the tick allows has no ramp to climb.
        /// </summary>
        public void NoteUnchanged()
        {
            if (!IdleBackoffEnabled || CurrentInterval <= TimeSpan.Zero)
            {
                return;
            }

            var doubled = CurrentInterval + CurrentInterval;
            CurrentInterval = doubled > MaxIdleInterval ? MaxIdleInterval : doubled;
        }

        /// <summary>Back to the floor AND due now (a forced refresh or a semantic-revision wake).</summary>
        public void CollapseBackoff()
        {
            CurrentInterval = MinCaptureInterval;
            NextDueUtcTicks = 0;
        }

        public void MarkDisposed() => Volatile.Write(ref _disposed, 1);

        /// <summary>
        /// Hand one event to this subscriber's serial drain. Events MUST reach a consumer in emission order:
        /// a consumer that assigns "latest snapshot" unconditionally (couch-coop's state observer does) pins a
        /// stale snapshot forever if two events land out of order, and one Task.Run per event gives no ordering
        /// guarantee whatsoever. One queue plus one drain task at a time restores it without blocking the
        /// capture path or serializing unrelated subscribers against each other.
        /// </summary>
        public void Enqueue(TEvent evt)
        {
            _pending.Enqueue(evt);
            if (Interlocked.CompareExchange(ref _draining, 1, 0) == 0)
            {
                _ = Task.Run(Drain);
            }
        }

        private void Drain()
        {
            do
            {
                while (_pending.TryDequeue(out var evt))
                {
                    if (Volatile.Read(ref _disposed) != 0)
                    {
                        continue;
                    }

                    Deliver(evt);
                }

                Volatile.Write(ref _draining, 0);
            }
            while (!_pending.IsEmpty && Interlocked.CompareExchange(ref _draining, 1, 0) == 0);
        }

        private void Deliver(TEvent evt)
        {
            try
            {
                OnEvent(evt);
            }
            catch (Exception ex)
            {
                try
                {
                    OnError?.Invoke(new EmbeddableRuntimeError(
                        "state-subscription-callback-failed",
                        ex.Message,
                        Retryable: false));
                }
                catch
                {
                    // Subscriber error handlers are isolated from the runtime watcher.
                }
            }
        }
    }

    private sealed record SubscriptionCaptureKey(
        ProjectionCacheKey Projection,
        TimeSpan? StateTimeout);
}
