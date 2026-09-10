using Spirectl.Sts2.Embedding;
using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// Pacing and delivery-order contract of the semantic state watch hub, driven through an INJECTED clock and
// explicit main-thread ticks rather than real sleeps: a backoff schedule asserted with Task.Delay is a schedule
// nobody can read and a test nobody can trust. The hub is generic over the snapshot/event type, so these use a
// trivial string snapshot and count captures directly instead of standing up a runtime.
public sealed class EmbeddableStateSubscriptionHubTests
{
    private static readonly TimeSpan Floor = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan Ceiling = TimeSpan.FromMilliseconds(400);

    private sealed record TestEvent(CurrentStateWatchEventType Type, ulong Sequence, string? Fingerprint, string? State);

    [Fact]
    public async Task BacksOffWhenStateIsUnchangedAndStopsAtTheCeiling()
    {
        using var settings = new StateWatchSettingsScope();
        using var harness = new Harness();
        using var subscription = harness.Subscribe(Floor, Ceiling);

        // The subscription's own forced refresh is capture #1.
        await harness.WaitForCapturesAsync(1);

        // Nothing changes, so the interval doubles after every capture: 50, 100, 200, 400, 400, ...
        foreach (var dueAfterMs in new[] { 50, 100, 200, 400, 400 })
        {
            // A tick one millisecond early must NOT capture: that is the gate that stops ~100 pool tasks a second.
            harness.Advance(TimeSpan.FromMilliseconds(dueAfterMs - 1));
            await harness.TickAndExpectNoCaptureAsync();

            harness.Advance(TimeSpan.FromMilliseconds(1));
            await harness.TickUntilCapturedAsync();
        }

        Assert.Equal(6, harness.Captures);
    }

    [Fact]
    public async Task CapturesEveryFloorIntervalWhileStateKeepsChanging()
    {
        using var settings = new StateWatchSettingsScope();
        using var harness = new Harness();
        using var subscription = harness.Subscribe(Floor, Ceiling);

        await harness.WaitForCapturesAsync(1);

        for (var i = 0; i < 6; i++)
        {
            harness.Value = $"changed-{i}";
            harness.Advance(Floor);
            await harness.TickUntilCapturedAsync();
        }

        // A changed fingerprint resets the ramp every time, so the floor never grows.
        Assert.Equal(7, harness.Captures);
        var events = await harness.WaitForEventsAsync(7);
        Assert.Equal(CurrentStateWatchEventType.Initial, events[0].Type);
        Assert.All(events.Skip(1), evt => Assert.Equal(CurrentStateWatchEventType.Changed, evt.Type));
    }

    // The compatibility guarantee: with backoff off the pacing is exactly the pre-backoff pacing, unchanged
    // fingerprint or not.
    [Fact]
    public async Task IdleBackoffOptOutKeepsFloorPacingWhileUnchanged()
    {
        using var settings = new StateWatchSettingsScope();
        using var harness = new Harness();
        using var subscription = harness.Subscribe(Floor, Ceiling, idleBackoff: false);

        await harness.WaitForCapturesAsync(1);

        for (var i = 0; i < 5; i++)
        {
            harness.Advance(TimeSpan.FromMilliseconds(49));
            await harness.TickAndExpectNoCaptureAsync();
            harness.Advance(TimeSpan.FromMilliseconds(1));
            await harness.TickUntilCapturedAsync();
        }

        Assert.Equal(6, harness.Captures);
        // One emission only: the state never changed, so dedup still suppresses everything after the initial.
        Assert.Single(await harness.WaitForEventsAsync(1));
    }

    [Fact]
    public async Task ProcessWideIdleBackoffLeverOverridesTheSubscription()
    {
        using var settings = new StateWatchSettingsScope();
        Sts2StateWatchRuntimeSettings.IdleBackoff = false;

        using var harness = new Harness();
        using var subscription = harness.Subscribe(Floor, Ceiling);

        await harness.WaitForCapturesAsync(1);

        // The subscription asked for backoff, but the process lever is off, so it stays at the floor.
        for (var i = 0; i < 3; i++)
        {
            harness.Advance(Floor);
            await harness.TickUntilCapturedAsync();
        }

        Assert.Equal(4, harness.Captures);
    }

    [Fact]
    public async Task RevisionBumpCollapsesIdleBackoff()
    {
        using var settings = new StateWatchSettingsScope();
        using var harness = new Harness();
        using var subscription = harness.Subscribe(Floor, Ceiling);

        await harness.WaitForCapturesAsync(1);

        // Ramp the interval up to the ceiling.
        foreach (var dueAfterMs in new[] { 50, 100, 200, 400 })
        {
            harness.Advance(TimeSpan.FromMilliseconds(dueAfterMs));
            await harness.TickUntilCapturedAsync();
        }

        var captured = harness.Captures;

        // Sitting at a 400 ms interval, a 10 ms advance is nowhere near due.
        harness.Advance(TimeSpan.FromMilliseconds(10));
        await harness.TickAndExpectNoCaptureAsync();

        // A game-thread hook says something happened: the very next tick captures, with no further advance.
        harness.Value = "hooked-change";
        Sts2SemanticStateRevision.Bump();
        await harness.TickUntilCapturedAsync();
        Assert.Equal(captured + 1, harness.Captures);

        // And the ramp restarted from the floor rather than resuming at the ceiling.
        harness.Advance(Floor);
        await harness.TickUntilCapturedAsync();
        Assert.Equal(captured + 2, harness.Captures);
    }

    [Fact]
    public async Task RevisionWakeCanBeTurnedOff()
    {
        using var settings = new StateWatchSettingsScope();
        Sts2StateWatchRuntimeSettings.RevisionWake = false;

        using var harness = new Harness();
        using var subscription = harness.Subscribe(Floor, Ceiling);

        await harness.WaitForCapturesAsync(1);

        harness.Advance(TimeSpan.FromMilliseconds(10));
        Sts2SemanticStateRevision.Bump();
        await harness.TickAndExpectNoCaptureAsync();

        // The poll still finds it: the revision is an accelerator, never the thing that makes a change visible.
        harness.Advance(Floor);
        await harness.TickUntilCapturedAsync();
    }

    [Fact]
    public async Task ForcedRefreshCapturesImmediatelyAndResetsTheRamp()
    {
        using var settings = new StateWatchSettingsScope();
        using var harness = new Harness();
        using var subscription = harness.Subscribe(Floor, Ceiling);

        await harness.WaitForCapturesAsync(1);
        foreach (var dueAfterMs in new[] { 50, 100, 200 })
        {
            harness.Advance(TimeSpan.FromMilliseconds(dueAfterMs));
            await harness.TickUntilCapturedAsync();
        }

        var captured = harness.Captures;

        // This is the path an accepted action takes: the action changed something, then forced a refresh.
        harness.Value = "after-action";
        harness.Hub.RequestRefresh(force: true);
        await harness.WaitForCapturesAsync(captured + 1);

        harness.Advance(Floor);
        await harness.TickUntilCapturedAsync();
        Assert.Equal(captured + 2, harness.Captures);
    }

    [Theory]
    [InlineData(5000, 400)]  // above the hard never-miss ceiling: clamped down
    [InlineData(120, 120)]   // inside the range: honoured
    [InlineData(10, 50)]     // below the subscriber's own floor: raised to the floor
    public async Task RequestedMaxIdleIntervalIsClampedIntoTheNeverMissBudget(int requestedMs, int effectiveMs)
    {
        using var settings = new StateWatchSettingsScope();
        using var harness = new Harness();
        using var subscription = harness.Subscribe(Floor, TimeSpan.FromMilliseconds(requestedMs));

        await harness.WaitForCapturesAsync(1);

        // Ramp well past any plausible ceiling, then assert where it actually settled.
        for (var i = 0; i < 8; i++)
        {
            harness.Advance(TimeSpan.FromMilliseconds(effectiveMs));
            await harness.TickUntilCapturedAsync();
        }

        var captured = harness.Captures;
        harness.Advance(TimeSpan.FromMilliseconds(effectiveMs - 1));
        await harness.TickAndExpectNoCaptureAsync();
        harness.Advance(TimeSpan.FromMilliseconds(1));
        await harness.TickUntilCapturedAsync();
        Assert.Equal(captured + 1, harness.Captures);
    }

    // The bug this replaces: one Task.Run per event gave the thread pool licence to run event 2's callback before
    // event 1's, and a consumer that assigns "latest snapshot" unconditionally then pins a stale snapshot.
    [Fact]
    public async Task DeliversEventsInSequenceOrderEvenWhenTheFirstCallbackIsSlow()
    {
        using var settings = new StateWatchSettingsScope();
        using var harness = new Harness();

        var seen = new List<ulong>();
        var slowCallbackEntered = new ManualResetEventSlim(false);
        var releaseSlowCallback = new ManualResetEventSlim(false);

        using var subscription = harness.Subscribe(
            Floor,
            Ceiling,
            onEvent: evt =>
            {
                if (evt.Sequence == 1)
                {
                    slowCallbackEntered.Set();
                    releaseSlowCallback.Wait(TimeSpan.FromSeconds(5));
                }

                lock (seen)
                {
                    seen.Add(evt.Sequence);
                }
            });

        Assert.True(slowCallbackEntered.Wait(TimeSpan.FromSeconds(5)));

        // Queue up four more changes while the first callback is still blocked.
        for (var i = 0; i < 4; i++)
        {
            harness.Value = $"changed-{i}";
            harness.Advance(Floor);
            await harness.TickUntilCapturedAsync();
        }

        releaseSlowCallback.Set();

        await WaitForAsync(() =>
        {
            lock (seen)
            {
                return seen.Count == 5;
            }
        });

        lock (seen)
        {
            Assert.Equal(new ulong[] { 1, 2, 3, 4, 5 }, seen);
        }
    }

    [Fact]
    public async Task DisposedSubscriptionStopsCapturingAndTicksBecomeInert()
    {
        using var settings = new StateWatchSettingsScope();
        using var harness = new Harness();
        var subscription = harness.Subscribe(Floor, Ceiling);

        await harness.WaitForCapturesAsync(1);
        subscription.Dispose();

        harness.Value = "changed";
        harness.Advance(TimeSpan.FromSeconds(5));
        await harness.TickAndExpectNoCaptureAsync();
        Assert.Single(await harness.WaitForEventsAsync(1));
    }

    private static async Task WaitForAsync(Func<bool> condition, [System.Runtime.CompilerServices.CallerMemberName] string? caller = null)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(2);
        }

        Assert.Fail($"Timed out waiting for the hub in {caller}.");
    }

    /// <summary>
    /// Saves and restores the process-global state-watch levers so one test's lever flip cannot leak into the
    /// next, and pins the defaults the assertions above are written against.
    /// </summary>
    private sealed class StateWatchSettingsScope : IDisposable
    {
        private readonly bool _idleBackoff = Sts2StateWatchRuntimeSettings.IdleBackoff;
        private readonly TimeSpan _maxIdle = Sts2StateWatchRuntimeSettings.MaxIdleInterval;
        private readonly bool _revisionWake = Sts2StateWatchRuntimeSettings.RevisionWake;
        private readonly bool _profile = Sts2StateWatchRuntimeSettings.Profile;

        public StateWatchSettingsScope()
        {
            Sts2StateWatchRuntimeSettings.IdleBackoff = true;
            Sts2StateWatchRuntimeSettings.MaxIdleInterval = Ceiling;
            Sts2StateWatchRuntimeSettings.RevisionWake = true;
            Sts2StateWatchRuntimeSettings.Profile = false;
        }

        public void Dispose()
        {
            Sts2StateWatchRuntimeSettings.IdleBackoff = _idleBackoff;
            Sts2StateWatchRuntimeSettings.MaxIdleInterval = _maxIdle;
            Sts2StateWatchRuntimeSettings.RevisionWake = _revisionWake;
            Sts2StateWatchRuntimeSettings.Profile = _profile;
        }
    }

    private sealed class Harness : IDisposable
    {
        private long _nowUtcTicks = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks;
        private string _value = "initial";
        private int _captures;

        public Harness()
        {
            Hub = new EmbeddableStateSubscriptionHub<string, TestEvent>(
                Capture,
                static state => state,
                static error => error.Code,
                static (type, sequence, observedAt, fingerprint, revision, state, error, health)
                    => new TestEvent(type, sequence, fingerprint, state),
                Now);
        }

        public EmbeddableStateSubscriptionHub<string, TestEvent> Hub { get; }

        public List<TestEvent> Events { get; } = [];

        public int Captures => Volatile.Read(ref _captures);

        public string Value
        {
            get => Volatile.Read(ref _value);
            set => Volatile.Write(ref _value, value);
        }

        public IDisposable Subscribe(
            TimeSpan floor,
            TimeSpan? maxIdle = null,
            bool idleBackoff = true,
            Action<TestEvent>? onEvent = null)
            => Hub.Subscribe(
                new StateProjection(null, null),
                emitInitial: true,
                floor,
                maxIdle,
                idleBackoff,
                onEvent ?? Record,
                null);

        public void Advance(TimeSpan delta) => Interlocked.Add(ref _nowUtcTicks, delta.Ticks);

        /// <summary>Wait for N events to be DELIVERED (the drain is asynchronous) and return a stable copy.</summary>
        public async Task<IReadOnlyList<TestEvent>> WaitForEventsAsync(int expected)
        {
            await WaitForAsync(() =>
            {
                lock (Events)
                {
                    return Events.Count >= expected;
                }
            });

            lock (Events)
            {
                Assert.Equal(expected, Events.Count);
                return [.. Events];
            }
        }

        public async Task WaitForCapturesAsync(int expected)
        {
            await WaitForAsync(() => Captures >= expected);
            Assert.Equal(expected, Captures);
        }

        /// <summary>Fire one main-thread tick and wait for the capture it must produce.</summary>
        public async Task TickUntilCapturedAsync()
        {
            var before = Captures;
            Sts2MainThreadDispatcher.NotifyMainThreadTick();
            await WaitForAsync(() => Captures > before);
            Assert.Equal(before + 1, Captures);
        }

        /// <summary>Fire one main-thread tick and assert the gate swallowed it.</summary>
        public async Task TickAndExpectNoCaptureAsync()
        {
            var before = Captures;
            Sts2MainThreadDispatcher.NotifyMainThreadTick();
            await Task.Delay(40);
            Assert.Equal(before, Captures);
        }

        public void Dispose() => Sts2MainThreadDispatcher.ResetForTests();

        private DateTimeOffset Now() => new(Volatile.Read(ref _nowUtcTicks), TimeSpan.Zero);

        private StateCaptureOutcome<string> Capture(StateProjection projection)
        {
            Interlocked.Increment(ref _captures);
            return new StateCaptureOutcome<string>(true, Value, null, null);
        }

        private void Record(TestEvent evt)
        {
            lock (Events)
            {
                Events.Add(evt);
            }
        }
    }
}
