using System;
using System.Reflection;
using Godot;
using Timer = Godot.Timer;
using Spirectl.Sts2.Live;

public partial class Probe : Node
{
    private IDisposable? _lease;
    private Timer? _timer;
    private int _phase;
    private int _frames;
    private int _releaseFrames;
    private bool _overdueSeen;
    private bool _redundantNoRestart;
    private readonly MethodInfo _reconcile = typeof(Sts2GodotMainThreadPump).GetMethod(
        "ReconcileTickTimerDemand", BindingFlags.Static | BindingFlags.NonPublic)!;

    public override void _Ready()
    {
        Engine.MaxFps = 60;
        Sts2GodotMainThreadPump.Install();
        _lease = Sts2MainThreadDispatcher.AcquireMainThreadTickLease();
        Console.WriteLine("FIXTURE: started source-linked pump at 60 FPS, 10 ms timer");
    }

    public override void _Process(double delta)
    {
        _frames++;
        if (_frames > 180)
        {
            Fail("timeout waiting for timer state");
            return;
        }
        _timer ??= GetTree().Root.GetNodeOrNull<Timer>("SpirectlMainThreadDispatcher");
        if (_timer == null) return;

        switch (_phase)
        {
            case 0:
                if (!_timer.IsProcessingInternal()) return;
                if (!_timer.IsStopped()) return;
                _overdueSeen = true;
                Console.WriteLine($"FIXTURE: overdue IsStopped={_timer.IsStopped()} IsProcessingInternal={_timer.IsProcessingInternal()} TimeLeft={_timer.TimeLeft:F6}");
                _lease!.Dispose();
                _lease = null;
                _phase = 1;
                break;
            case 1:
                if (_timer.IsProcessingInternal())
                {
                    if (++_releaseFrames > 10) Fail("last-lease still processing after release");
                    return;
                }
                Console.WriteLine($"FIXTURE: released IsStopped={_timer.IsStopped()} IsProcessingInternal={_timer.IsProcessingInternal()}");
                _lease = Sts2MainThreadDispatcher.AcquireMainThreadTickLease();
                _phase = 2;
                break;
            case 2:
                if (!_timer.IsProcessingInternal()) return;
                if (!_timer.IsStopped()) return;
                Console.WriteLine($"FIXTURE: reacquired IsProcessingInternal={_timer.IsProcessingInternal()}");
                var before = _timer.TimeLeft;
                _reconcile.Invoke(null, null);
                var after = _timer.TimeLeft;
                _redundantNoRestart = Math.Abs(after - before) < 0.0000001;
                Console.WriteLine($"FIXTURE: redundant demand before={before:F6} after={after:F6} unchanged={_redundantNoRestart}");
                if (!_overdueSeen || !_redundantNoRestart)
                {
                    Fail("timer demand regression");
                    return;
                }
                Console.WriteLine("FIXTURE_RESULT PASS source-linked overdue, stop, reacquire, redundant demand");
                GetTree().Quit(0);
                _phase = 3;
                break;
        }
    }

    private void Fail(string message)
    {
        Console.Error.WriteLine("FIXTURE_RESULT FAIL " + message);
        GetTree().Quit(1);
        _phase = 3;
    }
}
