using System.Threading;

namespace Spirectl.Sts2.Live;

public static class Sts2MainThreadDispatcher
{
    private static readonly object TickDemandGate = new();
    private static SynchronizationContext? _gameContext;
    private static int? _gameThreadId;
    private static int _tickLeaseCount;
    private static long _tickLeaseGeneration;

    internal static event Action? MainThreadTick;
    internal static event Action? MainThreadTickDemandChanged;

    internal static bool HasMainThreadTickDemand
    {
        get
        {
            lock (TickDemandGate)
            {
                return _tickLeaseCount > 0;
            }
        }
    }

    internal static int MainThreadTickLeaseCount
    {
        get
        {
            lock (TickDemandGate)
            {
                return _tickLeaseCount;
            }
        }
    }

    public static void Capture()
    {
        _gameContext = SynchronizationContext.Current;
        _gameThreadId = Environment.CurrentManagedThreadId;
    }

    public static void Capture(SynchronizationContext gameContext)
    {
        _gameContext = gameContext;
        _gameThreadId = Environment.CurrentManagedThreadId;
    }

    public static MainThreadDispatcherStatus DescribeStatus()
    {
        var diagnostics = _gameContext as IMainThreadDispatcherDiagnostics;
        return new MainThreadDispatcherStatus(
            HasCapturedContext: _gameContext is not null,
            CapturedThreadId: _gameThreadId,
            CurrentThreadId: Environment.CurrentManagedThreadId,
            IsOnCapturedThread: IsOnCapturedThread(),
            QueueDepth: diagnostics?.QueueDepth,
            LastQueueDrainAtUtc: diagnostics?.LastDrainAtUtc,
            DispatcherNote: diagnostics?.DispatcherNote,
            IsApplicationFocused: diagnostics?.IsApplicationFocused,
            LastFocusChangedAtUtc: diagnostics?.LastFocusChangedAtUtc);
    }

    public static T Invoke<T>(Func<T> action)
    {
        if (IsOnCapturedThread())
        {
            return InvokeWithCapturedContext(action);
        }

        if (_gameContext == null)
        {
            return action();
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _gameContext.Post(_ =>
        {
            try
            {
                completion.SetResult(action());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        }, null);

        return completion.Task.GetAwaiter().GetResult();
    }

    public static T Invoke<T>(Func<T> action, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            return Invoke(action);
        }

        if (IsOnCapturedThread() || _gameContext == null)
        {
            return Invoke(action);
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _gameContext.Post(_ =>
        {
            try
            {
                completion.TrySetResult(action());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }, null);

        if (!completion.Task.Wait(timeout))
        {
            throw new TimeoutException($"Timed out after {timeout.TotalMilliseconds:0}ms waiting for the STS2 main thread.");
        }

        return completion.Task.GetAwaiter().GetResult();
    }

    public static async Task<T> InvokeAsync<T>(Func<Task<T>> action)
    {
        if (IsOnCapturedThread())
        {
            return await InvokeWithCapturedContextAsync(action);
        }

        if (_gameContext == null)
        {
            return await action();
        }

        var completion = new TaskCompletionSource<Task<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _gameContext.Post(_ =>
        {
            try
            {
                completion.SetResult(action());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        }, null);

        return await await completion.Task.ConfigureAwait(false);
    }

    public static async Task<T> InvokeAsync<T>(Func<Task<T>> action, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            return await InvokeAsync(action).ConfigureAwait(false);
        }

        if (IsOnCapturedThread() || _gameContext == null)
        {
            return await InvokeAsync(action).ConfigureAwait(false);
        }

        var completion = new TaskCompletionSource<Task<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _gameContext.Post(_ =>
        {
            try
            {
                completion.TrySetResult(action());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }, null);

        var completed = await Task.WhenAny(completion.Task, Task.Delay(timeout)).ConfigureAwait(false);
        if (completed != completion.Task)
        {
            throw new TimeoutException($"Timed out after {timeout.TotalMilliseconds:0}ms waiting for the STS2 main thread.");
        }

        return await await completion.Task.ConfigureAwait(false);
    }

    public static void ResetForTests()
    {
        _gameContext = null;
        _gameThreadId = null;
        MainThreadTick = null;
        lock (TickDemandGate)
        {
            _tickLeaseCount = 0;
            _tickLeaseGeneration++;
        }
        MainThreadTickDemandChanged = null;
    }

    internal static IDisposable AcquireMainThreadTickLease()
    {
        long generation;
        var changed = false;
        lock (TickDemandGate)
        {
            generation = _tickLeaseGeneration;
            changed = _tickLeaseCount++ == 0;
        }

        if (changed)
        {
            MainThreadTickDemandChanged?.Invoke();
        }

        return new MainThreadTickLease(generation);
    }

    internal static void NotifyMainThreadTick()
        => MainThreadTick?.Invoke();

    private static void ReleaseMainThreadTickLease(long generation)
    {
        var changed = false;
        lock (TickDemandGate)
        {
            if (generation != _tickLeaseGeneration || _tickLeaseCount == 0)
            {
                return;
            }

            changed = --_tickLeaseCount == 0;
        }

        if (changed)
        {
            MainThreadTickDemandChanged?.Invoke();
        }
    }

    private sealed class MainThreadTickLease(long generation) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                ReleaseMainThreadTickLease(generation);
            }
        }
    }

    private static bool IsOnCapturedThread()
        => _gameThreadId == Environment.CurrentManagedThreadId
            || (_gameContext != null && SynchronizationContext.Current == _gameContext);

    private static T InvokeWithCapturedContext<T>(Func<T> action)
    {
        if (_gameContext == null || SynchronizationContext.Current == _gameContext)
        {
            return action();
        }

        var previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(_gameContext);
            return action();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private static async Task<T> InvokeWithCapturedContextAsync<T>(Func<Task<T>> action)
    {
        if (_gameContext == null || SynchronizationContext.Current == _gameContext)
        {
            return await action();
        }

        var previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(_gameContext);
            return await action();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
}

public sealed record MainThreadDispatcherStatus(
    bool HasCapturedContext,
    int? CapturedThreadId,
    int CurrentThreadId,
    bool IsOnCapturedThread,
    int? QueueDepth = null,
    DateTimeOffset? LastQueueDrainAtUtc = null,
    string? DispatcherNote = null,
    bool? IsApplicationFocused = null,
    DateTimeOffset? LastFocusChangedAtUtc = null)
{
    private static readonly TimeSpan StaleDrainThreshold = TimeSpan.FromMilliseconds(500);

    public string DispatcherLiveness
        => ClassifyLiveness(
            HasCapturedContext,
            IsApplicationFocused,
            LastQueueDrainAtUtc,
            QueueDepth,
            DateTimeOffset.UtcNow,
            StaleDrainThreshold);

    public static string ClassifyLiveness(
        bool hasCapturedContext,
        bool? isApplicationFocused,
        DateTimeOffset? lastQueueDrainAtUtc,
        int? queueDepth,
        DateTimeOffset nowUtc,
        TimeSpan staleDrainThreshold)
    {
        if (!hasCapturedContext)
        {
            return "not-installed";
        }

        if (isApplicationFocused == false)
        {
            return "backgrounded";
        }

        if (queueDepth > 0
            && (lastQueueDrainAtUtc is null || nowUtc - lastQueueDrainAtUtc > staleDrainThreshold))
        {
            return "stalled";
        }

        return "running";
    }
}

public interface IMainThreadDispatcherDiagnostics
{
    int QueueDepth { get; }

    DateTimeOffset? LastDrainAtUtc { get; }

    string? DispatcherNote { get; }

    bool? IsApplicationFocused { get; }

    DateTimeOffset? LastFocusChangedAtUtc { get; }
}
