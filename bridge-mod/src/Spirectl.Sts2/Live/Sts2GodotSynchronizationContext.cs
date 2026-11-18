using System.Collections.Concurrent;
using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace Spirectl.Sts2.Live;

internal sealed class Sts2GodotSynchronizationContext : SynchronizationContext, IMainThreadDispatcherDiagnostics
{
    private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();
    private readonly Action _scheduleDrain;
    private int _drainScheduled;

    internal Sts2GodotSynchronizationContext(Action scheduleDrain)
    {
        _scheduleDrain = scheduleDrain ?? throw new ArgumentNullException(nameof(scheduleDrain));
    }

    public int QueueDepth => _queue.Count;

    public DateTimeOffset? LastDrainAtUtc { get; private set; }

    public string? DispatcherNote => null;

    public bool? IsApplicationFocused { get; private set; }

    public DateTimeOffset? LastFocusChangedAtUtc { get; private set; }

    public override void Post(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);
        _queue.Enqueue((d, state));
        ScheduleDrainIfNeeded();
    }

    public override SynchronizationContext CreateCopy()
    {
        return this;
    }

    public void Drain()
    {
        try
        {
            while (_queue.TryDequeue(out var work))
            {
                work.Callback(work.State);
            }

            LastDrainAtUtc = DateTimeOffset.UtcNow;
        }
        finally
        {
            // A producer can enqueue after the empty TryDequeue and before this reset. Clearing first and then
            // rechecking closes that window: either this thread schedules the follow-up drain, or the producer
            // observes zero and schedules it itself. Re-entrant Post calls remain FIFO and are normally consumed
            // by the loop above without another deferred callback.
            Volatile.Write(ref _drainScheduled, 0);
            if (!_queue.IsEmpty)
            {
                ScheduleDrainIfNeeded();
            }
        }
    }

    internal void DrainBefore(Action observe)
    {
        ArgumentNullException.ThrowIfNull(observe);
        Drain();
        observe();
    }

    private void ScheduleDrainIfNeeded()
    {
        if (Interlocked.CompareExchange(ref _drainScheduled, 1, 0) != 0)
        {
            return;
        }

        try
        {
            _scheduleDrain();
        }
        catch
        {
            Volatile.Write(ref _drainScheduled, 0);
            throw;
        }
    }

    public void SetApplicationFocused(bool focused)
    {
        if (IsApplicationFocused == focused)
        {
            return;
        }

        IsApplicationFocused = focused;
        LastFocusChangedAtUtc = DateTimeOffset.UtcNow;
    }
}

internal sealed class Sts2MainThreadShutdownGate
{
    private readonly object _sync = new();
    private bool _closed;

    internal void Run(Action action)
    {
        lock (_sync)
        {
            if (!_closed)
            {
                // Keep the engine call inside the lock so Close waits for any in-flight call.
                action();
            }
        }
    }

    internal bool Close()
    {
        lock (_sync)
        {
            if (_closed)
            {
                return false;
            }

            _closed = true;
            return true;
        }
    }
}

public static class Sts2GodotMainThreadPump
{
    private const string DispatcherNodeName = "SpirectlMainThreadDispatcher";

    private static readonly Sts2MainThreadShutdownGate ShutdownGate = new();
    private static Sts2GodotSynchronizationContext? _context;
    private static Godot.Timer? _timer;
    private static Callable? _timerCallback;
    private static Callable? _drainCallback;
    private static Callable? _tickDemandCallback;
    private static Callable? _shutdownCallback;
    private static Callable? _focusEnteredCallback;
    private static Callable? _focusExitedCallback;
    private static bool _loggedFirstDrain;

    public static SynchronizationContext Install()
    {
        if (_context is not null)
        {
            return _context;
        }

        var tree = Engine.GetMainLoop() as SceneTree
            ?? throw new InvalidOperationException("Engine.GetMainLoop() did not return a SceneTree.");
        var root = tree.Root
            ?? throw new InvalidOperationException("SceneTree.Root is not available.");

        _drainCallback = Callable.From(DrainPostedWorkOnMainThread);
        _tickDemandCallback = Callable.From(ReconcileTickTimerDemand);
        var context = new Sts2GodotSynchronizationContext(SchedulePostedWorkDrain);
        SynchronizationContext.SetSynchronizationContext(context);
        _context = context;
        Sts2MainThreadDispatcher.MainThreadTickDemandChanged += OnTickDemandChanged;

        // Engine notifications reach this pump only through signal connections: virtual notification overrides
        // on nodes this mod creates are not dispatched. The root connections are made here, synchronously, so
        // the gate closes even when the engine tears down before the deferred install below ever runs. Statics
        // are per assembly copy, so every loaded copy of this pump connects and closes its own gate.
        _shutdownCallback = Callable.From(CloseShutdownGate);
        _focusEnteredCallback = Callable.From(OnRootWindowFocusEntered);
        _focusExitedCallback = Callable.From(OnRootWindowFocusExited);
        root.Connect(Node.SignalName.TreeExiting, _shutdownCallback.Value);
        root.Connect(Window.SignalName.FocusEntered, _focusEnteredCallback.Value);
        root.Connect(Window.SignalName.FocusExited, _focusExitedCallback.Value);

        var timer = new Godot.Timer
        {
            Name = DispatcherNodeName,
            WaitTime = 0.01,
            OneShot = false,
            Autostart = false,
            ProcessMode = Node.ProcessModeEnum.Always,
        };

        _timer = timer;
        _timerCallback = Callable.From(TickOnMainThread);
        var installCallback = Callable.From(() =>
        {
            Log.Info("[spirectl] Live bridge main-thread dispatcher deferred scene-tree add running.");
            root.AddChild(timer);
            timer.Connect(Godot.Timer.SignalName.Timeout, _timerCallback.Value);
            // Children leave the tree before the root does, so once installed this closes the gate earlier
            // than the root connection above. Closing is idempotent.
            timer.Connect(Node.SignalName.TreeExiting, _shutdownCallback.Value);
            ReconcileTickTimerDemand();
            Log.Info("[spirectl] Live bridge main-thread dispatcher tick timer installed dormant.");
        });
        ShutdownGate.Run(() => installCallback.CallDeferred());
        Log.Info("[spirectl] Live bridge main-thread dispatcher queued for deferred scene-tree install.");

        return context;
    }

    private static void CloseShutdownGate()
    {
        if (ShutdownGate.Close())
        {
            Log.Info("[spirectl] Live bridge main-thread dispatcher shutdown gate closed.");
        }
    }

    private static void OnRootWindowFocusEntered() => _context?.SetApplicationFocused(true);

    private static void OnRootWindowFocusExited() => _context?.SetApplicationFocused(false);

    private static void SchedulePostedWorkDrain()
        => ShutdownGate.Run(() => _drainCallback?.CallDeferred());

    private static void DrainPostedWorkOnMainThread()
    {
        if (_context is null)
        {
            return;
        }

        SynchronizationContext.SetSynchronizationContext(_context);
        if (!_loggedFirstDrain)
        {
            _loggedFirstDrain = true;
            Log.Info("[spirectl] Live bridge main-thread dispatcher is draining queued work.");
        }

        _context.Drain();
    }

    private static void TickOnMainThread()
    {
        if (_context is null || !Sts2MainThreadDispatcher.HasMainThreadTickDemand)
        {
            return;
        }

        SynchronizationContext.SetSynchronizationContext(_context);
        // Preserve the dispatcher's historical input-before-observation contract. CallDeferred normally drains
        // posts sooner, but Godot may flush deferred calls after this timer timeout in a frame. Draining here too
        // guarantees work already queued for the game thread is visible to tick consumers in this same capture.
        _context.DrainBefore(Sts2MainThreadDispatcher.NotifyMainThreadTick);
    }

    private static void OnTickDemandChanged()
        => ShutdownGate.Run(() => _tickDemandCallback?.CallDeferred());

    private static void ReconcileTickTimerDemand()
    {
        if (_timer is null || !GodotObject.IsInstanceValid(_timer) || !_timer.IsInsideTree())
        {
            return;
        }

        if (Sts2MainThreadDispatcher.HasMainThreadTickDemand)
        {
            // A repeating timer shorter than a frame can have an overdue countdown while still processing.
            // Timer.IsStopped() tests that countdown, so it cannot identify whether this pump is armed.
            if (!_timer.IsProcessingInternal())
            {
                _timer.Start();
            }
        }
        else
        {
            _timer.Stop();
        }
    }
}
