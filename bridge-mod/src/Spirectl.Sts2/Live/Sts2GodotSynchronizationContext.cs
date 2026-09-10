using System.Collections.Concurrent;
using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace Spirectl.Sts2.Live;

internal sealed class Sts2GodotSynchronizationContext : SynchronizationContext, IMainThreadDispatcherDiagnostics
{
    private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();

    public int QueueDepth => _queue.Count;

    public DateTimeOffset? LastDrainAtUtc { get; private set; }

    public string? DispatcherNote => null;

    public bool? IsApplicationFocused { get; private set; }

    public DateTimeOffset? LastFocusChangedAtUtc { get; private set; }

    public override void Post(SendOrPostCallback d, object? state)
    {
        _queue.Enqueue((d, state));
    }

    public override SynchronizationContext CreateCopy()
    {
        return this;
    }

    public void Drain()
    {
        while (_queue.TryDequeue(out var work))
        {
            work.Callback(work.State);
        }

        LastDrainAtUtc = DateTimeOffset.UtcNow;
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

public static class Sts2GodotMainThreadPump
{
    private const string DispatcherNodeName = "SpirectlMainThreadDispatcher";

    private static Sts2GodotSynchronizationContext? _context;
    private static Godot.Timer? _timer;
    private static Callable? _timerCallback;
    private static bool _loggedFirstProcess;

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

        var context = new Sts2GodotSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(context);
        _context = context;

        var timer = new Godot.Timer
        {
            Name = DispatcherNodeName,
            WaitTime = 0.01,
            OneShot = false,
            Autostart = true,
            ProcessMode = Node.ProcessModeEnum.Always,
        };

        _timer = timer;
        _timerCallback = Callable.From(DrainOnMainThread);
        Callable.From(() =>
        {
            Log.Info("[spirectl] Live bridge main-thread dispatcher deferred scene-tree add running.");
            root.AddChild(timer);
            root.AddChild(new DispatcherDiagnosticsNode(context));
            timer.Connect(Godot.Timer.SignalName.Timeout, _timerCallback.Value);
            Log.Info("[spirectl] Live bridge main-thread dispatcher timer installed.");
        }).CallDeferred();
        Log.Info("[spirectl] Live bridge main-thread dispatcher queued for deferred scene-tree install.");

        return context;
    }

    private static void DrainOnMainThread()
    {
        if (_context is null)
        {
            return;
        }

        SynchronizationContext.SetSynchronizationContext(_context);
        if (!_loggedFirstProcess)
        {
            _loggedFirstProcess = true;
            Log.Info("[spirectl] Live bridge main-thread dispatcher is draining queued work.");
        }

        _context.Drain();
        Sts2MainThreadDispatcher.NotifyMainThreadTick();
    }

    private sealed partial class DispatcherDiagnosticsNode(Sts2GodotSynchronizationContext context) : Node
    {
        private const int NotificationWmFocusIn = 1004;
        private const int NotificationWmFocusOut = 1005;
        private const int ApplicationFocusInNotification = 2016;
        private const int ApplicationFocusOutNotification = 2017;

        public override void _Notification(int what)
        {
            switch (what)
            {
                case NotificationWmFocusIn:
                case ApplicationFocusInNotification:
                    context.SetApplicationFocused(true);
                    break;
                case NotificationWmFocusOut:
                case ApplicationFocusOutNotification:
                    context.SetApplicationFocused(false);
                    break;
            }
        }
    }
}
