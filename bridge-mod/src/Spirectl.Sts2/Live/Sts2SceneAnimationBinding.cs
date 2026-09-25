using Spirectl.Sts2.Core.SceneInspection;
using Spirectl.Sts2.Embedding;

namespace Spirectl.Sts2.Live;

/// <summary>
/// Owns one runtime's callbacks into process-lifetime animation hooks. Replacing a runtime does
/// not unpatch hooks, and disposing an older binding cannot remove its replacement's callbacks.
/// </summary>
internal sealed class Sts2SceneAnimationBinding : IDisposable
{
    private readonly Func<ulong, TweenTargetChange, double, TweenEndpoint?> _endpoint;
    private readonly Action<IReadOnlyCollection<ulong>, bool> _cancel;
    private readonly Func<Sts2CardFlightResolveRequest, CardFlightHint?> _shuffle;
    private readonly Func<Sts2DiscardFlightResolveRequest, CardFlightHint?> _discard;
    private readonly Func<ulong, bool> _hasWindow;
    private int _active;
    private int _disposed;

    public Sts2SceneAnimationBinding(
        Func<ulong, TweenTargetChange, double, TweenEndpoint?> endpoint,
        Action<IReadOnlyCollection<ulong>, bool> cancel,
        Func<Sts2CardFlightResolveRequest, CardFlightHint?> shuffle,
        Func<Sts2DiscardFlightResolveRequest, CardFlightHint?> discard,
        Func<ulong, bool> hasWindow,
        bool activate = true)
    {
        _endpoint = (id, change, duration) => IsActive ? endpoint(id, change, duration) : null;
        _cancel = (ids, opacity) => { if (IsActive) cancel(ids, opacity); };
        _shuffle = request => IsActive ? shuffle(request) : null;
        _discard = request => IsActive ? discard(request) : null;
        _hasWindow = id => IsActive && hasWindow(id);

        if (activate)
        {
            Activate();
        }
    }

    private bool IsActive => Volatile.Read(ref _active) != 0 && Volatile.Read(ref _disposed) == 0;
    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public void Activate()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (Interlocked.Exchange(ref _active, 1) != 0)
        {
            return;
        }

        Sts2SceneAnimationCallbacks.TweenEndpointResolver = _endpoint;
        Sts2SceneAnimationCallbacks.TweenWindowCanceller = _cancel;
        Sts2SceneAnimationCallbacks.ShuffleResolver = _shuffle;
        Sts2SceneAnimationCallbacks.DiscardResolver = _discard;
        Sts2SceneAnimationCallbacks.HandEndpointResolver = _endpoint;
        Sts2SceneAnimationCallbacks.HandWindowCanceller = _cancel;
        Sts2SceneAnimationCallbacks.HasOpenWindow = _hasWindow;
    }

    public void Deactivate()
    {
        if (Interlocked.Exchange(ref _active, 0) == 0)
        {
            return;
        }

        // CompareExchange is identity-based, so even an in-flight replacement is retained.
        Interlocked.CompareExchange(ref Sts2SceneAnimationCallbacks.TweenEndpointResolver, null, _endpoint);
        Interlocked.CompareExchange(ref Sts2SceneAnimationCallbacks.TweenWindowCanceller, null, _cancel);
        Interlocked.CompareExchange(ref Sts2SceneAnimationCallbacks.ShuffleResolver, null, _shuffle);
        Interlocked.CompareExchange(ref Sts2SceneAnimationCallbacks.DiscardResolver, null, _discard);
        Interlocked.CompareExchange(ref Sts2SceneAnimationCallbacks.HandEndpointResolver, null, _endpoint);
        Interlocked.CompareExchange(ref Sts2SceneAnimationCallbacks.HandWindowCanceller, null, _cancel);
        Interlocked.CompareExchange(ref Sts2SceneAnimationCallbacks.HasOpenWindow, null, _hasWindow);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Deactivate();
    }
}
